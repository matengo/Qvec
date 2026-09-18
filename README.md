# Qvec ⚡ 
### The "SQLite of Vector Databases" for .NET 10

> **Status: 2.x / active hardening.** Qvec is usable for experiments and prototypes, but APIs may still change between minor versions. The on-disk format is **version 5**, self-describing and checksummed; there is no migration from the pre-2.0 formats (see [Upgrading from 1.0.x](#installation)).

**Qvec** is an open-source, embedded vector database written entirely in C# for **.NET 10**. It is designed for local AI-driven applications that need in-process vector search with **HNSW** (Hierarchical Navigable Small World) indexing.

Unlike client-server vector DBs, Qvec runs in-process, using **MemoryMappedFiles** for disk-backed storage and SIMD-friendly vector math for fast similarity scoring.

---

## 🚀 Key Features

*   **Embedded .NET Library:** Runs in-process with no vector database server, daemon, or external service required.
*   **HNSW Indexing:** Approximate nearest-neighbor search with tunable `maxNeighbors` and `efSearch` parameters for speed/recall trade-offs. The base layer uses double fan-out (`M0 = 2 × M`) as recommended by the HNSW paper.
*   **Disk-Backed Storage:** Uses `MemoryMappedFiles` for persistent local storage that survives application restarts.
*   **Growable, Sparse Files:** Capacity is a starting point, not a limit. The file is created sparse and grows geometrically — both row capacity and the metadata heap — when it runs out of room. Set `AutoGrow = false` for a hard ceiling.
*   **Three Distance Metrics:** `DotProduct`, `Cosine` and `Euclidean` (L2). Cosine normalizes stored copies; the other two store vectors untouched. Euclidean is the metric most image and audio embeddings — and every published ANN benchmark corpus — are defined against.
*   **Hardware-Accelerated Math:** Uses .NET vector APIs and unsafe pointer paths for SIMD-friendly scoring.
*   **Optional int8 Scalar Quantization:** Pass `quantization: VectorQuantization.Int8` to store one byte per dimension instead of four (plus 16 bytes of per-vector scale/offset). Scoring runs on the bytes with a widening SIMD integer kernel, so queries get faster as well as smaller. Float mode is untouched and byte-for-byte identical to before. See [docs/design-quantization-int8.md](docs/design-quantization-int8.md) for the accuracy trade-off.
*   **int8 with Float Rescoring:** `VectorQuantization.Int8Rescored` builds and walks the graph on int8 but keeps the floats and re-ranks the `efSearch` candidates on them, so you get float recall and exact scores at roughly int8 speed. Costs ~1.25× float storage. See [docs/design-quantization-rescoring.md](docs/design-quantization-rescoring.md).
*   **Reproducible Index Builds:** HNSW layer assignment is randomized by default, so two builds of the same data normally produce different graphs. Pass `indexSeed:` to pin it — necessary for benchmarks and recall regression tests to be re-derivable. (Multi-threaded `AddEntries` builds keep the seeded layers but not the link order, so they are not byte-reproducible.)
*   **Parallel Bulk Loading:** `AddEntries` inserts a batch with every core linking nodes into the graph at once — hnswlib-style striped node locks, one write lock for the whole batch. 6.3× faster than one-by-one `AddEntry` on 12 cores at a cost of ~0.1 pp recall. See [docs/design-insert-parallel.md](docs/design-insert-parallel.md).
*   **Guid Document IDs:** `AddEntry` returns a stable `Guid` document identifier; external IDs can be supplied for deduplication and sync scenarios.
*   **Update and Delete:** Supports tombstone-based delete, metadata updates, and vector updates by delete-and-reinsert. `Vacuum()` compacts the file, reuses tombstoned rows, reclaims orphaned metadata, and rebuilds the HNSW graph.
*   **Metadata Filtering:** General metadata predicates are supported after HNSW retrieval; `[QvecIndexed]` equality filters can pre-filter via an in-memory inverted index.
*   **Opt-in Sync:** Turn on change tracking and the file carries a replica id, a hybrid-logical-clock version per document and a change-log ring. The separate `Qvec.Sync` package replicates between instances over a shared directory with deterministic last-write-wins, and bootstraps new nodes by copying the file instead of rebuilding the index. Off by default; untracked files are byte-identical to 2.0. See [Sync](#-sync).
*   **AOT-Friendly Core:** `Qvec.Core` is dependency-free and avoids JSON/reflection requirements. The typed client currently uses reflection and expression compilation; see the AOT notes below.

---

## 📊 Performance Benchmark

Measured on **SIFT-1M** ([TexMex corpus](http://corpus-texmex.irisa.fr/)) against its published exact ground truth — not against a linear scan in the same process.

1,000,000 base vectors, 128 dimensions, 10,000 queries. `DistanceFunction.Euclidean`, `maxNeighbors = 32`, `maxLayers = 5`. Single-threaded queries on 12 logical cores, Windows 11.

| efSearch | recall@1 | recall@10 | QPS | mean latency |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 88.0 % | 84.4 % | 8,710 | 0.115 ms |
| 20 | 94.5 % | 92.5 % | 5,625 | 0.178 ms |
| 40 | 97.6 % | 97.0 % | 3,416 | 0.293 ms |
| 80 | 98.8 % | 99.1 % | 1,923 | 0.520 ms |
| 160 | 99.3 % | 99.7 % | 1,084 | 0.922 ms |
| 320 | 99.3 % | 99.9 % | 607 | 1.648 ms |

Index build: 1,146 s (873 inserts/s) single-threaded with `AddEntry`, or **142 s (7,037 inserts/s)** with `AddEntries` on 12 build threads — 8.1× — producing a 1,324 MiB file either way. The parallel graph scored 84.5 / 92.6 / 97.0 / 99.0 / 99.7 / 99.9 % recall@10 on the same rows, i.e. identical within 0.1 pp; it is not byte-reproducible though, so the table above is from the seeded serial build. The previous graph, built with the full O(M0²) neighbour heuristic on every back-link, scored 85.9 / 93.7 / 97.8 / 99.4 / 99.8 / 99.9 %; the incremental heuristic ([design doc](docs/design-insert-prune.md)) trades up to 1.5 points of recall at the narrowest beam, and nothing from efSearch 160 up, for a 1.4× faster build.

These numbers are higher than earlier revisions of this table because the benchmark now opts out of Windows 11 power throttling (EcoQoS), which had been silently capping background console processes at roughly a third of the machine — see [benchmarks/README.md](benchmarks/README.md#windows-power-throttling). Earlier figures were measured throttled and are not comparable.

A single recall figure would be misleading, because any ANN index reaches 99% by widening the beam until it has effectively scanned everything. The honest unit is the whole curve, so pick the row that matches your latency budget.

Reproduce with `dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --download`. See [benchmarks/README.md](benchmarks/README.md).

### Cohere 1M under concurrent load

The configuration Zvec publishes for Cohere 1M (768 dims, cosine, recall@100, `M = 15`, `efSearch = 180`, 12 concurrent clients), on the same 12-core laptop:

| mode | build (12 threads) | file | recall@100 | QPS 1 thread | QPS 12 threads |
| --- | ---: | ---: | ---: | ---: | ---: |
| float | 419 s | 3,376 MiB | 94.8 % | 565 | 3,764 |
| int8 | 181 s | 1,194 MiB | 93.2 % | 839 | 6,841 |
| int8 rescored | 170 s | 4,124 MiB | 94.7 % | 630 | 6,778 |

Rescored int8 walks the int8 graph and re-ranks the candidates on the floats: float recall at int8 throughput, paid for in storage. It cannot help when `efSearch == k`, since re-ranking reorders the candidate set but does not add to it. The full sweep, the single-threaded rows and what can and cannot be read into a comparison with Zvec's 16-vCPU figures are in [benchmarks/README.md](benchmarks/README.md#cohere-1m-measured).

### int8 quantization on siftsmall

Same code, `--dataset siftsmall` (10,000 vectors, 128 dimensions, 100 queries), float versus `--quantization int8`, same seed:

| | efSearch | recall@10 | QPS | file |
| --- | ---: | ---: | ---: | ---: |
| float | 10 | 98.6 % | 14,161 | 13.8 MiB |
| int8 | 10 | 97.9 % | 32,686 | 10.3 MiB |
| float | 40 | 100.0 % | 9,335 | |
| int8 | 40 | 99.3 % | 11,579 | |
| float | 160 | 100.0 % | 3,689 | |
| int8 | 160 | 99.3 % | 4,453 | |

The vector section shrinks 4× (5.1 → 1.3 MiB); the rest of the file is the HNSW graph, which quantization does not touch, so total file size drops less than 4×. int8 recall plateaus below float — 99.3 % here — because the ranking is done on the quantized codes and the floats are not kept for rescoring; `Int8Rescored` keeps them and closes that gap (99.8 % recall@10 at efSearch 80 on the same dataset, above float's 99.5 %). SIFT descriptors are natively 8-bit so this is close to a best case; on tight clusters under `Cosine` the loss is larger (see the design doc).

> **Note on the previous numbers.** Earlier versions of this README reported "~85% recall@1 at efSearch=50", measured on randomly generated uniform vectors and scored against Qvec's own linear scan. That figure understated real-world behaviour substantially: in high dimensions uniform random vectors are all roughly equidistant, which is an artificially hard case that no real embedding model produces.

---

## 💻 Quick Start

### Installation

Install via the .NET CLI:

```bash
dotnet add package Qvec.Core
```

Or for the typed client:

```bash
dotnet add package Qvec.Core.Client
```

And for replication between instances (see [Sync](#-sync)):

```bash
dotnet add package Qvec.Sync
```

> **Upgrading from 1.0.x:** 2.0 is a rewrite. The on-disk format (v5) is not readable by 1.0.x and 1.0.x files are rejected with `QvecFormatException`; there is no in-place migration. Export from the old database and re-insert into a new one.

### Initialize and Add Data

```csharp
using Qvec.Core;

// Fixed-capacity database: dim and max are chosen at creation time.
using var db = new QvecDatabase("vectors.qvec", dim: 1536, max: 10_000);

float[] embedding = GetEmbedding("Hello World");
Guid id = db.AddEntry(embedding, "{\"id\":1,\"category\":\"text\"}");

// Bulk load: links the batch into the graph on every core. Same result as a
// loop of AddEntry (ids in input order, duplicates skipped), 6× faster on 12 cores.
IReadOnlyList<Guid> ids = db.AddEntries(
    documents.Select(d => new QvecInsert(d.Embedding, d.MetadataJson, d.Id)).ToList());
```

> **Capacity note:** `max` is a **starting** size, not a ceiling. The file is laid out for that capacity up front and **grows geometrically** when it runs out of rows or metadata heap space. It is also created **sparse**, so `dim: 1536, max: 1_000_000` describes a ~7.3 GB layout while consuming only the pages you actually write. Set `AutoGrow = false` if you want a hard bound instead — a full database then throws `QvecFullException`.
>
> **Metadata note:** metadata is stored in an append-only heap addressed by a per-row `(offset, length)` descriptor. There is no per-entry size limit and the heap grows on demand. Updating metadata orphans the previous blob until `Vacuum()` reclaims it.

## HNSW Vector Search

```csharp
var results = db.Search(queryVector, topK: 5);

foreach (var r in results)
{
    Console.WriteLine($"Found match: {r.Id} with score {r.Score}");
}
```

`Search` returns `List<(Guid Id, float Score, string Metadata)>`.

## Metadata Filtered Search

```csharp
var results = db.Search(
    queryVector,
    meta => meta.Contains("\"category\":\"text\""),
    topK: 5);

foreach (var r in results)
{
    Console.WriteLine($"Found match: {r.Id} with score {r.Score}");
}
```

This overload performs HNSW retrieval first and then applies the metadata predicate to the retrieved candidates. It is convenient, but it is **post-filtering**, not integrated filtering inside the HNSW navigation loop.

## Typed client

```csharp
using Qvec.Core;
using Qvec.Core.Client;

using var db = new QvecDatabase("products.qvec", dim: 1536, max: 10_000);
var client = new QvecClient<Product>(db);

Guid id = client.AddEntry(new VectorData<Product>
{
    vector = myVector,
    Item = new Product(1, "Laptop", 12_000, true)
});

var results = client.Search(queryVector, p => p.Price < 15_000 && p.InStock);

foreach (var r in results)
{
    Console.WriteLine($"{r.Item?.Name}: {r.Score}");
}

public record Product(int Id, string Name, double Price, bool InStock);
```

## Typed client serialization

You can pass source-generated JSON metadata for serialization/deserialization:

```csharp
using System.Text.Json.Serialization;
using Qvec.Core;
using Qvec.Core.Client;

using var db = new QvecDatabase("products.qvec", dim: 1536, max: 10_000);
var client = new QvecClient<Product>(db, ProductJsonContext.Default.Product);

Guid id = client.AddEntry(new VectorData<Product>
{
    vector = myVector,
    Item = new Product(1, "Laptop", 12_000, true)
});

var results = client.Search(queryVector, p => p.Price < 15_000 && p.InStock);

public record Product(int Id, string Name, double Price, bool InStock);

[JsonSerializable(typeof(Product))]
internal partial class ProductJsonContext : JsonSerializerContext { }
```

### AOT scope

`Qvec.Core` is the AOT-friendly package: it is dependency-free and stores caller-provided metadata strings. `Qvec.Core.Client` is convenient, but it currently uses `typeof(T).GetProperties()`, expression-tree compilation, and reflection-based JSON serialization unless `JsonTypeInfo<T>` is supplied. Treat the typed client as not fully Native AOT-ready yet.

## Indexed Filtering with `[QvecIndexed]`

Qvec supports equality filtering via an in-memory inverted index. Mark properties with `[QvecIndexed]`, create the typed client with the generated extractor, and simple `==` expressions over indexed properties use the index instead of scanning JSON metadata.

### 1. Mark properties to index

```csharp
using Qvec.Core;

public class Product
{
    [QvecIndexed]
    public string Category { get; set; } = "";

    [QvecIndexed]
    public string Brand { get; set; } = "";

    public string Description { get; set; } = "";
    public double Price { get; set; }
}
```

### 2. Create the client with the extractor

When `Qvec.Core.Client` is consumed as a NuGet package, its analyzer includes the source generator. For a type in the global namespace, the generator emits `Qvec.Generated.ProductFieldExtractor`; for a type inside a namespace, the extractor is emitted into that same namespace.

```csharp
using Qvec.Core;
using Qvec.Core.Client;

using var db = new QvecDatabase("products.qvec", dim: 1536, max: 10_000);
var client = new QvecClient<Product>(
    db,
    new Qvec.Generated.ProductFieldExtractor());
```

> **Known limitation:** if you reference `Qvec.Core.Client` via `ProjectReference` instead of the NuGet package, the source generator/analyzer does not currently flow transitively. Add an explicit analyzer reference to `Qvec.SourceGen` in that setup until this is fixed.

The inverted index is rebuilt from disk at startup when an extractor is supplied and kept in sync on inserts and deletes.

### 3. Query with `Where`

`Where` accepts an `Expression<Func<T, bool>>`. If the expression consists of `==` comparisons on indexed properties, the inverted index is used automatically. Everything else falls back to a parallel scan — same syntax either way.

```csharp
var science = client.Where(p => p.Category == "Science");

var acmeScience = client.Where(
    p => p.Category == "Science" && p.Brand == "Acme");

string cat = "Science";
var results = client.Where(p => p.Category == cat);

var cheap = client.Where(p => p.Description.Contains("quantum"));
```

| Expression | Strategy | Complexity |
| :--- | :--- | :--- |
| `p => p.Category == "Science"` | Inverted index | **O(1)** lookup |
| `p => p.Category == "Science" && p.Brand == "Acme"` | Index intersection | HashSet intersection |
| `p => p.Price < 100` | Parallel scan fallback | O(N) |
| `p => p.Description.Contains("x")` | Parallel scan fallback | O(N) |

### Hybrid Search with Indexed Filtering

The typed client's `Search` can pre-filter only when the filter is made of equality comparisons on `[QvecIndexed]` properties. In that case, Qvec gets candidate row IDs from the inverted index and ranks only those candidates by vector similarity. Other filters fall back to HNSW search followed by post-filtering.

```csharp
var results = client.Search(queryVector, p => p.Category == "Science", topK: 5);

var acmeResults = client.Search(
    queryVector,
    p => p.Category == "Science" && p.Brand == "Acme",
    topK: 5);

var postFiltered = client.Search(queryVector, p => p.Price < 100, topK: 5);
```

| Scenario | Without index | With `[QvecIndexed]` equality filter |
| :--- | :--- | :--- |
| 1M entries, 1% match filter | HNSW finds 50 candidates → filter → **~0 results** | Index → 10K candidates → rank → **5 perfect results** |
| 1M entries, 50% match filter | HNSW + post-filter works OK | Index → 500K candidates → rank; HNSW may be faster depending on recall/latency needs |

### Combining JSON type info and indexed filtering

```csharp
var client = new QvecClient<Product>(
    db,
    ProductJsonContext.Default.Product,
    new Qvec.Generated.ProductFieldExtractor());
```

---

## 🔄 Sync

Qvec can replicate between instances — two desktops, an edge gateway and a server, a fleet of kiosks. It is **opt-in**: a database created without `changeTracking:` is exactly the file 2.0 wrote and pays nothing. With tracking on, `Qvec.Core` records what changed; the `Qvec.Sync` package moves those changes between replicas.

### What the core records

Pass `changeTracking: new ChangeTrackingOptions()` when creating a database (or call `EnableChangeTracking()` on an existing one, which stamps every live row and writes one change record per row). The file is then written as format **version 6** and carries:

- a **`ReplicaId`** (a `Guid`) identifying this copy;
- a **version per document** — a 64-bit hybrid logical clock (48 bits of wall-clock milliseconds, a 16-bit counter) plus the origin replica id, stamped on every insert, update and delete;
- a **change-log ring** — a fixed-size ring (`ChangeTrackingOptions.LogCapacity`, default = the row capacity `max`, 64 bytes per slot) of `(seq, document, version, operation)`; `db.ChangeSeq` is the latest sequence number, `db.OldestChangeSeq` the oldest still in the ring.

Three methods expose it: `GetChanges(sinceSeq, maxItems)` returns a `ChangeBatch` with the vectors and metadata of the affected documents; `ApplyChanges(batch)` merges a batch into another replica and reports `Applied`/`Skipped`/`Rejected`; `ExportSnapshot(stream)` writes a consistent copy of the whole file for bootstrapping.

### Running an agent

```csharp
using Qvec.Core;
using Qvec.Core.Sync;
using Qvec.Sync;

using var db = File.Exists(path)
    ? QvecDatabase.Open(path)
    : new QvecDatabase(path, dim: 768, max: 100_000, changeTracking: new ChangeTrackingOptions());

// Any directory every replica can reach: a network share, a synced folder, a mounted volume.
await using var peer = new DirectorySyncPeer(@"\\fileserver\qvec-sync");
await using var agent = new SyncAgent(db, peer, new SyncOptions
{
    PollInterval = TimeSpan.FromSeconds(5),
    BootstrapFromSnapshotIfBehind = true,   // copy a snapshot instead of failing when too far behind
    SnapshotInterval = TimeSpan.FromHours(1),
});

agent.IterationCompleted += r => { if (r.Applied > 0) Console.WriteLine($"applied {r.Applied} remote changes"); };
agent.DatabaseReplaced += fresh => { /* re-point your references after a snapshot bootstrap */ };

agent.Start();                 // background loop: push own changes, pull everyone else's
// ... or drive it yourself:
SyncIterationResult once = await agent.SyncOnceAsync();
```

`SyncAgent` pushes its own unsent changes in `ChangeBatch`es, pulls the other replicas' batches and applies them, and persists its cursor (`SyncOptions.StatePath`, default next to the database) so a restart continues where it stopped. Every batch is CRC-checked and, by default (`SyncCompression.Auto`), Brotli-compressed when metadata is a meaningful share of the payload — dense float vectors do not compress, so those go out raw. `Qvec.Sync` has no cloud SDK dependency; `DirectorySyncPeer` is plain `System.IO`. The `ISyncPeer` interface is public, so another transport (object store, HTTP hub) is a class, not a fork. A runnable two-process example lives in [`samples/Qvec.Samples.Sync`](samples/Qvec.Samples.Sync).

### What you should know before relying on it

- **Conflicts are resolved by last-write-wins, per document.** The document version with the greater HLC wins; ties break on origin replica id. Concurrent edits to the same document on two replicas lose one of them — silently, deterministically, the same way on every replica. A delete is a version like any other, so a later update on another replica resurrects the document. There is no field-level merge.
- **Clock skew is bounded, not eliminated.** The HLC never goes backwards and advances past anything it receives, so causally later writes always win. Two *independent* writes are ordered by wall clock; a replica whose clock runs ahead wins those races. Keep NTP running.
- **The change log is a ring.** A replica that has been offline longer than the ring covers cannot catch up incrementally: `GetChanges` throws `SyncCursorTooOldException` on the source side, the agent surfaces it as `SyncLogOverrunException`, or — with `BootstrapFromSnapshotIfBehind` — replaces the local file with the newest published snapshot. The bootstrapped copy gets a **new `ReplicaId`** and any unsent local changes are lost; `DatabaseReplaced` fires so you can re-point references. Size `LogCapacity` for the longest outage you want to survive.
- **Payloads must match.** Replicas must share dimension (checked — `ApplyChanges` throws) and distance function (not checked — your responsibility). Float and `Int8Rescored` replicas send floats, which any replica can apply (int8 replicas quantize on the way in). A plain `Int8` replica only has codes to send, and those are **rejected** by float and `Int8Rescored` replicas; `ApplyResult.Items` carries the reason and `SyncIterationResult.Rejected` counts them.
- **The directory transport echoes.** Every replica publishes its applied changes under its own prefix, so a change fans out through the folder and other replicas skip what they already have. Cheap and dependency-free, but the folder grows with every hop and it takes a couple of quiet polls before an idle fleet reports `DidWork == false`.
- **Field indexes are rebuilt, not synced.** `ApplyChanges` keeps the typed client's in-memory inverted index current like any other mutation; after a snapshot bootstrap the agent rebuilds it from scratch if `SyncOptions.FieldIndexExtractor` is set.
- **Cost.** Change tracking adds 24 bytes per row for the version plus 64 bytes per ring slot, and stamps one version and writes one log record per mutation. Measured numbers are in [benchmarks/README.md](benchmarks/README.md#change-tracking-cost).

The full design — file layout, HLC rules, the wire format and what was deliberately left out — is in [docs/design-sync-engine.md](docs/design-sync-engine.md) and [docs/design-format.md](docs/design-format.md).

---

## 🎯 Use Cases

Qvec is built as an **embedded** vector database — no server, no network overhead, just a library running in your process. This makes it suitable for scenarios where low latency, offline capability, and a small deployment footprint matter:

| Scenario | Why Qvec? |
| :--- | :--- |
| **AI Agents on the Edge** | Run RAG-powered agents on IoT gateways, factory floors, or retail kiosks without depending on cloud connectivity. |
| **Agent Memory** | Give autonomous agents persistent, searchable long-term memory that lives alongside the agent process. |
| **Embedded / Industrial Software** | In-process .NET storage with MemoryMappedFiles is a good fit for instruments, PLCs, and headless services. |
| **Mobile & Tablet Apps** | Ship a local vector store inside .NET MAUI or Uno Platform apps for offline semantic search. |
| **Desktop Copilots & Plugins** | Add similarity search to WPF / WinUI / Avalonia apps — no Docker, no external service. |
| **Serverless & Functions** | The core library has no server dependency, but validate cold-start, file-system, and AOT constraints for your host. |
| **Privacy-Sensitive Workloads** | Keep embeddings on-device for healthcare, legal, or finance apps where data must never leave the machine. |
| **Rapid Prototyping** | One NuGet reference, zero infrastructure — go from idea to working vector search quickly. |

---

## 🏗 Architecture

1. **Header:** Stores metadata, entry point, layer distribution, capacity, and format version.
2. **Vector Store:** Contiguous float arrays stored via `MemoryMappedFiles`.
3. **Graph Store:** Hierarchical adjacency lists for HNSW layers.
4. **Metadata Store:** Fixed-size 512-byte UTF-8 slots for caller-supplied metadata strings.
5. **ID and Tombstone Stores:** Stable `Guid` document IDs plus soft-delete markers.
6. **Entry Versions and Change Log (version 6, opt-in):** A hybrid-logical-clock version and origin replica per row, plus a fixed-size ring of change records that peers pull from. Present only when change tracking is enabled.
7. **Optional Inverted Index:** In-memory equality index rebuilt by the typed client when an extractor is supplied.

## ☁️ Cloud Readiness

Today, Qvec is a local embedded library. There is a small `Qvec.Api` sample project with `/health`, `/search`, `/stats`, update, and delete endpoints, but the API project is not published as a package and the core library does not register ASP.NET health checks.

Planned cloud work is tracked in design documents and the roadmap below.

## 📜 Roadmap / Not yet implemented

- **Storage format v5 / v6** — The on-disk format is self-describing (magic, version, CRC-32 over the header, a section table, and a `WriteInProgress` flag). Files with change tracking are written as v6; files without stay v5 and readable by 2.0. There is **no migration** from earlier formats; older files are rejected with `QvecFormatException`.
- **int8 scalar quantization** — ✅ Done. `quantization: VectorQuantization.Int8` stores one byte per dimension and scores on integers. `VectorQuantization.Int8Rescored` additionally keeps the floats and re-ranks the candidates on them, closing the recall gap at ~1.25× float storage.
- **Sync Engine** — ✅ Core done in 2.1: opt-in change tracking in `Qvec.Core` (replica id, HLC version per document, change-log ring, `GetChanges`/`ApplyChanges`/`ExportSnapshot`) and the `Qvec.Sync` package with `SyncAgent`, persisted cursors, CRC-checked, optionally Brotli-compressed batches, snapshot bootstrap and the `DirectorySyncPeer` transport (any shared folder). See [Sync](#-sync) and the [design](docs/design-sync-engine.md). Remaining: an object-store transport (`Qvec.Sync.AzureBlob`, S3-compatible) and `Qvec.Api` as a hub peer; the `ISyncPeer` interface they plug into is already public.
- **Azure Blob Storage and Managed Identity integration** — Planned as the `Qvec.Sync.AzureBlob` transport; no Azure SDK dependency is shipped today.
- **Container packaging** — A `Dockerfile` for `Qvec.Api` is included. Chiseled base images are not used yet.
- **ASP.NET health-check integration** — The core exposes `IsHealthy()` and the sample API maps `/health`; packaged Kubernetes/Azure health-check wiring is not implemented yet.
- **Full Native AOT support for the typed client** — Remove or replace reflection, expression compilation, and reflection-based JSON paths.
- **ProjectReference analyzer flow for source generation** — Ensure the `[QvecIndexed]` generator is available when consuming `Qvec.Core.Client` through project references.
- **Published benchmark methodology** — ✅ Done. `benchmarks/Qvec.Benchmarks` measures recall vs. QPS against the TexMex SIFT/GIST corpora and their published ground truth.
- **Faster index construction** — Done in three steps. Removing marshalling, pool and allocation overhead took SIFT-1M from 242 to 362 inserts/s; replacing the O(M0²) re-run of the neighbour heuristic on every full back-link with an incremental O(M0) update ([design doc](docs/design-insert-prune.md)) took Cohere 100K (768 dims) from 59 to 170 inserts/s; parallel construction through `AddEntries` ([design doc](docs/design-insert-parallel.md)) gives a further 6.3× on 12 cores. Remaining lever: hub-node lock contention during back-linking (~20 % of thread time on Cohere).
- **Multi-vector support** — Store and search multiple embeddings, such as image + text, for one logical entry.

## License

This project is licensed under the Apache License 2.0.

## Commercial Use

This software is free to use in commercial applications under the terms of the Apache 2.0 license.
