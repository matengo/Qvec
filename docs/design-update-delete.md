# Design: Update & Delete with Guid-based document IDs

## Background

Qvec currently uses an **append-only** file format where each document is assigned a sequential `int` index that maps directly to a physical position in the memory-mapped file. There is no mechanism for deleting or updating individual documents.

With [Guid as logical document ID](design-guid-id.md), we get a stable external reference that makes it natural to offer `Update(Guid, ...)` and `Delete(Guid)`.

## Challenge: append-only + HNSW graph

Physically moving or deleting data in the middle of a memory-mapped file is expensive:
- All subsequent indices would shift ? all neighbor references in the HNSW graph become invalid.
- Compaction requires rewriting the entire file.

Therefore, we choose a **tombstone-based soft-delete**, which is the standard pattern for this type of data structure.

## Design

### New section: Tombstone bit field

A new section is added to the file format. Each document is represented by a single byte (1 = deleted, 0 = active). This provides fast lookup and is alignment-friendly.

```
??????????????????????????  0
?  Header (1024 bytes)   ?
??????????????????????????  HeaderSize
?  Vector Section        ?  max * dim * 4 bytes
??????????????????????????
?  Graph Section         ?  max * maxLayers * maxNeighbors * 4 bytes
??????????????????????????
?  Metadata Section      ?  max * 512 bytes
??????????????????????????
?  Guid Section          ?  max * 16 bytes
??????????????????????????  ? NEW
?  Tombstone Section     ?  max * 1 byte
??????????????????????????
```

### New field in DbHeader

```csharp
public struct DbHeader
{
    // ... existing fields ...
    public int DeletedCount;  // Number of soft-deleted documents
}
```

`ActiveCount` can always be calculated as `CurrentCount - DeletedCount`.

### In-memory state

```csharp
private readonly long _tombstoneSectionOffset;
private readonly HashSet<int> _deletedIndices = new();
```

At startup, the tombstone section is loaded and `_deletedIndices` is built up.

---

## Delete

### Public API

```csharp
public bool Delete(Guid id)
```

### Steps

1. **Look up Guid ? int index** via `_guidIndex`. Return `false` if the Guid does not exist.
2. **Mark as deleted:**
   - Write `1` to the tombstone section on disk for that index.
   - Add to `_deletedIndices`.
   - Remove from `_guidIndex`.
3. **Clear neighbor references:**
   - Reset the deleted node's neighbors in all HNSW layers (write `-1`).
   - Iterate all neighbors that pointed back to the deleted node and remove the reference.
4. **Handle EntryPoint:**
   - If the deleted node was `EntryPoint`, choose a new EntryPoint among remaining neighbors or via linear scan.
5. **Update header:**
   - `DeletedCount++`.
   - Flush header to disk.

### Neighbor cleanup in detail

```csharp
private void DisconnectNode(int deletedIndex)
{
    int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
    try
    {
        for (int level = 0; level < _header.MaxLayers; level++)
        {
            // Get all neighbors of the deleted node
            GetNeighborsAtLevel(deletedIndex, level, neighbors);

            for (int j = 0; j < _header.MaxNeighbors; j++)
            {
                if (neighbors[j] == -1) break;
                int neighborId = neighbors[j];

                // Remove deletedIndex from the neighbor's neighbor list
                RemoveNeighborReference(neighborId, level, deletedIndex);
            }

            // Reset the deleted node's own neighbors
            InitNeighborsAtLevel(deletedIndex, level);
        }
    }
    finally
    {
        ArrayPool<int>.Shared.Return(neighbors);
    }
}

private void RemoveNeighborReference(int nodeIndex, int level, int targetToRemove)
{
    int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
    try
    {
        GetNeighborsAtLevel(nodeIndex, level, neighbors);

        for (int i = 0; i < _header.MaxNeighbors; i++)
        {
            if (neighbors[i] == targetToRemove)
            {
                // Shift the remaining neighbors one step to the left
                for (int k = i; k < _header.MaxNeighbors - 1; k++)
                    neighbors[k] = neighbors[k + 1];
                neighbors[_header.MaxNeighbors - 1] = -1;
                WriteNeighborsAtLevel(nodeIndex, level, neighbors);
                return;
            }
        }
    }
    finally
    {
        ArrayPool<int>.Shared.Return(neighbors);
    }
}
```

### Filter out tombstones in search

All search methods must skip deleted nodes. Existing `CalculateScore` and loop logic are changed:

```csharp
// In SearchLayerNearest, after visited.Add(neighbor):
if (_deletedIndices.Contains(neighbor)) continue;

// In SearchSimple/SearchSimpleParallel:
if (_deletedIndices.Contains(i)) continue;
```

This is an inexpensive `HashSet<int>.Contains` — O(1) per check.

---

## Update

### Public API

```csharp
public bool UpdateVector(Guid id, float[] newVector)
public bool UpdateMetadata(Guid id, string newMetadata)
public bool Update(Guid id, float[] newVector, string newMetadata)
```

### Strategy: In-place for metadata, Delete+Re-insert for vector

**Metadata update** (cheap):
- Look up Guid ? int index.
- Overwrite the metadata slot directly — it has a fixed size (512 bytes).
- No graph changes are needed.

**Vector update** (requires graph rewrite):
- The vector affects all neighbor relations in the HNSW graph.
- The simplest correct strategy: **soft-delete + re-insert**.

```csharp
public bool Update(Guid id, float[] newVector, string newMetadata)
{
    _lock.EnterWriteLock();
    try
    {
        if (!_guidIndex.TryGetValue(id, out int oldIndex))
            return false;

        // Metadata-only? Write directly.
        if (newVector == null)
        {
            WriteMetadataToDisk(oldIndex, newMetadata);
            return true;
        }

        // Vector changed ? delete + re-insert with the same Guid
        string metadata = newMetadata ?? GetMetadata(oldIndex);
        SoftDelete(oldIndex);
        AddEntry(newVector, metadata, externalId: id);
        return true;
    }
    finally { _lock.ExitWriteLock(); }
}
```

### UpdateMetadata — in-place

```csharp
public bool UpdateMetadata(Guid id, string newMetadata)
{
    _lock.EnterWriteLock();
    try
    {
        if (!_guidIndex.TryGetValue(id, out int index))
            return false;

        WriteMetadataToDisk(index, newMetadata);
        return true;
    }
    finally { _lock.ExitWriteLock(); }
}
```

---

## Compaction (Vacuum)

Soft-delete leaves "holes" in the file. With many deletes, storage is wasted and search performance degrades (more nodes to skip).

### Public API

```csharp
public void Vacuum()
```

### Steps

1. Create a new temporary database file.
2. Iterate all active entries (skip `_deletedIndices`).
3. Copy vector, metadata, and Guid to the new file via `AddEntry(..., externalId: guid)`.
4. The HNSW graph is rebuilt automatically by `AddEntry/ConnectNewNode`.
5. Replace the old file with the new one (atomically if possible).
6. Reset `DeletedCount`.

### When should Vacuum be run?

| Strategy | Trigger |
|---|---|
| Manual | The user calls `Vacuum()` explicitly |
| Automatic | When `DeletedCount / CurrentCount > threshold` (e.g. 25%) |
| Scheduled | In a background service during low load |

---

## Affected files

| File | Change |
|---|---|
| `Qvec.Core\QvecDatabase.cs` | Tombstone section, `Delete`, `Update*`, `Vacuum`, neighbor cleanup, search filtering |
| `Qvec.Core\PartitionedQvecDatabase.cs` | Propagate `Delete`/`Update` by finding the correct partition via Guid |
| `Qvec.Core.Client\QvecClient.cs` | `UpdateEntry`, `DeleteEntry` with typed methods |
| `Qvec.Api\*` | `PUT /vectors/{guid}`, `DELETE /vectors/{guid}` endpoints |
| `Qvec.Console.Test\*` | Tests for delete, update, vacuum, EntryPoint migration |

## Performance budget

| Operation | Cost |
|---|---|
| `Delete` | O(M × L) where M = maxNeighbors, L = maxLayers — clear neighbors |
| `UpdateMetadata` | O(1) — direct overwrite of 512-byte slot |
| `UpdateVector` | O(Delete) + O(AddEntry) — soft-delete + re-insert |
| `Vacuum` | O(N × AddEntry) — full rebuild, run rarely |
| Search overhead | +1 `HashSet.Contains` per visited node — negligible |
| Storage | +1 byte/entry for tombstone, +4 bytes in header for `DeletedCount` |

## Correctness guarantees

- **HNSW graph integrity:** `DisconnectNode` ensures that no neighbors point to a deleted node. Search can never reach a tombstoned node via neighbor links.
- **Guid stability:** A document that is updated with a new vector keeps its Guid. External systems that cached the Guid do not need to be updated.
- **Thread safety:** All mutations occur under `WriteLock`. Read operations filter out `_deletedIndices` under `ReadLock`.
- **Persistence:** Tombstones are written to disk before the header is updated, which guarantees that a crash can never hide a delete.

## Depends on

- [Design: Guid as document ID](design-guid-id.md) — Guid infrastructure must be implemented first.