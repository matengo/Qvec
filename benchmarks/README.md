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

`--quantization int8` builds the index with `VectorQuantization.Int8`. The report header records
the mode so a float row and an int8 row cannot be confused for each other.

`--reuse-index` opens the file a previous `--keep-index` run left behind instead of rebuilding
it, so `--k`, `--ef` and `--concurrency` can be swept without paying for the build again. The
build time is reported as zero in that case, not as the previous run's number.

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
byte-identical, so recall is re-measured rather than assumed. The int8 row shows the other open
item plainly: without rescoring on the floats, int8 recall@100 plateaus at 96.8 % on Cohere,
which is why Zvec's published figures use int8 *with* a refiner.

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
