using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests.Quantization;

/// <summary>
/// End-to-end behaviour of a database created with <see cref="VectorQuantization.Int8"/>:
/// the file shrinks, everything the float database can do still works, and search quality
/// stays close to the float index.
/// </summary>
public class QuantizedDatabaseTests
{
    [Fact]
    public void Create_WithInt8_StoresOneByteCodesAndNoFloatVectorSection()
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: 32, max: 100, quantization: VectorQuantization.Int8))
        {
            Assert.Equal(VectorQuantization.Int8, db.Quantization);
            db.AddEntry(Vec.Filled(32, 0.5f), "{}");
        }

        var header = ReadHeader(temp.Path);
        Assert.Equal(2, header.QuantizationMode);
        Assert.False(header.TryGetSection(V4SectionIds.Vectors, out _));
        var codes = header.GetRequiredSection(V4SectionIds.QuantizedVectors);
        Assert.Equal(32u, codes.ElementSize);
        Assert.Equal(100L * 32, codes.Length);
    }

    [Fact]
    public void Create_WithInt8_ProducesAFileRoughlyFourTimesSmallerInVectorStorage()
    {
        using var floatDb = new TempDb();
        using var int8Db = new TempDb();
        using (floatDb.Open(dim: 1024, max: 1000, quantization: VectorQuantization.None)) { }
        using (int8Db.Open(dim: 1024, max: 1000, quantization: VectorQuantization.Int8)) { }

        long floatVectors = ReadHeader(floatDb.Path).GetRequiredSection(V4SectionIds.Vectors).Length;
        long codes = ReadHeader(int8Db.Path).GetRequiredSection(V4SectionIds.QuantizedVectors).Length
                   + ReadHeader(int8Db.Path).GetRequiredSection(V4SectionIds.QuantizationVectorParameters).Length;

        Assert.Equal(1000L * 1024 * 4, floatVectors);
        Assert.Equal(1000L * 1024 + 1000L * 16, codes);
    }

    [Fact]
    public void Open_PreservesQuantizationFromHeader()
    {
        using var temp = new TempDb();
        Guid id;
        using (var db = temp.Open(dim: 8, quantization: VectorQuantization.Int8))
        {
            id = db.AddEntry(Vec.Basis(8, 3), "{\"k\":1}");
        }

        using var reopened = QvecDatabase.Open(temp.Path);
        Assert.Equal(VectorQuantization.Int8, reopened.Quantization);
        var hit = Assert.Single(reopened.Search(Vec.Basis(8, 3), topK: 1));
        Assert.Equal(id, hit.Id);
    }

    [Fact]
    public void Constructor_WithMismatchedQuantization_ThrowsFormatException()
    {
        using var temp = new TempDb();
        using (temp.Open(dim: 8, quantization: VectorQuantization.Int8)) { }

        var ex = Assert.Throws<QvecFormatException>(() => temp.Open(dim: 8, quantization: VectorQuantization.None));
        Assert.Contains("quantization", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetByGuid_ReturnsDequantizedApproximation()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 64, quantization: VectorQuantization.Int8);
        var rng = new Random(7);
        float[] original = Vec.Random(64, rng);
        var id = db.AddEntry(original, "{}");

        var stored = db.GetByGuid(id);
        Assert.NotNull(stored);

        float range = original.Max() - original.Min();
        float halfStep = range / 255f / 2f;
        for (int i = 0; i < 64; i++)
        {
            Assert.True(Math.Abs(original[i] - stored.Value.Vector[i]) <= halfStep + 1e-5f,
                $"element {i} drifted by more than half a quantisation step");
        }
    }

    [Theory]
    [InlineData(DistanceFunction.DotProduct)]
    [InlineData(DistanceFunction.Euclidean)]
    [InlineData(DistanceFunction.Cosine)]
    public void Search_OnClusteredVectors_MatchesFloatIndexClosely(DistanceFunction distance)
    {
        const int n = 2000, dim = 64, queries = 100;
        var floatSource = new Vec.ClusteredVectors(dim, clusterCount: 50, seed: 1234);
        var int8Source = new Vec.ClusteredVectors(dim, clusterCount: 50, seed: 1234);

        using var floatTemp = new TempDb();
        using var int8Temp = new TempDb();
        using var floatDb = floatTemp.Open(dim, n, distance: distance, indexSeed: 555);
        using var int8Db = int8Temp.Open(dim, n, distance: distance, indexSeed: 555, quantization: VectorQuantization.Int8);

        var floatRng = new Random(555);
        var int8Rng = new Random(555);
        for (int i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            floatDb.AddEntry(floatSource.Next(dim, floatRng), "{}", id);
            int8Db.AddEntry(int8Source.Next(dim, int8Rng), "{}", id);
        }

        double recallAt10 = 0;
        for (int q = 0; q < queries; q++)
        {
            var query = floatSource.Next(dim, floatRng);
            int8Source.Next(dim, int8Rng);
            var exact = floatDb.SearchSimple(query.ToArray(), topK: 10).Select(r => r.Id).ToHashSet();
            var approx = int8Db.Search(query.ToArray(), topK: 10, efSearch: 200).Select(r => r.Id);
            recallAt10 += approx.Count(exact.Contains) / 10.0;
        }
        recallAt10 /= queries;

        // Measured: DotProduct 98.5 %, Euclidean 98.1 %, Cosine 89.8 %. The int8 HNSW walk and an
        // exhaustive int8 scan return identical sets, so the whole loss is quantisation noise, not
        // graph quality. Cosine is hit hardest because these clusters are very tight (spread 0.08)
        // and on the unit sphere the top-10 differ only by tiny angles. The floor is deliberately
        // below the worst measured metric; a regression in the codec would still drop well under it.
        Assert.True(recallAt10 >= 0.85,
            $"int8 recall@10 against the exact float ranking was {recallAt10:P1} under {distance}, expected at least 85%.");
    }

    [Fact]
    public void Search_ScoresAreCloseToFloatScores()
    {
        using var floatTemp = new TempDb();
        using var int8Temp = new TempDb();
        using var floatDb = floatTemp.Open(dim: 16, max: 10, distance: DistanceFunction.Euclidean);
        using var int8Db = int8Temp.Open(dim: 16, max: 10, distance: DistanceFunction.Euclidean, quantization: VectorQuantization.Int8);

        var rng = new Random(3);
        var id = Guid.NewGuid();
        var v = Vec.Random(16, rng);
        floatDb.AddEntry(v, "{}", id);
        int8Db.AddEntry(v, "{}", id);

        var query = Vec.Random(16, rng);
        float exact = floatDb.Search(query, topK: 1)[0].Score;
        float approx = int8Db.Search(query, topK: 1)[0].Score;

        Assert.True(Math.Abs(exact - approx) < 0.05f, $"float {exact} vs int8 {approx}");
    }

    [Fact]
    public void UpdateVectorDeleteAndVacuum_WorkOnQuantizedDatabase()
    {
        using var temp = new TempDb();
        Guid keep, moved, gone;
        using (var db = temp.Open(dim: 8, max: 10, quantization: VectorQuantization.Int8))
        {
            keep = db.AddEntry(Vec.Basis(8, 0), "{\"n\":\"keep\"}");
            moved = db.AddEntry(Vec.Basis(8, 1), "{\"n\":\"moved\"}");
            gone = db.AddEntry(Vec.Basis(8, 2), "{\"n\":\"gone\"}");

            Assert.True(db.UpdateVector(moved, Vec.Basis(8, 5)));
            Assert.True(db.Delete(gone));
            db.Vacuum();

            Assert.Equal(VectorQuantization.Int8, db.Quantization);
            Assert.Equal(2, db.LiveCount);
            Assert.Equal(0, db.DeletedCount);
            Assert.Equal(moved, db.Search(Vec.Basis(8, 5), topK: 1)[0].Id);
            Assert.Equal(keep, db.Search(Vec.Basis(8, 0), topK: 1)[0].Id);
            Assert.Null(db.GetByGuid(gone));
        }

        Assert.Equal(2, ReadHeader(temp.Path).QuantizationMode);
    }

    [Fact]
    public void AddEntry_BeyondInitialCapacity_GrowsQuantizedSections()
    {
        using var temp = new TempDb();
        int maxCount;
        using (var db = temp.Open(dim: 8, max: 4, quantization: VectorQuantization.Int8))
        {
            var ids = new List<Guid>();
            for (int i = 0; i < 20; i++) ids.Add(db.AddEntry(Vec.Basis(8, i % 8), $"{{\"i\":{i}}}"));

            Assert.Equal(20, db.LiveCount);
            Assert.True(db.MaxCount >= 20);
            maxCount = db.MaxCount;

            // Every row must still be reachable and score correctly after the sections moved.
            for (int i = 0; i < 20; i++)
            {
                var hits = db.Search(Vec.Basis(8, i % 8), topK: 3);
                Assert.Contains(ids[i], hits.Select(h => h.Id));
                Assert.Equal(1f, hits[0].Score, precision: 3);
            }
        }

        var header = ReadHeader(temp.Path);
        Assert.True(header.GetRequiredSection(V4SectionIds.QuantizedVectors).Length >= maxCount * 8L);
        Assert.True(header.GetRequiredSection(V4SectionIds.QuantizationVectorParameters).Length >= maxCount * 16L);
    }

    [Fact]
    public void FilteredSearch_WorksOnQuantizedDatabase()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 50, quantization: VectorQuantization.Int8);
        for (int i = 0; i < 40; i++) db.AddEntry(Vec.Basis(8, i % 8), i % 2 == 0 ? "even" : "odd");

        var hits = db.Search(Vec.Basis(8, 1), m => m == "odd", topK: 5);
        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal("odd", h.Metadata));
    }

    private static V4Header ReadHeader(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return V4Header.Read(bytes.AsSpan(0, V4Header.HeaderSizeValue), bytes.LongLength);
    }
}
