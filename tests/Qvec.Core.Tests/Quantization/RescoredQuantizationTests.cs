using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests.Quantization;

/// <summary>
/// <see cref="VectorQuantization.Int8Rescored"/>: the graph is walked on int8 codes exactly as in
/// <see cref="VectorQuantization.Int8"/>, but the original floats are kept in an optional
/// section and the candidate set is re-ranked against them, so the returned top-k and scores
/// are the float ones.
/// </summary>
public class RescoredQuantizationTests
{
    [Fact]
    public void Create_WithInt8Rescored_KeepsCodesAndAnOptionalFloatSection()
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: 32, max: 100, quantization: VectorQuantization.Int8Rescored))
        {
            Assert.Equal(VectorQuantization.Int8Rescored, db.Quantization);
            db.AddEntry(Vec.Filled(32, 0.5f), "{}");
        }

        var header = ReadHeader(temp.Path);
        // Same header mode as plain int8: a reader that predates rescoring opens the file and
        // simply searches on the codes. Only the optional section tells the two apart.
        Assert.Equal(2, header.QuantizationMode);
        Assert.Equal((int)V4SectionIds.QuantizedVectors, header.QuantizationSectionId);
        Assert.True((header.HeaderFlags & V4HeaderFlags.HasOptionalSections) != 0);

        var codes = header.GetRequiredSection(V4SectionIds.QuantizedVectors);
        Assert.Equal(32u, codes.ElementSize);
        Assert.Equal(100L * 32, codes.Length);

        Assert.True(header.TryGetSection(V4SectionIds.Vectors, out var floats));
        Assert.Equal(32u * 4, floats.ElementSize);
        Assert.Equal(100L * 32 * 4, floats.Length);

        var entry = header.Sections.Single(s => s.SectionId == V4SectionIds.Vectors);
        Assert.Equal(SectionFlags.None, entry.SectionFlags & SectionFlags.Required);
    }

    [Fact]
    public void Open_ReportsInt8RescoredFromTheFile()
    {
        using var temp = new TempDb();
        Guid id;
        using (var db = temp.Open(dim: 8, quantization: VectorQuantization.Int8Rescored))
        {
            id = db.AddEntry(Vec.Basis(8, 3), "{\"k\":1}");
        }

        using var reopened = QvecDatabase.Open(temp.Path);
        Assert.Equal(VectorQuantization.Int8Rescored, reopened.Quantization);
        var hit = Assert.Single(reopened.Search(Vec.Basis(8, 3), topK: 1));
        Assert.Equal(id, hit.Id);
    }

    [Theory]
    [InlineData(VectorQuantization.None)]
    [InlineData(VectorQuantization.Int8)]
    public void Constructor_WithOtherQuantizationOnRescoredFile_ThrowsFormatException(VectorQuantization requested)
    {
        using var temp = new TempDb();
        using (temp.Open(dim: 8, quantization: VectorQuantization.Int8Rescored)) { }

        var ex = Assert.Throws<QvecFormatException>(() => temp.Open(dim: 8, quantization: requested));
        Assert.Contains("quantization", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithInt8RescoredOnPlainInt8File_ThrowsFormatException()
    {
        using var temp = new TempDb();
        using (temp.Open(dim: 8, quantization: VectorQuantization.Int8)) { }

        var ex = Assert.Throws<QvecFormatException>(() => temp.Open(dim: 8, quantization: VectorQuantization.Int8Rescored));
        Assert.Contains("quantization", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetByGuid_ReturnsTheOriginalFloats()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 64, quantization: VectorQuantization.Int8Rescored, distance: DistanceFunction.DotProduct);
        var rng = new Random(7);
        float[] original = Vec.Random(64, rng);
        var id = db.AddEntry(original, "{}");

        var stored = db.GetByGuid(id);
        Assert.NotNull(stored);
        Assert.Equal(original, stored.Value.Vector);
    }

    [Theory]
    [InlineData(DistanceFunction.DotProduct)]
    [InlineData(DistanceFunction.Euclidean)]
    [InlineData(DistanceFunction.Cosine)]
    public void Search_ScoresAreTheFloatScores(DistanceFunction distance)
    {
        using var floatTemp = new TempDb();
        using var rescoredTemp = new TempDb();
        using var floatDb = floatTemp.Open(dim: 48, max: 300, distance: distance, indexSeed: 11);
        using var rescoredDb = rescoredTemp.Open(dim: 48, max: 300, distance: distance, indexSeed: 11, quantization: VectorQuantization.Int8Rescored);

        var rng = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            var id = Guid.NewGuid();
            var v = Vec.Random(48, rng);
            floatDb.AddEntry(v, "{}", id);
            rescoredDb.AddEntry(v, "{}", id);
        }

        for (int q = 0; q < 20; q++)
        {
            var query = Vec.Random(48, rng);
            var hits = rescoredDb.Search(query, topK: 10, efSearch: 100);

            Assert.Equal(10, hits.Count);
            foreach (var hit in hits)
            {
                Assert.Equal(ScoreOf(floatDb, query, hit.Id), hit.Score, precision: 4);
            }

            // The list must be ordered by the float score, not the int8 one.
            for (int i = 1; i < hits.Count; i++) Assert.True(hits[i - 1].Score >= hits[i].Score);
        }
    }

    [Theory]
    [InlineData(DistanceFunction.DotProduct)]
    [InlineData(DistanceFunction.Euclidean)]
    [InlineData(DistanceFunction.Cosine)]
    public void Search_OnTightClusters_RecoversTheRecallInt8Loses(DistanceFunction distance)
    {
        // Same data as QuantizedDatabaseTests.Search_OnClusteredVectors_MatchesFloatIndexClosely,
        // where plain int8 measured 89.8 % recall@10 under Cosine. Re-ranking the ef candidates on
        // the floats should bring every metric back to within a point or two of the float index.
        const int n = 2000, dim = 64, queries = 100;
        var floatSource = new Vec.ClusteredVectors(dim, clusterCount: 50, seed: 1234);
        var rescoredSource = new Vec.ClusteredVectors(dim, clusterCount: 50, seed: 1234);

        using var floatTemp = new TempDb();
        using var rescoredTemp = new TempDb();
        using var floatDb = floatTemp.Open(dim, n, distance: distance, indexSeed: 555);
        using var rescoredDb = rescoredTemp.Open(dim, n, distance: distance, indexSeed: 555, quantization: VectorQuantization.Int8Rescored);

        var floatRng = new Random(555);
        var rescoredRng = new Random(555);
        for (int i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            floatDb.AddEntry(floatSource.Next(dim, floatRng), "{}", id);
            rescoredDb.AddEntry(rescoredSource.Next(dim, rescoredRng), "{}", id);
        }

        double recallAt10 = 0;
        for (int q = 0; q < queries; q++)
        {
            var query = floatSource.Next(dim, floatRng);
            rescoredSource.Next(dim, rescoredRng);
            var exact = floatDb.SearchSimple(query.ToArray(), topK: 10).Select(r => r.Id).ToHashSet();
            var approx = rescoredDb.Search(query.ToArray(), topK: 10, efSearch: 200).Select(r => r.Id);
            recallAt10 += approx.Count(exact.Contains) / 10.0;
        }
        recallAt10 /= queries;

        Assert.True(recallAt10 >= 0.97,
            $"rescored int8 recall@10 against the exact float ranking was {recallAt10:P1} under {distance}, expected at least 97%.");
    }

    [Fact]
    public void FilteredSearch_ReturnsFloatScoresAndHonoursTheFilter()
    {
        using var floatTemp = new TempDb();
        using var rescoredTemp = new TempDb();
        using var floatDb = floatTemp.Open(dim: 16, max: 100, distance: DistanceFunction.Euclidean, indexSeed: 5);
        using var rescoredDb = rescoredTemp.Open(dim: 16, max: 100, distance: DistanceFunction.Euclidean, indexSeed: 5, quantization: VectorQuantization.Int8Rescored);

        var rng = new Random(9);
        for (int i = 0; i < 80; i++)
        {
            var id = Guid.NewGuid();
            var v = Vec.Random(16, rng);
            string meta = i % 2 == 0 ? "even" : "odd";
            floatDb.AddEntry(v, meta, id);
            rescoredDb.AddEntry(v, meta, id);
        }

        var query = Vec.Random(16, rng);
        var hits = rescoredDb.Search(query, m => m == "odd", topK: 5, efSearch: 50);

        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal("odd", h.Metadata));
        Assert.All(hits, h => Assert.Equal(ScoreOf(floatDb, query, h.Id), h.Score, precision: 4));
    }

    [Fact]
    public void UpdateVectorDeleteAndVacuum_KeepFloatsAndCodesInStep()
    {
        using var temp = new TempDb();
        Guid keep, moved, gone;
        var rng = new Random(21);
        float[] movedTo = Vec.Random(8, rng);
        using (var db = temp.Open(dim: 8, max: 10, distance: DistanceFunction.Euclidean, quantization: VectorQuantization.Int8Rescored))
        {
            keep = db.AddEntry(Vec.Basis(8, 0), "{\"n\":\"keep\"}");
            moved = db.AddEntry(Vec.Basis(8, 1), "{\"n\":\"moved\"}");
            gone = db.AddEntry(Vec.Basis(8, 2), "{\"n\":\"gone\"}");

            Assert.True(db.UpdateVector(moved, movedTo));
            Assert.True(db.Delete(gone));
            db.Vacuum();

            Assert.Equal(VectorQuantization.Int8Rescored, db.Quantization);
            Assert.Equal(2, db.LiveCount);
            Assert.Equal(0, db.DeletedCount);
            Assert.Null(db.GetByGuid(gone));

            var top = db.Search(movedTo, topK: 1)[0];
            Assert.Equal(moved, top.Id);
            Assert.Equal(0f, top.Score, precision: 5);
            Assert.Equal(movedTo, db.GetByGuid(moved)!.Value.Vector);
            Assert.Equal(Vec.Basis(8, 0), db.GetByGuid(keep)!.Value.Vector);
        }

        Assert.Equal(VectorQuantization.Int8Rescored, QvecDatabase.Open(temp.Path).Quantization);
    }

    [Fact]
    public void AddEntry_BeyondInitialCapacity_GrowsBothVectorSections()
    {
        using var temp = new TempDb();
        int maxCount;
        var rng = new Random(4);
        var vectors = new List<float[]>();
        using (var db = temp.Open(dim: 8, max: 4, distance: DistanceFunction.DotProduct, quantization: VectorQuantization.Int8Rescored))
        {
            var ids = new List<Guid>();
            for (int i = 0; i < 20; i++)
            {
                vectors.Add(Vec.Random(8, rng));
                ids.Add(db.AddEntry(vectors[i], $"{{\"i\":{i}}}"));
            }

            Assert.Equal(20, db.LiveCount);
            Assert.True(db.MaxCount >= 20);
            maxCount = db.MaxCount;

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(vectors[i], db.GetByGuid(ids[i])!.Value.Vector);
            }
        }

        var header = ReadHeader(temp.Path);
        Assert.True(header.GetRequiredSection(V4SectionIds.QuantizedVectors).Length >= maxCount * 8L);
        Assert.True(header.TryGetSection(V4SectionIds.Vectors, out var floats));
        Assert.True(floats.Length >= maxCount * 8L * 4);
    }

    [Fact]
    public void AddEntries_Parallel_StoresFloatsForEveryRow()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 2000, distance: DistanceFunction.Euclidean, quantization: VectorQuantization.Int8Rescored);
        var rng = new Random(8);
        var batch = new List<QvecInsert>();
        for (int i = 0; i < 1500; i++) batch.Add(new QvecInsert(Vec.Random(16, rng), "{}"));

        var ids = db.AddEntries(batch, maxDegreeOfParallelism: 4);

        Assert.Equal(1500, ids.Count);
        for (int i = 0; i < 1500; i += 37)
        {
            Assert.Equal(batch[i].Vector, db.GetByGuid(ids[i])!.Value.Vector);
        }
    }

    private static float ScoreOf(QvecDatabase floatDb, float[] query, Guid id)
    {
        // SearchSimple is an exact scan on the float database; pull the score for one id.
        var all = floatDb.SearchSimple(query, topK: floatDb.LiveCount);
        return all.Single(r => r.Id == id).Score;
    }

    private static V4Header ReadHeader(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return V4Header.Read(bytes.AsSpan(0, V4Header.HeaderSizeValue), bytes.LongLength);
    }
}
