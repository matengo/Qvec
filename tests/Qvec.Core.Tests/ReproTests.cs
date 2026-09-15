using System.Text;
using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Reproductions for empirically observed defects. KnownDefect tests assert the
/// intended contract and are expected to fail until the corresponding fix lands.
/// </summary>
public class ReproTests
{
    /// <summary>
    /// Finding #1: reopening an existing file with a different max computes section
    /// offsets from the caller argument, so reads silently miss the stored GUID.
    /// </summary>
    [Fact]
    public void Reopen_WithDifferentMax_MustRoundTripOrThrowFormatException()
    {
        using var temp = new TempDb();
        var vector = Vec.Basis(64, 7);
        const string metadata = """{"name":"cli-default-max-mismatch"}""";
        Guid id;

        using (var created = new QvecDatabase(temp.Path, dim: 64, max: 1_000))
        {
            id = created.AddEntry(vector, metadata);
        }

        var exception = Record.Exception(() =>
        {
            using var reopened = new QvecDatabase(temp.Path, dim: 64, max: 10_000);
            Assert.Equal(1, reopened.GetCount());
            Assert.True(reopened.IsHealthy());

            var record = reopened.GetByGuid(id);
            Assert.NotNull(record);
            Assert.Equal(metadata, record.Value.Metadata);
            Assert.Equal(vector, record.Value.Vector);
        });

        Assert.True(
            exception is null or QvecFormatException,
            $"Reopening with different max must not silently return wrong data; got {exception?.GetType().Name}: {exception?.Message}");
    }

    /// <summary>
    /// Finding #1: reopening an existing file with a different dimension computes
    /// offsets from the caller argument, so GUID and metadata reads can be misaligned.
    /// </summary>
    [Fact]
    public void Reopen_WithDifferentDimension_MustRoundTripOrThrowFormatException()
    {
        using var temp = new TempDb();
        var vector = Vec.Basis(32, 11);
        const string metadata = """{"name":"dimension-mismatch"}""";
        Guid id;

        using (var created = new QvecDatabase(temp.Path, dim: 32, max: 100))
        {
            id = created.AddEntry(vector, metadata);
        }

        var exception = Record.Exception(() =>
        {
            using var reopened = new QvecDatabase(temp.Path, dim: 64, max: 100);
            var record = reopened.GetByGuid(id);
            Assert.NotNull(record);
            Assert.Equal(metadata, record.Value.Metadata);
            Assert.Equal(vector, record.Value.Vector);
        });

        Assert.True(
            exception is null or QvecFormatException,
            $"Reopening with different dim must not silently misread data; got {exception?.GetType().Name}: {exception?.Message}");
    }

    /// <summary>
    /// Finding #2: cosine AddEntry normalises the caller's vector in place instead
    /// of storing from an internal copy.
    /// </summary>
    [Fact]
    public void AddEntry_WithCosineDistance_DoesNotMutateCallerVector()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, distance: DistanceFunction.Cosine);
        var vector = new[] { 3f, 4f, 0f, 0f };
        var original = vector.ToArray();

        db.AddEntry(vector, "{}");

        Assert.Equal(original, vector);
    }

    public static IEnumerable<object[]> CosineSearchOperations()
    {
        yield return new object[] { "Search", (Action<QvecDatabase, float[]>)((db, query) => db.Search(query, topK: 1)) };
        yield return new object[] { "Search with filter", (Action<QvecDatabase, float[]>)((db, query) => db.Search(query, _ => true, topK: 1)) };
        yield return new object[] { "SearchSimple", (Action<QvecDatabase, float[]>)((db, query) => db.SearchSimple(query, topK: 1)) };
        yield return new object[] { "SearchSimpleParallel", (Action<QvecDatabase, float[]>)((db, query) => db.SearchSimpleParallel(query, topK: 1)) };
        yield return new object[] { "SearchWithCandidates", (Action<QvecDatabase, float[]>)((db, query) => db.SearchWithCandidates(query, new HashSet<int> { 0 }, topK: 1)) };
    }

    /// <summary>
    /// Finding #2: cosine search APIs normalise the caller's query array in place,
    /// so callers that reuse buffers observe unexpected mutation.
    /// </summary>
    [Theory]
    [MemberData(nameof(CosineSearchOperations))]
    public void Search_WithCosineDistance_DoesNotMutateCallerQuery(string operationName, Action<QvecDatabase, float[]> search)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, distance: DistanceFunction.Cosine);
        db.AddEntry(new[] { 1f, 0f, 0f, 0f }, "{}");
        var query = new[] { 3f, 4f, 0f, 0f };
        var original = query.ToArray();

        search(db, query);

        Assert.True(query.SequenceEqual(original), $"{operationName} mutated the caller's query vector.");
    }

    [Fact]
    public void PartitionedDatabase_RollsOverWhenCurrentPartitionIsFull()
    {
        using var temp = new TempDb();
        using var db = new PartitionedQvecDatabase(temp.Sibling("partitioned"), dim: 8, partitionSize: 2);
        var entries = Enumerable.Range(0, 5)
            .Select(i => (Vector: Vec.Basis(8, i), Metadata: $"{{\"i\":{i}}}"))
            .ToArray();

        var ids = entries.Select(entry => db.AddEntry(entry.Vector, entry.Metadata)).ToArray();

        foreach (var (entry, id) in entries.Zip(ids))
        {
            var results = db.Search(entry.Vector, topK: 5);
            Assert.Contains(results, result => result.Id == id && result.Metadata == entry.Metadata);
        }
    }

    /// <summary>
    /// Finding #4: vector Update delete-reinserts the row without moving inverted
    /// index terms, so indexed queries no longer find the live entry.
    /// </summary>
    [Fact]
    public void UpdateVector_PreservesInvertedIndexMembership()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);
        const string metadata = """{"category":"books"}""";
        var id = db.AddEntry(Vec.Basis(4, 0), metadata);
        db.AddFieldIndex(0, new[] { ("category", "books") });
        Assert.Contains(db.WhereIndexed("category", "books"), row => row.Id == id);

        Assert.True(db.Update(id, Vec.Basis(4, 1), newMetadata: null));

        var indexed = db.WhereIndexed("category", "books");
        Assert.Contains(indexed, row => row.Id == id && row.Metadata == metadata);
    }

    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    public void Metadata_AtOrBelowSlotBoundary_RoundTrips(int byteCount)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);
        var metadata = new string('a', byteCount);

        var id = db.AddEntry(Vec.Basis(4, 0), metadata);
        var record = db.GetByGuid(id);

        Assert.NotNull(record);
        Assert.Equal(metadata, record.Value.Metadata);
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(record.Value.Metadata));
    }

    /// <summary>
    /// Finding #5: metadata longer than the 512-byte slot is silently truncated on
    /// write instead of round-tripping or throwing an explicit exception.
    /// </summary>
    [Fact]
    public void Metadata_AboveSlotBoundary_RoundTripsOrThrowsExplicitException()
    {
        var metadata = new string('a', 513);

        AssertMetadataRoundTripsOrWriteThrows(metadata);
    }

    /// <summary>
    /// Finding #5: UTF-8 metadata can be truncated in the middle of a multi-byte
    /// character, producing corrupt text instead of a round-trip or explicit error.
    /// </summary>
    [Fact]
    public void Metadata_TruncatedInsideMultiByteCharacter_RoundTripsOrThrowsExplicitException()
    {
        var metadata = new string('a', 510) + "😀";
        Assert.Equal(514, Encoding.UTF8.GetByteCount(metadata));

        AssertMetadataRoundTripsOrWriteThrows(metadata);
    }

    [Fact]
    public void Search_ReturnsExactStoredVectorAtDefaultParameters()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 32, max: 256, maxNeighbors: 32);
        var rng = new Random(7);
        var entries = Enumerable.Range(0, 128)
            .Select(_ => Vec.Random(32, rng))
            .ToArray();
        var ids = entries.Select(vector => db.AddEntry(vector.ToArray(), "{}")).ToArray();

        for (int i = 0; i < entries.Length; i += 17)
        {
            var results = db.Search(entries[i].ToArray(), topK: 1);
            Assert.NotEmpty(results);
            Assert.Equal(ids[i], results[0].Id);
        }
    }

    [Fact]
    public void Search_AfterDeletingHalf_ReturnsEverySurvivingExactStoredVector()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 24, max: 256, maxNeighbors: 32);
        var rng = new Random(8);
        var entries = Enumerable.Range(0, 160)
            .Select(_ => Vec.Random(24, rng))
            .ToArray();
        var ids = entries.Select(vector => db.AddEntry(vector.ToArray(), "{}")).ToArray();

        for (int i = 0; i < ids.Length; i += 2)
        {
            Assert.True(db.Delete(ids[i]));
        }

        for (int i = 1; i < entries.Length; i += 16)
        {
            var results = db.Search(entries[i].ToArray(), topK: 1);
            Assert.NotEmpty(results);
            Assert.Equal(ids[i], results[0].Id);
        }
    }

    /// <summary>
    /// Finding #10: UpdateVector delete-reinserts into a fresh physical row every
    /// time, so repeated updates of one logical entry exhaust fixed capacity.
    /// </summary>
    [Fact]
    public void UpdateVector_ReusesLogicalSlotAndDoesNotExhaustCapacity()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 5);
        var id = db.AddEntry(Vec.Basis(4, 0), """{"name":"single"}""");

        for (int i = 0; i < 10; i++)
        {
            var next = Vec.Basis(4, i % 4);
            Assert.True(db.UpdateVector(id, next));
            Assert.Equal(1, db.LiveCount);
        }

        var final = db.GetByGuid(id);
        Assert.NotNull(final);
    }

    private static void AssertMetadataRoundTripsOrWriteThrows(string metadata)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);
        Guid id = Guid.Empty;

        var exception = Record.Exception(() => id = db.AddEntry(Vec.Basis(4, 0), metadata));

        if (exception is not null)
        {
            Assert.True(
                exception is QvecException or ArgumentException,
                $"Metadata overflow must throw an explicit Qvec or argument exception; got {exception.GetType().Name}: {exception.Message}");
            return;
        }

        var record = db.GetByGuid(id);
        Assert.NotNull(record);
        Assert.Equal(metadata, record.Value.Metadata);
    }
}
