# Design: int8 med rescoring (`Int8Rescored`)

Påbyggnad på [design-quantization-int8.md](design-quantization-int8.md). Läs den först.

## Vad det är

`quantization: VectorQuantization.Int8Rescored` bygger och går grafen på int8-koder exakt
som `Int8`, men sparar dessutom originalfloats i filen. Efter bottenlagrets sökning omrankas
alla `efSearch` kandidater mot floats, och det är den ordningen och de poängen som returneras.

Resultatet är float-recall vid (nära) int8-hastighet. Priset är lagring: float-sektionen plus
int8-sektionerna, dvs. ~1.25× en ren float-fil.

| Läge | Grafbygge/-vandring | Returnerade poäng | `GetByGuid` | Vektorlagring |
| --- | --- | --- | --- | --- |
| `None` | float | exakta | original | `dim × 4` |
| `Int8` | int8 | approximativa | dekvantiserad | `dim + 16` |
| `Int8Rescored` | int8 | exakta (float) | original | `dim × 5 + 16` |

## Varför inte bara `Int8`?

Int8-förlusten på Cohere 1M vid ef 180 var 1.6 procentenheter recall@100 (93.2 mot 94.8 %),
på täta cosine-kluster 10 punkter (89.8 mot ~100 %). Förlusten är kvantiseringsbrus i den
*slutliga rankningen*, inte i grafen: int8-HNSW och int8-brute-force ger samma mängd. Att
omranka kandidatmängden mot exakta floats tar bort den delen av bruset helt.

Det är också vad zvec gör med `--is-using-refiner` på 10M-datasetet.

## Vad rescoring *inte* kan göra

Omrankning ändrar ordningen inom kandidatmängden men kan inte lägga till kandidater grafen
missade. Därför:

- Vid `efSearch == topK` är kandidatmängden exakt `k` poster och recall@k är identisk med
  int8. Cohere 1M, k = 100, ef 100: int8 89.3 %, rescored 89.2 %, float 90.3 %. Poängen
  blir dock exakta även då.
- Recall@k för rescored är begränsad uppåt av "recall@ef för int8-vandringen". Ju större
  `ef / k`, desto närmare float kommer den.

## Format

Inget versionsbump och inget nytt `QuantizationMode`. Filen är en int8-fil
(`QuantizationMode = 2`, `QuantizationSectionId = 8`) som dessutom har sektion 1 (`Vectors`,
`ElementSize = dim × 4`) i slot 8 med flaggorna `Present | Mutable | MayMoveOnGrow` — **inte**
`Required` — och `HeaderFlags.HasOptionalSections` satt.

Konsekvens för äldre läsare (byggen före denna ändring): de ignorerar okända icke-`Required`
sektioner, öppnar filen som ren int8 och söker på koderna. Det är korrekt men utan
omrankning. `Grow` i en sådan läsare skulle lägga ut en ny fil utan sektion 1 och därmed
tappa floats; det accepteras eftersom versionerna aldrig samexisterar i produktion.

`VectorQuantization.Int8Rescored = 3` finns bara i API:t. `QvecDatabase.Quantization`
härleder värdet via `QvecFormatLayout.EffectiveQuantization(header)` (mode 2 + sektion 1
närvarande). Konstruktorn jämför det härledda värdet vid reopen, så `Int8` mot en rescored-fil
och `Int8Rescored` mot en ren int8-fil kastar båda `QvecFormatException`.

`V4Header`-validering: när sektion 1 finns i int8-läge måste den ha `ElementSize = dim × 4`
och `Length ≥ MaxCount × dim × 4`. `RequiredSectionIds` är oförändrad.

`CreateGrown` läser av om sektion 1 finns i den gamla tabellen (`CloneState` kopierar inte
sektionstabellen) och lägger ut den igen. `PlanSectionMoves` är generisk över närvarande
sektioner och behövde inte ändras.

## Implementation i `QvecDatabase`

`_vectorSectionOffset` delades i `_floatVectorSectionOffset` (float-läge och rescored) och
`_codesSectionOffset` (int8 och rescored); `_rescore` sätts i `CaptureSectionOffsets`.

- `WriteVectorToDisk`: kvantiserar och skriver koder + parametrar; i rescored-läge fortsätter
  den och skriver floats. Alla vägar (AddEntry, AddEntries, UpdateVector, Vacuum) går genom den.
- `ReadVectorInto` (används av `GetByGuid`/`GetVector` och Vacuum): läser floats när de finns,
  dekvantiserar annars. Vacuum i rescored-läge kvantiserar alltså om från original, inte från
  en tidigare dekvantisering.
- `FinalScore(query, index)`: exakt float-similarity om `_rescore`, annars `CalculateScore`.
  Används i alla uttömmande vägar (`SearchSimple`, `SearchSimpleParallel`,
  `ExhaustiveFilteredSearch`, `Rerank`).
- `RescoreCandidates`: skriver om `Score` i kandidatarrayen från `SearchLayerNearest` /
  `SearchLayerFiltered` innan `OrderByDescending().Take(topK)`. No-op om inte `_rescore`, så
  float- och int8-filer betalar ingenting.
- Insert-heuristiken (`StoredSimilarity`, `SelectNeighborsHeuristic`) kör kvar på int8, så
  grafen blir identisk med en ren int8-graf byggd i samma ordning.

## Mätningar

### siftsmall

10 000 × 128, Euclidean, 12 trådar, samma körning:

| | ef 20 | ef 80 |
| --- | --- | --- |
| float recall@10 / QPS | 98.6 % / 22 729 | 99.5 % / 8 169 |
| int8 recall@10 / QPS | 98.3 % / 27 161 | 99.0 % / 8 719 |
| rescored recall@10 / QPS | **99.1 %** / 18 096 | **99.8 %** / 8 118 |

På ett så litet index är grafvandringen billig och omrankningen av 20 kandidater à 128
floats syns i QPS vid ef 20. Vid ef 80 är kostnaden borta i bruset.

### Täta kluster (testet)

Samma data som `QuantizedDatabaseTests` (n = 2000, dim 64, spridning 0.08, ef 200):
`RescoredQuantizationTests` kräver ≥ 97 % recall@10 mot exakt float-brute-force i alla tre
metriker, där int8 ensamt låg på 89.8 % för Cosine.

### Cohere 1M

Se [benchmarks/README.md](../benchmarks/README.md#cohere-1m-measured) för tabellen. Kort:
rescored matchar float på recall@100 vid ef 180 och 320 (94.7/97.4 %) och gör det vid
int8-QPS (6 778 mot int8 6 841 och float 3 764 vid ef 180, 12 trådar). Bygget tog 170 s mot
419 s för float. Filen är 4 124 MiB mot 3 376 (float) och 1 194 (int8).

## Test

`tests/Qvec.Core.Tests/Quantization/RescoredQuantizationTests.cs`: layout (slot 8, ej
Required, `HasOptionalSections`), `Open` rapporterar `Int8Rescored`, mismatch i båda
riktningarna kastar, `GetByGuid` returnerar exakt original, poäng lika med float-brute-force
(1e-4) i alla metriker, recall ≥ 97 % på täta kluster, filtrerad sökning med float-poäng,
Update/Delete/Vacuum håller båda sektionerna i takt, Grow bevarar floats, parallell
`AddEntries` skriver floats för alla rader.
