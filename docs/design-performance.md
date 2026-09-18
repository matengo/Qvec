# Design: Performance programme (tracking and improvement)

Status: plan, September 2026. §3.2 (micro-benchmarks) is implemented; the rest is not. Every number
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

### 2.1 Micro-benchmark baseline (measured 2026-09-18, PR `perf-micro`)

`Qvec.MicroBenchmarks`, BenchmarkDotNet 0.15.8, .NET 10.0.12, Arm64 RyuJIT, reference
machine. Kernel figures are the default job; the search figures are the `--job short` job
(3 iterations) and should be read to ±5 %.

**Float kernels, one call, both operands in L1.** "Pointer" is the variant the graph walk
calls (`DotProductUnsafe` / `NegativeSquaredDistanceUnsafe`), "Span" the one the prune calls,
"Array" the public overload the tests use. `TensorPrimitives` is the reference.

| dim | Dot Pointer | Dot Span | Dot Array | Dot TensorPrimitives | L2 Pointer | L2 Span | L2 Array | L2 TensorPrimitives |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 18.4 ns | 12.3 ns | 27.8 ns | 11.9 ns | 12.5 ns | 20.4 ns | 26.0 ns | 16.3 ns |
| 768 | 135.6 ns | 136.0 ns | 187.6 ns | 139.0 ns | 123.2 ns | 137.1 ns | 181.7 ns | 125.2 ns |
| 1536 | 263.4 ns | 259.0 ns | 356.6 ns | 266.4 ns | 283.2 ns | 256.5 ns | 346.7 ns | 316.9 ns |

Reading: at 768 and 1536 the kernels the hot paths use are **within 5 % of
`TensorPrimitives`** — the loop is bound by the loads, and the per-iteration `Vector.Dot`
reduction that §4.2 originally singled out costs nothing measurable there. At 128 the pointer
dot product is 1.5× off the reference (18.4 vs 11.9 ns); the array overloads are 1.4–2.3×
slower everywhere but are not on any hot path. Kernel work is therefore a small, dimension-
dependent win, not the "several ×" the first draft of this document expected.

**One `Search(topK 10, efSearch 100)` on a 10,000-node clustered Euclidean index:**

| dim | mode | mean | Gen0 / 1k ops | Gen1 / 1k ops | allocated per query |
| ---: | --- | ---: | ---: | ---: | ---: |
| 128 | float | 92.4 µs | 7.4 | — | 30.8 KB |
| 128 | int8 | 88.8 µs | 7.4 | — | 30.8 KB |
| 128 | int8 rescored | 95.3 µs | 7.4 | — | 30.7 KB |
| 768 | float | 425.7 µs | 16.6 | 0.49 | 67.9 KB |
| 768 | int8 | 211.8 µs | 16.6 | 0.24 | 68.8 KB |
| 768 | int8 rescored | 275.5 µs | 16.6 | 0.98 | 68.7 KB |

Reading: **30–68 KB of garbage per query and a gen1 collection every one to four thousand
queries** on a graph that fits in cache. At 128-d the arithmetic for a walk of this size is on
the order of 20–40 µs, so more than half the query is overhead. This is the §4.1 finding
quantified, and it is why §4.1 is first in the queue.

**After PR `perf-query-scratch` (same machine, same benchmark, `--job short`):**

| dim | mode | mean before → after | Gen0 / 1k ops | Gen1 / 1k ops | allocated per query |
| ---: | --- | ---: | ---: | ---: | ---: |
| 128 | float | 92.4 → 50.9 µs (−45 %) | 0.31 | — | 1.48 KB |
| 128 | int8 | 88.8 → 58.5 µs (−34 %) | 0.31 | — | 1.48 KB |
| 128 | int8 rescored | 95.3 → 38.4 µs (−60 %) | 0.31 | — | 1.48 KB |
| 768 | float | 425.7 → 386.4 µs (−9 %) | — | — | 1.48 KB |
| 768 | int8 | 211.8 → 163.6 µs (−23 %) | 0.24 | — | 1.48 KB |
| 768 | int8 rescored | 275.5 → 243.5 µs (−12 %) | 0.24 | — | 1.48 KB |

The remaining 1.48 KB is the result: the `ef`-sized candidate array the walk returns and the
`List<(Guid, float, string)>` handed to the caller. Gen1 collections are gone entirely, gen0
dropped 20–70×. The 768-d float row moved least because that walk is dominated by the
distance kernel (768 × 4 B per candidate is memory traffic, not allocation), which is §4.2's
territory. `--job short` error bars are wide (see the artifacts), so treat the percentages as
indicative to ±10 %; the allocation column is exact.

**Macro confirmation with `compare.ps1`** (reference machine, siftsmall, `e8ebef2` → `607dda6`,
3 interleaved rounds, 10 query passes, build threads 1; PR `perf-workflow`):

| metric | 1 query thread, head/base | 12 query threads, head/base |
| --- | ---: | ---: |
| build inserts/s | 1.06× (rounds 0.99–1.19) | 1.02× (rounds 0.75–1.06) |
| QPS @ ef 10 | 1.87× (1.75–1.94) | 1.58× (0.73–1.77) |
| QPS @ ef 40 | 1.89× (1.78–1.90) | 1.56× (0.78–7.01) |
| QPS @ ef 160 | 2.33× (2.28–2.77) | 2.06× (1.59–2.30) |
| recall@10, every ef | unchanged | unchanged |

Reading: end to end the query path is 1.9–2.3× faster on 128-d single-threaded with recall
identical, which is more than the micro-benchmark's −45 % because a real query loop also pays
for the GC pauses the micro-benchmark amortises away. The build did not move, as expected —
inserts already used the scratch. The 12-thread columns at ef 10/40 are not measurements:
100 queries × 10 passes finish in a few milliseconds across 12 threads, hence the 0.73–7.01
spread. The ef 160 row is long enough to trust. Absolute 12-thread QPS on siftsmall
(head, ef 160): 84,921 against 10,675 single-threaded, a 7.95× ratio on 12 cores; the base
binary's ratio (9.0×) was higher only because its single-thread number was worse. The
Cohere-1M 12-thread re-measurement that §4.1 owes is a reference-machine job, not a CI one.

Two of these are already suspicious as a starting point: 6.3× build scaling and 6.7× query
scaling on 12 cores leave a third of the machine idle, and the 128-d single-thread insert rate
is about 1,400/s while the arithmetic it has to do (an `O(M0²)` prune at 64 neighbours) is on
the order of a few thousand 128-d distance evaluations per insert — that should be well under
half a millisecond.

### 2.2 CPU profiles (measured 2026-09-19, PR `perf-profile`)

Reference machine on mains power (a first run on battery gave the same distribution with
lower absolute rates; battery numbers are not quoted). `dotnet-trace collect --format
speedscope` (EventPipe sample profiler, 1 ms), summarised by `benchmarks/profile-summary.py`
(self time; pseudo frames excluded). Head `cabcfc9`, SIFT-1M, M = 32, `Release`, JIT.

**A. Single-thread build**, first 180 s of a SIFT-1M build (~250k rows indexed):

| frame (self) | share |
| --- | ---: |
| `SearchLayerNearest` (distance kernel and `CalculateScore` inlined into it) | 52.3 % |
| `Monitor.Enter_Slowpath` — **an artefact, see below** | 38.9 % |
| `PruneNeighbors` | 4.9 % |
| `AddNeighborConnection` | 1.8 % |
| sorting (`IntroSort`, `StableSortDescending`, `Sort` with comparer) | 0.7 % |
| everything else, including dataset decode | < 1 % |

**The `Monitor` frame is a stack-walk artefact, not lock cost.** It was ruled out in three
steps. (1) Uncontended `lock (object)` costs ~25 ns on this machine (20 M iterations over
1,024 stripes); at ≤ 65 lock acquisitions per level per insert that is ≈ 2 µs of a
~350–700 µs insert, under 1 %. (2) An ablation build that skips the node locks entirely in
the single-thread path changed nothing: siftsmall 2,850/2,737 inserts/s with locks against
2,723/2,618 without (alternating runs), and the first 100k of SIFT-1M 1,406 against 1,218 —
noise, if anything the wrong sign. (3) Profiling that ablation build, the `Monitor` frame is
gone and the same share reappears as `AddNeighborConnection` 24.7 %, `PruneNeighbors` 9.4 %,
`FindWorstNonDiverse` 4.7 % and `IntroSort` 4.6 %. The samples had been taken inside the
`lock` bodies and the walk attributed them to the frame that took the lock; the reported
"callers" (`LinkPending`, which only calls `LinkOne`, and in the 12-thread trace
`Parallel.ForWorker` directly) are likewise impossible and confirm a truncated walk. Treat
any `Monitor.Enter_Slowpath` self time in an EventPipe profile from this Windows/ARM64
machine as "time inside a locked region" until proven otherwise.

Corrected reading of the single-thread build, therefore:

| phase | share |
| --- | ---: |
| candidate search (`SearchLayerNearest` incl. kernels) | ≈ 50 % |
| back-linking the new node into its neighbours' lists (`AddNeighborConnection` + `FindWorstNonDiverse` + `PruneNeighbors` + sort) | ≈ 44 % |
| selecting the new node's own neighbours (`SelectNeighborsHeuristic`, `StableSortDescending`) | < 1 % |

The back-link phase is the one §2.1 already found suspicious. What it does, per neighbour
whose list is full (the steady state on layer 0 with `M0 = 64`): re-read the 64 stored ids,
recompute 65 `StoredSimilarity` values from the mapped vectors, sort them with an `IComparer`
object, run `FindWorstNonDiverse` (up to 64 more `StoredSimilarity`), and when that finds
nothing to evict fall back to the full `PruneNeighbors` — `O(M0²)`, up to ~2,000
`StoredSimilarity` calls, two `List` allocations and a `ToArray`. Multiplied by up to 64
neighbours per insert this rivals the candidate search itself. That is the §4.4 target, and
it is arithmetic, not allocation: a `StoredSimilarity` reads two vectors from the mapping and
runs the kernel, so the kernel work of §4.2 helps here as much as it helps the query.

**B. 12-thread build**, full SIFT-1M (`--threads 0`, 16 sampled threads, idle-wait samples on
pool threads included in the total): `SearchLayerNearest` 32.5 %, `Monitor` frame 26.6 %,
`AddNeighborConnection` 6.0 %, `PruneNeighbors` 2.6 %, thread-pool waits (`LowLevelLifoSemaphore`,
`WaitHandle`) 22 %, `GC.RunFinalizers` 6 % (finalizer thread idle). The ratio of the `Monitor`
frame to `SearchLayerNearest` is 0.82 against 0.74 single-threaded, so real lock contention
on the 1,024 stripes is at most a few percent of build time; the idle-wait share says the
`Parallel.For` batches leave workers waiting at batch boundaries (50k rows per `AddEntries`
call in the benchmark) rather than that the locks serialise them. Neither is the first thing
to fix; §4.5 stays parked behind §4.4.

**C. 12-thread query** on the index from B (`--concurrency 12 --passes 20`, ef 40 and 160;
17,169 and 9,615 QPS under the profiler): `SearchLayerNearest` 83.9 %, `Search` 1.4 %,
`Monitor` frame 6.0 % (caller `Search`, which takes no `lock` — the same artefact covering
the RWLS read lock, scratch rent/return and the result sort), `Thread.Join` 6.7 % (the
benchmark's main thread waiting). The query path is now the distance kernel plus the
candidate heap and nothing else, which is what §4.1 set out to reach and what §4.2 has to
attack next.

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

### 3.3 Macro A/B: `benchmarks/compare.ps1` and a `perf` workflow — done in PR `perf-workflow`

`benchmarks/compare.ps1` builds two commits of `Qvec.Benchmarks` (base and head) in
throw-away git worktrees, then runs them **alternating** on the same dataset — base, head,
base, head — and reports the paired medians. Alternating cancels the slow drift a runner
exhibits; pairing makes a 10 % difference visible under 20 % noise. It parses the Markdown each
binary writes with `--out`, so it works against any commit since that flag existed, and it
forces invariant globalization on both binaries so the numbers parse on any host culture.

Datasets for CI: `siftsmall` (10k × 128) for insert and query, `--threads 1`; `-Concurrency`
is a parameter. It fits in a few minutes. Cohere is for the reference machine only. Note that
siftsmall has only 100 queries: the script defaults to 10 passes per `ef` row, and even then
12-thread numbers at low `ef` are too short to be stable (see §2.1) — multi-threaded query
scaling is a reference-machine measurement.

Workflow `.github/workflows/perf.yml`:

- `workflow_dispatch` with `base` / `head` / `rounds` inputs (defaults `master` / the ref the
  workflow runs on / 3), and a weekly `schedule` that compares `master` against the latest
  `v*` tag.
- Runs the `SearchBenchmarks` micro-benchmarks once (`--job short`; BenchmarkDotNet's own
  statistics are enough there and the allocation column is exact) and the macro A/B script.
- Writes both to `$GITHUB_STEP_SUMMARY` as Markdown tables: build inserts/s, QPS and recall@k
  at each swept `ef`, and the head/base ratio as the median over rounds with the per-round
  spread.
- Uploads BenchmarkDotNet's results and the A/B Markdown as an artifact.
- **Does not fail the build.** Every measuring step is `continue-on-error`; a regression is a
  review comment, not a red X; the reviewer decides. Turning it into a gate is deferred until
  we have a month of data showing what the noise floor actually is. The siftsmall download is
  FTP (`ftp.irisa.fr`); if the runner cannot reach it the macro step is skipped and says so in
  the summary.

Not done: a confidence interval proper (three rounds are too few for one; the spread is
reported instead), and publishing JSON to `gh-pages` with
`benchmark-action/github-action-benchmark` — worth it once the weekly data looks stable.

### 3.4 Fix the existing gate — done in PR `perf-workflow`

`InsertThroughputTests` stays as a coarse smoke test, but:

- it appends the measured value to `$GITHUB_STEP_SUMMARY` when that variable is set (a
  passing test previously left no trace), so we accumulate a series of CI values for free;
- the comment records that 500/s is a *smoke* floor, and points to the perf workflow for the
  real measurement.

### 3.5 Later: self-hosted runner on the reference machine

The only way to get README-comparable absolute numbers from automation is to run on the
Snapdragon. GitHub supports self-hosted ARM64 Windows runners. This would let the weekly run
regenerate the Cohere 100K table automatically. It is deferred because it ties a personal
machine to CI; the A/B approach above gives most of the value without it.

## 4. Improvement candidates

Ordered by (measured or expected gain × confidence) / effort; the order changed after the
§2.1 measurements — the kernels moved down, the allocations moved up. Each item states what the code does today,
what to change, how to measure, and what "done" means. Items marked **profile first** are
not to be started until a CPU profile of the reference workload confirms they matter.

### 4.1 Query path allocates per query — done in PR `perf-query-scratch`

**Status.** Implemented. §2.1 "after" table: 30.8/68 KB → 1.48 KB per query, gen1 gone,
single-thread 128-d query −34…−60 % in the micro-benchmark and 1.9–2.3× QPS end to end on
siftsmall (`compare.ps1`, §2.1). The Cohere-1M 12-thread re-measurement for the README is
still owed and is a reference-machine job.

**Before.** `Search(float[], int, int)` called `SearchLayerNearest(prepared, entryPoint, 0, ef)`
with no scratch, so each query allocated a `HashSet<int>` (which resized several
times as the walk visited one to two thousand nodes), two `PriorityQueue<int,float>`, a
`PreparedQuery`, the result array, an `ArrayPool` neighbour copy per hop, and (in int8 mode) a
`byte[dim]` for the quantised query, then ran a LINQ `OrderByDescending.Take.Select.ToList`
chain. `InsertScratch` already had the right shape — an epoch-stamped visited array and
reusable heaps — but only the insert path used it.

§2.1 put a number on it: **30.8 KB per query at 128-d, 68 KB at 768-d, with gen1
collections every 1–4k queries.** At 13,540 QPS on 12 threads that is close to a gigabyte of
garbage per second, and every gen0/gen1 collection stops all twelve query threads — the
pattern that caps the measured scaling at 6.7× (float) on 12 cores.

**Change (as landed).** `InsertScratch` renamed `SearchScratch` and extended with a reusable
`PreparedQuery`, normalised-query buffer and int8 query-code buffer. `Search` rents one from a
`ConcurrentBag` pool under the read lock and returns it in `finally`; inserts keep their
`ThreadLocal` instance. `SearchLayerNearest`, `SearchLayerFiltered` and `GreedyClosest` read
neighbour lists in place via `NeighborPointer` (safe: searches hold the read lock, and slots
are whole int32 writes — see §4.3). The LINQ tail is replaced by a stable insertion sort that
runs only in rescored mode (the heap already yields descending order otherwise) and a
pre-sized result list. `SearchLayerFiltered` keeps metadata strings only for admitted nodes.
Visiting order is unchanged, so recall and the built graph are identical by construction.

**Measure.** `SearchBenchmarks` allocated-bytes column before/after (done, §2.1);
`dotnet-counters` gen0/gen1 rate during a 12-thread Cohere 1M run; QPS at 1 and 12 threads
and the ratio between them (owed).

**Done when** allocations per query are O(result size) ✔, the micro-benchmark shows it ✔, and
the 12-thread/1-thread ratio has been re-measured and published (pending).

### 4.2 Distance kernels — smaller than expected

**Today.** Every dot-product variant reduces horizontally on each iteration
(`dot += Vector.Dot(v1, v2)`, `QvecDatabase.cs` ~1088, ~1104, ~3468) and the Euclidean
kernels use a single accumulator chain. The first draft of this document expected that to
cost "several ×" on 4-lane NEON.

**Measured (§2.1):** it does not. At 768 and 1536 the pointer kernels the walk uses are within
5 % of `TensorPrimitives`; the loop is bound by the two loads per iteration, not by the
reduction. At 128-d the pointer dot product is 1.5× off (18.4 vs 11.9 ns) and the span
Euclidean kernel 1.25× off (20.4 vs 16.3 ns). The public array overloads are 1.4–2.3× slower
than the span/pointer ones because `new Vector<float>(array, i)` bounds-checks, but nothing
hot calls them.

**Change.** For 128-d dot product only: multi-accumulator unroll, or delegate to
`TensorPrimitives.Dot` (see open question 2). Leave the 768+ kernels alone. Bring the array
overloads down to the span implementation by forwarding to it — a cleanup, not a win.

**Measure.** `FloatKernelBenchmarks`; then SIFT-1M (128-d, dot/Euclidean) query QPS on a
kept index. Cohere (768-d) is not expected to move and will be measured once to confirm.

**Expected.** Up to ~1.3× on the 128-d kernel; a few percent end to end on SIFT; nothing on
Cohere. Worth doing because SIFT is a headline dataset, but it goes after 4.1.

### 4.3 Neighbour list copy in the walk — done in PR `perf-query-scratch` (search side)

**Before.** `SearchLayerNearest` called `GetNeighborsAtLevel(candidateId, level, neighborBuffer)`,
copying up to 64 ints out of the mapping into a rented buffer, then iterated the buffer.
`NeighborPointer` already existed and was used by `MergeNeighborsAtLevel`.

**Change.** `SearchLayerNearest`, `SearchLayerFiltered` and `GreedyClosest` now iterate
`NeighborPointer(candidateId, level)` directly under the read lock (the mapping cannot be
remapped while the lock is held — the same invariant `StoredVector` relies on). Landed
together with §4.1; not measured separately. `AddNeighborConnection`'s first pass still
copies and belongs to §4.4.

### 4.4 Insert back-link prune — profiled, proceed (PR 6)

**Today.** Per insert, per level: `SelectNeighborsHeuristic` allocates `ordered`,
`StableSortDescending` allocates an index array, a sorted copy and a closure for the
comparer, `PruneNeighbors` allocates two `List`s. `AddNeighborConnection` on a full list (the
steady state) allocates a `candidates` array and sorts with a comparer object. With `M0 = 64`
these are up to ~65 back-link prunes per insert.

**Profile verdict (§2.2 A).** The back-link phase is ≈ 44 % of a single-thread build and it
is dominated by `StoredSimilarity` arithmetic, not by allocation: `AddNeighborConnection`
recomputes all 65 owner–neighbour similarities on every full list, `FindWorstNonDiverse` adds
up to 64, and the `PruneNeighbors` fallback adds `O(M0²)`. Sorting is < 5 %; the `List`s and
arrays are cheap by comparison.

**Change (in order of expected pay-off).**
1. Cut `StoredSimilarity` calls without changing the outcome: `FindWorstNonDiverse` already
   computes candidate-to-new-node similarities that `PruneNeighbors` recomputes when it runs
   as the fallback; reuse them. Check how often the fallback runs at all (a counter in a
   `Slow` test) — if it is the common case, the two-step scheme is doing the `O(M0²)` work
   twice over and the cheaper first step should be dropped or folded in.
2. Make the kernel behind `StoredSimilarity` as fast as the query kernel (§4.2 covers both).
3. Only then the scratch buffers and insertion sort from the original plan, measured
   separately, since the profile says they are worth a few percent at most.

**Measure.** Single-thread SIFT-1M build rate via `compare.ps1` (`-Threads 1`), and the
graph comparison. Note that the `.qvec` file is **not** byte-identical between two identical
builds (header carries an id/timestamp), so the check must compare the graph section, not the
file hash; the `Array.Sort` in `AddNeighborConnection` is unstable and SIFT has many tied
distances (integer-valued vectors), so replacing that sort with a stable one *will* change
tie order and hence the graph. Either keep that sort or accept the change and prove recall
unchanged across the whole ef sweep.

**Expected.** If the fallback prune is frequent, removing the duplicate arithmetic alone
could take a third off the back-link phase, ≈ 15 % of build time; the kernel work adds to
that. Not to be quoted until measured.

### 4.5 Parallel build scaling — profiled, parked

**Today.** 6.3× on 12 cores (Cohere 100K). Candidates: the entry-point lock in `LinkPending`,
1,024 striped `NodeLock`s (with 12 threads and 64 back-links per insert, collisions are
frequent enough to matter), `Parallel.For` chunking, and `ArrayPool.Shared` contention.

**Profile verdict (§2.2 B).** Lock contention is at most a few percent; the visible loss is
pool threads waiting at the boundaries of the 50k-row `AddEntries` batches. Whatever §4.4
removes from the critical section shortens the locked regions too, so redo this profile after
PR 6 rather than optimising the locks now. If the batch-boundary wait is confirmed, the fix is
on the caller's side (larger or pipelined batches in the benchmark and README guidance), not
in `Qvec.Core`.

**Expected.** Unchanged: 8–10× is plausible if the remaining loss is synchronisation; nothing
short of a layout change helps if it is memory bandwidth on the mapped file.

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
| 1 | `perf-micro` | `Qvec.MicroBenchmarks` project (kernels + `TensorPrimitives` reference, one-query search with memory diagnoser), `InternalsVisibleTo`, README section; baseline recorded in §2.1 | — |
| 2 | `perf-workflow` | `compare.ps1` A/B script, `perf.yml` (dispatch + weekly), step-summary tables, `InsertThroughputTests` prints to summary — **done**; validated by re-measuring PR 3 end to end (§2.1) | 1 |
| 3 | `perf-query-scratch` | §4.1 + §4.3: query-path scratch, direct neighbour pointer; micro-benchmark before/after in §2.1 — **done**; Cohere 12-thread README re-measurement still owed | 1 |
| 4 | `perf-kernels` | §4.2: 128-d dot product only, array overloads forwarded to span; SIFT QPS re-measured | 2 |
| 5 | `perf-profile` | CPU profile of single-thread build and 12-thread build/query on the reference machine; `benchmarks/profile-summary.py`; findings in §2.2 — **done**: 4.4 proceeds (back-link arithmetic), 4.5 parked until after 6 | 2 |
| 6 | `perf-insert-prune` | §4.4: remove duplicate `StoredSimilarity` work in the back-link path, then scratch/sort; graph-section comparison, recall sweep | 5 |
| 7 | `perf-parallel-build` | §4.5: re-profile after 6; batch-boundary waits first, locks only if still visible | 6 |

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
3. §3.2 says the prune and `SearchLayerNearest` should get internal micro-benchmarks. The
   first PR covers the kernels and the public `Search`; the internal ones are added when
   4.3/4.4 need them, so the internal surface is not widened speculatively.
4. ~~The "well under one percent" profile figure predates the mapped-pointer fixes and the
   incremental prune. It should be re-taken (PR 5) before it is quoted again.~~ Re-taken in
   §2.2: sorting and selection are indeed under one percent; the back-link arithmetic is not.
5. The EventPipe stack-walk artefact in §2.2 (`Monitor.Enter_Slowpath` absorbing the locked
   region) makes lock cost unmeasurable by sampling on the reference machine. If §4.5 ever
   needs a real contention number, use the runtime's contention events
   (`Microsoft-Windows-DotNETRuntime:Contention`) or an x64 machine for that profile.
