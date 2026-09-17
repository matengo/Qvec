using System;

namespace Qvec.Core.Sync;

/// <summary>
/// A 64-bit hybrid logical clock: the upper 48 bits are wall-clock milliseconds since the Unix
/// epoch, the lower 16 bits a logical counter that breaks ties within a millisecond. Values are
/// strictly increasing per replica, compare as plain <see cref="long"/>s, and stay close to real
/// time, which is what makes last-writer-wins across replicas both deterministic and explainable.
/// </summary>
/// <remarks>
/// Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots in Globally Distributed
/// Databases" (2014). The counter is 16 bits, so more than 65 536 events in one millisecond spill
/// into the next millisecond rather than overflow; the clock then runs marginally ahead of the
/// wall clock until it catches up, which the algorithm tolerates by design.
/// </remarks>
public static class HybridLogicalClock
{
    public const int CounterBits = 16;
    public const int MaxCounter = (1 << CounterBits) - 1;

    public static long Pack(long physicalMs, int counter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalMs);
        ArgumentOutOfRangeException.ThrowIfNegative(counter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(counter, MaxCounter);
        return (physicalMs << CounterBits) | (uint)counter;
    }

    public static long PhysicalMs(long hlc) => hlc >>> CounterBits;

    public static int Counter(long hlc) => (int)(hlc & MaxCounter);

    /// <summary>The next timestamp for a local event, using the real wall clock.</summary>
    public static long Next(long last) => Next(last, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// The next timestamp for a local event given the wall clock reads <paramref name="nowMs"/>.
    /// Always strictly greater than <paramref name="last"/>, even if the wall clock went backwards.
    /// </summary>
    public static long Next(long last, long nowMs)
    {
        long lastPt = PhysicalMs(last);
        long pt = Math.Max(nowMs, lastPt);
        int c = pt == lastPt ? Counter(last) + 1 : 0;
        return Finish(pt, c);
    }

    /// <summary>
    /// The next timestamp after observing <paramref name="remote"/> from another replica, using
    /// the real wall clock. Strictly greater than both <paramref name="last"/> and the remote value.
    /// </summary>
    public static long Receive(long last, long remote) => Receive(last, remote, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static long Receive(long last, long remote, long nowMs)
    {
        long lastPt = PhysicalMs(last);
        long remotePt = PhysicalMs(remote);
        long pt = Math.Max(nowMs, Math.Max(lastPt, remotePt));

        int c;
        if (pt == lastPt && pt == remotePt) c = Math.Max(Counter(last), Counter(remote)) + 1;
        else if (pt == lastPt) c = Counter(last) + 1;
        else if (pt == remotePt) c = Counter(remote) + 1;
        else c = 0;

        return Finish(pt, c);
    }

    private static long Finish(long pt, int c)
    {
        if (c > MaxCounter)
        {
            pt++;
            c = 0;
        }

        return Pack(pt, c);
    }
}
