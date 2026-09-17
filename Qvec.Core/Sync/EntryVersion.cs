using System;

namespace Qvec.Core.Sync;

/// <summary>
/// The version of one document: the hybrid logical clock value at which it was last written and
/// the replica that wrote it. Versions from different replicas are totally ordered by
/// <c>(Hlc, Origin)</c>, so two replicas can never produce equal versions for different writes and
/// last-writer-wins has a single, deterministic answer everywhere.
/// </summary>
/// <param name="Hlc">See <see cref="HybridLogicalClock"/>.</param>
/// <param name="Origin">The <c>ReplicaId</c> of the database that produced this version.</param>
public readonly record struct EntryVersion(long Hlc, Guid Origin) : IComparable<EntryVersion>
{
    public int CompareTo(EntryVersion other)
    {
        int byHlc = Hlc.CompareTo(other.Hlc);
        return byHlc != 0 ? byHlc : Origin.CompareTo(other.Origin);
    }

    public static bool operator <(EntryVersion left, EntryVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(EntryVersion left, EntryVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(EntryVersion left, EntryVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(EntryVersion left, EntryVersion right) => left.CompareTo(right) >= 0;
}
