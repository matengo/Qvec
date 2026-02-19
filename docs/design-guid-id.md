# Design: Guid som dokument-ID

## Bakgrund

Idag identifieras varje dokument av ett sekventiellt `int`-index som direkt mappar till en fysisk position i den minnesmappade filen:

```
offset = index * dimension * sizeof(float)
```

Det ger O(1)-åtkomst utan lookup, men gör det omöjligt att synkronisera eller merga flera databaser — ett index i databas A har ingen relation till samma index i databas B.

## Mål

Införa `Guid` som **logiskt dokument-ID** medan `int` behålls som **fysiskt positionsindex**. Detta möjliggör:

- Deterministisk deduplicering vid synk mellan noder
- Idempotent `AddEntry` (samma Guid = samma dokument)
- Stabil extern referens som överlever rebuild/kompaktering

## Jämförelse

| Aspekt | `int` index (nuläge) | `Guid` + `int` index (nytt) |
|---|---|---|
| Lagring per post | 0 extra bytes | +16 bytes (Guid) |
| Lookup by ID | O(1) direkt offset | O(1) via `Dictionary<Guid, int>` |
| Synk/merge | Omöjligt (index är lokala) | Naturlig dedup via Guid |
| Minnesoverhead | Inget | ~40 bytes/post i dictionary |
| Uppstartstid | Direkt | Linjärt scan för att bygga Guid?index-map |

## Filformat: ny Guid-sektion

En ny sektion läggs till efter metadata-sektionen. Varje post är exakt 16 bytes (`sizeof(Guid)`).

```
??????????????????????????  0
?  Header (1024 bytes)   ?
??????????????????????????  HeaderSize
?  Vector Section        ?  max * dim * 4 bytes
??????????????????????????
?  Graph Section         ?  max * maxLayers * maxNeighbors * 4 bytes
??????????????????????????
?  Metadata Section      ?  max * 512 bytes
??????????????????????????  ? NYTT
?  Guid Section          ?  max * 16 bytes
??????????????????????????
```

`DbHeader.Version` bumpas till `2` för att skilja det nya formatet.

## Ändringar i kod

### 1. Nya fält i `QvecDatabase`

```csharp
private const int GuidSize = 16;
private readonly long _guidSectionOffset;
private readonly Dictionary<Guid, int> _guidIndex = new();
```

### 2. Konstruktor — beräkna Guid-sektion och bygga index

```csharp
_guidSectionOffset = _metadataSectionOffset + metadataSpace;
long guidSpace = (long)max * GuidSize;
long totalSize = _guidSectionOffset + guidSpace;

// Vid uppstart av befintlig databas:
if (exists)
{
    _headerAccessor.Read(0, out _header);
    RebuildGuidIndex();
}
```

### 3. Disk I/O för Guid

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

### 4. Index-rebuild vid uppstart

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

### 5. Ändrad `AddEntry` — returnerar Guid, stödjer extern Guid

```csharp
public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
{
    _lock.EnterWriteLock();
    try
    {
        if (_header.CurrentCount >= _header.MaxCount) throw new Exception("DB Full");

        Guid docId = externalId ?? Guid.NewGuid();

        // Dedup: om Guid redan finns, hoppa över
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

        // ... resten av HNSW-logiken oförändrad ...

        _headerAccessor.Write(0, ref _header);
        return docId;
    }
    finally { _lock.ExitWriteLock(); }
}
```

### 6. Sökresultat returnerar Guid

Alla publika `Search`-metoder ändrar sin returtyp:

```csharp
// Före:
List<(int Id, float Score, string Metadata)>

// Efter:
List<(Guid Id, float Score, string Metadata)>
```

Internt används fortfarande `int` för all graf-navigering och vektor-åtkomst.

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

### 8. Synk mellan databaser

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

## Påverkade filer

| Fil | Ändring |
|---|---|
| `Qvec.Core\QvecDatabase.cs` | Ny sektion, Guid-fält, ändrad `AddEntry`, nya metoder |
| `Qvec.Core\PartitionedQvecDatabase.cs` | Propagera Guid genom partitioner |
| `Qvec.Core.Client\QvecClient.cs` | Uppdatera returtyper till Guid |
| `Qvec.Api\*` | Uppdatera API-endpoints att returnera Guid |
| `Qvec.Console.Test\*` | Uppdatera tester |

## Bakåtkompatibilitet

- Filer med `Version == 1` saknar Guid-sektionen. Vid öppning av en v1-fil kan vi antingen:
  - **Migrera:** Generera Guid:s för alla befintliga poster och bumpa version till 2.
  - **Avvisa:** Kasta ett undantag som uppmanar till manuell migrering.
- Rekommendation: automatisk migrering vid första öppning.

## Prestandabudget

| Operation | Kostnad |
|---|---|
| `AddEntry` | +1 `WriteArray` (16 bytes) — försumbart |
| `Search` | +1 `ReadGuidFromDisk` per resultat (topK st) — försumbart |
| Uppstart (1M poster) | ~16 MB sekventiell läsning + dictionary-allokering ? <100 ms |
| Minne | ~56 bytes/post (16 Guid + 40 dictionary entry) ? ~56 MB vid 1M poster |
