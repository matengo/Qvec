#Requires -Version 7.0
<#
.SYNOPSIS
    Paired A/B comparison of two Qvec commits on a real ANN dataset.

.DESCRIPTION
    Builds benchmarks/Qvec.Benchmarks at two commits (base and head) in throw-away git
    worktrees, then runs the two binaries ALTERNATING on the same dataset -- base, head, base,
    head, ... -- for the requested number of rounds. Interleaving cancels the slow drift a
    shared runner exhibits, and pairing the rounds makes a 10 % change visible under 20 % noise.
    Only the head/base ratio is meaningful on a shared runner; absolute numbers are not.

    The per-run Markdown report each binary writes with --out is parsed, so the script works
    against any commit since that flag existed. Both binaries run with invariant globalization
    so the numbers parse regardless of the host culture.

    Recall is reported next to throughput in every row, because a faster build that loses
    recall is a regression, not an improvement.

.PARAMETER Base
    Commit-ish for the baseline (default: master).
.PARAMETER Head
    Commit-ish for the candidate (default: HEAD).
.PARAMETER Dataset
    Dataset name understood by Qvec.Benchmarks (default: siftsmall). Cohere is meant for the
    reference machine, not for CI.
.PARAMETER Rounds
    Number of base+head pairs to run (default: 3). Medians are taken over rounds.
.PARAMETER Threads
    Index build threads passed through as --threads (default: 1, byte-reproducible).
.PARAMETER Concurrency
    Query threads passed through as --concurrency (default: 1).
.PARAMETER Ef
    efSearch sweep passed through as --ef (default: 10,40,160).
.PARAMETER Passes
    Query passes per efSearch row passed through as --passes (default: 10; siftsmall has only
    100 queries, so a single pass is far too short to time).
.PARAMETER Out
    Optional path to write the Markdown result to (it is always printed).
.PARAMETER Data
    Dataset cache directory (default: the benchmark's own default under TEMP).

.EXAMPLE
    pwsh benchmarks/compare.ps1 -Base master -Head perf-kernels -Rounds 5
#>
[CmdletBinding()]
param(
    [string]$Base = 'master',
    [string]$Head = 'HEAD',
    [string]$Dataset = 'siftsmall',
    [ValidateRange(1, 50)][int]$Rounds = 3,
    [int]$Threads = 1,
    [int]$Concurrency = 1,
    [string]$Ef = '10,40,160',
    [int]$Passes = 10,
    [string]$Out,
    [string]$Data
)

$ErrorActionPreference = 'Stop'
# The report is Markdown for GitHub; format numbers the same way regardless of host culture.
[cultureinfo]::CurrentCulture = [cultureinfo]::InvariantCulture
$repo = (git rev-parse --show-toplevel).Trim()
if (-not $repo) { throw 'compare.ps1 must run inside the Qvec repository.' }

function Resolve-Commit([string]$ref) {
    $sha = (git -C $repo rev-parse --verify --quiet "$ref^{commit}")
    # A branch that only exists on the remote (a CI checkout of master comparing against a
    # feature branch, say) resolves through its remote-tracking ref.
    if (-not $sha) { $sha = (git -C $repo rev-parse --verify --quiet "origin/$ref^{commit}") }
    if (-not $sha) { throw "Cannot resolve '$ref' to a commit." }
    return $sha.Trim()
}

$baseSha = Resolve-Commit $Base
$headSha = Resolve-Commit $Head
Write-Host "base = $Base ($($baseSha.Substring(0, 10)))"
Write-Host "head = $Head ($($headSha.Substring(0, 10)))"

$work = Join-Path ([IO.Path]::GetTempPath()) "qvec-compare-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work | Out-Null

# Build one commit into a detached worktree and return the path of the produced executable.
function Build-Commit([string]$label, [string]$sha) {
    $tree = Join-Path $work $label
    Write-Host "Building $label ($($sha.Substring(0, 10))) ..."
    git -C $repo worktree add --detach --quiet $tree $sha | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for $label" }

    $project = Join-Path $tree 'benchmarks/Qvec.Benchmarks/Qvec.Benchmarks.csproj'
    $bin = Join-Path $tree "bin-$label"
    dotnet build $project -c Release -o $bin --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $label" }

    $dll = Join-Path $bin 'Qvec.Benchmarks.dll'
    if (-not (Test-Path $dll)) { throw "Build of $label produced no Qvec.Benchmarks.dll" }
    return $dll
}

function Remove-Worktrees {
    foreach ($label in 'base', 'head') {
        $tree = Join-Path $work $label
        if (Test-Path $tree) { git -C $repo worktree remove --force $tree 2>$null | Out-Null }
    }
    git -C $repo worktree prune 2>$null | Out-Null
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

# Run one binary once and return the parsed report.
function Invoke-Run([string]$label, [string]$dll, [int]$round) {
    $report = Join-Path $work "$label-$round.md"
    $index = Join-Path $work "$label-$round.qvec"
    $argList = @(
        $dll, '--dataset', $Dataset, '--download',
        '--threads', $Threads, '--concurrency', $Concurrency,
        '--ef', $Ef, '--passes', $Passes,
        '--index', $index, '--out', $report, '--allow-throttling'
    )
    if ($Data) { $argList += @('--data', $Data) }

    $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
    $stdout = & dotnet @argList 2>&1
    if ($LASTEXITCODE -ne 0) {
        $stdout | Write-Host
        throw "$label round $round failed"
    }
    return Read-Report $report
}

# Parse the Markdown that Qvec.Benchmarks writes with --out. Format (invariant culture):
#   Build: 12.3 s (813 inserts/s, 1 build thread). File: 6.1 MiB.
#   | 40 | 99.0 % | 98.7 % | 4,512 | 0.222 ms |
function Read-Report([string]$path) {
    $text = Get-Content $path -Raw
    $build = [regex]::Match($text, 'Build: ([\d.]+) s \(([\d,]+) inserts/s')
    if (-not $build.Success) { throw "Could not find build line in $path" }

    $points = @{}
    foreach ($m in [regex]::Matches($text, '(?m)^\| (\d+) \| ([\d.]+)\s*% \| ([\d.]+)\s*% \| ([\d,]+) \| ([\d.]+) ms \|')) {
        $points[[int]$m.Groups[1].Value] = [pscustomobject]@{
            RecallAt1 = [double]$m.Groups[2].Value / 100
            RecallAtK = [double]$m.Groups[3].Value / 100
            Qps       = [double]($m.Groups[4].Value -replace ',', '')
            LatencyMs = [double]$m.Groups[5].Value
        }
    }
    if ($points.Count -eq 0) { throw "Could not find any efSearch rows in $path" }

    return [pscustomobject]@{
        BuildSeconds     = [double]$build.Groups[1].Value
        InsertsPerSecond = [double]($build.Groups[2].Value -replace ',', '')
        Points           = $points
    }
}

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return $sorted[[int][math]::Floor($n / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

function Format-Ratio([double[]]$ratios) {
    $median = Get-Median $ratios
    $min = ($ratios | Measure-Object -Minimum).Minimum
    $max = ($ratios | Measure-Object -Maximum).Maximum
    $pct = ($median - 1) * 100
    $sign = if ($pct -ge 0) { '+' } else { '' }
    return ('{0:F3}× ({1}{2:F1} %; rounds {3:F2}–{4:F2})' -f $median, $sign, $pct, $min, $max)
}

try {
    $baseDll = Build-Commit 'base' $baseSha
    $headDll = Build-Commit 'head' $headSha

    $baseRuns = @()
    $headRuns = @()
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Host "Round $round/${Rounds}: base ..."
        $baseRuns += Invoke-Run 'base' $baseDll $round
        Write-Host "Round $round/${Rounds}: head ..."
        $headRuns += Invoke-Run 'head' $headDll $round
    }

    $sb = [System.Text.StringBuilder]::new()
    $hardware = "$([Environment]::ProcessorCount) logical cores, $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
    [void]$sb.AppendLine("### A/B: ``$Head`` vs ``$Base`` on $Dataset")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("base ``$($baseSha.Substring(0, 10))``, head ``$($headSha.Substring(0, 10))``; $Rounds interleaved rounds, build threads $Threads, query threads $Concurrency, $Passes query passes. Hardware: $hardware.")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Ratios are head/base per paired round, then the median over rounds; the range in brackets is the spread of the per-round ratios. Absolute values are the median over rounds and are only comparable within this run.')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| metric | base (median) | head (median) | head/base |')
    [void]$sb.AppendLine('| --- | ---: | ---: | ---: |')

    $ratios = for ($i = 0; $i -lt $Rounds; $i++) { $headRuns[$i].InsertsPerSecond / $baseRuns[$i].InsertsPerSecond }
    [void]$sb.AppendLine(('| build inserts/s | {0:N0} | {1:N0} | {2} |' -f
        (Get-Median ($baseRuns | ForEach-Object InsertsPerSecond)),
        (Get-Median ($headRuns | ForEach-Object InsertsPerSecond)),
        (Format-Ratio $ratios)))

    foreach ($efValue in ($baseRuns[0].Points.Keys | Sort-Object)) {
        $bq = $baseRuns | ForEach-Object { $_.Points[$efValue].Qps }
        $hq = $headRuns | ForEach-Object { $_.Points[$efValue].Qps }
        $ratios = for ($i = 0; $i -lt $Rounds; $i++) { $hq[$i] / $bq[$i] }
        $br = Get-Median ($baseRuns | ForEach-Object { $_.Points[$efValue].RecallAtK })
        $hr = Get-Median ($headRuns | ForEach-Object { $_.Points[$efValue].RecallAtK })
        [void]$sb.AppendLine(('| QPS @ ef {0} | {1:N0} | {2:N0} | {3} |' -f $efValue, (Get-Median $bq), (Get-Median $hq), (Format-Ratio $ratios)))
        $recallDelta = ($hr - $br) * 100
        $recallNote = if ([math]::Abs($recallDelta) -lt 0.05) { 'unchanged' } else { ('{0:+0.00;-0.00} pp' -f $recallDelta) }
        [void]$sb.AppendLine(('| recall@k @ ef {0} | {1:P2} | {2:P2} | {3} |' -f $efValue, $br, $hr, $recallNote))
    }

    $result = $sb.ToString()
    Write-Host
    Write-Host $result
    if ($Out) { Set-Content -Path $Out -Value $result -Encoding utf8 }
}
finally {
    Remove-Worktrees
}
