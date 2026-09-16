# Design: Filformat v4

## Bakgrund

Qvec v3 har ett kompakt men hårt kodat filformat:

- `DbHeader` är 52 bytes med `[StructLayout(LayoutKind.Sequential, Pack = 1)]`.
- Header-regionen är alltid 1024 bytes.
- Alla sektioner ligger i fast ordning efter headern.
- Offset för varje sektion räknas fram med `ComputeLayout(...)` från `VectorDimension`, `MaxCount`, `MaxNeighbors` och `MaxLayers`.
- Metadata är en fast 512-byte slot per rad.
- Tombstones är en byte per rad och laddas genom scan vid uppstart.

Det har fungerat för små och fasta databaser, men v3 är inte tillräckligt självbeskrivande för ett AOT-orienterat bibliotek som ska kunna växa, återöppnas säkert och utvecklas med nya sektioner.

v4 ersätter de aritmetiskt härledda offsetarna med en explicit section table, lägger till header-integritet, metadata heap, persistent free list och reserverade formatkrokar för framtida kvantisering.

Detta dokument är en implementeringsspecifikation. Det beskriver inte en migration från v2/v3; äldre filer ska avvisas.

---

## Mål

- Headern och section table är **enda källan till sanning** för en befintlig fil.
- Konstruktorargument används bara när en ny fil skapas.
- Äldre format (`Version` 1, 2, 3) migreras inte automatiskt.
- En trasig, avbruten eller delvis skriven header ska upptäckas vid `Open(...)` och `IsHealthy()`.
- Metadata får vara större än 512 bytes och ska inte preallokera 512 MB för 1M rader.
- Tombstoned slots ska kunna återanvändas efter processrestart utan att `AllocateSlot` behöver scanna hela tombstone-sektionen.
- Filen ska kunna växa genom remap av memory-mapped file.
- Skapande ska undvika att fysiskt materialisera hela filen när filsystemet stöder sparse files.

## Icke-mål

- Ingen automatisk v2/v3 -> v4 migration.
- Ingen design av själva int8-kvantiseringen.
- Ingen multi-process writer-garanti. v4 kan tillåta flera samtidiga readers som öppnar filen på nytt, men exakt live-coherency mellan processer är inte ett mål i denna iteration.
- Ingen full datachecksum över vektorer, graf eller metadata heap. Checksumen skyddar header och section table, inte hela databasen.

---

## Byteordning och primitiva typer

Alla flervärdesfält lagras little-endian. Det matchar .NET på stödda vanliga plattformar, men implementationen ska läsa/skriva explicit med `BinaryPrimitives` eller validerad `MemoryMarshal`-layout så att offsets inte blir beroende av runtime-padding.

| Typ | Storlek | Kommentar |
|---|---:|---|
| `UInt16` | 2 | unsigned little-endian |
| `Int32` | 4 | signed little-endian |
| `UInt32` | 4 | unsigned little-endian |
| `Int64` | 8 | signed little-endian |
| `UInt64` | 8 | unsigned little-endian |
| `Double` | 8 | IEEE 754 little-endian |

Alla reserverade bytes måste skrivas som `0` vid `Create`. Vid `Open` får okända reserverade bytes ignoreras, men checksumen beräknas över dem.

---

## Översiktlig layout

v4 använder en större header-region än v3.

```
????????????????????????????????????????  0
? Primary Header (512 bytes)          ?
????????????????????????????????????????  512
? Section Table (64 * 32 = 2048 bytes)?
????????????????????????????????????????  2560
? Reserved Header Area (1536 bytes)   ?
????????????????????????????????????????  4096 = HeaderSize
? Section data, described by table     ?
?   Vector section                     ?
?   Graph section                      ?
?   Metadata descriptor section        ?
?   Metadata heap section              ?
?   Guid section                       ?
?   Tombstone section                  ?
?   Free-list section                  ?
?   Future/optional sections           ?
????????????????????????????????????????
```

`HeaderSize` för v4 är alltid `4096`.

Implementation får fortfarande använda `DbHeader`, men den ska antingen:

1. vara en explicit v4-struct med unit tests för varje offset, eller
2. ersättas med manuell header-serialisering.

Rekommendationen är manuell serialisering med namngivna offset-konstanter. Det minskar risken att en framtida field-order ändrar filformatet.

---

## Primary Header

Primary header är 512 bytes. Fälten nedan är absoluta offsetar från filens början.

| Offset | Storlek | Typ | Fältnamn | Värde / betydelse |
|---:|---:|---|---|---|
| 0 | 4 | `Int32` | `MagicNumber` | `0x5A564543` (`"ZVEC"`) |
| 4 | 4 | `Int32` | `Version` | `CurrentFormatVersion` (`5`) |
| 8 | 4 | `Int32` | `HeaderSize` | `4096` |
| 12 | 4 | `Int32` | `PrimaryHeaderSize` | `512` |
| 16 | 4 | `Int32` | `SectionTableOffset` | `512` |
| 20 | 4 | `Int32` | `SectionTableEntrySize` | `32` |
| 24 | 4 | `Int32` | `SectionTableEntryCount` | `64` |
| 28 | 4 | `UInt32` | `HeaderCrc32` | CRC-32 över header + section table, med detta fält nollat |
| 32 | 8 | `UInt64` | `Generation` | Monotont ökande commit-generation |
| 40 | 4 | `UInt32` | `WriteInProgress` | `0` = clean, `1` = writer höll på |
| 44 | 4 | `UInt32` | `HeaderFlags` | se flaggor nedan |
| 48 | 4 | `Int32` | `VectorDimension` | antal `float` per vektor, `> 0` |
| 52 | 8 | `Int64` | `CurrentCount` | högsta allokerade rad + 1 |
| 60 | 8 | `Int64` | `MaxCount` | aktuell radkapacitet för radsektioner |
| 68 | 4 | `Int32` | `MaxNeighbors` | HNSW `M`, `>= 2` |
| 72 | 4 | `Int32` | `MaxLayers` | antal lager, `> 0` |
| 76 | 8 | `Double` | `LayerProbability` | samma betydelse som v3 |
| 84 | 8 | `Int64` | `EntryPoint` | radindex eller `-1` |
| 92 | 4 | `Int32` | `EntryPointLevel` | `0..MaxLayers-1`, eller `0` när tom |
| 96 | 8 | `Int64` | `DeletedCount` | antal tombstoned rader |
| 104 | 4 | `Int32` | `DistanceFunction` | `0 = DotProduct`, `1 = Cosine`, `2 = Euclidean` |
| 108 | 8 | `Int64` | `MetadataHeapUsed` | antal använda bytes i metadata heap |
| 116 | 8 | `Int64` | `FreeListHead` | första fria radindex eller `-1` |
| 124 | 8 | `Int64` | `FreeListCount` | antal noder i free list |
| 132 | 4 | `UInt32` | `FormatOptions` | se formatflaggor |
| 136 | 4 | `Int32` | `QuantizationMode` | `0 = None`, övriga reserverade |
| 140 | 4 | `Int32` | `QuantizationSectionId` | `0` när `None`, annars section id |
| 144 | 8 | `Int64` | `FileLength` | förväntad logisk fillängd |
| 152 | 8 | `Int64` | `MetadataHeapCapacity` | samma som metadata heap-sektionens `Length` |
| 160 | 8 | `Int64` | `NextSectionDataOffset` | första fria byte efter alla kända sektioner |
| 168 | 8 | `Int64` | `CreatedUnixTimeSeconds` | `DateTimeOffset.UtcNow.ToUnixTimeSeconds()` vid create |
| 176 | 8 | `Int64` | `UpdatedUnixTimeSeconds` | uppdateras vid header-commit |
| 184 | 328 | bytes | `Reserved` | måste vara `0` i v4 |

### `HeaderFlags`

| Bit | Namn | Betydelse |
|---:|---|---|
| 0 | `SparseRequested` | Skaparen försökte markera filen sparse |
| 1 | `SparseConfirmed` | Sparse-markering lyckades eller plattformen har naturlig sparse-semantik |
| 2 | `HasOptionalSections` | Minst en okänd/optional section slot är present |
| 3..31 | reserverade | måste skrivas `0`; ignoreras av v4-reader |

### `FormatOptions`

| Bit | Namn | Betydelse |
|---:|---|---|
| 0 | `AllowGrow` | filen får växa när radkapacitet eller metadata heap tar slut |
| 1 | `RequireCleanOpen` | `Open` måste avvisa `WriteInProgress != 0`; ska vara satt i v4 |
| 2 | `HasMetadataHeap` | ska vara satt i v4 |
| 3 | `HasPersistentFreeList` | ska vara satt i v4 |
| 4 | `ReservedQuantizationHooks` | section ids för kvantisering är reserverade |
| 5..31 | reserverade | måste skrivas `0`; ignoreras av v4-reader |

---

## Section Table

Section table börjar vid `SectionTableOffset` och består av exakt `SectionTableEntryCount` slots. v4 reserverar `64` slots. Varje slot är 32 bytes.

Tom slot:

- `SectionId = 0`
- övriga fält `0`

Entry layout, offset relativt slotens början:

| Offset | Storlek | Typ | Fältnamn | Betydelse |
|---:|---:|---|---|---|
| 0 | 4 | `UInt32` | `SectionId` | typ av sektion |
| 4 | 4 | `UInt32` | `SectionFlags` | `Present`, `Required`, osv |
| 8 | 8 | `Int64` | `Offset` | absolut offset från filens början |
| 16 | 8 | `Int64` | `Length` | antal bytes reserverade för sektionen |
| 24 | 4 | `UInt32` | `ElementSize` | logisk elementstorlek, eller `1` för byte heap |
| 28 | 4 | `UInt32` | `Reserved` | `0` i v4 |

### `SectionFlags`

| Bit | Namn | Betydelse |
|---:|---|---|
| 0 | `Present` | sloten beskriver en sektion |
| 1 | `Required` | reader måste förstå `SectionId` |
| 2 | `Mutable` | sektionen skrivs efter create |
| 3 | `AppendOnly` | sektionen växer append-only inom sitt `Length` |
| 4 | `MayMoveOnGrow` | sektionen kan flyttas när filen växer |
| 5..31 | reserverade | måste skrivas `0` i v4 |

Regler:

- Om `SectionId == 0` måste `SectionFlags`, `Offset`, `Length`, `ElementSize` och `Reserved` vara `0`.
- Om `SectionId != 0` måste `Present` vara satt.
- `Required` betyder att en v4-reader som inte känner igen `SectionId` ska avvisa filen.
- Okända sektioner utan `Required` ska ignoreras semantiskt men ändå valideras strukturellt.
- `Reserved` måste vara `0`; annars är headern korrupt.
- `Offset` måste vara `>= HeaderSize`.
- `Length` måste vara `>= 0`.
- `ElementSize` måste vara `> 0` för present sections.
- `Offset + Length` måste vara `<= FileLength` och `<= actual file length` vid `Open`.
- Present sections får inte överlappa varandra. Sortera intervallen efter `Offset` och kontrollera att föregående `End <= nästa Offset`.
- Dubbletter av samma kända `SectionId` är korrupt format, förutom framtida optional ids där readern inte tolkar innehållet. För v4 ska även okända ids vara unika för enkel diagnostik.

### Reserverade section ids

| Id | Namn | Required | ElementSize | Kommentar |
|---:|---|---|---:|---|
| 0 | `Unused` | nej | 0 | tom slot |
| 1 | `Vectors` | ja | `VectorDimension * 4` | `float32[VectorDimension]` per rad |
| 2 | `Graph` | ja | `(MaxLayers + 1) * MaxNeighbors * 4` | `Int32` grannar per rad och lager; level 0 har `2 * MaxNeighbors` platser |
| 3 | `MetadataDescriptors` | ja | `16` | en descriptor per rad |
| 4 | `MetadataHeap` | ja | `1` | UTF-8 metadata bytes, append-only |
| 5 | `Guids` | ja | `16` | `Guid.ToByteArray()`-kompatibla bytes per rad |
| 6 | `Tombstones` | ja | `1` | `0 = live/never used`, `1 = deleted` |
| 7 | `FreeList` | ja | `8` | `Int64 nextIndex` per rad |
| 8 | `QuantizedVectors` | nej | reserverad | framtida int8-vektorer |
| 9 | `QuantizationDatasetParameters` | nej | reserverad | framtida dataset-scale/offset |
| 10 | `QuantizationVectorParameters` | nej | reserverad | framtida per-vektor-scale/offset |
| 11..1023 | reserverade | nej | varierar | Qvec framtida format |
| 1024.. | third-party/experiment | nej | varierar | får aldrig markeras `Required` av Qvec v4 |

V4-reader måste förstå ids `1..7`. Om någon saknas eller inte är `Required`, är filen korrupt.

---

## Kända sektioner i detalj

### `Vectors` section

Layout:

```
row i offset = Vectors.Offset + i * Vectors.ElementSize
Vectors.ElementSize = VectorDimension * sizeof(float)
```

Varje rad är `float32[VectorDimension]`. Semantiken för cosine är oförändrad: inlagrade vektorer är normaliserade kopior, caller-arrayer muteras inte. För `DotProduct` och `Euclidean` lagras vektorn som den kom in — att normalisera under euklidisk metrik vore direkt fel, eftersom skalning ändrar avståndet till allt annat.

Validering:

- `Vectors.Length >= MaxCount * VectorDimension * 4`
- `Vectors.ElementSize == VectorDimension * 4`

### `Graph` section

Layout:

Baslagret (level 0) har dubbel fan-out: `M0 = 2 * MaxNeighbors` platser, medan varje
lager ovanför har `MaxNeighbors` platser. Det följer originalpubliceringen av HNSW och
är det som gör baslagret navigerbart. Level 0 ligger först i raden, vilket betyder att
varje level `l > 0` börjar på `(l + 1) * MaxNeighbors` — inte `l * MaxNeighbors`.

```
row i offset      = Graph.Offset + i * Graph.ElementSize
level 0 offset    = row i offset
level l offset    = row i offset + (l + 1) * MaxNeighbors * sizeof(Int32)   // l > 0
neighbor j offset = level l offset + j * sizeof(Int32)
```

Varje neighbor är `Int32` row index eller `-1` för tom plats.

Validering:

- `Graph.Length >= MaxCount * (MaxLayers + 1) * MaxNeighbors * 4`
- `Graph.ElementSize == (MaxLayers + 1) * MaxNeighbors * 4`
- `EntryPoint == -1` eller `0 <= EntryPoint < CurrentCount`
- `EntryPointLevel` inom `0..MaxLayers-1`

### `MetadataDescriptors` section

V4 ersätter den fasta 512-byte metadata-sloten med en descriptor per rad.

Descriptor layout, 16 bytes per rad:

| Offset | Storlek | Typ | Fältnamn | Betydelse |
|---:|---:|---|---|---|
| 0 | 8 | `Int64` | `HeapOffset` | offset relativt `MetadataHeap.Offset` |
| 8 | 4 | `Int32` | `Length` | antal UTF-8 bytes |
| 12 | 4 | `UInt32` | `Flags` | `0` i v4 |

`Length == 0` betyder tom metadata-sträng. Då ska `HeapOffset` ignoreras och normalt skrivas `0`.

Validering:

- `MetadataDescriptors.Length >= MaxCount * 16`
- `MetadataDescriptors.ElementSize == 16`
- För varje live rad `i < CurrentCount`:
  - `Length >= 0`
  - `HeapOffset >= 0`
  - `HeapOffset + Length <= MetadataHeapUsed`
  - `MetadataHeapUsed <= MetadataHeap.Length`

`GetMetadata(index)` läser descriptor, hyr byte-buffer eller använder stackalloc för små payloads, läser exakt `Length` bytes från heapen och avkodar UTF-8. Ogiltig UTF-8 ska ge `QvecFormatException` vid read/open-validering om man väljer eager validering; lazy read får kasta `DecoderFallbackException` inlindad i `QvecFormatException`.

### `MetadataHeap` section

Metadata heap är append-only inom sektionens `Length`.

Write:

1. UTF-8-koda metadata.
2. Om `MetadataHeapUsed + byteCount > MetadataHeap.Length`, försök växa filen om `AllowGrow` är satt.
3. Skriv bytes på `MetadataHeap.Offset + MetadataHeapUsed`.
4. Skriv descriptorn för raden med `HeapOffset = old MetadataHeapUsed`, `Length = byteCount`.
5. Öka `MetadataHeapUsed` i commit-headern.

När metadata uppdateras skrivs nya bytes längst bak i heapen och descriptorn pekas om. Gamla bytes blir garbage. De återanvänds inte inline.

`Vacuum()` ska vara mekanismen som reclaimar heap-garbage:

- Skapa en ny v4-fil.
- Iterera live rader.
- Skriv vector, Guid och aktuell metadata via normal `AddEntry(..., externalId: guid)`.
- Bygg om HNSW-grafen genom normal insert eller dedikerad rebuild.
- Byt fil atomiskt när plattformen tillåter.

Heap exhaustion:

- Om `AllowGrow` är satt och remap/grow lyckas: operationen fortsätter.
- Om grow är avstängt eller misslyckas: kasta `QvecException` med meddelandet:

```text
Metadata heap is full: {requiredBytes} bytes required but only {availableBytes} bytes remain. Enable growth or run Vacuum().
```

`MaxMetadataBytes` bör ändras från `512` till ett dokumenterat praktiskt maxvärde, till exempel `int.MaxValue`, men implementationen ska också skydda mot `Length > int.MaxValue` eftersom publika API:t tar `string` och .NET-arrayer inte kan hyras obegränsat.

### `Guids` section

Layout:

```
row i offset = Guids.Offset + i * 16
```

Bytes ska vara kompatibla med nuvarande `Guid.ToByteArray()` / `new Guid(byte[])` så befintlig round-trip-semantik bevaras inom v4.

Validering:

- `Guids.Length >= MaxCount * 16`
- `Guids.ElementSize == 16`

`RebuildGuidIndex()` ska fortfarande byggas vid `Open`, men den ska hoppa tombstoned rader.

### `Tombstones` section

Layout:

```
row i offset = Tombstones.Offset + i
```

Värden:

- `0`: raden är inte tombstoned.
- `1`: raden är tombstoned och får återanvändas via free list.

Alla andra värden är korrupt format.

Validering:

- `Tombstones.Length >= MaxCount`
- `Tombstones.ElementSize == 1`
- Antal `1` för `i < CurrentCount` ska vara `DeletedCount`.

### `FreeList` section

Free list är en persistent stack över tombstoned slots.

Layout:

```
row i offset = FreeList.Offset + i * 8
```

Varje element är `Int64 nextIndex`.

Värden:

- För tombstoned rader som ingår i stacken: nästa tombstoned radindex eller `-1`.
- För live/never-used rader: ska skrivas `-1`.

Headerfält:

- `FreeListHead`: första tombstoned slot eller `-1`.
- `FreeListCount`: antal slots i listan.

Delete:

1. Sätt tombstone byte till `1`.
2. Skriv `FreeList[index] = FreeListHead`.
3. Sätt `FreeListHead = index`.
4. Öka `FreeListCount` och `DeletedCount`.
5. Commit-header.

Allocate:

1. Om `FreeListHead != -1`:
   - `slot = FreeListHead`
   - `next = FreeList[slot]`
   - validera `0 <= slot < CurrentCount`, tombstone är `1`
   - sätt `FreeListHead = next`
   - sätt `FreeList[slot] = -1`
   - sätt tombstone byte till `0`
   - minska `FreeListCount` och `DeletedCount`
   - returnera `slot`
2. Annars, om `CurrentCount < MaxCount`: returnera `CurrentCount++`.
3. Annars: väx filen om `AllowGrow`, annars kasta `QvecFullException`.

### Scan eller free list?

Behåll både tombstone-scan och persistent free list, men ändra deras roller:

- Tombstone-sektionen är den normativa sanningen för om en rad är live.
- Free list är den normativa allokeringsordningen för återanvändning.
- Vid `Open` ska implementationen scanna tombstones för att bygga `TombstoneSet` i minnet och samtidigt validera free list:
  - inga cykler,
  - alla free-list-index är `< CurrentCount`,
  - varje free-list-index har tombstone `1`,
  - varje tombstoned rad förekommer exakt en gång i free list,
  - antal noder är `FreeListCount` och matchar `DeletedCount`.

Motivering: `AllocateSlot` blir O(1) efter restart, men startup hittar korrupta eller ofullständiga free-list-skrivningar deterministiskt. Eftersom v4 har `WriteInProgress` ska en crash mitt i delete normalt avvisas redan innan free-list-valideringen.

---

## Version och kompatibilitet

`CurrentFormatVersion = 5`.

Version 5 skiljer sig från version 4 endast i `Graph.ElementSize`: baslagret fick dubbel
fan-out (`M0 = 2 * MaxNeighbors`), vilket ändrade radlängden. Allt annat i headern är
oförändrat. En v4-fil skulle redan ha avvisats av `ValidateSections`, eftersom
`ElementSize` valideras mot formeln, men två olika layouter får inte kalla sig samma
version.

Exakt kompatibilitetsregel:

- En implementation får bara öppna filer med `MagicNumber == 0x5A564543` och `Version == CurrentFormatVersion`.
- Version 1 till 4 migreras inte.
- Versioner större än `CurrentFormatVersion` avvisas.
- `QvecDatabase.Open(path)` och den publika konstruktorn ska följa samma regel för befintliga filer.
- Konstruktorargument (`dim`, `max`, `maxNeighbors`, `maxLayers`, `distanceFunction`) får inte användas för att tolka en befintlig fil. Om argumenten skiljer sig från headern ska konstruktorn kasta argument/header-mismatch på samma sätt som dagens säkra beteende, men offsets ska alltid komma från section table.

För en äldre Qvec-fil ska `Open` kasta `QvecFormatException` med meddelande som innehåller följande ordalydelse:

```text
'{path}' has Qvec format version {version}. This build only supports format version {CurrentFormatVersion}. Qvec does not migrate older files automatically; export with the matching Qvec version and re-import.
```

För framtida version:

```text
'{path}' has Qvec format version {version}. This build only supports format version {CurrentFormatVersion}.
```

För fel magic number:

```text
'{path}' is not a Qvec database: expected magic number 0x5A564543 but found 0x{actual:X8}.
```

För clean-check:

```text
'{path}' was not closed cleanly: WriteInProgress is set for generation {generation}. The file may contain a torn write.
```

För checksum:

```text
'{path}' has a corrupt Qvec header: CRC-32 mismatch (stored 0x{stored:X8}, computed 0x{computed:X8}).
```

Tester ska inte kräva exakt hela strängen, men ska kräva dessa viktiga fraser så att användaren får en tydlig remediation.

---

## Integritet

### Checksum-algoritm

Använd `System.IO.Hashing.Crc32`.

Microsoft Learn beskriver `Crc32` i namespace `System.IO.Hashing`, assembly `System.IO.Hashing.dll`, och som en implementation av CRC-32 enligt ITU-T V.42 / IEEE 802.3. Om `Qvec.Core` inte redan får assemblyn transitivt från `net10.0` ska implementationen lägga till ett Microsoft `PackageReference` till `System.IO.Hashing` med version som matchar SDK:n. Detta är inte en tredjepartsdependency.

CRC-32 är inte kryptografiskt. Det räcker här eftersom målet är torn/corrupt header-detektion, inte angriparskydd.

### Exakta bytes som checksumas

Checksumen beräknas över `HeaderSize` bytes, alltså:

- Primary header 0..511
- Section table 512..2559
- Reserved header area 2560..4095

Fältet `HeaderCrc32` på offset `28..31` behandlas som fyra nollbytes under beräkningen.

Alla andra headerfält, inklusive `WriteInProgress`, `Generation`, `FileLength`, `MetadataHeapUsed` och hela section table, ingår.

Pseudokod:

```csharp
Span<byte> header = stackalloc byte[HeaderSize]; // eller ArrayPool för implementation
ReadHeaderBytes(header);
BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), 0);
uint crc = Crc32.HashToUInt32(header);
```

Vid commit:

1. Bygg hela headerbilden i minne med `HeaderCrc32 = 0`.
2. Beräkna CRC.
3. Skriv CRC till offset 28 i headerbilden.
4. Skriv hela `HeaderSize` till filen.
5. Flush headern.

### `WriteInProgress` och `Generation`

Varje muterande operation som kan lämna data och header ur synk ska använda tvåfas-commit:

1. Under write lock: skriv en dirty header med:
   - samma section table som nuvarande committade läge,
   - `WriteInProgress = 1`,
   - `Generation = currentGeneration + 1`,
   - korrekt CRC för dirty headern.
2. Flush dirty header.
3. Skriv data-sektioner.
4. Flush berörda accessors/streams.
5. Skriv clean commit-header:
   - alla nya räknare, offsets, längder och section table-värden,
   - `WriteInProgress = 0`,
   - samma `Generation`,
   - ny CRC.
6. Flush header.

Detta är "data före commit-header". Dirty-headern skrivs före data för att en crash under operationen ska upptäckas.

Operationer som bara skriver data men inte ändrar headern, till exempel en in-place neighbor update, ska ändå sätta `WriteInProgress` om en torn write kan göra grafen inkonsistent. En framtida optimering kan batcha graph updates, men v4 bör börja konservativt.

### `Flush()` och `Dispose()`

Ny intern metod:

```csharp
private void FlushAll()
```

ska minst:

- anropa `Flush()` på alla aktiva `MemoryMappedViewAccessor` som kan ha ändrats,
- säkerställa att clean headern skrivits efter datan,
- om implementationen håller en underliggande `FileStream`, anropa `Flush(flushToDisk: true)` när möjligt.

Osäkerhet: `MemoryMappedViewAccessor.Flush()` dokumenterar att viewn flushas till filen, men exakt garanti om fysisk lagring och disk-cache beror på OS och storage. Därför ska `Dispose()` garantera Qvecs logiska ordning och synlighet för omedelbar reopen i samma OS, inte absolut strömavbrottssäkerhet.

`Dispose()` måste:

1. ta write lock eller stoppa nya operationer,
2. om en pointer är acquired, släppa den,
3. flush:a data och clean header,
4. dispose:a accessors,
5. dispose:a `MemoryMappedFile`,
6. dispose:a lock.

Om `Dispose()` anropas när `WriteInProgress` fortfarande är satt på grund av exception, ska implementationen försöka skriva en clean header endast om den vet att datasteget slutfördes. Annars ska dirty läge lämnas kvar så att nästa `Open` avvisar filen.

### `IsHealthy()`

`IsHealthy()` ska inte längre vara en magic-number-peek.

Den ska:

1. läsa `HeaderSize` bytes från filen/accessorn,
2. validera magic och `Version == CurrentFormatVersion`,
3. validera CRC,
4. kräva `WriteInProgress == 0`,
5. validera section table och kända section-längder,
6. validera grundläggande räknare:
   - `0 <= CurrentCount <= MaxCount`
   - `0 <= DeletedCount <= CurrentCount`
   - `0 <= FreeListCount <= DeletedCount`
   - `MetadataHeapUsed <= MetadataHeap.Length`
7. returnera `false` vid alla exceptions.

Full graph-konsistens behöver inte kontrolleras i `IsHealthy()` eftersom det skulle bli dyrt, men section bounds måste kontrolleras.

---

## Create-layout

Vid `Create` beräknas initiala sektioner från argumenten. Dessa argument används därefter inte för befintlig fil.

Rekommenderad initial layout:

1. `HeaderSize = 4096`
2. `Vectors`
3. `Graph`
4. `MetadataDescriptors`
5. `Guids`
6. `Tombstones`
7. `FreeList`
8. `MetadataHeap`

`MetadataHeap` sist för att mindre metadata-only growth i vissa fall kan ske utan att andra sektioner behöver flyttas. Radsektionerna ligger före heapen eftersom de har enkel kapacitetsberäkning.

Initial heap capacity:

```text
max(64 KiB, min(64 MiB, MaxCount * 128 bytes))
```

Detta är bara startkapacitet. Filen får växa.

Alla sektioners offset ska alignas till 64 bytes. Det är inte ett korrekthetskrav för memory-mapped file, men det är billigt och hjälper SIMD/cache-linjer. `Length` behöver inte vara alignment-paddad; nästa `Offset` görs aligned.

`FileLength` ska vara sista sektionens `Offset + Length`, eventuellt alignad till 4096 bytes för OS page friendliness.

---

## Growable file

### När växer filen?

Filen växer när:

- `CurrentCount == MaxCount` och en ny rad behövs,
- metadata heap saknar plats,
- framtida optional section behöver mer plats.

För radkapacitet:

```text
newMaxCount = max(MaxCount + 1, ceil(MaxCount * 1.5))
```

För metadata heap:

```text
newMetadataHeapCapacity = max(requiredEnd, ceil(oldCapacity * 1.5), oldCapacity + 64 KiB)
```

### Flytta sektioner eller reservera slack?

v4 ska flytta sektioner vid grow, inte reservera enorma fasta slack-zoner.

Motivering:

- Qvec ska vara "SQLite of vector databases"; en liten databas ska förbli liten.
- Sparse files minskar fysisk allokering men inte alla miljöer stöder sparse lika bra.
- En stor förreserverad adresslayout gör section table svårare att resonera om och kan ge mycket stor logisk fillängd.
- Grow är en write-lockad, relativt sällsynt operation. Kostnaden är acceptabel och testbar.

Sektioner med `MayMoveOnGrow` får nya offsets vid grow. Alla v4-kända data-sektioner ska ha flaggan satt. Reader får aldrig cache:a section offsets utanför objektets nuvarande layout-generation.

### Grow-algoritm

Under write lock:

1. Skriv dirty header (`WriteInProgress = 1`, `Generation + 1`) och flush.
2. Släpp `_dataBasePtr` om den är acquired.
3. Flush och dispose:a alla view accessors.
4. Dispose:a nuvarande `MemoryMappedFile`.
5. Öppna filen med `FileStream` för read/write.
6. Beräkna ny section table.
7. `SetLength(newFileLength)`.
8. Flytta sektioner som fått nytt offset.
   - Flytta bakifrån och fram när destinationen ligger efter source för att undvika överlappskorruption.
   - Använd buffert från `ArrayPool<byte>`, till exempel 1-8 MiB.
   - Metadata heap flyttas som bytes `0..MetadataHeapUsed`, men section `Length` blir ny kapacitet.
   - Radsektioner kopieras upp till gamla `MaxCount * ElementSize`; ny kapacitetsdel initieras:
     - graph neighbors till `-1`,
     - tombstones till `0`,
     - free-list entries till `-1`,
     - descriptors till zero,
     - guids till zero.
9. Flush stream.
10. Skapa ny `MemoryMappedFile` med ny capacity.
11. Skapa nya accessors.
12. Uppdatera in-memory offsets från section table.
13. Skriv clean header med ny `MaxCount`, section table, `FileLength`, `Generation`, `WriteInProgress = 0`, CRC.
14. Flush header.

### Pointer-invalidering

Nuvarande implementation cache:ar `_dataBasePtr` för objektets livstid. Det är inte säkert i en growable design.

Regel:

- `_dataBasePtr` är bara giltig för aktuell mapping-generation.
- Innan grow/remap måste implementationen:
  - ta write lock,
  - säkerställa att inga readers är aktiva,
  - anropa `ReleasePointer()` om `_dataBasePtr != null`,
  - sätta `_dataBasePtr = null`,
  - dispose:a gamla accessors.
- Efter remap reacquiras pointer lazy nästa gång `DataBasePointer` används.

Lägg till ett internt `int _mappingGeneration` om det hjälper debug asserts, men write lock räcker för korrekthet inom processen.

### Readers mitt under grow

Samma process:

- `ReaderWriterLockSlim` blockerar nya readers medan grow håller write lock.
- Aktiva readers slutför innan grow börjar.
- Därför ser de antingen gamla mappingen eller nya mappingen, aldrig en halv remap.

Andra processer:

- v4 ger ingen live-remap-garanti.
- En reader som redan har mappat filen när en annan process växer den kan fortsätta se sin gamla mapping och gammal header.
- Rekommenderad regel: multi-process readers ska stänga och öppna om filen om de ser att `Generation` ändrats, och libraryt ska dokumentera att concurrent writer + external reader inte är en stark v4-garanti.
- En framtida version kan lägga till named mutex/file lock och snapshot readers.

---

## Sparse allocation

Vid `Create` ska Qvec undvika att fysiskt skriva nollor över hela `FileLength`.

Windows:

- Öppna filen.
- Anropa `DeviceIoControl` med `FSCTL_SET_SPARSE` på filhandtaget innan `SetLength(...)`.
- Microsofts Win32-dokumentation säger att `FSCTL_SET_SPARSE` markerar filen sparse och att stora nollområden kan sakna fysisk allokering tills nonzero data skrivs.
- Det finns ingen enkel hög-nivå .NET API i `FileStream` som säkert motsvarar `FSCTL_SET_SPARSE`; använd P/Invoke med `SafeFileHandle`.
- Om P/Invoke misslyckas ska `Create` fortsätta utan sparse och sätta `SparseRequested = 1`, `SparseConfirmed = 0`.

Linux/macOS:

- `FileStream.SetLength(...)` på en ny fil skapar normalt en sparse fil på filsystem som stöder holes, eftersom nollområden inte skrivs fysiskt förrän data skrivs.
- .NET exponerar inte ett portabelt "make sparse"-anrop.
- Sätt `SparseConfirmed = 1` endast om implementationen kan verifiera det billigt och portabelt; annars lämna `0` och dokumentera att beteendet är filsystemsberoende.

Viktigt: sparse påverkar fysisk allokering, inte den logiska `FileInfo.Length`. Tester ska därför kontrollera logisk layout och round-trip, inte kräva ett specifikt antal allokerade diskblock.

---

## Reserverat utrymme för kvantisering

v4 reserverade formatkrokar utan att designa algoritmen. Mode `2` (`Int8ScalarPerVector`) är
sedan implementerad utan versionsbump; se [design-quantization-int8.md](design-quantization-int8.md).
Rescoring (`Int8Rescored` i API:t) är mode `2` plus en optional, icke-`Required` sektion 1
(`Vectors`) i slot 8 med `HasOptionalSections` satt; se
[design-quantization-rescoring.md](design-quantization-rescoring.md).

Headerfält:

- `QuantizationMode`
  - `0 = None`
  - `1 = Int8ScalarPerDataset` reserverad, avvisas
  - `2 = Int8ScalarPerVector` implementerad
- `QuantizationSectionId`
  - `0` när `QuantizationMode == None`
  - `8` när `QuantizationMode == 2`

Section ids:

- `8 = QuantizedVectors` — `ElementSize = dim`, en byte per dimension
- `9 = QuantizationDatasetParameters` — reserverad, används inte
- `10 = QuantizationVectorParameters` — `ElementSize = 16`, `scale`/`offset`/`sumOfCodes`/`squaredNorm` per rad

Regler:

- Mode `0`: sektion `1` (`Vectors`) är required, `8` och `10` får inte vara required.
- Mode `2`: sektion `8` och `10` är required, `1` saknas. Sektion `8` ligger i slot 0 och `10` i slot 7, övriga slots är oförändrade så grow-algoritmen behöver inte veta om mode.
- Mode `1` eller okänt mode ger `QvecFormatException`:

```text
'{path}' uses quantization mode {mode}, which this build reserves but does not implement.
```

- Att öppna en fil med ett annat mode än det som begärs i konstruktorn ger `QvecFormatException` som nämner `quantization`.

---

## Ändrade operationer

### `ReadAndValidateHeader`

Ny ordning:

1. Kontrollera att filen inte är tom.
2. Läs minst 4096 bytes. Om filen är kortare än 4096:
   - om magic/version kan läsas och version är 1..3, kasta äldre-format-meddelandet,
   - annars kasta truncation/corrupt header.
3. Läs `MagicNumber`.
4. Läs `Version`.
5. Kräv `Version == CurrentFormatVersion`.
6. Kräv header-konstanter:
   - `HeaderSize == 4096`
   - `PrimaryHeaderSize == 512`
   - `SectionTableOffset == 512`
   - `SectionTableEntrySize == 32`
   - `SectionTableEntryCount == 64`
7. Beräkna och jämför CRC.
8. Kräv `WriteInProgress == 0`.
9. Validera section table.
10. Validera kända sektioner mot headerfält.
11. Returnera en in-memory layoutmodell.

### `ComputeLayout`

`ComputeLayout(...)` ska tas bort för befintliga filer. Ersätt med:

```csharp
private sealed record QvecLayout(...);
private static QvecLayout ReadAndValidateLayout(string path);
private static QvecLayout CreateInitialLayout(CreateOptions options);
```

För ny fil kan `CreateInitialLayout` fortfarande räkna fram initiala offsets, men resultatet skrivs till section table och därefter är tabellen sanning.

### `WriteVectorToDisk`

Använd:

```text
offset = Vectors.Offset + index * Vectors.ElementSize
```

Inte `HeaderSize` plus aritmetiskt framräknade sektioner.

### `WriteMetadataToDisk`

Skriv till heap + descriptor enligt ovan. Ta bort 512-byte-padding och truncation-regeln. `MaxMetadataBytes` är inte längre `512`.

### `GetMetadata`

Läs descriptor och exakt antal bytes från heap. `TrimEnd('\0')` ska bort; null bytes inuti metadata är vanliga bytes i UTF-8 payloaden och ska inte styra längden.

### `ReadGuidFromDisk`

Använd `Guids.Offset + index * 16`.

### `WriteTombstone` / `ReadTombstone` / `LoadTombstones`

Använd `Tombstones.Offset`.

`LoadTombstones` ska även bygga `TombstoneSet`, ta bort tombstoned GUIDs från `_guidIndex` och validera free list.

### `GetNeighborsAtLevel` / `WriteNeighborsAtLevel` / `InitNeighborsAtLevel`

Använd `Graph.Offset`, `Graph.ElementSize`, `MaxNeighbors` och `MaxLayers`.

### `AllocateSlot`

Använd persistent free list först. Fallback till append. Om append når `MaxCount`, grow innan `QvecFullException` så länge `AllowGrow` är satt.

### `Open`

`QvecDatabase.Open(path)` ska vara det rekommenderade sättet att öppna befintlig fil. Det ska aldrig behöva `dim`, `max`, `maxNeighbors` eller `maxLayers`.

Den publika konstruktorn kan fortsatt skapa ny fil med parametrar. För befintlig fil ska den läsa headern först och därefter:

- om parametrarna matchar: öppna,
- om de inte matchar: kasta `QvecFormatException` med mismatch-meddelande,
- aldrig räkna offsets från parametrarna.

### `PartitionedQvecDatabase`

Partitioner är fortfarande separata `.zvec`-filer.

`PartitionedQvecDatabase.ReadHeader` måste uppdateras så att den inte läser gamla 52-byte `DbHeader`. Den ska antingen:

- använda en intern v4 header reader från `QvecDatabase`, eller
- öppna partitionen med `QvecDatabase.Open(...)` och läsa publika properties.

Den ska validera:

- samma `VectorDimension`,
- samma `DistanceFunction`,
- gärna samma format version om en publik/internal property exponeras.

Eftersom v4 tillåter grow inom en partition kan `PartitionSize` bli en policy för när partitioned wrapper skapar ny partition, inte en hård filkapacitet. Rekommendation: behåll befintligt rollover-beteende i första implementationen för minsta API-överraskning, men låt en enskild `QvecDatabase` växa när den används direkt.

---

## Migration och rollout

Det här bryter för användare:

- Befintliga v2/v3 `.zvec`-filer öppnas inte av v4.
- `MaxMetadataBytes == 512` gäller inte längre.
- Råverktyg eller tester som antar gamla offsetar (`HeaderSize = 1024`, metadata direkt efter graph, osv.) måste uppdateras.
- Filstorlek vid create kan ändras kraftigt:
  - logisk längd kan vara större på grund av grow/slack,
  - fysisk allokering kan vara mindre med sparse files.
- `QvecDatabase` är inte längre en strikt fixed-capacity databas när `AllowGrow` är på.

README måste säga:

- Qvec v4 använder ett nytt on-disk format.
- v2/v3-filer migreras inte automatiskt.
- För att uppgradera: exportera vektorer + metadata + Guid med äldre Qvec-version och importera i en ny v4-fil.
- Ta backup före uppgradering.
- Metadata är variabel längd och lagras i append-only heap; kör `Vacuum()` för att reclaim:a metadata-garbage efter många updates.
- Sparse files används opportunistiskt; rapporterad filstorlek är logisk storlek och kan vara större än fysisk diskförbrukning.

CHANGELOG måste ha en breaking-change-post:

```text
BREAKING: Qvec on-disk format is now version 4. Qvec v4 does not open or migrate v2/v3 `.zvec` files. Export with an older Qvec version and re-import into a new v4 database.
```

Felmeddelanden ska peka användaren mot export/import, inte mot att ändra konstruktorargument.

---

## Testplan och implementeringsordning

Repo hade 114 gröna tester när remediation-planen godkändes. Hård regel: build ska vara 0 warnings. Implementera i små steg och kör minsta relevanta testfil efter varje steg; kör hela sviten när formatet är färdigt.

### Steg 1: Introducera v4 header reader/writer utan att ändra publikt API

Kod:

- Lägg till offset-konstanter för primary header.
- Lägg till `SectionEntry`/`QvecLayout`.
- Lägg till CRC-beräkning.
- Lägg till create/read round-trip för headerbild i minne.

Tester i `tests\Qvec.Core.Tests\PersistenceTests.cs`:

- `V4HeaderLayoutConstants_DocumentPersistedOffsets`
- `V4SectionEntryLayoutConstants_DocumentPersistedOffsets`
- `Create_WritesVersion4HeaderAndSectionTable`
- `Open_WithVersion3File_ThrowsClearNoMigrationFormatException`
- `Open_WithFutureVersion_ThrowsFormatException`
- `Open_WithHeaderCrcMismatch_ThrowsFormatException`
- `IsHealthy_WithCrcMismatch_ReturnsFalse`

### Steg 2: Byt sektionoffsets till section table

Kod:

- Ta bort `ComputeLayout` från open-path.
- Alla läs/skrivmetoder använder `QvecLayout`.
- Behåll v3-testsemantik på API-nivå, men uppdatera råoffset-tester.

Tester i `PersistenceTests.cs`:

- `Entries_RoundTripWithIdenticalConstructorArguments` ska fortsätta gälla.
- `Reopen_WithMismatchedMax_EitherHonorsHeaderOrThrowsFormatException` ska bli strikt: konstruktorn kastar mismatch, `Open` roundtrippar.
- `Open_WithOverlappingSections_ThrowsFormatException`
- `Open_WithSectionPastEndOfFile_ThrowsFormatException`
- `Open_WithUnknownOptionalSection_IgnoresButValidatesBounds`
- `Open_WithUnknownRequiredSection_ThrowsFormatException`

### Steg 3: Implementera dirty/clean header commit

Kod:

- Alla mutationer går genom commit helper.
- `Dispose()` flushar data före clean header.
- `IsHealthy()` kör verklig validering.

Tester i `PersistenceTests.cs`:

- `Open_WithWriteInProgress_ThrowsFormatException`
- `IsHealthy_WithWriteInProgress_ReturnsFalse`
- `Dispose_PersistsDataVisibleToImmediateReopen` ska fortsätta gälla.
- `AddEntry_WritesDataBeforeCleanHeader` kan testas via en intern/fake stream om möjligt; annars täck med dirty-header patch-test.

### Steg 4: Metadata heap

Kod:

- Ersätt `MetadataSize = 512` för lagring med descriptors + heap.
- Ta bort truncation/padding.
- Uppdatera `MaxMetadataBytes`.
- Metadata update append:ar och lämnar garbage.

Tester i `ReproTests.cs` och `PersistenceTests.cs`:

- `Metadata_AboveSlotBoundary_RoundTrips` ska nu kräva round-trip, inte "roundtrip or throws".
- `Metadata_TruncatedInsideMultiByteCharacter_RoundTrips`
- `UpdateMetadata_WithLongerPayload_RoundTripsAfterReopen`
- `MetadataHeap_DescriptorPointsWithinHeap`
- `Open_WithMetadataDescriptorPastHeapUsed_ThrowsFormatException`
- `MetadataHeap_WhenFullAndGrowthDisabled_ThrowsClearQvecException`

### Steg 5: Persistent free list

Kod:

- Lägg till `FreeList` section.
- Delete pushar slot.
- Allocate poppar slot.
- Startup scannar tombstones och validerar free list.

Tester i `PersistenceTests.cs`:

- `DeletedEntries_StayDeletedAfterReopen` ska fortsätta gälla.
- `DeleteThenReopenThenAdd_ReusesFreedSlot`
- `Open_WithFreeListCycle_ThrowsFormatException`
- `Open_WithFreeListEntryNotTombstoned_ThrowsFormatException`
- `Open_WithDeletedCountMismatch_ThrowsFormatException`

### Steg 6: Grow/remap

Kod:

- Introducera remap helper.
- Släpp cached pointer före remap.
- Reacquire lazy efter remap.
- Flytta sektioner och uppdatera table.

Tester i `PersistenceTests.cs`:

- `AddEntry_WhenCurrentCountReachesMax_GrowsAndRoundTrips`
- `MetadataHeap_WhenFull_GrowsAndRoundTrips`
- `Grow_InvalidatesCachedPointerAndSearchStillWorks`
- `Grow_PreservesGuidIndexTombstonesAndGraph`
- `Open_AfterGrow_UsesSectionTableNotConstructorArguments`

### Steg 7: Sparse create

Kod:

- Lägg till plattformsabstraktion för sparse-markering.
- Windows P/Invoke isoleras bakom liten intern helper.
- Misslyckande är non-fatal.

Tester:

- `Create_DoesNotWriteZeroesAcrossEntireFile` är svår att testa portabelt; undvik att kräva fysisk diskallokering.
- Lägg istället `Create_WithSparseUnavailable_StillCreatesHealthyDatabase` med injicerad/fake sparse helper om implementationen gör helpern testbar.
- `Create_HeaderFlagsReflectSparseAttempt`.

### Steg 8: Partitioned wrapper och docs

Kod:

- Uppdatera `PartitionedQvecDatabase.ReadHeader`.
- Uppdatera README/CHANGELOG.

Tester:

- `PartitionedDatabase_RollsOverWhenCurrentPartitionIsFull` ska fortsätta gälla.
- Lägg `PartitionedDatabase_OpenExistingV4Partitions_UsesHeaderValues`.
- Lägg `PartitionedDatabase_WithOldPartition_ThrowsClearFormatException`.

### Steg 9: Full verifiering

Kör:

```powershell
dotnet test --no-restore
```

Om restore krävs:

```powershell
dotnet test
```

Slutmål:

- alla tester gröna,
- 0 warnings,
- inga v2/v3 migrationsvägar kvar i v4 open-path,
- inga råa `HeaderSize = 1024` antaganden i core/tests.

---

## Korrekthetsgarantier som ska bevaras

Från nuvarande `PersistenceTests.cs` och `ReproTests.cs` ska dessa kontrakt fortsätta gälla på API-nivå:

- Entries roundtrippar efter dispose/reopen.
- `QvecDatabase.Open(path)` adopterar headerns dimension/kapacitet i stället för caller guesses.
- Den publika konstruktorn får inte öppna med motstridiga argument utan tydligt formatfel.
- Invalid magic, future version, zero-length file och truncated body avvisas.
- Empty database kan öppnas och användas efter reopen.
- Deleted entries förblir deleted efter reopen.
- Search och SearchSimple returnerar inte tombstoned entries.
- Cosine normalisering muterar inte caller-arrayer.
- Metadata över gamla 512-byte-gränsen ska i v4 roundtrippa.
- Uppdaterad vektor ska bevara Guid och inverted-index membership.
- Repeated update ska inte exhausta kapacitet när tombstoned slot kan återanvändas.

Råfilstester som förväntar v3-layout måste skrivas om, inte behållas.

---

## Öppna frågor / risker

- **`System.IO.Hashing` i net10.0:** Microsoft Learn visar `System.IO.Hashing.Crc32` i assembly/package `System.IO.Hashing`. Implementeraren måste verifiera om Qvec.Core kan använda den direkt i aktuell SDK eller behöver `PackageReference`.
- **Flush-garantier:** `MemoryMappedViewAccessor.Flush()` och `FileStream.Flush(true)` ger bästa praktiska .NET/OS-garanti, men absolut power-loss-semantik varierar med OS, disk och cache. Dokumentera detta ärligt.
- **Sparse-verifiering:** Windows kräver `FSCTL_SET_SPARSE` via `DeviceIoControl`; .NET har ingen enkel portabel sparse API. Linux/macOS sparse-beteende beror på filsystem. Tester ska inte anta fysisk allokering.
- **Multi-process readers under grow:** Denna design garanterar säkerhet inom en process via write lock. Externa readers behöver reopen/snapshot-policy i framtiden.
- **Section move-kostnad:** Att flytta stora vector/graph sections vid grow kan bli dyrt. Alternativet enorm slack valdes bort, men implementeraren bör mäta och eventuellt justera growth factor.
- **Free-list recovery:** Designen avvisar inkonsistent clean free list i stället för att tyst reparera. Det är säkrare, men kan vara striktare än användare förväntar sig.
- **Vacuum atomics:** Atomiskt filbyte är plattformsberoende, särskilt på Windows om filen är öppen/mappad. `Vacuum()` behöver egen detaljerad implementation/testning.
- **Guid byteordning:** v4 behåller `Guid.ToByteArray()`-semantik för .NET-kompatibilitet, inte RFC 4122 network byte order. Det bör dokumenteras om formatet ska läsas av andra språk.
