# Qvec ⚡ 
### The "SQLite of Vector Databases" for .NET 10

> **Status: pre-1.0 / active hardening.** Qvec is usable for experiments and prototypes, but APIs are still changing. Version `0.1.x` should be treated as an early preview. The on-disk format is **v4**, self-describing and checksummed; there is no migration from the pre-0.1 formats.

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
*   **Reproducible Index Builds:** HNSW layer assignment is randomized by default, so two builds of the same data normally produce different graphs. Pass `indexSeed:` to pin it — necessary for benchmarks and recall regression tests to be re-derivable.
*   **Guid Document IDs:** `AddEntry` returns a stable `Guid` document identifier; external IDs can be supplied for deduplication and sync scenarios.
*   **Update and Delete:** Supports tombstone-based delete, metadata updates, and vector updates by delete-and-reinsert. `Vacuum()` compacts the file, reuses tombstoned rows, reclaims orphaned metadata, and rebuilds the HNSW graph.
*   **Metadata Filtering:** General metadata predicates are supported after HNSW retrieval; `[QvecIndexed]` equality filters can pre-filter via an in-memory inverted index.
*   **AOT-Friendly Core:** `Qvec.Core` is dependency-free and avoids JSON/reflection requirements. The typed client currently uses reflection and expression compilation; see the AOT notes below.

---

## 📊 Performance Benchmark

Measured on **SIFT-1M** ([TexMex corpus](http://corpus-texmex.irisa.fr/)) against its published exact ground truth — not against a linear scan in the same process.

1,000,000 base vectors, 128 dimensions, 10,000 queries. `DistanceFunction.Euclidean`, `maxNeighbors = 32`, `maxLayers = 5`. Single-threaded queries on 12 logical cores, Windows 11.

| efSearch | recall@1 | recall@10 | QPS | mean latency |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 89.7 % | 85.9 % | 8,110 | 0.123 ms |
| 20 | 95.3 % | 93.7 % | 5,128 | 0.195 ms |
| 40 | 98.2 % | 97.8 % | 3,231 | 0.310 ms |
| 80 | 99.0 % | 99.4 % | 1,873 | 0.534 ms |
| 160 | 99.2 % | 99.8 % | 1,023 | 0.978 ms |
| 320 | 99.2 % | 99.9 % | 573 | 1.744 ms |

Index build: 2,762 s (362 inserts/s), producing a 1,324 MiB file, with `indexSeed` pinned so the run can be reproduced. Build throughput is still the weakest number here: the insert path is now dominated by the O(M0²) distance arithmetic of the neighbour-selection heuristic and by memory latency once the file outgrows the CPU caches. Query performance is not affected by it.

A single recall figure would be misleading, because any ANN index reaches 99% by widening the beam until it has effectively scanned everything. The honest unit is the whole curve, so pick the row that matches your latency budget.

Reproduce with `dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --download`. See [benchmarks/README.md](benchmarks/README.md).

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

### Initialize and Add Data

```csharp
using Qvec.Core;

// Fixed-capacity database: dim and max are chosen at creation time.
using var db = new QvecDatabase("vectors.qvec", dim: 1536, max: 10_000);

float[] embedding = GetEmbedding("Hello World");
Guid id = db.AddEntry(embedding, "{\"id\":1,\"category\":\"text\"}");
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
6. **Optional Inverted Index:** In-memory equality index rebuilt by the typed client when an extractor is supplied.

## ☁️ Cloud Readiness

Today, Qvec is a local embedded library. There is a small `Qvec.Api` sample project with `/health`, `/search`, `/stats`, update, and delete endpoints, but the API project is not published as a package and the core library does not register ASP.NET health checks.

Planned cloud work is tracked in design documents and the roadmap below.

## 📜 Roadmap / Not yet implemented

- **Storage format v5** — The on-disk format is self-describing (magic, version, CRC-32 over the header, a section table, and a `WriteInProgress` flag). There is **no migration** from earlier formats; older files are rejected with `QvecFormatException`.
- **int8 scalar quantization** — ~4× smaller vectors on disk and in memory. The format reserves space for the metadata this needs.
- **Sync Engine** — Opt-in edge-cloud synchronization. Connect multiple local Qvec databases to a central sync server so connected instances can stay in sync automatically. The current design discusses Azure Append Blob and Azure Web PubSub, but this is not implemented. See [design doc](docs/design-sync-engine.md).
- **Azure Blob Storage and Managed Identity integration** — Planned as part of the sync/cloud work; no Azure SDK dependency is shipped today.
- **Container packaging** — A `Dockerfile` for `Qvec.Api` is included. Chiseled base images are not used yet.
- **ASP.NET health-check integration** — The core exposes `IsHealthy()` and the sample API maps `/health`; packaged Kubernetes/Azure health-check wiring is not implemented yet.
- **Full Native AOT support for the typed client** — Remove or replace reflection, expression compilation, and reflection-based JSON paths.
- **ProjectReference analyzer flow for source generation** — Ensure the `[QvecIndexed]` generator is available when consuming `Qvec.Core.Client` through project references.
- **Published benchmark methodology** — ✅ Done. `benchmarks/Qvec.Benchmarks` measures recall vs. QPS against the TexMex SIFT/GIST corpora and their published ground truth.
- **Faster index construction** — Partly done: removing marshalling, pool and allocation overhead from the insert path took SIFT-1M from 242 to 362 inserts/s and doubled query throughput. What remains is algorithmic (the O(M0²) neighbour heuristic) and memory-bound; parallel construction and multi-accumulator SIMD kernels are the next candidates.
- **Multi-vector support** — Store and search multiple embeddings, such as image + text, for one logical entry.

## License

This project is licensed under the Apache License 2.0.

## Commercial Use

This software is free to use in commercial applications under the terms of the Apache 2.0 license.
