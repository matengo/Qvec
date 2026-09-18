# Design: Performance programme (tracking and improvement)

Status: plan, September 2026. Nothing in this document is implemented yet. Every number
quoted as "expected" is a hypothesis to be measured, not a claim; the README rule applies —
no figure is published without a measurement on the reference machine.

## 1. Why

The project's goal is to show the best throughput the design allows, and to show it honestly.
Two things stand in the way today:

1. **We cannot see performance move.** The only automated guard is
   `InsertThroughputTests` — a single absolute threshold on a shared GitHub runner. It fired
   twice on documentation-only commits (711/s and 786/s against an 800/s floor, #41/#42 master
   pushes) and had to be lowered to 500/s (#43). It cannot tell a 15 % regression from runner
   noise, and passing runs do not even print the value. The real numbers live in
   `benchmarks/README.md` and are produced by hand.
2. **There are known, unmeasured costs in the hot paths.** Reading the code shows several
   places where the arithmetic or the allocation pattern is clearly not what the hardware wants
   (§4). None has been profiled in isolation, so we do not know which are worth fixing.

The order matters: tracking first, then improvement. Without a repeatable A/B measurement,
every optimisation PR is argued rather than demonstrated.

## 2. Baseline (what we know today)

Reference machine: Snapdragon X Elite, 12 cores, ARM64, Windows 11, power-throttling exemption
in place. On ARM64 `Vector<float>` is 128 bits = **4 lanes** (NEON); there is no 256/512-bit
path on this machine.

| Workload | Measured | Source |
| --- | --- | --- |
| SIFT-1M build, 12 threads, float | 168.9 s = 5,920 inserts/s | benchmarks/README.md, "Change tracking cost" |
| Cohere 100K build, 1 thread, float | 371 s = 270 inserts/s | benchmarks/README.md, Cohere 100K table |
| Cohere 100K build, 12 threads, float | 59 s = 1,707 inserts/s (6.3× on 12 cores) | same |
| Cohere 1M float, efSearch 180, 12 threads | 3,764 QPS, recall@100 94.8 % | Cohere 1M table |
| Cohere 1M int8 rescored, efSearch 180, 12 threads | 6,778 QPS, recall@100 94.7 % | same |
| Cohere 1M int8, efSearch 100, 12 threads | 13,540 QPS | same |
| Thread scaling, queries, 1 → 12 threads | 6.7× float, 8.2× int8, 9–11× rescored | same |
| Local `InsertThroughputTests` (5k × 128-d, 1 thread) | 1,300–1,450/s dev machine; 711–786/s observed on CI | test comment, CI logs |

Two of these are already suspicious as a starting point: 6.3× build scaling and 6.7× query
scaling on 12 cores leave a third of the machine idle, and the 128-d single-thread insert rate
is about 1,400/s while the arithmetic it has to do (an `O(M0²)` prune at 64 neighbours) is on
the order of a few thousand 128-d distance evaluations per insert — that should be well under
half a millisecond.

## 3. Tracking

### 3.1 Principles

- **Relative, not absolute, on shared runners.** A GitHub-hosted runner varies by tens of
  percent between runs of the same binary. Nothing that runs there may compare against a
  stored number. It may compare two binaries built in the *same job* and run interleaved.
- **Absolute numbers only from the reference machine.** The README tables stay hand-run on
  the Snapdragon; a self-hosted runner on that machine is an option later (§3.5), not a
  prerequisite.
- **Micro and macro are different tools.** Kernel-level changes (a distance function) are
  measured with BenchmarkDotNet, where noise is a few percent. End-to-end changes (build time,
  QPS, recall) are measured with `Qvec.Benchmarks` on a real dataset.
- **Recall is part of every performance measurement.** A faster build that loses recall is a
  regression. Every macro result reports recall next to throughput.

### 3.2 Micro-benchmarks: `benchmarks/Qvec.MicroBenchmarks` (BenchmarkDotNet)

New project, separate from `Qvec.Benchmarks` so the latter stays a plain console app.
Benchmarks, each parameterised over `dim ∈ {128, 768, 1536}`:

| Benchmark | What it isolates |
| --- | --- |
| `DotProduct` / `NegativeSquaredDistance` (span, array, pointer variants) | the float kernels in `QvecDatabase.cs` |
| `Int8Quantizer.Similarity` (dot, Euclidean) | the widened-ushort int8 kernel |
| `Int8Quantizer.Quantize` / `Dequantize` | rescoring and apply cost |
| `SearchLayerNearest` on a fixed 10k-node graph, ef = 100 | one graph walk without the outer API |
| `SelectNeighborsHeuristic` + `PruneNeighbors` with 200 candidates, m = 32/64 | the insert prune |
| `AddNeighborConnection` on a full list | the back-link path |

The last three need `InternalsVisibleTo("Qvec.MicroBenchmarks")` and small internal
entry points; they are worth it because these are where a profile says the time goes.

Kernels are compared against `System.Numerics.Tensors.TensorPrimitives.Dot` /
`.Distance` as a reference implementation in the same run, so we know how far from
"library-quality" our own loops are before deciding whether to rewrite or replace them.

### 3.3 Macro A/B: `benchmarks/compare.ps1` and a `perf` workflow

A script that builds two commits of `Qvec.Benchmarks` (base and head), then runs them
**alternating** on the same dataset — base, head, base, head — and reports the paired medians.
Alternating cancels the slow drift a runner exhibits; pairing makes a 10 % difference visible
under 20 % noise.

Datasets for CI: `siftsmall` (10k × 128) for insert and query, `--threads 1` and `--threads 0`.
It fits in a few minutes. Cohere is for the reference machine only.

Workflow `.github/workflows/perf.yml`:

- `workflow_dispatch` with `base` / `head` inputs (defaults `master` / current branch), and a
  weekly `schedule` that compares `master` against the last release tag.
- Runs the micro-benchmarks once (BenchmarkDotNet's own statistics are enough there) and the
  macro A/B script.
- Writes both to `$GITHUB_STEP_SUMMARY` as Markdown tables: build time, inserts/s, QPS at the
  swept `ef`, recall, and the head/base ratio with its confidence interval.
- Uploads BenchmarkDotNet's JSON as an artifact.
- **Does not fail the build.** A regression is a review comment, not a red X; the reviewer
  decides. Turning it into a gate is deferred until we have a month of data showing what the
  noise floor actually is.

Optional, once the data looks stable: publish the JSON to a `gh-pages` branch with
`benchmark-action/github-action-benchmark` so there is a chart to link from the README.

### 3.4 Fix the existing gate

`InsertThroughputTests` stays as a coarse smoke test, but:

- it prints the measured value into `$GITHUB_STEP_SUMMARY` when that variable is set (a
  passing test today leaves no trace), so we accumulate a series of CI values for free;
- the comment records that 500/s is a *smoke* floor, and points to the perf workflow for the
  real measurement.

### 3.5 Later: self-hosted runner on the reference machine

The only way to get README-comparable absolute numbers from automation is to run on the
Snapdragon. GitHub supports self-hosted ARM64 Windows runners. This would let the weekly run
regenerate the Cohere 100K table automatically. It is deferred because it ties a personal
machine to CI; the A/B approach above gives most of the value without it.

## 4. Improvement candidates

Ordered by (expected gain × confidence) / effort. Each item states what the code does today,
what to change, how to measure, and what "done" means. Items marked **profile first** are
not to be started until a CPU profile of the reference workload confirms they matter.

### 4.1 Distance kernels — horizontal reduction inside the loop

**Today.** Every dot-product variant reduces horizontally on each iteration:

```csharp
// QvecDatabase.cs ~1088, ~1104, ~3468
dot += Vector.Dot(v1, v2);
```

`Vector.Dot` is a lane-wise multiply *plus a full horizontal add* — on NEON that is two
`FADDP` after the `FMUL`, and the scalar `dot +=` is a serial dependency chain. With four lanes
per vector this runs at a small fraction of what the FMA units can do. The Euclidean kernels
use a single vector accumulator (`accumulator += difference * difference`), which is better
but still one dependency chain: on a core with 4-cycle FMA latency and two FMA pipes, one
chain uses 1/8 of the throughput.

**Change.** Four (or eight, measured) independent `Vector<float>` accumulators, `dim`-unrolled,
one `Vector.Sum` at the end; or replace the bodies with `TensorPrimitives.Dot` /
`TensorPrimitives.Distance` and keep our functions as the metric-dispatch shell.
`TensorPrimitives` already does the multi-accumulator unroll, picks `Vector512` on AVX-512
x64 (which `Vector<T>` does not), and is Native-AOT friendly. The pointer variant
(`DotProductUnsafe(float[], float*, int)`) is the one the graph walk uses; it wraps the
pointer in a `ReadOnlySpan<float>` for free.

**Measure.** Micro-benchmark first (§3.2). Then Cohere 1M float at efSearch 180 and SIFT-1M
build on the reference machine, both against a kept index so recall is byte-identical.

**Done when** the micro-benchmark shows the kernel within ~10 % of `TensorPrimitives` at
128/768/1536, and the macro run reports the QPS change with recall unchanged.

**Expected.** Kernel: several ×. End to end: unknown — the graph walk is also memory-bound;
the profile referenced in `InsertThroughputTests` said distance arithmetic was "well under one
percent" of insert time *before* the marshalling fixes, so for inserts the gain may be small.
For queries at high `ef` on 768-d Cohere, the kernel share is larger. Measure.

### 4.2 Query path allocates per query

**Today.** `Search(float[], int, int)` calls `SearchLayerNearest(prepared, entryPoint, 0, ef)`
with no scratch (line ~2443), so each query allocates a `HashSet<int>`, two
`PriorityQueue<int,float>`, a `PreparedQuery`, the result array, and (in int8 mode) a
`byte[dim]` for the quantised query. `InsertScratch` already has the right shape — an epoch-
stamped visited array and reusable heaps — but only the insert path uses it. At 13,540 QPS
that is on the order of 100k allocations per second of short-lived objects across 12 threads,
which is exactly the pattern that costs scaling (gen0 GCs stop all threads).

**Change.** A `[ThreadStatic]` or pooled `SearchScratch` (rename/generalise `InsertScratch`),
passed from both `Search` overloads and the filtered search. Reuse the `PreparedQuery` buffer.

**Measure.** `dotnet-counters` gen0 count and allocation rate during a 12-thread Cohere 1M
run, before and after; QPS at 1 and 12 threads; thread-scaling ratio. Recall is unaffected by
construction (same algorithm, same order) — verify with the byte-identical index.

**Done when** allocations per query are O(result size) only and the 12-thread/1-thread ratio
has been re-measured and published.

**Expected.** Better 12-thread scaling than today's 6.7×; single-thread QPS gain small.

### 4.3 Neighbour list copy in the walk

**Today.** `SearchLayerNearest` calls `GetNeighborsAtLevel(candidateId, level, neighborBuffer)`,
copying up to 64 ints out of the mapping into a rented buffer, then iterates the buffer.
`NeighborPointer` already exists and is used by `MergeNeighborsAtLevel`.

**Change.** Iterate `NeighborPointer(candidateId, level)` directly under the read lock (the
mapping cannot be remapped while the lock is held — the same invariant `StoredVector` relies
on). Same for `AddNeighborConnection`'s first pass.

**Measure.** Part of the §4.2 macro run; separate micro-benchmark on `SearchLayerNearest`.

**Expected.** Small (a few percent); bundle with 4.2.

### 4.4 Insert prune: allocation and sorting — profile first

**Today.** Per insert, per level: `SelectNeighborsHeuristic` allocates `ordered`,
`StableSortDescending` allocates an index array, a sorted copy and a closure for the
comparer, `PruneNeighbors` allocates two `List`s. `AddNeighborConnection` on a full list (the
steady state) allocates a `candidates` array and sorts with a comparer object. With `M0 = 64`
these are up to ~65 back-link prunes per insert.

**Change.** Move all of it into `InsertScratch` buffers; replace the comparer sort with an
insertion sort (≤ 200 candidates after search, ≤ 65 in the back-link path — insertion sort
wins at these sizes and is naturally stable).

**Measure.** Micro-benchmark on the prune; then single-thread SIFT/Cohere build time and the
graph-bytes comparison (`--keep-index`, `--threads 1`) — the graph must be byte-identical
because the sort is stable and the arithmetic unchanged. That is a very strong check.

**Expected.** Unknown until profiled. The `O(M0²)` `StoredSimilarity` calls in
`PruneNeighbors` and `FindWorstNonDiverse` — each reading two mapped vectors — may dominate,
in which case 4.1 helps here too and allocation does not.

### 4.5 Parallel build scaling — profile first

**Today.** 6.3× on 12 cores (Cohere 100K). Candidates: the entry-point lock in `LinkPending`,
1,024 striped `NodeLock`s (with 12 threads and 64 back-links per insert, collisions are
frequent enough to matter), `Parallel.For` chunking, and `ArrayPool.Shared` contention.

**Change.** Not decided. First: `dotnet-trace` with the `ThreadPool`/contention providers
during a 12-thread build, and a `--threads 1,2,4,8,12` sweep to see where the curve bends.

**Expected.** If the bend is lock contention, 8–10× is plausible. If it is memory bandwidth
on the mapped file, nothing short of a layout change helps.

### 4.6 Not on the list (and why)

- **`Sse.Prefetch` / software prefetch of neighbour vectors.** No portable equivalent on
  ARM64 in .NET; the reference machine would not benefit. Revisit if x64 becomes the target.
- **Changing HNSW parameters or the prune heuristic.** Those trade recall for speed and belong
  in a separate discussion; this programme keeps the graph identical wherever possible so
  that performance is the only variable.
- **`TieredPGO` / `ReadyToRun` tuning.** Native AOT is the shipping configuration; measure
  under AOT before assuming JIT-only knobs matter.

## 5. Plan

| # | PR | Scope | Depends on |
| --- | --- | --- | --- |
| 1 | `perf-micro` | `Qvec.MicroBenchmarks` project (kernels + `TensorPrimitives` reference), `InternalsVisibleTo`, README section | — |
| 2 | `perf-workflow` | `compare.ps1` A/B script, `perf.yml` (dispatch + weekly), step-summary tables, `InsertThroughputTests` prints to summary | 1 |
| 3 | `perf-kernels` | §4.1: multi-accumulator or `TensorPrimitives` kernels; micro + macro results in `benchmarks/README.md` | 1, 2 |
| 4 | `perf-query-scratch` | §4.2 + §4.3: query-path scratch, direct neighbour pointer; allocation counters and scaling re-measured | 2 |
| 5 | `perf-profile` | CPU profile of single-thread build and 12-thread build/query on the reference machine; findings appended to §2 of this document; decides whether 4.4 / 4.5 proceed | 2 |
| 6 | `perf-insert-prune` | §4.4 if profile says so; byte-identical graph check | 5 |
| 7 | `perf-parallel-build` | §4.5 if profile says so | 5 |

Each PR that changes `Qvec.Core` publishes a before/after row in `benchmarks/README.md`
measured on the reference machine on the same day, with recall, per the existing convention.

## 6. Open questions

1. Should `perf.yml` run on every PR touching `Qvec.Core/*.cs` (as a non-blocking summary),
   or only on dispatch and schedule? Every-PR gives reviewers the number when they need it but
   adds ~5–8 minutes of runner time per PR.
2. `TensorPrimitives` adds a dependency on `System.Numerics.Tensors` to `Qvec.Core`. It is
   first-party, AOT-compatible and small, but Core has had no package dependencies so far.
   Decide after the micro-benchmark shows whether our own multi-accumulator loop gets close
   enough to make it unnecessary.
3. The "well under one percent" profile figure predates the mapped-pointer fixes and the
   incremental prune. It should be re-taken (PR 5) before it is quoted again.
