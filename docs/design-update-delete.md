# Design: Update & Delete med Guid-baserade dokument-ID

## Bakgrund

Qvec använder idag ett **append-only** filformat där varje dokument tilldelas ett sekventiellt `int`-index som direkt mappar till en fysisk position i den minnesmappade filen. Det finns ingen mekanism för att ta bort eller uppdatera enskilda dokument.

Med [Guid som logiskt dokument-ID](design-guid-id.md) får vi en stabil extern referens som gör det naturligt att erbjuda `Update(Guid, ...)` och `Delete(Guid)`.

## Utmaning: append-only + HNSW-graf

Att fysiskt flytta eller ta bort data i mitten av en memory-mapped fil är kostsamt:
- Alla efterföljande index skulle förskjutas ? alla grannreferenser i HNSW-grafen blir ogiltiga.
- Kompaktering kräver omskrivning av hela filen.

Därför väljer vi en **tombstone-baserad soft-delete** som är standardmönstret för denna typ av datastruktur.

## Design

### Ny sektion: Tombstone-bitfält

En ny sektion läggs till i filformatet. Varje dokument representeras av en enda byte (1 = borttagen, 0 = aktiv). Detta ger snabb lookup och är alignment-vänligt.

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
??????????????????????????  ? NYTT
?  Tombstone Section     ?  max * 1 byte
??????????????????????????
```

### Nytt fält i DbHeader

```csharp
public struct DbHeader
{
    // ... befintliga fält ...
    public int DeletedCount;  // Antal soft-deleted dokument
}
```

`ActiveCount` kan alltid beräknas som `CurrentCount - DeletedCount`.

### In-memory state

```csharp
private readonly long _tombstoneSectionOffset;
private readonly HashSet<int> _deletedIndices = new();
```

Vid uppstart laddas tombstone-sektionen och `_deletedIndices` byggs upp.

---

## Delete

### Publikt API

```csharp
public bool Delete(Guid id)
```

### Steg

1. **Slå upp Guid ? int index** via `_guidIndex`. Returnera `false` om Guid inte finns.
2. **Markera som deleted:**
   - Skriv `1` till tombstone-sektionen på disk för det indexet.
   - Lägg till i `_deletedIndices`.
   - Ta bort från `_guidIndex`.
3. **Rensa grannreferenser:**
   - Nollställ den borttagna nodens grannar i alla HNSW-lager (skriv `-1`).
   - Iterera alla grannar som pekade tillbaka till den borttagna noden och ta bort referensen.
4. **Hantera EntryPoint:**
   - Om den borttagna noden var `EntryPoint`, välj en ny EntryPoint bland kvarvarande grannar eller via linjärt scan.
5. **Uppdatera header:**
   - `DeletedCount++`.
   - Flush header till disk.

### Grannrensning i detalj

```csharp
private void DisconnectNode(int deletedIndex)
{
    int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
    try
    {
        for (int level = 0; level < _header.MaxLayers; level++)
        {
            // Hämta alla grannar till den borttagna noden
            GetNeighborsAtLevel(deletedIndex, level, neighbors);

            for (int j = 0; j < _header.MaxNeighbors; j++)
            {
                if (neighbors[j] == -1) break;
                int neighborId = neighbors[j];

                // Ta bort deletedIndex från grannens grannlista
                RemoveNeighborReference(neighborId, level, deletedIndex);
            }

            // Nollställ den borttagna nodens egna grannar
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
                // Skifta resterande grannar ett steg åt vänster
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

### Filtrera bort tombstones i sökning

Alla sökmetoder måste hoppa över borttagna noder. Befintlig `CalculateScore` och loop-logik ändras:

```csharp
// I SearchLayerNearest, efter visited.Add(neighbor):
if (_deletedIndices.Contains(neighbor)) continue;

// I SearchSimple/SearchSimpleParallel:
if (_deletedIndices.Contains(i)) continue;
```

Detta är en billig `HashSet<int>.Contains` — O(1) per check.

---

## Update

### Publikt API

```csharp
public bool UpdateVector(Guid id, float[] newVector)
public bool UpdateMetadata(Guid id, string newMetadata)
public bool Update(Guid id, float[] newVector, string newMetadata)
```

### Strategi: In-place för metadata, Delete+Re-insert för vektor

**Metadata-uppdatering** (billig):
- Slå upp Guid ? int index.
- Skriv över metadata-sloten direkt — den har fast storlek (512 bytes).
- Inga grafändringar behövs.

**Vektor-uppdatering** (kräver graf-omskrivning):
- Vektorn påverkar alla grannrelationer i HNSW-grafen.
- Enklaste korrekta strategin: **soft-delete + re-insert**.

```csharp
public bool Update(Guid id, float[] newVector, string newMetadata)
{
    _lock.EnterWriteLock();
    try
    {
        if (!_guidIndex.TryGetValue(id, out int oldIndex))
            return false;

        // Metadata-only? Skriv direkt.
        if (newVector == null)
        {
            WriteMetadataToDisk(oldIndex, newMetadata);
            return true;
        }

        // Vektor ändrad ? delete + re-insert med samma Guid
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

## Kompaktering (Vacuum)

Soft-delete lämnar "hål" i filen. Vid många deletes slösas lagring och sökprestanda försämras (fler noder att hoppa över).

### Publikt API

```csharp
public void Vacuum()
```

### Steg

1. Skapa en ny temporär databasfil.
2. Iterera alla aktiva poster (hoppa över `_deletedIndices`).
3. Kopiera vektor, metadata och Guid till den nya filen via `AddEntry(..., externalId: guid)`.
4. HNSW-grafen byggs om automatiskt av `AddEntry/ConnectNewNode`.
5. Byt ut den gamla filen med den nya (atomiskt om möjligt).
6. Nollställ `DeletedCount`.

### När ska man köra Vacuum?

| Strategi | Trigger |
|---|---|
| Manuellt | Användaren kallar `Vacuum()` explicit |
| Automatiskt | När `DeletedCount / CurrentCount > threshold` (t.ex. 25%) |
| Schemalagt | I bakgrundstjänst vid låg last |

---

## Påverkade filer

| Fil | Ändring |
|---|---|
| `Qvec.Core\QvecDatabase.cs` | Tombstone-sektion, `Delete`, `Update*`, `Vacuum`, grannrensning, sökfiltrering |
| `Qvec.Core\PartitionedQvecDatabase.cs` | Propagera `Delete`/`Update` genom att hitta rätt partition via Guid |
| `Qvec.Core.Client\QvecClient.cs` | `UpdateEntry`, `DeleteEntry` med typade metoder |
| `Qvec.Api\*` | `PUT /vectors/{guid}`, `DELETE /vectors/{guid}` endpoints |
| `Qvec.Console.Test\*` | Tester för delete, update, vacuum, EntryPoint-migration |

## Prestandabudget

| Operation | Kostnad |
|---|---|
| `Delete` | O(M × L) där M = maxNeighbors, L = maxLayers — rensa grannar |
| `UpdateMetadata` | O(1) — direkt överskrivning av 512-byte slot |
| `UpdateVector` | O(Delete) + O(AddEntry) — soft-delete + re-insert |
| `Vacuum` | O(N × AddEntry) — full ombyggnad, körs sällan |
| Sökoverhead | +1 `HashSet.Contains` per besökt nod — försumbart |
| Lagring | +1 byte/post för tombstone, +4 bytes i header för `DeletedCount` |

## Korrekthetsgarantier

- **HNSW-grafens integritet:** `DisconnectNode` säkerställer att inga grannar pekar på en borttagen nod. Sökning kan aldrig nå en tombstoned nod via grannlänkar.
- **Guid-stabilitet:** Ett dokument som uppdateras med ny vektor behåller sin Guid. Externa system som cachelagrat Guid:t behöver inte uppdateras.
- **Trådsäkerhet:** Alla mutationer sker under `WriteLock`. Läsoperationer filtrerar bort `_deletedIndices` under `ReadLock`.
- **Persistens:** Tombstones skrivs till disk innan header uppdateras, vilket garanterar att en crash aldrig kan dölja en delete.

## Beroende på

- [Design: Guid som dokument-ID](design-guid-id.md) — Guid-infrastruktur måste implementeras först.
