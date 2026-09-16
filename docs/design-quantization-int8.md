# Design: int8 scalar quantization

## Vad det är

`quantization: VectorQuantization.Int8` i `QvecDatabase`-konstruktorn lagrar varje vektor som
`dim` bytes plus 16 bytes parametrar i stället för `dim × 4` bytes float. Sökning, insert och
grafbygge räknar på bytes; floats finns inte kvar i filen.

Läget väljs när filen skapas och sparas i headern (`QuantizationMode = 2`). Att öppna filen
igen med ett annat läge ger `QvecFormatException`.

## Kodek

Asymmetrisk per-vektor-skalning till osignerade koder `0..255`:

```
scale  = (max - min) / 255        (0 om vektorn är konstant)
offset = min
q_i    = round((x_i - offset) / scale)
```

Per vektor sparas dessutom `sumOfCodes = Σ q_i` och `squaredNorm = ‖x̂‖²` där `x̂` är den
*dekvantiserade* vektorn. Att normen räknas på `x̂` och inte `x` gör att en vektors
Euclidean-avstånd till sig själv blir exakt 0, vilket testerna kräver.

Skalärprodukten expanderas så att den integer-tunga delen är ren byte×byte:

```
a·b ≈ Sa·Sb·Σ(qa·qb) + Sa·Ob·Σqa + Sb·Oa·Σqb + d·Oa·Ob
```

`Σ(qa·qb)` räknas med en widening SIMD-kärna (`Vector<byte>` → `ushort` → `uint` → `long`);
resten är fyra skalära multiplikationer i double. Euclidean blir `-(Na + Nb − 2·a·b)`, Cosine
normaliserar före kvantisering precis som i float-läget.

Frågan kvantiseras också. Det ger en enda kärna (byte×byte) för alla vägar och gör att
insert-grafen och sökningen ser exakt samma avstånd.

## Vad som *inte* gjordes

- **Ingen rescoring.** Floats sparas inte, så top-k kan inte omrankas exakt. Det är det som
  lämnar recall-gapet nedan. Att spara floats i en extra optional sektion (`Vectors` bredvid
  `QuantizedVectors`) och omranka de sista `k` kandidaterna är en naturlig påbyggnad och
  kräver ingen headerändring.
- **Ingen per-dataset-skalning** (`QuantizationMode = 1`). Reserverad, avvisas.
- **Ingen migrering** float → int8 i samma fil. Skapa en ny databas och läs in.
- `GetByGuid`/`GetVector` returnerar den dekvantiserade approximationen, inte originalet.

## Format

Inget versionsbump. Header: `QuantizationMode = 2`, `QuantizationSectionId = 8`.
Sektion 8 (`QuantizedVectors`, `ElementSize = dim`) tar slot 0 i stället för sektion 1;
sektion 10 (`QuantizationVectorParameters`, `ElementSize = 16`) tar slot 7. Slots 1–6 är
oförändrade, så `Grow`/`PlanSectionMoves` behöver inte känna till läget.

Float-läget är oförändrat: siftsmall-index byggt från `master` respektive denna branch är
byteidentiska i alla sju sektioner (endast header-CRC/tidsstämplar skiljer).

## Mätningar

siftsmall (10 000 × 128, Euclidean, `indexSeed` pinnat), samma maskin, samma körning:

| | ef 10 | ef 40 | ef 160 | fil |
| --- | --- | --- | --- | --- |
| float recall@10 / QPS | 98.6 % / 14 161 | 100 % / 9 335 | 100 % / 3 689 | 13.8 MiB |
| int8 recall@10 / QPS | 97.9 % / 32 686 | 99.3 % / 11 579 | 99.3 % / 4 453 | 10.3 MiB |

Vektorsektionen: 5.12 → 1.28 MiB. Grafen (7.7 MiB) påverkas inte, därför krymper filen mindre
än 4×.

Syntetiska täta kluster (spridning 0.08, n = 2000, ef = 200) mot exakt float-brute-force,
recall@10:

| dim | DotProduct | Euclidean | Cosine |
| ---: | ---: | ---: | ---: |
| 64 | 98.5 % | 98.1 % | 89.8 % |
| 384 | 98.2 % | 96.9 % | 89.1 % |

int8-HNSW ger samma resultat som int8-brute-force, så hela förlusten är kvantiseringsbrus,
inte grafen. Cosine tappar mest eftersom rankningen är ren vinkel med små gap på enhetssfären;
`Int8` är därför ett medvetet val för `Cosine` på mycket täta data. Testet i
`QuantizedDatabaseTests` kräver ≥ 85 % just av det skälet.

## Test

- `Int8QuantizerTests`: round-trip ≤ halva steget, integer-dot mot skalär referens för alla
  SIMD-svansar (1, 15, 16, 17, 63, 64, 65, 1536) med maximala koder, similarity mot float
  < 1 % relativt fel, parametrar round-trip.
- `QuantizedDatabaseTests`: layout, 4× storlek, Open bevarar läge, mismatch kastar,
  recall-golv, score-närhet, Update/Delete/Vacuum, Grow, filtrerad sökning.
- `V4HeaderTests`: kvantiserad layout, felaktig mode/section-kombination, mode 1 reserverad,
  ogiltiga sektionsformer.
