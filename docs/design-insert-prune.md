# Design: incremental neighbour pruning on insert

## Problem

Every insert into the HNSW graph creates up to M0 = 2·M back-links on layer 0: each of the
new node's neighbours gets the new node appended to its own list. Once the graph has warmed up
that list is almost always full, and the original code handled a full list by re-running the
complete selection heuristic (Malkov & Yashunin, algorithm 4) over all M0 + 1 candidates:

1. recompute the owner's similarity to every existing neighbour (M0 distances),
2. sort,
3. for each candidate in order, compare it against every already-selected candidate
   (up to M0² / 2 distances).

With M = 32 that is ~2,000 distance computations per full back-link and ~130,000 per insert.
Each `StoredSimilarity` reads two vectors from the memory-mapped file, so at 768 dimensions
(3 KiB per vector) an insert moved roughly 400 MB through the memory hierarchy. Profiling the
Cohere 100K build with `dotnet-trace` put 66 % of insert time in `PruneNeighbors` and gave
**59 inserts/s**, 1,696 s for 100K vectors.

## Change

`AddNeighborConnection` now applies the heuristic incrementally, the way Lucene's HNSW graph
builder does in `findWorstNonDiverse`:

1. Recompute the owner's similarity to the existing neighbours and the new node (M0 + 1
   distances — unavoidable because the graph section stores ids only), and sort descending.
2. Walk the candidates from farthest to nearest. A candidate is *non-diverse* if it is closer
   to some nearer candidate than to the owner. Only the new node is *unchecked*, so:
   - an existing neighbour needs to be compared against the new node only (1 distance), and
     only if the new node is nearer than it;
   - the new node itself is compared against every candidate nearer than it (≤ M0 distances).
3. Evict the first non-diverse candidate found. If that is the new node, the stored list is
   left untouched.

Typical cost is O(M0) distances instead of O(M0²).

### The fallback

Lucene can stop there and evict the farthest candidate when nothing is non-diverse, because
its lists only ever contain mutually diverse nodes. Qvec's lists do not: `SelectNeighborsHeuristic`
tops an under-filled result up with the best discarded candidates (`keepPrunedConnections`),
so a full list can hold neighbours that already fail the diversity test against each other.

Evicting the farthest candidate in that situation drops a diverse long-range link in favour of
a redundant short one. On a 200-vector Euclidean test set with widely varying magnitudes that
was enough to leave seven nodes with in-degree zero — unreachable from the entry point — and
fail `Search_WithEuclidean_ReturnsAnExactlyStoredVectorFirst`.

So when the incremental walk finds nothing, the code falls back to the full heuristic
(`PruneNeighbors`) over the M0 + 1 candidates, which is exactly the old behaviour and does
tell the topped-up neighbours apart. Instrumented on clustered vectors the fallback fires in
0.1 % (M = 32, 128 dims) to 0.7 % (M = 16, 64 dims) of full back-links, so it costs almost
nothing and keeps the graph connected in the cases the incremental rule cannot judge.

### Also

`SelectNeighborsHeuristic` no longer uses LINQ (`Where` / `OrderByDescending` / `ToArray`); a
stable index sort replaces it with identical ordering, so the forward selection produces the
same graph as before.

## What is not identical

The graph is **not** byte-identical to the one the old code built. The old full re-run evicted
the worst candidate that was non-diverse against the *selected set*; the incremental rule
evicts the worst candidate that is non-diverse against the *new node*. Both are legitimate
readings of the heuristic, but they can pick different victims, so recall had to be re-measured
rather than proved unchanged.

## Measurements

Same machine (12 logical cores, Windows 11), same seeds, `maxNeighbors = 32`.

| dataset | build before | build after | inserts/s before → after |
| --- | ---: | ---: | ---: |
| siftsmall (10K × 128) | ~7–10 s | 4.0 s | ~1,000–1,500 → 2,485 |
| Cohere 100K (768, cosine) | 1,696 s | 589 s | 59 → 170 (2.9×) |
| SIFT-1M (128, euclidean), same day | 2,279 s | 1,631 s | 439 → 613 (1.4×) |

The gain is largest where a distance computation is most expensive: at 768 dimensions the
O(M0²) pass was two thirds of the insert; at 128 dimensions, cache-resident, it was a smaller
share and the search phase dominates.

Recall, Cohere 100K, k = 100, both index files measured back to back with `--reuse-index`:

| efSearch | recall@100 before | recall@100 after | QPS before | QPS after |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 97.89 % | 97.60 % | 347 | 381 |
| 180 | 99.29 % | 99.03 % | 247 | 263 |

Recall, SIFT-1M, k = 10, both graphs rebuilt the same day and measured back to back twice:

| efSearch | recall@10 before | recall@10 after | Δ | QPS before / after |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 85.94 % | 84.44 % | −1.50 | 6,679–7,016 / 6,509–6,734 |
| 20 | 93.72 % | 92.48 % | −1.24 | 3,951–4,101 / 4,252–4,341 |
| 40 | 97.81 % | 97.03 % | −0.78 | 2,556–2,644 / 2,561–2,624 |
| 80 | 99.35 % | 99.05 % | −0.30 | 1,488–1,573 / 1,456–1,576 |
| 160 | 99.82 % | 99.73 % | −0.09 | 831–853 / 824–863 |
| 320 | 99.91 % | 99.90 % | −0.01 | 470–475 / 484–498 |

So the new graph is slightly less well connected at the narrowest beams — up to 1.5 points on
SIFT-1M, 0.3 on Cohere — and indistinguishable from efSearch 160 up. Query throughput at a
given efSearch is the same within run-to-run noise on both datasets. The recall/QPS frontier
moves by roughly one efSearch step at the low end; a caller who needs the old recall at
efSearch 10 gets it at efSearch 20 for ~35 % fewer QPS, and a caller at efSearch ≥ 80 sees no
difference. siftsmall recall is unchanged (100 % recall@10 from efSearch 100).

## Tests

`NeighborDiversityTests` pins the required *outcome* of a full-list back-link on hand-checkable
2-D and 3-D Euclidean constructions — new node rejected when redundant, the neighbour it makes
redundant evicted, farthest evicted when everyone is diverse — plus a well-formedness sweep
(no duplicates, no self-links, valid ids, no gaps) over clustered data under all three
distance functions. The tests pass against both the old and the new implementation, which is
the point: they describe the heuristic, not the shortcut.

## Next

Insert is still single-threaded. With the per-insert work now dominated by the search phase
and M0 + 1 owner-score recomputations, parallel insert (per-node locks on neighbour lists, as
in hnswlib) is the next lever, followed by measuring Cohere 1M.
