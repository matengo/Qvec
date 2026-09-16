using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Pins what the neighbour-selection heuristic (Malkov &amp; Yashunin, algorithm 4) must decide
/// when a back-link lands on a node whose neighbour list is already full. The vectors are
/// two-dimensional and Euclidean, with a single layer, so every expected outcome can be checked
/// by hand. The tests describe the required <em>result</em>, not how it is computed: the
/// incremental update must agree with a full re-run of the heuristic on these cases.
/// </summary>
public sealed class NeighborDiversityTests
{
    private const int M = 2;          // M0 = 4 slots on layer 0
    private const int Owner = 0;      // (0, 0), inserted first, so row 0 and entry point

    // Four neighbours at distance 10 from the owner in the four axis directions. Each is at
    // least 14.1 from every other, so all four are diverse with respect to the owner and the
    // owner's layer-0 list is exactly full after inserting them.
    private static readonly float[][] Cross =
    [
        [0f, 0f],
        [10f, 0f],
        [-10f, 0f],
        [0f, 10f],
        [0f, -10f],
    ];

    [Fact]
    public void FullList_RejectsNewNodeThatIsCloserToAnExistingNeighbourThanToOwner()
    {
        using var temp = new TempDb();
        int[] before;
        using (var db = temp.Open(dim: 2, max: 16, maxNeighbors: M, maxLayers: 1, distance: DistanceFunction.Euclidean))
        {
            foreach (var v in Cross) db.AddEntry(v, "{}");
            // Row 5: 0.5 from A = (10, 0) but 10.5 from the owner. Linking it to the owner adds
            // nothing that A does not already provide, so the owner must keep its four neighbours.
            db.AddEntry([10.5f, 0f], "{}");
        }

        var graph = RawGraph.Read(temp.Path);
        before = [1, 2, 3, 4];

        Assert.Contains(Owner, graph.Neighbors(5, level: 0));   // the back-link was attempted
        Assert.Equal(before, graph.Neighbors(Owner, level: 0).Order());
    }

    [Fact]
    public void FullList_EvictsTheExistingNeighbourMadeRedundantByACloserNewNode()
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: 2, max: 16, maxNeighbors: M, maxLayers: 1, distance: DistanceFunction.Euclidean))
        {
            foreach (var v in Cross) db.AddEntry(v, "{}");
            // Row 5: 4.0 from the owner, on the way to A = (10, 0). A is now closer to the new
            // node (6.0) than to the owner (10), so A is the one that should go; B, C and D stay.
            db.AddEntry([4f, 0.1f], "{}");
        }

        var graph = RawGraph.Read(temp.Path);

        Assert.Equal(new[] { 2, 3, 4, 5 }, graph.Neighbors(Owner, level: 0).Order());
    }

    [Fact]
    public void FullList_EvictsTheFarthestNeighbourWhenEveryoneIsDiverse()
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: 3, max: 16, maxNeighbors: M, maxLayers: 1, distance: DistanceFunction.Euclidean))
        {
            db.AddEntry([0f, 0f, 0f], "{}");
            // Distances 10, 11, 12, 13 from the owner along ±x and ±y; all mutually diverse.
            db.AddEntry([10f, 0f, 0f], "{}");
            db.AddEntry([0f, 11f, 0f], "{}");
            db.AddEntry([-12f, 0f, 0f], "{}");
            db.AddEntry([0f, -13f, 0f], "{}");
            // Row 5 at distance 3 along z, perpendicular to every existing neighbour, so it is
            // diverse and makes nobody redundant. The plain fallback applies and the farthest
            // existing neighbour (row 4) is dropped.
            db.AddEntry([0f, 0f, 3f], "{}");
        }

        var graph = RawGraph.Read(temp.Path);

        Assert.Equal(new[] { 1, 2, 3, 5 }, graph.Neighbors(Owner, level: 0).Order());
    }

    /// <summary>
    /// Structural invariants that must hold no matter how the replacement is decided: every
    /// slot is either empty or a valid row, nothing appears twice, no node links to itself and
    /// the list never exceeds its capacity.
    /// </summary>
    [Theory]
    [InlineData(DistanceFunction.Euclidean, 31)]
    [InlineData(DistanceFunction.Cosine, 32)]
    [InlineData(DistanceFunction.DotProduct, 33)]
    public void NeighbourLists_StayWellFormedUnderHeavyBackLinkPressure(DistanceFunction distance, int seed)
    {
        const int n = 600;
        const int maxLayers = 3;

        using var temp = new TempDb();
        using (var db = temp.Open(dim: 8, max: n, maxNeighbors: 3, maxLayers: maxLayers, distance: distance, indexSeed: seed))
        {
            var clusters = new Vec.ClusteredVectors(dim: 8, clusterCount: 6, seed: seed);
            var rng = new Random(seed);
            for (int i = 0; i < n; i++) db.AddEntry(clusters.Next(8, rng), "{}");
        }

        var graph = RawGraph.Read(temp.Path);
        for (int node = 0; node < n; node++)
        {
            for (int level = 0; level < maxLayers; level++)
            {
                var slots = graph.Slots(node, level);
                bool seenEmpty = false;
                var seen = new HashSet<int>();
                foreach (int slot in slots)
                {
                    if (slot == -1) { seenEmpty = true; continue; }
                    Assert.False(seenEmpty, $"Node {node} level {level} has a neighbour after an empty slot.");
                    Assert.InRange(slot, 0, n - 1);
                    Assert.NotEqual(node, slot);
                    Assert.True(seen.Add(slot), $"Node {node} level {level} lists neighbour {slot} twice.");
                }
            }
        }
    }
}
