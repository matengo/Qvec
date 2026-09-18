# Design: Guid as document ID

## Background

Today, each document is identified by a sequential `int` index that maps directly to a physical position in the memory-mapped file:

```
offset = index * dimension * sizeof(float)
```

This gives O(1) access without lookup, but makes it impossible to synchronize or merge multiple databases — an index in database A has no relation to the same index in database B.

## Goals

Introduce `Guid` as the **logical document ID** while `int` is kept as the **physical position index**. This enables:

- Deterministic deduplication when syncing between nodes
- Idempotent `AddEntry` (same Guid = same document)
- Stable external reference that survives rebuild/compaction

## Comparison

| Aspect | `int` index (current state) | `Guid` + `int` index (new) |
|---|---|---|
| Storage per entry | 0 extra bytes | +16 bytes (Guid) |
| Lookup by ID | O(1) direct offset | O(1) via `Dictionary<Guid, int>` |
| Sync/merge | Impossible (indices are local) | Natural dedup via Guid |
| Memory overhead | None | ~40 bytes/entry in dictionary |
| Startup time | Direct | Linear scan to build Guid?index map |

## File format: new Guid section

A new section is added after the metadata section. Each entry is exactly 16 bytes (`sizeof(Guid)`).

```
??????????????????????????  0
?  Header (1024 bytes)   ?
??????????????????????????  HeaderSize
?  Vector Section        ?  max * dim * 4 bytes
??????????????????????????
?  Graph Section         ?  max * maxLayers * maxNeighbors * 4 bytes
??????????????????????????
?  Metadata Section      ?  max * 512 bytes
??????????????????????????  ? NEW
?  Guid Section          ?  max * 16 bytes
??????????????????????????
```

`DbHeader.Version` is bumped to `2` to distinguish the new format.

## Code changes

### 1. New fields in `QvecDatabase`

```csharp
private const int GuidSize = 16;
private readonly long _guidSectionOffset;
private readonly Dictionary<Guid, int> _guidIndex = new();
```

### 2. Constructor — calculate Guid section and build index

```csharp
_guidSectionOffset = _metadataSectionOffset + metadataSpace;
long guidSpace = (long)max * GuidSize;
long totalSize = _guidSectionOffset + guidSpace;

// On startup of an existing database:
if (exists)
{
    _headerAccessor.Read(0, out _header);
    RebuildGuidIndex();
}
```

### 3. Disk I/O for Guid

```csharp
private void WriteGuidToDisk(int index, Guid guid)
{
    long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
    byte[] bytes = guid.ToByteArray();
    _dataAccessor.WriteArray(offset, bytes, 0, GuidSize);
}

private Guid ReadGuidFromDisk(int index)
{
    byte[] bytes = new byte[GuidSize];
    long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
    _dataAccessor.ReadArray(offset, bytes, 0, GuidSize);
    return new Guid(bytes);
}
```

### 4. Index rebuild at startup

```csharp
private void RebuildGuidIndex()
{
    _guidIndex.Clear();
    _guidIndex.EnsureCapacity(_header.CurrentCount);
    for (int i = 0; i < _header.CurrentCount; i++)
    {
        Guid id = ReadGuidFromDisk(i);
        _guidIndex[id] = i;
    }
}
```

### 5. Changed `AddEntry` — returns Guid, supports external Guid

```csharp
public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
{
    _lock.EnterWriteLock();
    try
    {
        if (_header.CurrentCount >= _header.MaxCount) throw new Exception("DB Full");

        Guid docId = externalId ?? Guid.NewGuid();

        // Dedup: if the Guid already exists, skip
        if (_guidIndex.ContainsKey(docId))
            return docId;

        int index = _header.CurrentCount;
        int level = RandomLayer();

        WriteVectorToDisk(index, vector);
        WriteMetadataToDisk(index, metadata);
        WriteGuidToDisk(index, docId);
        InitNeighborsOnDisk(index);

        _guidIndex[docId] = index;
        _header.CurrentCount++;

        // ... rest of the HNSW logic unchanged ...

        _headerAccessor.Write(0, ref _header);
        return docId;
    }
    finally { _lock.ExitWriteLock(); }
}
```

### 6. Search results return Guid

All public `Search` methods change their return type:

```csharp
// Before:
List<(int Id, float Score, string Metadata)>

// After:
List<(Guid Id, float Score, string Metadata)>
```

Internally, `int` is still used for all graph navigation and vector access.

### 7. Lookup via Guid

```csharp
public (float[] Vector, string Metadata)? GetByGuid(Guid id)
{
    _lock.EnterReadLock();
    try
    {
        if (!_guidIndex.TryGetValue(id, out int index))
            return null;
        return (GetVector(index), GetMetadata(index));
    }
    finally { _lock.ExitReadLock(); }
}
```

### 8. Sync between databases

```csharp
public int SyncFrom(QvecDatabase source)
{
    int synced = 0;
    for (int i = 0; i < source._header.CurrentCount; i++)
    {
        Guid docId = source.ReadGuidFromDisk(i);
        if (_guidIndex.ContainsKey(docId))
            continue;

        float[] vector = source.GetVector(i);
        string metadata = source.GetMetadata(i);
        AddEntry(vector, metadata, externalId: docId);
        synced++;
    }
    return synced;
}
```

## Affected files

| File | Change |
|---|---|
| `Qvec.Core\QvecDatabase.cs` | New section, Guid field, changed `AddEntry`, new methods |
| `Qvec.Core\PartitionedQvecDatabase.cs` | Propagate Guid through partitions |
| `Qvec.Core.Client\QvecClient.cs` | Update return types to Guid |
| `Qvec.Api\*` | Update API endpoints to return Guid |
| `Qvec.Console.Test\*` | Update tests |

## Backward compatibility

- Files with `Version == 1` lack the Guid section. When opening a v1 file, we can either:
  - **Migrate:** Generate Guids for all existing entries and bump the version to 2.
  - **Reject:** Throw an exception that prompts manual migration.
- Recommendation: automatic migration on first open.

## Performance budget

| Operation | Cost |
|---|---|
| `AddEntry` | +1 `WriteArray` (16 bytes) — negligible |
| `Search` | +1 `ReadGuidFromDisk` per result (topK items) — negligible |
| Startup (1M entries) | ~16 MB sequential read + dictionary allocation ? <100 ms |
| Memory | ~56 bytes/entry (16 Guid + 40 dictionary entry) ? ~56 MB at 1M entries |