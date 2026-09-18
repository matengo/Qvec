# Design: int8 scalar quantization

## What it is

`quantization: VectorQuantization.Int8` in the `QvecDatabase` constructor stores each vector as
`dim` bytes plus 16 bytes of parameters instead of `dim × 4` bytes of float. Search, insert, and
graph construction compute on bytes; floats are not kept in the file.

The mode is chosen when the file is created and is stored in the header (`QuantizationMode = 2`).
Opening the file again with another mode gives `QvecFormatException`.

## Codec

Asymmetric per-vector scaling to unsigned codes `0..255`:

```
scale  = (max - min) / 255        (0 if the vector is constant)
offset = min
q_i    = round((x_i - offset) / scale)
```

Per vector, `sumOfCodes = Σ q_i` and `squaredNorm = ‖x̂‖²` are also stored, where `x̂` is the
*dequantized* vector. Computing the norm on `x̂` and not `x` makes a vector's Euclidean distance
to itself exactly 0, which the tests require.

The dot product expands so that the integer-heavy part is pure byte×byte:

```
a·b ≈ Sa·Sb·Σ(qa·qb) + Sa·Ob·Σqa + Sb·Oa·Σqb + d·Oa·Ob
```

`Σ(qa·qb)` is computed with a widening SIMD kernel (`Vector<byte>` → `ushort` → `uint` → `long`);
the rest is four scalar multiplications in double. Euclidean becomes `-(Na + Nb − 2·a·b)`, Cosine
normalizes before quantization exactly as in float mode.

The query is also quantized. That gives a single kernel (byte×byte) for all paths and makes the
insert graph and the search see exactly the same distances.

## What was *not* done

- **No rescoring in this mode.** Floats are not stored, so top-k cannot be reranked exactly.
  That is what leaves the recall gap below. It is solved in the separate mode
  `Int8Rescored`; see [design-quantization-rescoring.md](design-quantization-rescoring.md).
- **No per-dataset scaling** (`QuantizationMode = 1`). Reserved, rejected.
- **No migration** float → int8 in the same file. Create a new database and load it.
- `GetByGuid`/`GetVector` returns the dequantized approximation, not the original.

## Format

No version bump. Header: `QuantizationMode = 2`, `QuantizationSectionId = 8`.
Section 8 (`QuantizedVectors`, `ElementSize = dim`) takes slot 0 instead of section 1;
section 10 (`QuantizationVectorParameters`, `ElementSize = 16`) takes slot 7. Slots 1–6 are
unchanged, so `Grow`/`PlanSectionMoves` do not need to know the mode.

Float mode is unchanged: siftsmall indices built from `master` and this branch, respectively, are
byte-identical in all seven sections (only header CRC/timestamps differ).

## Measurements

siftsmall (10,000 × 128, Euclidean, `indexSeed` pinned), same machine, same run:

| | ef 10 | ef 40 | ef 160 | file |
| --- | --- | --- | --- | --- |
| float recall@10 / QPS | 98.6% / 14,161 | 100% / 9,335 | 100% / 3,689 | 13.8 MiB |
| int8 recall@10 / QPS | 97.9% / 32,686 | 99.3% / 11,579 | 99.3% / 4,453 | 10.3 MiB |

Vector section: 5.12 → 1.28 MiB. The graph (7.7 MiB) is not affected, so the file shrinks less
than 4×.

Synthetic dense clusters (spread 0.08, n = 2000, ef = 200) against exact float-brute-force,
recall@10:

| dim | DotProduct | Euclidean | Cosine |
| ---: | ---: | ---: | ---: |
| 64 | 98.5% | 98.1% | 89.8% |
| 384 | 98.2% | 96.9% | 89.1% |

int8-HNSW gives the same result as int8-brute-force, so the entire loss is quantization noise,
not the graph. Cosine loses the most because the ranking is pure angle with small gaps on the unit
sphere; `Int8` is therefore a deliberate choice for `Cosine` on very dense data. The test in
`QuantizedDatabaseTests` requires ≥ 85% for exactly that reason.

## Tests

- `Int8QuantizerTests`: round-trip ≤ half the step, integer dot against scalar reference for all
  SIMD tails (1, 15, 16, 17, 63, 64, 65, 1536) with maximum codes, similarity against float
  < 1% relative error, parameters round-trip.
- `QuantizedDatabaseTests`: layout, 4× size, Open preserves mode, mismatch throws,
  recall floor, score closeness, Update/Delete/Vacuum, Grow, filtered search.
- `V4HeaderTests`: quantized layout, invalid mode/section combination, mode 1 reserved,
  invalid section shapes.