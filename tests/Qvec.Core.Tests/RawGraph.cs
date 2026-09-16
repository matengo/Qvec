using System.Buffers.Binary;
using Qvec.Core.Format;

namespace Qvec.Core.Tests;

/// <summary>
/// Reads neighbour slots straight out of a closed .qvec file, independently of the library, so
/// tests fail if the production offset arithmetic and the documented layout ever disagree.
/// </summary>
internal sealed class RawGraph
{
    private readonly byte[] _bytes;
    private readonly V4Header _header;
    private readonly SectionExtent _graph;

    private RawGraph(byte[] bytes, V4Header header)
    {
        _bytes = bytes;
        _header = header;
        _graph = header.GetRequiredSection(V4SectionIds.Graph);
    }

    public static RawGraph Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var header = V4Header.Read(bytes.AsSpan(0, V4Header.HeaderSizeValue), bytes.LongLength);
        return new RawGraph(bytes, header);
    }

    private int NodeStride => (_header.MaxLayers + 1) * _header.MaxNeighbors;

    private int SlotsAt(int level) => level == 0 ? _header.MaxNeighbors * 2 : _header.MaxNeighbors;

    private int LevelStart(int level) => level == 0 ? 0 : (level + 1) * _header.MaxNeighbors;

    public int[] Slots(int node, int level)
    {
        var result = new int[SlotsAt(level)];
        long start = _graph.Offset + ((long)node * NodeStride + LevelStart(level)) * sizeof(int);
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(checked((int)(start + i * sizeof(int))), sizeof(int)));
        return result;
    }

    /// <summary>Neighbour ids in slot order, stopping at the first empty slot.</summary>
    public int[] Neighbors(int node, int level)
    {
        var slots = Slots(node, level);
        int degree = Array.IndexOf(slots, -1);
        return degree < 0 ? slots : slots[..degree];
    }

    public int Degree(int node, int level) => Neighbors(node, level).Length;
}
