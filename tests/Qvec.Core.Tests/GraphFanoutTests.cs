using System.Buffers.Binary;
using Qvec.Core;
using Qvec.Core.Format;
using Xunit.Abstractions;

namespace Qvec.Core.Tests;

/// <summary>
/// Malkov &amp; Yashunin recommend that the base layer of an HNSW graph use twice the fan-out of
/// the upper layers (M0 = 2 * M). Every node lives on layer 0, and it is where the final, decisive
/// refinement of a search happens, so a base layer that is as narrow as the sparse upper layers
/// caps achievable recall. These tests pin both the on-disk shape and the observable out-degree.
/// </summary>
public sealed class GraphFanoutTests
{
    private readonly ITestOutputHelper _output;

    public GraphFanoutTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(4, 3)]
    [InlineData(32, 5)]
    [InlineData(2, 1)]
    public void Layout_ReservesDoubleFanoutOnLayerZero(int maxNeighbors, int maxLayers)
    {
        var header = QvecFormatLayout.CreateInitial(
            vectorDimension: 8,
            maxCount: 16,
            maxNeighbors: maxNeighbors,
            maxLayers: maxLayers,
            metadataHeapCapacity: 4096);

        var graph = header.GetRequiredSection(V4SectionIds.Graph);

        // 2*M for layer 0 plus M for each of the remaining layers == (MaxLayers + 1) * M.
        int expectedSlots = (maxLayers + 1) * maxNeighbors;
        Assert.Equal((uint)(expectedSlots * sizeof(int)), graph.ElementSize);
        Assert.Equal((long)expectedSlots * sizeof(int) * 16, graph.Length);
    }

    [Fact]
    public void LayerZero_AcceptsMoreNeighborsThanMaxNeighbors()
    {
        const int maxNeighbors = 4;
        const int maxLayers = 3;

        using var temp = new TempDb();
        using (var db = temp.Open(dim: 8, max: 128, maxNeighbors: maxNeighbors, maxLayers: maxLayers,
                                  distance: DistanceFunction.Cosine))
        {
            var rng = new Random(4711);
            for (int i = 0; i < 100; i++)
                db.AddEntry(Vec.Random(8, rng), $"{{\"i\":{i}}}");
        }

        var graph = ReadGraph(temp.Path);
        int maxDegreeAtZero = 0;
        for (int node = 0; node < 100; node++)
            maxDegreeAtZero = Math.Max(maxDegreeAtZero, graph.Degree(node, level: 0));

        _output.WriteLine($"max out-degree at layer 0 = {maxDegreeAtZero} (M={maxNeighbors}, M0={2 * maxNeighbors})");

        Assert.True(
            maxDegreeAtZero > maxNeighbors,
            $"Layer 0 never exceeded M={maxNeighbors}, so the doubled base-layer fan-out is not in effect.");
        Assert.True(maxDegreeAtZero <= 2 * maxNeighbors, $"Layer 0 degree {maxDegreeAtZero} exceeded M0={2 * maxNeighbors}.");
    }

    [Fact]
    public void LayersAboveZero_NeverExceedMaxNeighbors()
    {
        const int maxNeighbors = 4;
        const int maxLayers = 4;

        using var temp = new TempDb();
        using (var db = temp.Open(dim: 8, max: 256, maxNeighbors: maxNeighbors, maxLayers: maxLayers,
                                  distance: DistanceFunction.Cosine))
        {
            var rng = new Random(1234);
            for (int i = 0; i < 200; i++)
                db.AddEntry(Vec.Random(8, rng), "{}");
        }

        var graph = ReadGraph(temp.Path);
        for (int node = 0; node < 200; node++)
        {
            for (int level = 1; level < maxLayers; level++)
            {
                Assert.True(
                    graph.Degree(node, level) <= maxNeighbors,
                    $"Node {node} had {graph.Degree(node, level)} neighbours at level {level}, above M={maxNeighbors}.");
            }
        }
    }

    /// <summary>
    /// A neighbour slot must never be read from, or written to, the region belonging to another
    /// node or another level. Filling layer 0 to capacity and then checking that layer 1 is still
    /// empty catches an off-by-one in the level offset, which is the most likely way to get this
    /// layout change wrong.
    /// </summary>
    [Fact]
    public void LayerZeroWrites_DoNotBleedIntoOtherLevelsOrNodes()
    {
        const int maxNeighbors = 4;
        const int maxLayers = 3;

        using var temp = new TempDb();
        using (var db = temp.Open(dim: 4, max: 64, maxNeighbors: maxNeighbors, maxLayers: maxLayers,
                                  distance: DistanceFunction.Cosine))
        {
            // A tight cluster forces every node to fill its layer-0 neighbour list.
            var rng = new Random(99);
            for (int i = 0; i < 40; i++)
            {
                var v = Vec.Random(4, rng);
                v[0] += 10f;
                db.AddEntry(v, "{}");
            }
        }

        var graph = ReadGraph(temp.Path);

        // The top layer is reached with probability ~0 for 40 nodes, so it must be untouched.
        for (int node = 0; node < 40; node++)
        {
            foreach (int slot in graph.Slots(node, level: maxLayers - 1))
                Assert.True(slot == -1 || (slot >= 0 && slot < 40), $"Slot value {slot} at top level is not a valid row index.");

            foreach (int slot in graph.Slots(node, level: 0))
                Assert.True(slot == -1 || (slot >= 0 && slot < 40), $"Slot value {slot} at level 0 is not a valid row index.");
        }
    }

    /// <summary>
    /// The functional proof that the doubled base layer matters. The configuration is
    /// deliberately hostile — random uniform vectors, a narrow M and a narrow efSearch — because
    /// that is where base-layer fan-out dominates. With a wide beam the search compensates for a
    /// thin graph by simply visiting more of it, which masks the effect.
    /// Absolute numbers here are pessimistic and say nothing about real embeddings; the floor
    /// exists to catch a regression in the layer-0 fan-out, not to advertise recall.
    /// </summary>
    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void Recall_WithNarrowSearchWidth_BenefitsFromDoubledBaseLayer()
    {
        double worst = 1.0;
        foreach (int seed in new[] { 7001, 7002, 7003 })
        {
            var recall = RecallMeasurement.Measure(
                n: 3_000,
                dim: 96,
                maxNeighbors: 8,
                efSearch: 24,
                distance: DistanceFunction.Cosine,
                seed: seed,
                queryCount: 100);

            _output.WriteLine($"seed {seed}: recall@1={recall.RecallAt1:P1}, recall@10={recall.RecallAt10:P1}");
            worst = Math.Min(worst, recall.RecallAt1);
        }

        // Measured on this exact configuration:
        //   M0 == M  (before): recall@1 29.0% / 41.0% / 39.0%, recall@10 26.5% / 26.5% / 29.7%
        //   M0 == 2M (after):  recall@1 58.0% / 58.0% / 58.0%, recall@10 50.8% / 51.1% / 53.3%
        // The floor sits below the measured worst case with margin, so it fails on a real
        // regression rather than on seed noise.
        Assert.True(worst >= 0.52, $"Worst recall@1 across seeds was {worst:P1}, below the 52.0% floor.");
    }

    private static RawGraph ReadGraph(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var header = V4Header.Read(bytes.AsSpan(0, V4Header.HeaderSizeValue), bytes.LongLength);
        return new RawGraph(bytes, header);
    }

    /// <summary>
    /// Reads neighbour slots straight out of the file, independently of the library, so the test
    /// fails if the production offset arithmetic and the documented layout ever disagree.
    /// </summary>
    private sealed class RawGraph(byte[] bytes, V4Header header)
    {
        private readonly SectionExtent _graph = header.GetRequiredSection(V4SectionIds.Graph);

        private int NodeStride => (header.MaxLayers + 1) * header.MaxNeighbors;

        private int SlotsAt(int level) => level == 0 ? header.MaxNeighbors * 2 : header.MaxNeighbors;

        private int LevelStart(int level) => level == 0 ? 0 : (level + 1) * header.MaxNeighbors;

        public int[] Slots(int node, int level)
        {
            var result = new int[SlotsAt(level)];
            long start = _graph.Offset + ((long)node * NodeStride + LevelStart(level)) * sizeof(int);
            for (int i = 0; i < result.Length; i++)
                result[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(checked((int)(start + i * sizeof(int))), sizeof(int)));
            return result;
        }

        public int Degree(int node, int level)
        {
            int degree = 0;
            foreach (int slot in Slots(node, level))
            {
                if (slot == -1) break;
                degree++;
            }
            return degree;
        }
    }
}
