using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Regression tests for the input-validation and empty-database guards added in
/// the Phase 0 hardening pass. These correspond to findings #3, #6 and #25 in the
/// library review and previously reproduced as out-of-bounds reads, phantom
/// results and untyped <c>Exception("DB Full")</c> respectively.
/// </summary>
public class ValidationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(64)]
    public void Search_WithWrongDimension_ThrowsInsteadOfReadingOutOfBounds(int wrongDim)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        db.AddEntry(Vec.Basis(8, 0), "{}");

        var bad = new float[wrongDim];

        Assert.Throws<QvecDimensionException>(() => db.Search(bad, 1));
        Assert.Throws<QvecDimensionException>(() => db.SearchSimple(bad, 1));
        Assert.Throws<QvecDimensionException>(() => db.SearchSimpleParallel(bad, 1));
        Assert.Throws<QvecDimensionException>(() => db.Search(bad, _ => true, 1));
    }

    [Fact]
    public void DimensionException_CarriesExpectedAndActual()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        db.AddEntry(Vec.Basis(8, 0), "{}");

        var ex = Assert.Throws<QvecDimensionException>(() => db.Search(new float[3], 1));
        Assert.Equal(8, ex.Expected);
        Assert.Equal(3, ex.Actual);
    }

    [Fact]
    public void AddEntry_WithWrongDimension_Throws()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        Assert.Throws<QvecDimensionException>(() => db.AddEntry(new float[3], "{}"));
    }

    [Fact]
    public void AddEntry_WithNullArguments_Throws()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        Assert.Throws<ArgumentNullException>(() => db.AddEntry(null!, "{}"));
        Assert.Throws<ArgumentNullException>(() => db.AddEntry(Vec.Basis(8, 0), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Search_WithNonPositiveTopK_Throws(int topK)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        db.AddEntry(Vec.Basis(8, 0), "{}");

        Assert.Throws<ArgumentOutOfRangeException>(() => db.Search(Vec.Basis(8, 0), topK));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.SearchSimple(Vec.Basis(8, 0), topK));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Search_WithNonPositiveEfSearch_Throws(int efSearch)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8);
        db.AddEntry(Vec.Basis(8, 0), "{}");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => db.Search(Vec.Basis(8, 0), topK: 1, efSearch: efSearch));
    }

    [Fact]
    public void Search_OnEmptyDatabase_ReturnsNoResults()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);

        var q = Vec.Basis(4, 0);
        Assert.Empty(db.Search(q, 3));
        Assert.Empty(db.SearchSimple(q, 3));
        Assert.Empty(db.SearchSimpleParallel(q, 3));
        Assert.Empty(db.Search(q, _ => true, 3));
    }

    [Fact]
    public void Search_AfterDeletingEveryEntry_ReturnsNoResults()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);

        var id = db.AddEntry(Vec.Basis(4, 0), "{}");
        Assert.True(db.Delete(id));

        Assert.Empty(db.Search(Vec.Basis(4, 0), 3));
        Assert.Equal(0, db.LiveCount);
    }

    [Fact]
    public void AddEntry_BeyondCapacity_GrowsByDefaultButThrowsWhenGrowthIsDisabled()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 1);

        db.AddEntry(Vec.Basis(4, 0), "{}");

        // max is a starting size, not a ceiling: by default the file grows instead.
        db.AddEntry(Vec.Basis(4, 1), "{}");
        Assert.Equal(2, db.LiveCount);

        // Callers that need a hard bound -- PartitionedQvecDatabase in particular -- opt out,
        // and then a full database still reports itself as full.
        db.AutoGrow = false;
        int ceiling = db.MaxCount;
        for (int i = db.GetCount(); i < ceiling; i++)
            db.AddEntry(Vec.Basis(4, i % 4), "{}");

        var ex = Assert.Throws<QvecFullException>(() => db.AddEntry(Vec.Basis(4, 2), "{}"));
        Assert.Equal(ceiling, ex.MaxCount);
        Assert.IsAssignableFrom<QvecException>(ex);
    }

    [Fact]
    public void Update_WithBothArgumentsNull_Throws()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4);
        var id = db.AddEntry(Vec.Basis(4, 0), "{}");

        Assert.Throws<ArgumentException>(() => db.Update(id, null, null));
    }

    [Fact]
    public void PublicSurface_ExposesCapacityAndDimension()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 100);

        Assert.Equal(8, db.VectorDimension);
        Assert.Equal(100, db.MaxCount);
        Assert.Equal(0, db.LiveCount);
        Assert.Equal(0, db.DeletedCount);

        var id = db.AddEntry(Vec.Basis(8, 0), "{}");
        Assert.Equal(1, db.LiveCount);

        db.Delete(id);
        Assert.Equal(1, db.DeletedCount);
        Assert.Equal(0, db.LiveCount);
    }
}
