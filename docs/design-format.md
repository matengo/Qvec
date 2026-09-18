# Design: File format (current version 5, version 6 with change tracking)

> **About the naming.** The layout in this document was introduced as format version 4 and is
> therefore called "v4" in the running text below. The header field `Version` is currently **5**
> (`CurrentFormatVersion`) for files without change tracking and **6**
> (`ChangeTrackingFormatVersion`) for files with it: version 5 was bumped when the base layer got
> double fan-out (`M0 = 2 * MaxNeighbors`) and changed `Graph.ElementSize`; version 6 adds the
> header fields 184..239 and the sections `EntryVersions`/`ChangeLog` for
> [sync tracking](design-sync-engine.md), see [Version and compatibility](#version-and-compatibility).
> Everything said about "v4-reader", "v4-file", and so on therefore also applies to version 5 and
> 6, unless otherwise stated.

## Background

Qvec v3 has a compact but hard-coded file format:

- `DbHeader` is 52 bytes with `[StructLayout(LayoutKind.Sequential, Pack = 1)]`.
- The header region is always 1024 bytes.
- All sections are in a fixed order after the header.
- The offset for each section is calculated with `ComputeLayout(...)` from `VectorDimension`,
  `MaxCount`, `MaxNeighbors`, and `MaxLayers`.
- Metadata is a fixed 512-byte slot per row.
- Tombstones are one byte per row and are loaded through a scan at startup.

That has worked for small and fixed databases, but v3 is not self-describing enough for an
AOT-oriented library that must be able to grow, be reopened safely, and evolve with new sections.

v4 replaces the arithmetically derived offsets with an explicit section table, adds header
integrity, metadata heap, persistent free list, and reserved format hooks for future quantization.

This document is an implementation specification. It does not describe a migration from v2/v3;
older files must be rejected.

---

## Goals

- The header and section table are the **single source of truth** for an existing file.
- Constructor arguments are used only when a new file is created.
- Older formats (`Version` 1, 2, 3) are not migrated automatically.
- A broken, interrupted, or partially written header must be detected by `Open(...)` and
  `IsHealthy()`.
- Metadata may be larger than 512 bytes and must not preallocate 512 MB for 1M rows.
- Tombstoned slots must be reusable after process restart without `AllocateSlot` having to scan the
  entire tombstone section.
- The file must be able to grow through remapping of the memory-mapped file.
- Creation must avoid physically materializing the whole file when the file system supports sparse
  files.

## Non-goals

- No automatic v2/v3 -> v4 migration.
- No design of the int8 quantization itself.
- No multi-process writer guarantee. v4 may allow several concurrent readers that reopen the file,
  but exact live coherency between processes is not a goal in this iteration.
- No full data checksum over vectors, graph, or metadata heap. The checksum protects the header and
  section table, not the whole database.

---

## Byte order and primitive types

All multi-value fields are stored little-endian. This matches .NET on supported common platforms,
though the implementation must read/write explicitly with `BinaryPrimitives` or a validated
`MemoryMarshal` layout so that offsets do not depend on runtime padding.

| Type | Size | Comment |
|---|---:|---|
| `UInt16` | 2 | unsigned little-endian |
| `Int32` | 4 | signed little-endian |
| `UInt32` | 4 | unsigned little-endian |
| `Int64` | 8 | signed little-endian |
| `UInt64` | 8 | unsigned little-endian |
| `Double` | 8 | IEEE 754 little-endian |

All reserved bytes must be written as `0` on `Create`. On `Open`, unknown reserved bytes may be
ignored, but the checksum is computed over them.

---

## Overview layout

v4 uses a larger header region than v3.

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

`HeaderSize` for v4 is always `4096`.

The implementation may still use `DbHeader`, but it must either:

1. be an explicit v4 struct with unit tests for every offset, or
2. be replaced with manual header serialization.

The recommendation is manual serialization with named offset constants. It reduces the risk that a
future field order changes the file format.

---

## Primary Header

The primary header is 512 bytes. The fields below are absolute offsets from the beginning of the
file.

| Offset | Size | Type | Field name | Value / meaning |
|---:|---:|---|---|---|
| 0 | 4 | `Int32` | `MagicNumber` | `0x5A564543` (`"ZVEC"`) |
| 4 | 4 | `Int32` | `Version` | `CurrentFormatVersion` (`5`) |
| 8 | 4 | `Int32` | `HeaderSize` | `4096` |
| 12 | 4 | `Int32` | `PrimaryHeaderSize` | `512` |
| 16 | 4 | `Int32` | `SectionTableOffset` | `512` |
| 20 | 4 | `Int32` | `SectionTableEntrySize` | `32` |
| 24 | 4 | `Int32` | `SectionTableEntryCount` | `64` |
| 28 | 4 | `UInt32` | `HeaderCrc32` | CRC-32 over header + section table, with this field zeroed |
| 32 | 8 | `UInt64` | `Generation` | Monotonically increasing commit generation |
| 40 | 4 | `UInt32` | `WriteInProgress` | `0` = clean, `1` = writer was in progress |
| 44 | 4 | `UInt32` | `HeaderFlags` | see flags below |
| 48 | 4 | `Int32` | `VectorDimension` | number of `float` values per vector, `> 0` |
| 52 | 8 | `Int64` | `CurrentCount` | highest allocated row + 1 |
| 60 | 8 | `Int64` | `MaxCount` | current row capacity for row sections |
| 68 | 4 | `Int32` | `MaxNeighbors` | HNSW `M`, `>= 2` |
| 72 | 4 | `Int32` | `MaxLayers` | number of layers, `> 0` |
| 76 | 8 | `Double` | `LayerProbability` | same meaning as v3 |
| 84 | 8 | `Int64` | `EntryPoint` | row index or `-1` |
| 92 | 4 | `Int32` | `EntryPointLevel` | `0..MaxLayers-1`, or `0` when empty |
| 96 | 8 | `Int64` | `DeletedCount` | number of tombstoned rows |
| 104 | 4 | `Int32` | `DistanceFunction` | `0 = DotProduct`, `1 = Cosine`, `2 = Euclidean` |
| 108 | 8 | `Int64` | `MetadataHeapUsed` | number of used bytes in the metadata heap |
| 116 | 8 | `Int64` | `FreeListHead` | first free row index or `-1` |
| 124 | 8 | `Int64` | `FreeListCount` | number of nodes in the free list |
| 132 | 4 | `UInt32` | `FormatOptions` | see format flags |
| 136 | 4 | `Int32` | `QuantizationMode` | `0 = None`, others reserved |
| 140 | 4 | `Int32` | `QuantizationSectionId` | `0` when `None`, otherwise section id |
| 144 | 8 | `Int64` | `FileLength` | expected logical file length |
| 152 | 8 | `Int64` | `MetadataHeapCapacity` | same as the metadata heap section's `Length` |
| 160 | 8 | `Int64` | `NextSectionDataOffset` | first free byte after all known sections |
| 168 | 8 | `Int64` | `CreatedUnixTimeSeconds` | `DateTimeOffset.UtcNow.ToUnixTimeSeconds()` on create |
| 176 | 8 | `Int64` | `UpdatedUnixTimeSeconds` | updated on header commit |
| 184 | 16 | `Guid` | `ReplicaId` | identity of the replica; `Guid.Empty` without change tracking |
| 200 | 8 | `Int64` | `ChangeSeq` | sequence number for the latest change-log entry; `0` without tracking |
| 208 | 8 | `Int64` | `ChangeLogHead` | next free slot in the change-log ring; `0` without tracking |
| 216 | 8 | `Int64` | `ChangeLogCount` | number of valid entries in the ring; `0` without tracking |
| 224 | 8 | `Int64` | `LastHlc` | latest issued HLC value (48 bits ms + 16 bits counter); `0` without tracking |
| 232 | 8 | `Int64` | `TrackingEnabledUnixSeconds` | when tracking was enabled; `0` without tracking |
| 240 | 272 | bytes | `Reserved` | must be `0` |

Fields 184..239 are meaningful only when `HasChangeTracking` is set (version 6). In a version 5
file they are in what was previously `Reserved` and must be `0`, so a version 5 file written by
Qvec 2.0.0 is byte-identical to a file written by a later version without tracking. See
`design-sync-engine.md` for the semantics.
### `HeaderFlags`

| Bit | Name | Meaning |
|---:|---|---|
| 0 | `SparseRequested` | The creator tried to mark the file sparse |
| 1 | `SparseConfirmed` | Sparse marking succeeded or the platform has natural sparse semantics |
| 2 | `HasOptionalSections` | At least one unknown/optional section slot is present |
| 3 | `HasChangeTracking` | The file carries `EntryVersions` and `ChangeLog`; requires `FormatVersion = 6` |
| 4..31 | reserved | must be written `0`; ignored by v4-reader |

### `FormatOptions`

| Bit | Name | Meaning |
|---:|---|---|
| 0 | `AllowGrow` | the file may grow when row capacity or metadata heap runs out |
| 1 | `RequireCleanOpen` | `Open` must reject `WriteInProgress != 0`; must be set in v4 |
| 2 | `HasMetadataHeap` | must be set in v4 |
| 3 | `HasPersistentFreeList` | must be set in v4 |
| 4 | `ReservedQuantizationHooks` | section ids for quantization are reserved |
| 5..31 | reserved | must be written `0`; ignored by v4-reader |

---

## Section Table

The section table starts at `SectionTableOffset` and consists of exactly `SectionTableEntryCount`
slots. v4 reserves `64` slots. Each slot is 32 bytes.

Empty slot:

- `SectionId = 0`
- other fields `0`

Entry layout, offset relative to the beginning of the slot:

| Offset | Size | Type | Field name | Meaning |
|---:|---:|---|---|---|
| 0 | 4 | `UInt32` | `SectionId` | type of section |
| 4 | 4 | `UInt32` | `SectionFlags` | `Present`, `Required`, etc. |
| 8 | 8 | `Int64` | `Offset` | absolute offset from the beginning of the file |
| 16 | 8 | `Int64` | `Length` | number of bytes reserved for the section |
| 24 | 4 | `UInt32` | `ElementSize` | logical element size, or `1` for byte heap |
| 28 | 4 | `UInt32` | `Reserved` | `0` in v4 |

### `SectionFlags`

| Bit | Name | Meaning |
|---:|---|---|
| 0 | `Present` | the slot describes a section |
| 1 | `Required` | reader must understand `SectionId` |
| 2 | `Mutable` | the section is written after create |
| 3 | `AppendOnly` | the section grows append-only within its `Length` |
| 4 | `MayMoveOnGrow` | the section may be moved when the file grows |
| 5..31 | reserved | must be written `0` in v4 |

Rules:

- If `SectionId == 0`, `SectionFlags`, `Offset`, `Length`, `ElementSize`, and `Reserved` must be
  `0`.
- If `SectionId != 0`, `Present` must be set.
- `Required` means that a v4-reader that does not recognize `SectionId` must reject the file.
- Unknown sections without `Required` must be ignored semantically but still validated structurally.
- `Reserved` must be `0`; otherwise the header is corrupt.
- `Offset` must be `>= HeaderSize`.
- `Length` must be `>= 0`.
- `ElementSize` must be `> 0` for present sections.
- `Offset + Length` must be `<= FileLength` and `<= actual file length` on `Open`.
- Present sections must not overlap each other. Sort the intervals by `Offset` and check that the
  previous `End <= next Offset`.
- Duplicates of the same known `SectionId` are corrupt format, except for future optional ids where
  the reader does not interpret the content. For v4, unknown ids should also be unique for simple
  diagnostics.

### Reserved section ids

| Id | Name | Required | ElementSize | Comment |
|---:|---|---|---:|---|
| 0 | `Unused` | no | 0 | empty slot |
| 1 | `Vectors` | yes | `VectorDimension * 4` | `float32[VectorDimension]` per row |
| 2 | `Graph` | yes | `(MaxLayers + 1) * MaxNeighbors * 4` | `Int32` neighbors per row and layer; level 0 has `2 * MaxNeighbors` places |
| 3 | `MetadataDescriptors` | yes | `16` | one descriptor per row |
| 4 | `MetadataHeap` | yes | `1` | UTF-8 metadata bytes, append-only |
| 5 | `Guids` | yes | `16` | `Guid.ToByteArray()`-compatible bytes per row |
| 6 | `Tombstones` | yes | `1` | `0 = live/never used`, `1 = deleted` |
| 7 | `FreeList` | yes | `8` | `Int64 nextIndex` per row |
| 8 | `QuantizedVectors` | no | reserved | future int8 vectors |
| 9 | `QuantizationDatasetParameters` | no | reserved | future dataset-scale/offset |
| 10 | `QuantizationVectorParameters` | no | reserved | future per-vector scale/offset |
| 11 | `EntryVersions` | yes if `HasChangeTracking` | `24` | `Int64 hlc` + `Guid origin` per row; version 6 |
| 12 | `ChangeLog` | yes if `HasChangeTracking` | `64` | ring of change entries; version 6, see `design-sync-engine.md` |
| 13..1023 | reserved | no | varies | Qvec future formats |
| 1024.. | third-party/experiment | no | varies | must never be marked `Required` by Qvec v4 |

A v4-reader must understand ids `1..7`. If any is missing or is not `Required`, the file is corrupt.
Ids `11` and `12` may only occur when `HasChangeTracking` is set and must then both be `Required`
and present; they are placed last in the layout so that `EnableChangeTracking` can add them without
moving existing sections.

---

## Known sections in detail

### `Vectors` section

Layout:

```
row i offset = Vectors.Offset + i * Vectors.ElementSize
Vectors.ElementSize = VectorDimension * sizeof(float)
```

Each row is `float32[VectorDimension]`. The semantics for cosine are unchanged: stored vectors are
normalized copies, and caller arrays are not mutated. For `DotProduct` and `Euclidean`, the vector is
stored as it arrived — normalizing under Euclidean metric would be directly wrong, because scaling
changes the distance to everything else.

Validation:

- `Vectors.Length >= MaxCount * VectorDimension * 4`
- `Vectors.ElementSize == VectorDimension * 4`

### `Graph` section

Layout:

The base layer (level 0) has double fan-out: `M0 = 2 * MaxNeighbors` places, while each layer above
has `MaxNeighbors` places. This follows the original HNSW publication and is what makes the base
layer navigable. Level 0 comes first in the row, which means each level `l > 0` starts at
`(l + 1) * MaxNeighbors` — not `l * MaxNeighbors`.

```
row i offset      = Graph.Offset + i * Graph.ElementSize
level 0 offset    = row i offset
level l offset    = row i offset + (l + 1) * MaxNeighbors * sizeof(Int32)   // l > 0
neighbor j offset = level l offset + j * sizeof(Int32)
```

Each neighbor is an `Int32` row index or `-1` for an empty place.

Validation:

- `Graph.Length >= MaxCount * (MaxLayers + 1) * MaxNeighbors * 4`
- `Graph.ElementSize == (MaxLayers + 1) * MaxNeighbors * 4`
- `EntryPoint == -1` or `0 <= EntryPoint < CurrentCount`
- `EntryPointLevel` within `0..MaxLayers-1`

### `MetadataDescriptors` section

V4 replaces the fixed 512-byte metadata slot with one descriptor per row.

Descriptor layout, 16 bytes per row:

| Offset | Size | Type | Field name | Meaning |
|---:|---:|---|---|---|
| 0 | 8 | `Int64` | `HeapOffset` | offset relative to `MetadataHeap.Offset` |
| 8 | 4 | `Int32` | `Length` | number of UTF-8 bytes |
| 12 | 4 | `UInt32` | `Flags` | `0` in v4 |

`Length == 0` means an empty metadata string. Then `HeapOffset` should be ignored and normally
written `0`.

Validation:

- `MetadataDescriptors.Length >= MaxCount * 16`
- `MetadataDescriptors.ElementSize == 16`
- For each live row `i < CurrentCount`:
  - `Length >= 0`
  - `HeapOffset >= 0`
  - `HeapOffset + Length <= MetadataHeapUsed`
  - `MetadataHeapUsed <= MetadataHeap.Length`

`GetMetadata(index)` reads the descriptor, rents a byte buffer or uses stackalloc for small payloads,
reads exactly `Length` bytes from the heap, and decodes UTF-8. Invalid UTF-8 should produce
`QvecFormatException` during read/open validation if eager validation is chosen; lazy read may throw
`DecoderFallbackException` wrapped in `QvecFormatException`.
### `MetadataHeap` section

The metadata heap is append-only within the section's `Length`.

Write:

1. UTF-8-encode metadata.
2. If `MetadataHeapUsed + byteCount > MetadataHeap.Length`, try to grow the file if `AllowGrow` is
   set.
3. Write bytes at `MetadataHeap.Offset + MetadataHeapUsed`.
4. Write the descriptor for the row with `HeapOffset = old MetadataHeapUsed`, `Length = byteCount`.
5. Increase `MetadataHeapUsed` in the commit header.

When metadata is updated, new bytes are written at the back of the heap and the descriptor is
repointed. Old bytes become garbage. They are not reused inline.

`Vacuum()` must be the mechanism that reclaims heap garbage:

- Create a new v4 file.
- Iterate live rows.
- Write vector, Guid, and current metadata through normal `AddEntry(..., externalId: guid)`.
- Rebuild the HNSW graph through normal insert or a dedicated rebuild.
- Swap file atomically when the platform allows it.

Heap exhaustion:

- If `AllowGrow` is set and remap/grow succeeds: the operation continues.
- If grow is disabled or fails: throw `QvecException` with the message:

```text
Metadata heap is full: {requiredBytes} bytes required but only {availableBytes} bytes remain. Enable growth or run Vacuum().
```

`MaxMetadataBytes` should be changed from `512` to a documented practical max value, for example
`int.MaxValue`, but the implementation must also protect against `Length > int.MaxValue` because the
public API takes `string` and .NET arrays cannot be rented without limit.

### `Guids` section

Layout:

```
row i offset = Guids.Offset + i * 16
```

Bytes must be compatible with the current `Guid.ToByteArray()` / `new Guid(byte[])` so existing
round-trip semantics are preserved within v4.

Validation:

- `Guids.Length >= MaxCount * 16`
- `Guids.ElementSize == 16`

`RebuildGuidIndex()` should still be built on `Open`, but it must skip tombstoned rows.

### `Tombstones` section

Layout:

```
row i offset = Tombstones.Offset + i
```

Values:

- `0`: the row is not tombstoned.
- `1`: the row is tombstoned and may be reused through the free list.

All other values are corrupt format.

Validation:

- `Tombstones.Length >= MaxCount`
- `Tombstones.ElementSize == 1`
- Number of `1` values for `i < CurrentCount` must be `DeletedCount`.

### `FreeList` section

The free list is a persistent stack over tombstoned slots.

Layout:

```
row i offset = FreeList.Offset + i * 8
```

Each element is `Int64 nextIndex`.

Values:

- For tombstoned rows that are part of the stack: next tombstoned row index or `-1`.
- For live/never-used rows: should be written `-1`.

Header fields:

- `FreeListHead`: first tombstoned slot or `-1`.
- `FreeListCount`: number of slots in the list.

Delete:

1. Set tombstone byte to `1`.
2. Write `FreeList[index] = FreeListHead`.
3. Set `FreeListHead = index`.
4. Increase `FreeListCount` and `DeletedCount`.
5. Commit header.

Allocate:

1. If `FreeListHead != -1`:
   - `slot = FreeListHead`
   - `next = FreeList[slot]`
   - validate `0 <= slot < CurrentCount`, tombstone is `1`
   - set `FreeListHead = next`
   - set `FreeList[slot] = -1`
   - set tombstone byte to `0`
   - decrease `FreeListCount` and `DeletedCount`
   - return `slot`
2. Otherwise, if `CurrentCount < MaxCount`: return `CurrentCount++`.
3. Otherwise: grow the file if `AllowGrow`, otherwise throw `QvecFullException`.

### Scan or free list?

Keep both tombstone scan and persistent free list, but change their roles:

- The tombstone section is the normative truth for whether a row is live.
- The free list is the normative allocation order for reuse.
- On `Open`, the implementation must scan tombstones to build `TombstoneSet` in memory and validate
  the free list at the same time:
  - no cycles,
  - all free-list indices are `< CurrentCount`,
  - every free-list index has tombstone `1`,
  - every tombstoned row occurs exactly once in the free list,
  - the number of nodes is `FreeListCount` and matches `DeletedCount`.

Rationale: `AllocateSlot` becomes O(1) after restart, but startup deterministically finds corrupt or
incomplete free-list writes. Because v4 has `WriteInProgress`, a crash in the middle of delete should
normally be rejected even before free-list validation.

### `EntryVersions` section (version 6)

Exists only when `HasChangeTracking` is set. One row per slot, 24 bytes:

```
row i offset = EntryVersions.Offset + i * 24
  0  8  Int64  Hlc       hybrid logical clock (48 bit wall clock in ms << 16 | 16 bit counter)
  8 16  Guid   Origin    ReplicaId for the replica that created the version
```

Written after every `AddEntry`/`Update`/`UpdateMetadata` and when `ApplyChanges` accepts a remote
version. The live row's version is normative; a deleted row's version lives only in `ChangeLog`.

### `ChangeLog` section (version 6)

Exists only when `HasChangeTracking` is set. A ring buffer with `ChangeLogCapacity` slots of 64
bytes; the order in the ring is the normative change order and `ChangeSeq` in the header is the
sequence number of the latest entry.

```
slot i offset = ChangeLog.Offset + i * 64
  0  8  Int64  Seq         1-based, strictly increasing per file
  8  8  Int64  Hlc         version clock
 16 16  Guid   DocumentId
 32 16  Guid   Origin      version origin
 48  1  Byte   Type        1 = Upsert, 2 = Delete
 49 15  -      reserved, zeros
```

Header fields:

- `ChangeLogHead`: next slot to be written.
- `ChangeLogCount`: number of valid entries (`<= ChangeLogCapacity`).
- the entry for `seq` is in slot `(ChangeLogHead - 1 - (ChangeSeq - seq)) mod ChangeLogCapacity`.

Append writes the slot first and commits the header afterwards together with the mutation that caused
the entry. An entry that lies beyond `ChangeLogCount` after a crash is therefore ignored on `Open`.
On `Open`, the ring is replayed oldest→newest to recreate the versions of deleted documents
(tombstone versions) in memory; entries for documents that are live are ignored.

`Grow` with changed `ChangeLogCapacity` and `Vacuum` repack the ring: all valid entries are written
contiguously from slot 0 with preserved `Seq`, and `ChangeSeq` is kept so that other replicas'
cursors remain valid. Cursors older than `ChangeSeq - ChangeLogCount + 1` are rejected with
`SyncCursorTooOldException`; see `design-sync-engine.md` §4 for `GetChanges`/`ApplyChanges`.

---

## Version and compatibility

`CurrentFormatVersion = 5`.

Version 5 differs from version 4 only in `Graph.ElementSize`: the base layer got double fan-out
(`M0 = 2 * MaxNeighbors`), which changed the row length. Everything else in the header is unchanged.
A v4 file would already have been rejected by `ValidateSections`, because `ElementSize` is validated
against the formula, but two different layouts must not call themselves the same version.

Version 6 (`ChangeTrackingFormatVersion`) is version 5 plus change tracking: the
`HasChangeTracking` flag, the header fields 184..239, and the sections `EntryVersions` (11) and
`ChangeLog` (12). Version is chosen per file: a database without tracking is still written as
version 5 and is readable by Qvec 2.0.0; `EnableChangeTracking` (or `ChangeTrackingOptions` on
create) bumps the file to 6. Flag and version must always accompany each other.

Exact compatibility rule:

- An implementation may only open files with `MagicNumber == 0x5A564543` and `Version` equal to `5`
  or `6`.
- Versions 1 through 4 are not migrated.
- Versions greater than `6` are rejected.
- `QvecDatabase.Open(path)` and the public constructor must follow the same rule for existing files.
- Constructor arguments (`dim`, `max`, `maxNeighbors`, `maxLayers`, `distanceFunction`) must not be
  used to interpret an existing file. If the arguments differ from the header, the constructor must
  throw an argument/header mismatch in the same way as today's safe behavior, but offsets must always
  come from the section table.

For an older Qvec file, `Open` must throw `QvecFormatException` with a message containing the
following wording:

```text
'{path}' has Qvec format version {version}. This build only supports format version {CurrentFormatVersion}. Qvec does not migrate older files automatically; export with the matching Qvec version and re-import.
```

For a future version:

```text
'{path}' has Qvec format version {version}. This build only supports format version {CurrentFormatVersion}.
```

For an incorrect magic number:

```text
'{path}' is not a Qvec database: expected magic number 0x5A564543 but found 0x{actual:X8}.
```

For a clean-check:

```text
'{path}' was not closed cleanly: WriteInProgress is set for generation {generation}. The file may contain a torn write.
```

For checksum:

```text
'{path}' has a corrupt Qvec header: CRC-32 mismatch (stored 0x{stored:X8}, computed 0x{computed:X8}).
```

Tests must not require the exact whole string, but must require these important phrases so that the
user gets clear remediation.

---

## Integrity
### Checksum algorithm

Use `System.IO.Hashing.Crc32`.

Microsoft Learn describes `Crc32` in namespace `System.IO.Hashing`, assembly
`System.IO.Hashing.dll`, and as an implementation of CRC-32 according to ITU-T V.42 / IEEE 802.3. If
`Qvec.Core` does not already get the assembly transitively from `net10.0`, the implementation must
add a Microsoft `PackageReference` to `System.IO.Hashing` with a version that matches the SDK. This
is not a third-party dependency.

CRC-32 is not cryptographic. It is sufficient here because the goal is torn/corrupt header
detection, not attacker protection.

### Exact bytes that are checksummed

The checksum is computed over `HeaderSize` bytes, that is:

- Primary header 0..511
- Section table 512..2559
- Reserved header area 2560..4095

The field `HeaderCrc32` at offset `28..31` is treated as four zero bytes during the calculation.

All other header fields, including `WriteInProgress`, `Generation`, `FileLength`,
`MetadataHeapUsed`, and the whole section table, are included.

Pseudocode:

```csharp
Span<byte> header = stackalloc byte[HeaderSize]; // or ArrayPool for implementation
ReadHeaderBytes(header);
BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), 0);
uint crc = Crc32.HashToUInt32(header);
```

On commit:

1. Build the whole header image in memory with `HeaderCrc32 = 0`.
2. Compute CRC.
3. Write CRC to offset 28 in the header image.
4. Write the whole `HeaderSize` to the file.
5. Flush the header.

### `WriteInProgress` and `Generation`

Every mutating operation that can leave data and header out of sync must use two-phase commit:

1. Under write lock: write a dirty header with:
   - the same section table as the current committed state,
   - `WriteInProgress = 1`,
   - `Generation = currentGeneration + 1`,
   - correct CRC for the dirty header.
2. Flush dirty header.
3. Write data sections.
4. Flush affected accessors/streams.
5. Write clean commit header:
   - all new counters, offsets, lengths, and section table values,
   - `WriteInProgress = 0`,
   - same `Generation`,
   - new CRC.
6. Flush header.

This is "data before commit header". The dirty header is written before data so that a crash during
the operation will be detected.

Operations that only write data but do not change the header, for example an in-place neighbor
update, should still set `WriteInProgress` if a torn write can make the graph inconsistent. A future
optimization can batch graph updates, but v4 should start conservatively.

### `Flush()` and `Dispose()`

New internal method:

```csharp
private void FlushAll()
```

must at least:

- call `Flush()` on all active `MemoryMappedViewAccessor` instances that may have changed,
- ensure that the clean header has been written after the data,
- if the implementation holds an underlying `FileStream`, call `Flush(flushToDisk: true)` when
  possible.

Uncertainty: `MemoryMappedViewAccessor.Flush()` documents that the view is flushed to the file, but
the exact guarantee about physical storage and disk cache depends on OS and storage. Therefore,
`Dispose()` must guarantee Qvec's logical order and visibility for immediate reopen in the same OS,
not absolute power-loss safety.

`Dispose()` must:

1. take write lock or stop new operations,
2. if a pointer is acquired, release it,
3. flush data and clean header,
4. dispose accessors,
5. dispose `MemoryMappedFile`,
6. dispose lock.

If `Dispose()` is called when `WriteInProgress` is still set because of an exception, the
implementation should try to write a clean header only if it knows the data step completed.
Otherwise, dirty state must be left in place so that the next `Open` rejects the file.

### `IsHealthy()`

`IsHealthy()` must no longer be a magic-number peek.

It must:

1. read `HeaderSize` bytes from the file/accessor,
2. validate magic and `Version == CurrentFormatVersion`,
3. validate CRC,
4. require `WriteInProgress == 0`,
5. validate section table and known section lengths,
6. validate basic counters:
   - `0 <= CurrentCount <= MaxCount`
   - `0 <= DeletedCount <= CurrentCount`
   - `0 <= FreeListCount <= DeletedCount`
   - `MetadataHeapUsed <= MetadataHeap.Length`
7. return `false` for all exceptions.

Full graph consistency does not need to be checked in `IsHealthy()` because it would be expensive,
but section bounds must be checked.

---

## Create layout

On `Create`, initial sections are computed from the arguments. These arguments are not used
thereafter for an existing file.

Recommended initial layout:

1. `HeaderSize = 4096`
2. `Vectors`
3. `Graph`
4. `MetadataDescriptors`
5. `Guids`
6. `Tombstones`
7. `FreeList`
8. `MetadataHeap`

`MetadataHeap` is last so that smaller metadata-only growth can in some cases happen without needing
to move other sections. The row sections are before the heap because they have simple capacity
calculation.

Initial heap capacity:

```text
max(64 KiB, min(64 MiB, MaxCount * 128 bytes))
```

This is only starting capacity. The file may grow.

All section offsets must be aligned to 64 bytes. It is not a correctness requirement for the
memory-mapped file, but it is cheap and helps SIMD/cache lines. `Length` does not need to be
alignment-padded; the next `Offset` is made aligned.

`FileLength` must be the last section's `Offset + Length`, optionally aligned to 4096 bytes for OS
page friendliness.

---

## Growable file

### When does the file grow?

The file grows when:

- `CurrentCount == MaxCount` and a new row is needed,
- metadata heap lacks space,
- a future optional section needs more space.

For row capacity:

```text
newMaxCount = max(MaxCount + 1, ceil(MaxCount * 1.5))
```

For metadata heap:

```text
newMetadataHeapCapacity = max(requiredEnd, ceil(oldCapacity * 1.5), oldCapacity + 64 KiB)
```

### Move sections or reserve slack?

v4 must move sections on grow, not reserve enormous fixed slack zones.

Rationale:

- Qvec must be the "SQLite of vector databases"; a small database should remain small.
- Sparse files reduce physical allocation, but not all environments support sparse equally well.
- A large pre-reserved address layout makes the section table harder to reason about and can produce
  a very large logical file length.
- Grow is a write-locked, relatively rare operation. The cost is acceptable and testable.

Sections with `MayMoveOnGrow` get new offsets on grow. All v4-known data sections must have the flag
set. A reader must never cache section offsets outside the object's current layout generation.

### Grow algorithm

Under write lock:

1. Write dirty header (`WriteInProgress = 1`, `Generation + 1`) and flush.
2. Release `_dataBasePtr` if it is acquired.
3. Flush and dispose all view accessors.
4. Dispose the current `MemoryMappedFile`.
5. Open the file with `FileStream` for read/write.
6. Compute new section table.
7. `SetLength(newFileLength)`.
8. Move sections that received a new offset.
   - Move from back to front when the destination is after the source to avoid overlap corruption.
   - Use a buffer from `ArrayPool<byte>`, for example 1-8 MiB.
   - Metadata heap is moved as bytes `0..MetadataHeapUsed`, but section `Length` becomes new
     capacity.
   - Row sections are copied up to old `MaxCount * ElementSize`; the new capacity part is
     initialized:
     - graph neighbors to `-1`,
     - tombstones to `0`,
     - free-list entries to `-1`,
     - descriptors to zero,
     - guids to zero.
9. Flush stream.
10. Create a new `MemoryMappedFile` with new capacity.
11. Create new accessors.
12. Update in-memory offsets from section table.
13. Write clean header with new `MaxCount`, section table, `FileLength`, `Generation`,
    `WriteInProgress = 0`, CRC.
14. Flush header.

### Pointer invalidation

The current implementation caches `_dataBasePtr` for the object's lifetime. That is not safe in a
growable design.

Rule:

- `_dataBasePtr` is only valid for the current mapping generation.
- Before grow/remap, the implementation must:
  - take write lock,
  - ensure that no readers are active,
  - call `ReleasePointer()` if `_dataBasePtr != null`,
  - set `_dataBasePtr = null`,
  - dispose old accessors.
- After remap, the pointer is reacquired lazily the next time `DataBasePointer` is used.

Add an internal `int _mappingGeneration` if it helps debug asserts, but write lock is enough for
correctness within the process.

### Readers in the middle of grow

Same process:

- `ReaderWriterLockSlim` blocks new readers while grow holds the write lock.
- Active readers finish before grow begins.
- Therefore they see either the old mapping or the new mapping, never a half remap.

Other processes:

- v4 gives no live-remap guarantee.
- A reader that has already mapped the file when another process grows it can continue to see its old
  mapping and old header.
- Recommended rule: multi-process readers should close and reopen the file if they see that
  `Generation` changed, and the library should document that concurrent writer + external reader is
  not a strong v4 guarantee.
- A future version can add named mutex/file lock and snapshot readers.

---

## Sparse allocation
On `Create`, Qvec must avoid physically writing zeros over the whole `FileLength`.

Windows:

- Open the file.
- Call `DeviceIoControl` with `FSCTL_SET_SPARSE` on the file handle before `SetLength(...)`.
- Microsoft's Win32 documentation says that `FSCTL_SET_SPARSE` marks the file sparse and that large
  zero areas can lack physical allocation until nonzero data is written.
- There is no simple high-level .NET API in `FileStream` that safely corresponds to
  `FSCTL_SET_SPARSE`; use P/Invoke with `SafeFileHandle`.
- If P/Invoke fails, `Create` must continue without sparse and set `SparseRequested = 1`,
  `SparseConfirmed = 0`.

Linux/macOS:

- `FileStream.SetLength(...)` on a new file normally creates a sparse file on file systems that
  support holes, because zero areas are not written physically until data is written.
- .NET does not expose a portable "make sparse" call.
- Set `SparseConfirmed = 1` only if the implementation can verify it cheaply and portably;
  otherwise leave `0` and document that the behavior is file-system-dependent.

Important: sparse affects physical allocation, not the logical `FileInfo.Length`. Tests should
therefore check logical layout and round-trip, not require a specific number of allocated disk
blocks.

---

## Reserved space for quantization

v4 reserved format hooks without designing the algorithm. Mode `2` (`Int8ScalarPerVector`) has since
been implemented without a version bump; see [design-quantization-int8.md](design-quantization-int8.md).
Rescoring (`Int8Rescored` in the API) is mode `2` plus an optional, non-`Required` section 1
(`Vectors`) in slot 8 with `HasOptionalSections` set; see
[design-quantization-rescoring.md](design-quantization-rescoring.md).

Header fields:

- `QuantizationMode`
  - `0 = None`
  - `1 = Int8ScalarPerDataset` reserved, rejected
  - `2 = Int8ScalarPerVector` implemented
- `QuantizationSectionId`
  - `0` when `QuantizationMode == None`
  - `8` when `QuantizationMode == 2`

Section ids:

- `8 = QuantizedVectors` — `ElementSize = dim`, one byte per dimension
- `9 = QuantizationDatasetParameters` — reserved, unused
- `10 = QuantizationVectorParameters` — `ElementSize = 16`, `scale`/`offset`/`sumOfCodes`/`squaredNorm` per row

Rules:

- Mode `0`: section `1` (`Vectors`) is required, `8` and `10` must not be required.
- Mode `2`: sections `8` and `10` are required, `1` is absent. Section `8` is in slot 0 and `10` in
  slot 7, the other slots are unchanged so the grow algorithm does not need to know about mode.
- Mode `1` or unknown mode gives `QvecFormatException`:

```text
'{path}' uses quantization mode {mode}, which this build reserves but does not implement.
```

- Opening a file with a different mode than the one requested in the constructor gives
  `QvecFormatException` that mentions `quantization`.

---

## Changed operations

### `ReadAndValidateHeader`

New order:

1. Check that the file is not empty.
2. Read at least 4096 bytes. If the file is shorter than 4096:
   - if magic/version can be read and version is 1..3, throw the older-format message,
   - otherwise throw truncation/corrupt header.
3. Read `MagicNumber`.
4. Read `Version`.
5. Require `Version == CurrentFormatVersion`.
6. Require header constants:
   - `HeaderSize == 4096`
   - `PrimaryHeaderSize == 512`
   - `SectionTableOffset == 512`
   - `SectionTableEntrySize == 32`
   - `SectionTableEntryCount == 64`
7. Compute and compare CRC.
8. Require `WriteInProgress == 0`.
9. Validate section table.
10. Validate known sections against header fields.
11. Return an in-memory layout model.

### `ComputeLayout`

`ComputeLayout(...)` must be removed for existing files. Replace it with:

```csharp
private sealed record QvecLayout(...);
private static QvecLayout ReadAndValidateLayout(string path);
private static QvecLayout CreateInitialLayout(CreateOptions options);
```

For a new file, `CreateInitialLayout` can still calculate initial offsets, but the result is written
to the section table and the table is truth thereafter.

### `WriteVectorToDisk`

Use:

```text
offset = Vectors.Offset + index * Vectors.ElementSize
```

Not `HeaderSize` plus arithmetically calculated sections.

### `WriteMetadataToDisk`

Write to heap + descriptor as above. Remove 512-byte padding and the truncation rule.
`MaxMetadataBytes` is no longer `512`.

### `GetMetadata`

Read the descriptor and exact number of bytes from the heap. `TrimEnd('\0')` must be removed; null
bytes inside metadata are ordinary bytes in the UTF-8 payload and must not control the length.

### `ReadGuidFromDisk`

Use `Guids.Offset + index * 16`.

### `WriteTombstone` / `ReadTombstone` / `LoadTombstones`

Use `Tombstones.Offset`.

`LoadTombstones` must also build `TombstoneSet`, remove tombstoned GUIDs from `_guidIndex`, and
validate the free list.

### `GetNeighborsAtLevel` / `WriteNeighborsAtLevel` / `InitNeighborsAtLevel`

Use `Graph.Offset`, `Graph.ElementSize`, `MaxNeighbors`, and `MaxLayers`.

### `AllocateSlot`

Use persistent free list first. Fall back to append. If append reaches `MaxCount`, grow before
`QvecFullException` as long as `AllowGrow` is set.

### `Open`

`QvecDatabase.Open(path)` must be the recommended way to open an existing file. It must never need
`dim`, `max`, `maxNeighbors`, or `maxLayers`.

The public constructor can continue to create a new file with parameters. For an existing file, it
must read the header first and then:

- if the parameters match: open,
- if they do not match: throw `QvecFormatException` with a mismatch message,
- never calculate offsets from parameters.

### `PartitionedQvecDatabase`

Partitions are still separate `.zvec` files.

`PartitionedQvecDatabase.ReadHeader` must be updated so that it does not read the old 52-byte
`DbHeader`. It must either:

- use an internal v4 header reader from `QvecDatabase`, or
- open the partition with `QvecDatabase.Open(...)` and read public properties.

It must validate:

- same `VectorDimension`,
- same `DistanceFunction`,
- preferably same format version if a public/internal property is exposed.

Because v4 allows grow within a partition, `PartitionSize` can become a policy for when the
partitioned wrapper creates a new partition, not a hard file capacity. Recommendation: keep existing
rollover behavior in the first implementation for least API surprise, but allow an individual
`QvecDatabase` to grow when it is used directly.
---

## Migration and rollout

This breaks for users:

- Existing v2/v3 `.zvec` files are not opened by v4.
- `MaxMetadataBytes == 512` no longer applies.
- Raw tools or tests that assume old offsets (`HeaderSize = 1024`, metadata directly after graph,
  etc.) must be updated.
- File size on create can change substantially:
  - logical length can be larger because of grow/slack,
  - physical allocation can be smaller with sparse files.
- `QvecDatabase` is no longer a strictly fixed-capacity database when `AllowGrow` is on.

README must say:

- Qvec 2.x uses a new on-disk format (version 5).
- v2/v3 files are not migrated automatically.
- To upgrade: export vectors + metadata + Guid with an older Qvec version and import into a new
  file.
- Back up before upgrading.
- Metadata is variable length and stored in an append-only heap; run `Vacuum()` to reclaim metadata
  garbage after many updates.
- Sparse files are used opportunistically; reported file size is logical size and can be larger than
  physical disk consumption.

CHANGELOG must have a breaking-change entry:

```text
BREAKING: Qvec on-disk format is now version 5. Qvec 2.x does not open or migrate v2/v3 `.zvec` files. Export with an older Qvec version and re-import into a new database.
```

Error messages must point the user toward export/import, not toward changing constructor arguments.

---

## Test plan and implementation order

The repo had 114 green tests when the remediation plan was approved. Hard rule: build must have 0
warnings. Implement in small steps and run the smallest relevant test file after each step; run the
whole suite when the format is complete.

### Step 1: Introduce v4 header reader/writer without changing public API

Code:

- Add offset constants for primary header.
- Add `SectionEntry`/`QvecLayout`.
- Add CRC calculation.
- Add create/read round-trip for header image in memory.

Tests in `tests\Qvec.Core.Tests\PersistenceTests.cs`:

- `V4HeaderLayoutConstants_DocumentPersistedOffsets`
- `V4SectionEntryLayoutConstants_DocumentPersistedOffsets`
- `Create_WritesVersion4HeaderAndSectionTable`
- `Open_WithVersion3File_ThrowsClearNoMigrationFormatException`
- `Open_WithFutureVersion_ThrowsFormatException`
- `Open_WithHeaderCrcMismatch_ThrowsFormatException`
- `IsHealthy_WithCrcMismatch_ReturnsFalse`

### Step 2: Switch section offsets to section table

Code:

- Remove `ComputeLayout` from open-path.
- All read/write methods use `QvecLayout`.
- Keep v3 test semantics at API level, but update raw-offset tests.

Tests in `PersistenceTests.cs`:

- `Entries_RoundTripWithIdenticalConstructorArguments` must continue to apply.
- `Reopen_WithMismatchedMax_EitherHonorsHeaderOrThrowsFormatException` must become strict: the
  constructor throws mismatch, `Open` round-trips.
- `Open_WithOverlappingSections_ThrowsFormatException`
- `Open_WithSectionPastEndOfFile_ThrowsFormatException`
- `Open_WithUnknownOptionalSection_IgnoresButValidatesBounds`
- `Open_WithUnknownRequiredSection_ThrowsFormatException`

### Step 3: Implement dirty/clean header commit

Code:

- All mutations go through commit helper.
- `Dispose()` flushes data before clean header.
- `IsHealthy()` runs real validation.

Tests in `PersistenceTests.cs`:

- `Open_WithWriteInProgress_ThrowsFormatException`
- `IsHealthy_WithWriteInProgress_ReturnsFalse`
- `Dispose_PersistsDataVisibleToImmediateReopen` must continue to apply.
- `AddEntry_WritesDataBeforeCleanHeader` can be tested through an internal/fake stream if possible;
  otherwise cover with dirty-header patch test.

### Step 4: Metadata heap

Code:

- Replace `MetadataSize = 512` for storage with descriptors + heap.
- Remove truncation/padding.
- Update `MaxMetadataBytes`.
- Metadata update appends and leaves garbage.

Tests in `ReproTests.cs` and `PersistenceTests.cs`:

- `Metadata_AboveSlotBoundary_RoundTrips` must now require round-trip, not "roundtrip or throws".
- `Metadata_TruncatedInsideMultiByteCharacter_RoundTrips`
- `UpdateMetadata_WithLongerPayload_RoundTripsAfterReopen`
- `MetadataHeap_DescriptorPointsWithinHeap`
- `Open_WithMetadataDescriptorPastHeapUsed_ThrowsFormatException`
- `MetadataHeap_WhenFullAndGrowthDisabled_ThrowsClearQvecException`

### Step 5: Persistent free list

Code:

- Add `FreeList` section.
- Delete pushes slot.
- Allocate pops slot.
- Startup scans tombstones and validates free list.

Tests in `PersistenceTests.cs`:

- `DeletedEntries_StayDeletedAfterReopen` must continue to apply.
- `DeleteThenReopenThenAdd_ReusesFreedSlot`
- `Open_WithFreeListCycle_ThrowsFormatException`
- `Open_WithFreeListEntryNotTombstoned_ThrowsFormatException`
- `Open_WithDeletedCountMismatch_ThrowsFormatException`

### Step 6: Grow/remap

Code:

- Introduce remap helper.
- Release cached pointer before remap.
- Reacquire lazily after remap.
- Move sections and update table.

Tests in `PersistenceTests.cs`:

- `AddEntry_WhenCurrentCountReachesMax_GrowsAndRoundTrips`
- `MetadataHeap_WhenFull_GrowsAndRoundTrips`
- `Grow_InvalidatesCachedPointerAndSearchStillWorks`
- `Grow_PreservesGuidIndexTombstonesAndGraph`
- `Open_AfterGrow_UsesSectionTableNotConstructorArguments`

### Step 7: Sparse create

Code:

- Add platform abstraction for sparse marking.
- Windows P/Invoke is isolated behind a small internal helper.
- Failure is non-fatal.

Tests:

- `Create_DoesNotWriteZeroesAcrossEntireFile` is difficult to test portably; avoid requiring
  physical disk allocation.
- Instead add `Create_WithSparseUnavailable_StillCreatesHealthyDatabase` with an injected/fake sparse
  helper if the implementation makes the helper testable.
- `Create_HeaderFlagsReflectSparseAttempt`.

### Step 8: Partitioned wrapper and docs

Code:

- Update `PartitionedQvecDatabase.ReadHeader`.
- Update README/CHANGELOG.

Tests:

- `PartitionedDatabase_RollsOverWhenCurrentPartitionIsFull` must continue to apply.
- Add `PartitionedDatabase_OpenExistingV4Partitions_UsesHeaderValues`.
- Add `PartitionedDatabase_WithOldPartition_ThrowsClearFormatException`.

### Step 9: Full verification

Run:

```powershell
dotnet test --no-restore
```

If restore is required:

```powershell
dotnet test
```

Final goals:

- all tests green,
- 0 warnings,
- no v2/v3 migration paths left in v4 open-path,
- no raw `HeaderSize = 1024` assumptions in core/tests.

---

## Correctness guarantees to preserve

From current `PersistenceTests.cs` and `ReproTests.cs`, these contracts must continue to apply at API
level:

- Entries round-trip after dispose/reopen.
- `QvecDatabase.Open(path)` adopts the header's dimension/capacity instead of caller guesses.
- The public constructor must not open with contradictory arguments without a clear format error.
- Invalid magic, future version, zero-length file, and truncated body are rejected.
- Empty database can be opened and used after reopen.
- Deleted entries remain deleted after reopen.
- Search and SearchSimple do not return tombstoned entries.
- Cosine normalization does not mutate caller arrays.
- Metadata beyond the old 512-byte boundary must round-trip in v4.
- Updated vector must preserve Guid and inverted-index membership.
- Repeated update must not exhaust capacity when a tombstoned slot can be reused.

Raw-file tests that expect v3 layout must be rewritten, not kept.

---

## Open questions / risks

- **`System.IO.Hashing` in net10.0:** Microsoft Learn shows `System.IO.Hashing.Crc32` in assembly/package `System.IO.Hashing`. The implementer must verify whether Qvec.Core can use it directly in the current SDK or needs `PackageReference`.
- **Flush guarantees:** `MemoryMappedViewAccessor.Flush()` and `FileStream.Flush(true)` give the best practical .NET/OS guarantee, but absolute power-loss semantics vary with OS, disk, and cache. Document this honestly.
- **Sparse verification:** Windows requires `FSCTL_SET_SPARSE` via `DeviceIoControl`; .NET has no simple portable sparse API. Linux/macOS sparse behavior depends on file system. Tests must not assume physical allocation.
- **Multi-process readers during grow:** This design guarantees safety within one process through write lock. External readers need reopen/snapshot policy in the future.
- **Section move cost:** Moving large vector/graph sections on grow can become expensive. The enormous-slack alternative was rejected, but the implementer should measure and possibly adjust growth factor.
- **Free-list recovery:** The design rejects an inconsistent clean free list instead of silently repairing it. That is safer, but may be stricter than users expect.
- **Vacuum atomics:** Atomic file replacement is platform-dependent, especially on Windows if the file is open/mapped. `Vacuum()` needs its own detailed implementation/testing.
- **Guid byte order:** v4 keeps `Guid.ToByteArray()` semantics for .NET compatibility, not RFC 4122 network byte order. This should be documented if the format is to be read by other languages.
