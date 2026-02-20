# Qvec ⚡ 
### The "SQLite of Vector Databases" for .NET 10

**Qvec** is an open-source, embedded, high-performance vector database written entirely in C# for **.NET 10**. It is designed to be the fastest local vector store for AI-driven applications, offering **HNSW** (Hierarchical Navigable Small World) indexing with **Native AOT** support.

Unlike client-server vector DBs, Qvec runs in-process, utilizing **MemoryMappedFiles** for zero-copy disk access and **SIMD** for hardware-accelerated vector mathematics.

---

## 🚀 Key Features

*   **Native AOT Ready:** Compiled to a single, dependency-free binary for Windows, Linux, and macOS.
*   **HNSW Indexing:** Logarithmic search complexity ($O(\log N)$) providing up to 1000x speedup over linear scanning.
*   **Hybrid Search:** Integrated metadata filtering ($AI \text{ Similarity} + \text{Scalar Filters}$) directly within the HNSW navigation loop.
*   **Zero-Copy Storage:** Built on `MemoryMappedFiles` for persistent, disk-backed storage that survives application restarts.
*   **Hardware Accelerated:** Uses .NET 10 `Vector<T>` and `Intrinsics` to leverage **AVX2 / NEON** SIMD instructions.
*   **Cloud-Native:** Built-in support for **Azure Blob Storage** synchronization and **Managed Identity** for passwordless security.

---

## 📊 Performance Benchmark (1M Vectors)

| Method | Search Time | Throughput | Recall |
| :--- | :--- | :--- | :--- |
| **Linear Search** | 4.65 ms | ~215 QPS | 100% |
| **Qvec HNSW** | **0.110 ms** | **~9,070 QPS** | **98%** |

*Test conducted on .NET 10 Native AOT (128-dim vectors). Surface Laptop Windows 11 ARM64, Qualcomm Snapdragon X Elite (X1E-80100)*

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

```C#
using Qvec;

// Initialize DB (1536 dims for OpenAI, max 1M vectors)
using var db = new VectorDatabase("vectors.qvec", dim: 1536, max: 1000000);

float[] myEmbedding = GetEmbedding("Hello World");
db.AddEntry(myEmbedding, "{\"id\": 1, \"category\": \"text\"}");
```
## HNSW Vector Search

```c#
var results = db.Search(queryVector, topK: 5);

foreach (var r in results) {
    Console.WriteLine($"Found Match: {r.Id} with Score: {r.Score}");
}
```

## Hybrid HNSW Search

```c#
var results = db.Search(queryVector, meta => {
    return meta.Contains("\"category\": \"text\"");
}, topK: 5);

foreach (var r in results) {
    Console.WriteLine($"Found Match: {r.Id} with Score: {r.Score}");
}
```

## Typed client

```c#
var db = new QvecDatabase("products.qvec", dim: 1536, max: 10000);
var client = new QvecClient<Product>(db);

// Add entry
client.AddEntry(myVector, new Product(1, "Laptop", 12000, true));

// Hybrid search with strongly typed filter
var results = client.Search(queryVector, p => p.Price < 15000 && p.InStock);

foreach (var r in results)
{
    Console.WriteLine($"{r.Item.Name}: {r.Score}");
}
```

## Typed client (AOT)

### 1. for AOT define object serialization
```c#
// define a class
public record Product(int Id, string Name, double Price, bool InStock);

// Source Generator for JSON (needed for AOT)
[JsonSerializable(typeof(Product))]
internal partial class ProductJsonContext : JsonSerializerContext { }
```

### 2. Use typed client
```c#
var db = new QvecDatabase("products.qvec", dim: 1536, max: 10000);
var client = new QvecClient<Product>(db, ProductJsonContext.Default.Product);

// Add entry
client.AddEntry(myVector, new Product(1, "Laptop", 12000, true));

// Hybrid search
var results = client.Search(queryVector, p => p.Price < 15000 && p.InStock);
```

## Indexed Filtering with `[QvecIndexed]`

Qvec supports **O(1) metadata filtering** via an in-memory inverted index. Mark properties with `[QvecIndexed]` and Qvec will automatically build and maintain an index — no full scan, no JSON parsing at query time.

### 1. Mark properties to index

```c#
using Qvec.Core;

public class Product
{
    [QvecIndexed]
    public string Category { get; set; }

    [QvecIndexed]
    public string Brand { get; set; }

    public string Description { get; set; }  // not indexed — Where falls back to scan
    public double Price { get; set; }
}
```

### 2. Create the client with the extractor

The source generator is bundled with `Qvec.Core.Client` — no extra package references needed. It automatically generates a `ProductFieldExtractor` class that implements `IQvecFieldExtractor<Product>`.

```c#
var db = new QvecDatabase("products.qvec", dim: 1536, max: 10000);
var client = new QvecClient<Product>(db, new ProductFieldExtractor());
```

The inverted index is rebuilt from disk at startup and kept in sync on every insert and delete.

### 3. Query with `Where`

`Where` accepts an `Expression<Func<T, bool>>`. If the expression consists of `==` comparisons on indexed properties, the inverted index is used automatically. Everything else falls back to a full scan — same syntax either way.

```c#
// O(1) — single indexed field lookup
var science = client.Where(p => p.Category == "Science");

// O(1) — compound AND, uses HashSet intersection
var acmeScience = client.Where(p => p.Category == "Science" && p.Brand == "Acme");

// O(1) — captured variables work too
string cat = "Science";
var results = client.Where(p => p.Category == cat);

// Automatic fallback to full scan for non-indexed or complex expressions
var cheap = client.Where(p => p.Description.Contains("quantum"));
```

| Expression | Strategy | Complexity |
| :--- | :--- | :--- |
| `p => p.Category == "Science"` | Inverted index | **O(1)** |
| `p => p.Category == "Science" && p.Brand == "Acme"` | Index intersection | **O(1)** |
| `p => p.Price < 100` | Full scan (fallback) | O(N) |
| `p => p.Description.Contains("x")` | Full scan (fallback) | O(N) |

### Hybrid Search with Indexed Filtering

The same expression analysis works for `Search` (vector + filter). When the filter uses `==` on indexed properties, Qvec pre-filters via the inverted index and computes vector similarity **only on matching entries** — no HNSW post-filtering, no wasted similarity calculations:

```c#
// Pre-filtered: vector similarity computed only for Category == "Science" entries
var results = client.Search(queryVector, p => p.Category == "Science", topK: 5);

// Compound: intersection first, then vector ranking over the small candidate set
var results = client.Search(queryVector,
    p => p.Category == "Science" && p.Brand == "Acme", topK: 5);

// Non-indexed filter: falls back to HNSW + post-filter automatically
var results = client.Search(queryVector, p => p.Price < 100, topK: 5);
```

| Scenario | Without index | With `[QvecIndexed]` |
| :--- | :--- | :--- |
| 1M entries, 1% match filter | HNSW finds 50 candidates → filter → **~0 results** | Index → 10K candidates → rank → **5 perfect results** |
| 1M entries, 50% match filter | HNSW + post-filter works OK | Index → 500K candidates (fallback to HNSW is better) |

### Combining with AOT

Pass both the JSON type info and the extractor:

```c#
var client = new QvecClient<Product>(
    db,
    ProductJsonContext.Default.Product,
    new ProductFieldExtractor());
```


## 🎯 Use Cases

Qvec is built as an **embedded** vector database — no server, no network overhead, just a library running in your process. This makes it ideal for scenarios where low latency, offline capability, and small footprint matter:

| Scenario | Why Qvec? |
| :--- | :--- |
| **AI Agents on the Edge** | Run RAG-powered agents on IoT gateways, factory floors, or retail kiosks without depending on cloud connectivity. |
| **Agent Memory** | Give autonomous agents persistent, searchable long-term memory that lives alongside the agent process. |
| **Embedded / Industrial Software** | Native AOT + MemoryMappedFiles keeps the footprint tiny — perfect for instruments, PLCs, and headless services. |
| **Mobile & Tablet Apps** | Ship a local vector store inside .NET MAUI or Uno Platform apps for offline semantic search. |
| **Desktop Copilots & Plugins** | Add similarity search to WPF / WinUI / Avalonia apps — no Docker, no external service. |
| **Serverless & Functions** | Cold-start friendly: a single-file AOT binary boots instantly in Azure Functions or AWS Lambda. |
| **Privacy-Sensitive Workloads** | Keep embeddings on-device for healthcare, legal, or finance apps where data must never leave the machine. |
| **Rapid Prototyping** | One NuGet reference, zero infrastructure — go from idea to working vector search in minutes. |

---

## 🏗 Architecture

1. **Header:** Stores metadata, EntryPoint, and layer distribution.
2. **Vector Store:** Contiguous float arrays stored via MemoryMappedFiles.
3. **Graph Store:** Hierarchical adjacency lists (HNSW layers) mapped to disk.
4. **Metadata Store:** Fixed-size UTF-8 slots for rapid scalar filtering.

## ☁️ Cloud Readiness

Qvec is built for modern cloud environments:
- **Chiseled Containers:** Run on ~20MB Docker images for Azure Container Apps.
- **Health Checks:** Built-in /health endpoints for Kubernetes/Azure liveness probes.

## 📜 Roadmap

- ~~**Guid Document IDs** — Replace sequential `int` IDs with `Guid` as the logical document identifier to enable multi-database sync, deduplication, and stable external references. Internal storage remains index-based for zero-overhead disk access. See [design doc](docs/design-guid-id.md).~~
- ~~**Update & Delete** — Tombstone-based soft-delete with HNSW graph repair, in-place metadata updates, and delete+re-insert for vector updates. Includes `Vacuum()` for storage reclamation. See [design doc](docs/design-update-delete.md).~~
- **Sync Engine** — Opt-in edge-cloud synchronization. Connect multiple local Qvec databases to a central sync server so all connected instances stay in sync automatically. Offline-first with delta-sync via Azure Append Blob and real-time push via Azure Web PubSub. See [design doc](docs/design-sync-engine.md).
- Multi-Vector Support (Image + Text in one entry)

## License

This project is licensed under the Apache License 2.0.

## Commercial Use

This software is free to use in commercial applications under the terms of the Apache 2.0 license.
