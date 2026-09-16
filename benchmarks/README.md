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
`indexSeed` is pinned, two builds of the same dataset from the same code produce byte-identical
graph sections, which makes this the way to prove that an insert-path change did not alter the
graph: build once from `master`, once from your branch, and compare the section bytes.

`--quantization int8` builds the index with `VectorQuantization.Int8`. The report header records
the mode so a float row and an int8 row cannot be confused for each other.

`--reuse-index` opens the file a previous `--keep-index` run left behind instead of rebuilding
it, so `--k`, `--ef` and `--concurrency` can be swept without paying for the build again. The
build time is reported as zero in that case, not as the previous run's number.

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
VectorDBBench's index build is parallel, while Qvec's insert path is single-threaded, so the
build time is not comparable at all — it is the honest measurement of where Qvec stands, not a
like-for-like number.

### Cohere 100K, measured

`--dataset cohere100k --k 100`, `maxNeighbors = 32`, 12 logical cores, Windows 11. Build is
single-threaded; queries at `--concurrency 12`.

| mode | build | inserts/s | file | efSearch | recall@100 | QPS 1 thread | QPS 12 threads |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| float | 589 s | 170 | 382 MiB | 100 | 97.6 % | 381 | 1,652 |
| float | | | | 180 | 99.0 % | 263 | 1,097 |
| int8 | 672 s | 149 | 164 MiB | 100 | 95.7 % | 549 | — |
| int8 | | | | 180 | 96.6 % | 395 | — |
| int8 | | | | 300 | 96.8 % | 308 | — |

The float row is the incremental-prune build ([design doc](../docs/design-insert-prune.md)).
Before it the same build took 1,696 s (59 inserts/s) and reached 97.9 % / 99.3 % at efSearch
100 / 180 — the graph is not byte-identical, so recall is re-measured rather than assumed.
Absolute QPS on this laptop varies by up to 40 % between sessions (the same index file gave
585 and 347 QPS single-threaded on two different days), so compare QPS only within one table
measured back to back; the design doc does that for old versus new graph. The int8 row was
built with the old prune and shows the other open item plainly: without rescoring on the
floats, int8 recall@100 plateaus at 96.8 % on Cohere, which is why Zvec's published figures
use int8 *with* a refiner.

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
