# Qvec benchmarks

Recall and throughput measured against a published ANN corpus, using the corpus's own ground
truth.

## Why this exists

Every recall figure this project published before was measured on uniformly random vectors that
the test generated itself, and scored against its own brute-force scan. Both halves of that are
weak:

- **Uniform random vectors are not what anyone stores.** In high dimensions they are all roughly
  equidistant, so the nearest neighbour is barely distinguishable from the hundredth. Any graph
  index looks bad, the numbers are not comparable to anything, and they conceal how the index
  behaves on the clustered data real embeddings produce.
- **Self-computed ground truth proves less than it appears to.** A brute-force scan in the same
  process shares every assumption the index makes — the same distance function, the same
  normalisation, the same tie-breaking. It can only show that the index agrees with us. The
  published ground truth is independent of all of that.

So this measures against [TexMex](http://corpus-texmex.irisa.fr/) SIFT and GIST, which ship exact
precomputed neighbours, and reports the whole recall-versus-QPS curve rather than a single
number. A single recall figure is close to meaningless for an ANN index, because any
implementation can reach 99% by widening the beam until it has effectively scanned everything.

## Running it

```bash
# 5 MB, a few seconds. Good for checking that everything works.
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset siftsmall --download

# 160 MB download, 1M vectors. This is the number worth quoting.
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --download

# Everything it takes
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --help
```

The dataset is cached under `%TEMP%/qvec-ann-datasets` (or `$TMPDIR` on Unix), so `--download` is
only slow the first time. `curl` does the fetching, because the TexMex corpora are served over
FTP and `HttpClient` does not speak it.

`--index <path> --keep-index` builds into a file of your choosing and leaves it on disk. Because
`indexSeed` is pinned, two single-threaded builds of the same dataset from the same code produce
byte-identical graph sections, which makes this the way to prove that an insert-path change did
not alter the graph: build once from `master`, once from your branch, and compare the section
bytes. Builds with `--threads` above 1 are not byte-reproducible (link order depends on
scheduling), so compare those by recall instead.

`--threads <n>` builds the index through `QvecDatabase.AddEntries` with `n` threads linking
nodes concurrently ([design doc](../docs/design-insert-parallel.md)); `0` means every core. The
default is 1, which produces exactly the graph a loop of `AddEntry` calls would.

`--quantization int8` builds the index with `VectorQuantization.Int8`; `--quantization
int8rescored` uses `VectorQuantization.Int8Rescored`, which walks the graph on the int8 codes
but keeps the floats and re-ranks the `efSearch` candidates on them
([design doc](../docs/design-quantization-rescoring.md)). The report header records the mode
so rows from different modes cannot be confused for each other.

`--reuse-index` opens the file a previous `--keep-index` run left behind instead of rebuilding
it, so `--k`, `--ef` and `--concurrency` can be swept without paying for the build again. The
build time is reported as zero in that case, not as the previous run's number.

`--passes <n>` runs the query set `n` times per `efSearch` row inside the timed region. Cohere
ships only 1,000 queries, which at several thousand QPS is over in a fraction of a second — too
short to be a throughput number. Use 5–10 there. Recall is unaffected (the results are
deterministic and only the first pass is scored). Each row is also preceded by a warm-up on the
same thread count that runs at least one full pass and at least two seconds, because a fresh
process must soft-fault every page of a multi-gigabyte mapping into its working set even when
the OS has the file cached; without that the first row of a sweep looked 2–3× slower than the
second for reasons unrelated to `efSearch`.

### Windows power throttling

On startup the benchmark asks Windows to exempt it from power throttling (EcoQoS) and prints
whether that succeeded. Without the exemption, a Balanced power plan treats a console process
that is not in the foreground as background work and runs it on few cores at reduced clocks:
the same single-threaded Cohere 100K build took 589 s throttled and 371 s exempted, and a
12-thread build got about four cores' worth of CPU time. Pass `--allow-throttling` to measure
what a background process would actually get. Numbers in this file are marked when they were
measured before the exemption existed.

## Cohere (VectorDBBench)

`--dataset cohere100k` and `--dataset cohere1m` load the Cohere corpora that
[VectorDBBench](https://github.com/zilliztech/VectorDBBench) ships — 768-dimensional, **cosine**
ground truth — which is what Zvec, Milvus and most vendor benchmarks publish against. The files
are the VectorDBBench parquet triple (`train`, `test`, `neighbors`); the ground truth refers to
train ids rather than row positions and is remapped on load.

VectorDBBench reports **recall@100** under **12–20 concurrent clients**, so the comparable
invocation is:

```bash
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset cohere1m --download --k 100 --ef 120,180 --concurrency 16
```

Two things differ from what the vendors publish and should be kept in mind when reading the
numbers side by side. VectorDBBench runs each concurrency level for a fixed duration and reports
the best; this benchmark runs every query once per `efSearch` and reports the aggregate. And
VectorDBBench's index build is parallel, so compare build times against a `--threads 0` run,
not the single-threaded default.

### Cohere 100K, measured

`--dataset cohere100k --k 100`, `maxNeighbors = 32`, Snapdragon X Elite (12 cores), Windows 11,
power-throttling exemption in place. The 1-thread and 12-thread float rows were built back to
back on the same day; queries at `--concurrency 12` on the 12-thread graph.

| mode | build threads | build | inserts/s | file | efSearch | recall@100 | QPS 1 thread | QPS 12 threads |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| float | 1 | 371 s | 270 | 382 MiB | 100 | 97.6 % | 712 | |
| float | 1 | | | | 180 | 99.0 % | 440 | |
| float | 12 | 59 s | 1,707 | 382 MiB | 100 | 97.5 % | 693 | 2,991 |
| float | 12 | | | | 180 | 99.0 % | 453 | 2,469 |
| int8 † | 1 | 672 s | 149 | 164 MiB | 100 | 95.7 % | 549 | — |
| int8 † | 1 | | | | 180 | 96.6 % | 395 | — |
| int8 † | 1 | | | | 300 | 96.8 % | 308 | — |

† Measured before the power-throttling exemption and with the old O(M0²) prune; build time and
QPS are not comparable to the float rows, recall is.

Parallel construction ([design doc](../docs/design-insert-parallel.md)) gives 6.3× on 12 cores
at a cost of 0.1 pp recall@100. The float rows use the incremental prune
([design doc](../docs/design-insert-prune.md)); before it the same single-threaded build took
1,696 s (59 inserts/s) and reached 97.9 % / 99.3 % at efSearch 100 / 180 — the graph is not
byte-identical, so recall is re-measured rather than assumed. The int8 row shows the plain-int8
ceiling: without rescoring on the floats, int8 recall@100 plateaus at 96.8 % on Cohere, which is
why Zvec's published Cohere 10M figures use int8 *with* a refiner (their 1M run does not). The
`int8rescored` mode measured on 1M below is Qvec's equivalent.

### Cohere 1M, measured

Zvec's published Cohere 1M run is `--quantize-type int8 --m 15 --ef-search 180` under 12–20
concurrent clients on a 16-vCPU g9i.4xlarge ([their reproduction
guide](https://zvec.org/en/docs/db/benchmarks/#cohere-1m)). The matching invocation here is

```bash
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset cohere1m --k 100 --m 15 --ef 100,180,320 --threads 0 --concurrency 12 --passes 10 [--quantization int8|int8rescored]
```

Snapdragon X Elite (12 cores, ARM64, laptop), Windows 11, throttling exemption in place, all
indexes built with 12 threads, queries at `--concurrency 12`:

| mode | build | inserts/s | file | efSearch | recall@1 | recall@100 | QPS 1 thread | QPS 12 threads | latency @ 12 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| float | 419 s | 2,385 | 3,376 MiB | 100 | 97.1 % | 90.3 % | — ‡ | 6,441 | 1.86 ms |
| float | | | | 180 | 98.1 % | 94.8 % | 565 | 3,764 | 3.19 ms |
| float | | | | 320 | 98.8 % | 97.4 % | 351 | 2,207 | 5.44 ms |
| int8 | 181 s | 5,516 | 1,194 MiB | 100 | 93.1 % | 89.3 % | 1,395 | 13,540 | 0.89 ms |
| int8 | | | | 180 | 94.4 % | 93.2 % | 839 | 6,841 | 1.75 ms |
| int8 | | | | 320 | 95.4 % | 95.3 % | 502 | 4,714 | 2.55 ms |
| int8 rescored | 170 s | 5,885 | 4,124 MiB | 100 | 96.4 % | 89.2 % | 1,200 | 11,231 | 1.07 ms |
| int8 rescored | | | | 180 | 97.9 % | **94.7 %** | 630 | **6,778** | 1.77 ms |
| int8 rescored | | | | 320 | 98.9 % | **97.4 %** | 421 | **3,884** | 3.09 ms |

‡ The single-threaded float sweep was measured at `--passes 2`, where the first row had not
finished faulting the 3.4 GB mapping in; it is omitted rather than reported wrong.

The float and int8 rows were measured a few days before the rescored ones. Re-querying those two
kept indexes on the rescored run's day gave 6,409 / 3,713 / 2,200 QPS (float) and 12,539 /
7,256 / 4,377 QPS (int8) at 12 threads, within 6 % of the table, so the three modes can be read
against each other.

Twelve query threads give 6.7× (float), 8.2× (int8) and 9–11× (rescored) over one. Zvec's
chart for the same configuration reads as roughly 8–9 thousand QPS at recall@100 ≈ 0.93–0.94
on 16 vCPUs; the exact values are only published as an image, so treat that as approximate. Per
core, the int8 rows at efSearch 180 (6.8 thousand QPS / 12 cores) are in the same range, on
different hardware, a different OS and a different day — which is as far as the comparison
honestly goes.

What the table shows without caveats is Qvec's own shape. Plain int8 is 1.8× the float QPS at
the same efSearch but loses 1.6 pp recall@100 at 180 and plateaus around 95 %. Rescored int8
([design doc](../docs/design-quantization-rescoring.md)) walks the same int8 graph and then
re-ranks the `efSearch` candidates on the floats: at 180 and 320 it returns float recall
(94.7 / 97.4 %) at 99 % and 82 % of the int8 QPS, i.e. 1.8× the float QPS with no recall loss,
and the build is 2.5× faster than float. The costs are a file 22 % larger than float, and no
recall gain at all when `efSearch == k` (the 100 row): re-ranking reorders the candidate set
but cannot add to it, so there the row is bounded by what the int8 walk found.

Measuring this uncovered a scaling bug in the search path, fixed on the same branch. Result
materialisation read the Guid and metadata of every hit through `MemoryMappedViewAccessor`,
whose every call takes an interlocked reference on the shared `SafeBuffer`; at `k = 100` that is
a few hundred atomic operations per query on one cache line, and twelve threads serialised on
it. Cohere 1M float at efSearch 100 went from 1,883 to 6,441 QPS on 12 threads once the reads
went through the raw mapping pointer like the distance computations already did. Single-threaded
throughput was unaffected, which is why the SIFT numbers in the main README did not move.

## Change tracking cost

Change tracking (the foundation for `Qvec.Sync`) is off by default, so the question is what
turning it on costs a database that may never sync. Two SIFT-1M builds on the reference machine,
same day, same settings, `--tracking` being the only difference:

| | tracking off | tracking on | delta |
| --- | ---: | ---: | ---: |
| Build, 12 threads | 168.9 s (5,920 inserts/s) | 172.0 s (5,813 inserts/s) | +1.8 % |
| File | 1,323.8 MiB | 1,407.7 MiB | +83.9 MiB |
| recall@10, efSearch 10 | 84.5 % | 84.5 % | — |

The file delta is exactly what the format says: 24 bytes per row for `EntryVersions` plus 64
bytes per change-log slot, and the ring here was sized to the dataset (`LogCapacity = 1M`), so
88 bytes × 1M = 83.9 MiB. The build delta is one HLC stamp and one 64-byte log record per
insert and sits inside the run-to-run noise of a 170-second build; both runs are slower than the
142 s quoted in the main README because the machine was not idle, which is also why they were
run back to back rather than compared against the old number.

The sync step itself, measured after the tracked build by pushing the first 100,000 documents
through the whole pipeline into a fresh replica:

| step | time | docs/s |
| --- | ---: | ---: |
| `GetChanges` (200 batches of 500) | 0.13 s | 763,508 |
| wire encode (`SyncCompression.Auto`) | 0.24 s | 419,240 |
| wire decode | 0.08 s | 1,247,837 |
| `ApplyChanges` into a fresh replica, single thread | 60.80 s | 1,645 |

Wire size was 53.1 MiB, 557 bytes per document — **100 % of the uncompressed encoding**.
`Auto` only reaches for Brotli when metadata makes up at least a fifth of the payload; SIFT has
none, and 128 IEEE floats of descriptor data would not have compressed anyway, so the frame went
out raw. The 45 bytes over the 512-byte vector are the document id, the version, the operation
and the (empty) metadata. Compression earns its keep on metadata-heavy payloads, not on dense
float vectors.

`ApplyChanges` is the cost that matters and it is the HNSW insert, not the sync: a replica
receiving a document has to link it into its own graph exactly like `AddEntry` does, on one
thread, so it runs at single-threaded insert speed. Reading a batch, checking versions and
writing the log are rounding errors next to it. That is the argument for snapshot bootstrap:
replaying this 1M-document database through `ApplyChanges` at this rate is about ten minutes of
single-threaded graph building on the receiver, while copying the 1.4 GiB file is a file copy.

Reproduce with:

```powershell
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --threads 0 --ef 10
dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --threads 0 --ef 10 --tracking
```

`--sync-items` and `--sync-batch` change the size of the sync step; `--sync-items 0` skips it.

## Micro-benchmarks

`benchmarks/Qvec.MicroBenchmarks` is a [BenchmarkDotNet](https://benchmarkdotnet.org/) project
for the pieces the dataset benchmark cannot separate: the distance kernels on their own, the int8
quantise/dequantise/dot path, and one `Search` call on a 10,000-node in-memory-sized index with
the memory diagnoser on. It needs no download and takes a few minutes.

```bash
dotnet run -c Release --project benchmarks/Qvec.MicroBenchmarks -- --list flat
dotnet run -c Release --project benchmarks/Qvec.MicroBenchmarks -- --filter '*FloatKernel*'
dotnet run -c Release --project benchmarks/Qvec.MicroBenchmarks -- --filter '*Search*' --job short
```

Results land in `BenchmarkDotNet.Artifacts/results/`. The float kernels are measured next to
`System.Numerics.Tensors.TensorPrimitives` as a "how fast can this machine go" reference. The
baseline on the reference machine, and what it changed about the plan, is recorded in
[docs/design-performance.md](../docs/design-performance.md) §2.1 — in short, the kernels the
graph walk uses are already within 5 % of `TensorPrimitives` at 768 and 1536 dimensions, and a
single query allocates 30–68 KB, which is the first thing that programme fixes.

## Metric

SIFT and GIST ground truth is **Euclidean**, Cohere is **Cosine**. The benchmark defaults to the
dataset's own metric. Overriding it with `--distance` compares the index against neighbours that
are not the ones the dataset means, and the resulting recall number says nothing useful — it is
not a low score, it is a meaningless one.

`DistanceFunction.Euclidean` exists largely because of this exercise: without it Qvec could not
be measured against any published ANN ground truth at all.

## Why this is not a CI gate

SIFT-1M is a 160 MB download and a multi-minute index build. Neither belongs in a pull-request
check, and a gate that depends on an FTP server in Rennes is a gate that will eventually fail for
reasons that have nothing to do with the code.

The regression gates live in `tests/Qvec.Core.Tests/ClusteredRecallTests.cs` instead. They run
offline on deterministic *clustered* vectors — a Gaussian mixture, which behaves far more like
real embeddings than uniform noise — with floors set well below the measured values so they fail
on regressions rather than on noise.
