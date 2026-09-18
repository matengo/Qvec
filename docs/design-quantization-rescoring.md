# Design: int8 with rescoring (`Int8Rescored`)

Extension of [design-quantization-int8.md](design-quantization-int8.md). Read that first.

## What it is

`quantization: VectorQuantization.Int8Rescored` builds and walks the graph on int8 codes exactly
like `Int8`, but also stores the original floats in the file. After the bottom-layer search, all
`efSearch` candidates are reranked against floats, and that is the order and the scores returned.

The result is float recall at (near) int8 speed. The price is storage: the float section plus
the int8 sections, i.e. ~1.25× a pure float file.

| Mode | Graph build/walk | Returned scores | `GetByGuid` | Vector storage |
| --- | --- | --- | --- | --- |
| `None` | float | exact | original | `dim × 4` |
| `Int8` | int8 | approximate | dequantized | `dim + 16` |
| `Int8Rescored` | int8 | exact (float) | original | `dim × 5 + 16` |

## Why not just `Int8`?

The int8 loss on Cohere 1M at ef 180 was 1.6 percentage points recall@100 (93.2 vs. 94.8%),
on dense cosine clusters 10 points (89.8 vs. ~100%). The loss is quantization noise in the
*final ranking*, not in the graph: int8-HNSW and int8-brute-force produce the same set. Reranking
the candidate set against exact floats removes that part of the noise completely.

This is also what zvec does with `--is-using-refiner` on the 10M dataset.

## What rescoring *cannot* do

Reranking changes the order within the candidate set but cannot add candidates the graph
missed. Therefore:

- At `efSearch == topK`, the candidate set is exactly `k` entries and recall@k is identical to
  int8. Cohere 1M, k = 100, ef 100: int8 89.3%, rescored 89.2%, float 90.3%. The scores
  are exact even then, however.
- Recall@k for rescored is bounded above by "recall@ef for the int8 walk". The larger
  `ef / k`, the closer it gets to float.

## Format

No version bump and no new `QuantizationMode`. The file is an int8 file
(`QuantizationMode = 2`, `QuantizationSectionId = 8`) that also has section 1 (`Vectors`,
`ElementSize = dim × 4`) in slot 8 with the flags `Present | Mutable | MayMoveOnGrow` — **not**
`Required` — and `HeaderFlags.HasOptionalSections` set.

Consequence for older readers (builds before this change): they ignore unknown non-`Required`
sections, open the file as pure int8, and search on the codes. That is correct but without
reranking. `Grow` in such a reader would lay out a new file without section 1 and thus lose the
floats; that is accepted because the versions never coexist in production.

`VectorQuantization.Int8Rescored = 3` exists only in the API. `QvecDatabase.Quantization`
derives the value via `QvecFormatLayout.EffectiveQuantization(header)` (mode 2 + section 1
present). The constructor compares the derived value on reopen, so `Int8` against a rescored file
and `Int8Rescored` against a pure int8 file both throw `QvecFormatException`.

`V4Header` validation: when section 1 exists in int8 mode, it must have `ElementSize = dim × 4`
and `Length ≥ MaxCount × dim × 4`. `RequiredSectionIds` is unchanged.

`CreateGrown` reads whether section 1 exists in the old table (`CloneState` does not copy the
section table) and lays it out again. `PlanSectionMoves` is generic over present sections and did
not need to change.

## Implementation in `QvecDatabase`

`_vectorSectionOffset` was split into `_floatVectorSectionOffset` (float mode and rescored) and
`_codesSectionOffset` (int8 and rescored); `_rescore` is set in `CaptureSectionOffsets`.

- `WriteVectorToDisk`: quantizes and writes codes + parameters; in rescored mode it continues
  and writes floats. All paths (AddEntry, AddEntries, UpdateVector, Vacuum) go through it.
- `ReadVectorInto` (used by `GetByGuid`/`GetVector` and Vacuum): reads floats when they exist,
  otherwise dequantizes. Vacuum in rescored mode therefore requantizes from the original, not from
  a previous dequantization.
- `FinalScore(query, index)`: exact float similarity if `_rescore`, otherwise `CalculateScore`.
  Used in all exhaustive paths (`SearchSimple`, `SearchSimpleParallel`,
  `ExhaustiveFilteredSearch`, `Rerank`).
- `RescoreCandidates`: rewrites `Score` in the candidate array from `SearchLayerNearest` /
  `SearchLayerFiltered` before `OrderByDescending().Take(topK)`. No-op unless `_rescore`, so
  float and int8 files pay nothing.
- The insert heuristic (`StoredSimilarity`, `SelectNeighborsHeuristic`) remains on int8, so
  the graph is identical to a pure int8 graph built in the same order.

## Measurements

### siftsmall

10,000 × 128, Euclidean, 12 threads, same run:

| | ef 20 | ef 80 |
| --- | --- | --- |
| float recall@10 / QPS | 98.6% / 22,729 | 99.5% / 8,169 |
| int8 recall@10 / QPS | 98.3% / 27,161 | 99.0% / 8,719 |
| rescored recall@10 / QPS | **99.1%** / 18,096 | **99.8%** / 8,118 |

On such a small index, graph traversal is cheap and reranking 20 candidates of 128 floats each
shows up in QPS at ef 20. At ef 80 the cost is lost in the noise.

### Dense clusters (the test)

Same data as `QuantizedDatabaseTests` (n = 2000, dim 64, spread 0.08, ef 200):
`RescoredQuantizationTests` requires ≥ 97% recall@10 against exact float-brute-force in all three
metrics, where int8 alone was at 89.8% for Cosine.

### Cohere 1M

See [benchmarks/README.md](../benchmarks/README.md#cohere-1m-measured) for the table. In short:
rescored matches float on recall@100 at ef 180 and 320 (94.7/97.4%) and does so at
int8 QPS (6,778 vs. int8 6,841 and float 3,764 at ef 180, 12 threads). The build took 170 s vs.
419 s for float. The file is 4,124 MiB vs. 3,376 (float) and 1,194 (int8).

## Tests

`tests/Qvec.Core.Tests/Quantization/RescoredQuantizationTests.cs`: layout (slot 8, not
Required, `HasOptionalSections`), `Open` reports `Int8Rescored`, mismatch in both
directions throws, `GetByGuid` returns the exact original, scores equal float-brute-force
(1e-4) in all metrics, recall ≥ 97% on dense clusters, filtered search with float scores,
Update/Delete/Vacuum keeps both sections in sync, Grow preserves floats, parallel
`AddEntries` writes floats for all rows.