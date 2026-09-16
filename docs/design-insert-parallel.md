# Design: parallel index construction (`AddEntries`)

## Problem

`AddEntry` holds the database write lock for the whole insert and links one node at a time.
After the incremental prune change ([design-insert-prune.md](design-insert-prune.md)) a
Cohere 100K build (768 dimensions, M = 32) still took 371 s on a 12-core laptop — one core
busy, eleven idle. Every competitor Qvec is measured against (hnswlib, Lucene, zvec) builds
its graph with all cores.

## Change

`QvecDatabase.AddEntries(IReadOnlyList<QvecInsert> entries, int maxDegreeOfParallelism = -1)`
inserts a batch. The scheme is the one hnswlib uses: allocate rows serially, link them
concurrently, protect each node's neighbour lists with a fine-grained lock.

The database write lock is held for the entire call, so readers see either the state before
the batch or after it. Inside, the batch is processed in chunks of 8,192 entries:

**Phase A — serial.** For each entry: skip duplicate external ids, allocate a slot (reusing
tombstones, growing the file when needed), draw the layer, prepare the vector (normalised copy
for cosine), write vector, metadata, id and an empty neighbour list, register the id. Growth
remaps the file, so it can only happen while no other thread touches the mapping — which is
why this phase is serial and why a chunk is the unit of work.

**Phase B — parallel.** `Parallel.For` over the chunk with `MaxDegreeOfParallelism` threads,
each with its own `InsertScratch` (visited-epoch array and priority queues). Each thread runs
the unchanged `ConnectNewNode` for its node: greedy descent from the entry point, `ef`-search
per layer, neighbour selection heuristic, write the node's list, add back-links.

One `CommitHeader()` per chunk. `maxDegreeOfParallelism = 1` runs Phase B as a plain loop.

### Locking

Three kinds of shared state exist during Phase B.

**Neighbour lists** are protected by 1,024 striped locks (`nodeIndex & 1023`). A lock is held
during a node's read-modify-write in `AddNeighborConnection` and while the new node writes its
own list. Locks are never nested, so no ordering is needed and deadlock is impossible.

Searches (`GreedyClosest`, `SearchLayerNearest`) read neighbour lists without locking. This is
deliberate: slots are whole `int32` writes, each holds either a valid row index or `-1`, and a
half-updated list is still a valid list — at worst a search sees a slightly stale or truncated
neighbourhood, which affects which candidates it visits, not correctness. Every row a search
can reach is fully written, because Phase A finished before Phase B began.

**The new node's own list.** With the serial algorithm the new node's list is written before
any back-link can be added to it. Concurrently, a sibling may have selected the new node as a
neighbour and back-linked into it *before* the new node writes its own list. Overwriting would
lose that link, so `MergeNeighborsAtLevel` writes the selected neighbours first and appends
any pre-existing entries that are not already present, while room remains. On the serial path
the list is still all `-1` and this is a plain write.

**The entry point** is guarded by a `ReaderWriterLockSlim`. A normal insert takes the read
lock, so many threads descend from the same entry point at once. A node whose layer exceeds
the current entry-point layer (or that finds nothing to attach to) takes the write lock, links
itself exclusively and becomes the entry point. This happens a handful of times per million
inserts. If a chunk starts with no live entry point (empty database, or every earlier row
deleted) its first node is linked serially and promoted before the parallel loop starts.

### What is thread-safe already

The scoring functions read vectors and int8 codes in place from the mapping and use no shared
buffers. `_deletedIndices` is only read during Phase B (deletes hold the same write lock as
`AddEntries`). `ArrayPool<int>.Shared` is per-thread. `BeginWrite()` is called before Phase B
so its dirty flag is already set.

## Consequences

**Reproducibility.** With more than one thread the order in which nodes are linked depends on
scheduling, so two builds of the same data with the same `indexSeed` are no longer
byte-identical. The layer assignment is still seeded and drawn serially in Phase A; only the
link order varies. `maxDegreeOfParallelism = 1` reproduces the serial `AddEntry` graph exactly
(test: `AddEntries_WithDegreeOfParallelismOne_ReproducesTheSerialGraphExactly`).

**Graph quality.** Siblings linked at the same time cannot see each other's links, and they
compete for the same full neighbour lists. The measured effect is small: recall@100 on Cohere
100K is 97.51 % (12 threads) vs 97.60 % (1 thread) at efSearch 100; on SIFT-1M recall@10 is
identical to the serial build at every efSearch. Structurally, the parallel build leaves
somewhat more nodes without incoming layer-0 links on a stress case (M = 4, clustered data:
43 vs 26 of 4,000); the test suite bounds this against the serial build.

**Readers are excluded for the whole batch.** `AddEntries` is for bulk loading, not for
interleaving with queries. Callers who need queries to proceed during a load should submit
smaller batches.

**Memory.** Prepared vectors are kept for one chunk only (8,192 × dimension × 4 bytes,
25 MB at 768 dimensions).

## Measured

Same machine (Snapdragon X Elite, 12 cores), same day, back to back, power throttling
disabled (see below).

| Dataset | 1 thread | 12 threads | Speed-up | Recall change |
| --- | ---: | ---: | ---: | --- |
| Cohere 100K float, M = 32 | 370.9 s (270/s) | 58.6 s (1,707/s) | 6.3× | recall@100 −0.09 pp @ ef 100 |
| SIFT-1M, M = 32 | 1,145.6 s (873/s) | 142.1 s (7,037/s) | 8.1× | recall@10 within 0.1 pp at all ef |

SIFT scales better than Cohere because a 128-dimension vector is 512 bytes — the graph
fits the last-level cache far better, so the lock waits on hubs dominate less.

Scaling is below linear because the workload is memory-latency bound (each distance is a
random 3 KiB read) and because hub nodes serialise back-links: on Cohere 100K about 20 % of
thread time is spent waiting for another thread's `AddNeighborConnection` on the same hub.
A lock-free or optimistic back-link scheme could recover some of that; it was not pursued.

### Windows power throttling

While investigating why 12 threads used only four cores' worth of CPU time, the cause turned
out to be outside the library: Windows 11 on a Balanced power plan classifies a console
process that is not in the foreground as background work (EcoQoS) and schedules it on few
cores at reduced clock speed. That is also why single-thread numbers on this machine varied by
40 % between sessions. The benchmark executable now opts out via
`SetProcessInformation(ProcessPowerThrottling)` (`PowerThrottling.cs`); the serial Cohere
100K build went from 589 s to 371 s from that alone, with no code change in the library.
Every figure in this document was measured with the exemption in place, and README figures
measured before it are marked as such. An application that bulk-loads in the background on a
Windows laptop will see the throttled numbers unless it does the same.
