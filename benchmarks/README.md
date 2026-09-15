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

## Metric

SIFT and GIST ground truth is **Euclidean**. Run them with `--distance Euclidean`, which is the
default. Measuring them under `Cosine` or `DotProduct` compares the index against neighbours that
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
