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

## 📊 Performance Benchmark (10,000 Vectors)

| Method | Search Time | Throughput | Recall |
| :--- | :--- | :--- | :--- |
| **Linear Search** | 4.65 ms | ~215 QPS | 100% |
| **Qvec HNSW** | **0.0045 ms** | **~220,000 QPS** | **100%** |

*Test conducted on .NET 10 Native AOT (128-dim vectors).*

---

## 🛠 Installation

Since Qvec is designed for high-performance embedding, simply include the `VectorDatabase.cs` core in your .NET 10 project.

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <OptimizationPreference>Speed</OptimizationPreference>
</PropertyGroup>
```

## 💻 Quick Start

## Initialize and Add Data

```code
using Qvec;

// Initialize DB (1536 dims for OpenAI, max 1M vectors)
using var db = new VectorDatabase("vectors.qvec", dim: 1536, max: 1000000);

float[] myEmbedding = GetEmbedding("Hello World");
db.AddEntry(myEmbedding, "{\"id\": 1, \"category\": \"text\"}");
```

## Hybrid HNSW Search

```code
var results = db.SearchHybridHNSW(queryVector, meta => {
    return meta.Contains("\"category\": \"text\"");
}, topK: 5);

foreach (var r in results) {
    Console.WriteLine($"Found Match: {r.Id} with Score: {r.Score}");
}
```

## 🏗 Architecture

Header: Stores metadata, EntryPoint, and layer distribution.
Vector Store: Contiguous float arrays stored via MemoryMappedFiles.
Graph Store: Hierarchical adjacency lists (HNSW layers) mapped to disk.
Metadata Store: Fixed-size UTF-8 slots for rapid scalar filtering.

## ☁️ Cloud Readiness

Qvec is built for modern cloud environments:
Chiseled Containers: Run on ~20MB Docker images for Azure Container Apps.
Managed Identity: Connect to Azure Storage without connection strings.
Health Checks: Built-in /health endpoints for Kubernetes/Azure liveness probes.

## 📜 Roadmap

HNSW Multi-layer Indexing
Hybrid Search (Pre-filtering)
Native AOT Support
Multi-Vector Support (Image + Text in one entry)
Product Quantization (PQ) for 4x memory reduction
SQLite Extension (Virtual Table)
