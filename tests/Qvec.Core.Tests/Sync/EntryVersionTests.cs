using Qvec.Core;
using Qvec.Core.Format;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class EntryVersionTests
{
    private static readonly ChangeTrackingOptions Tracking = new();

    [Fact]
    public void Untracked_ByDefault_AndTryGetVersionReturnsFalse()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var id = db.AddEntry(Vec.Basis(8, 0), "a");

        Assert.False(db.IsChangeTrackingEnabled);
        Assert.Equal(Guid.Empty, db.ReplicaId);
        Assert.False(db.TryGetVersion(id, out _));
    }

    [Fact]
    public void Untracked_FileIsByteIdenticalToReference()
    {
        // Generated once by the 2.0.0-era code path (before change tracking existed) with exactly
        // these parameters and operations. Anything that alters the untracked layout breaks
        // compatibility with files in the wild and must fail here.
        using var tmp = new TempDb();
        using (var db = new QvecDatabase(tmp.Path, 8, 50, 4, 3, DistanceFunction.Cosine, indexSeed: 7))
        {
            var rng = new Random(1);
            for (int i = 0; i < 40; i++) db.AddEntry(Vec.Random(8, rng), $"m{i}", new Guid(i + 1, 0, 0, new byte[8]));
            db.Delete(new Guid(3, 0, 0, new byte[8]));
            db.UpdateMetadata(new Guid(5, 0, 0, new byte[8]), "changed");
        }

        var actual = File.ReadAllBytes(tmp.Path);
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v5-untracked-reference.qvec"));
        Assert.Equal(expected.Length, actual.Length);

        // CRC (28..31) and the two timestamps (168..183) legitimately differ between runs;
        // every other byte, header and data alike, must match the reference exactly.
        for (int i = 0; i < expected.Length; i++)
        {
            if (i is >= 28 and < 32 or >= 168 and < 184) continue;
            if (expected[i] != actual[i])
                Assert.Fail($"Byte {i} differs from the 2.0.0 reference file: expected 0x{expected[i]:X2}, actual 0x{actual[i]:X2}.");
        }

        var header = V4Header.Read(actual, actual.Length);
        Assert.Equal(5, header.Version);
        Assert.False(header.HasChangeTracking);
        Assert.Equal(7, header.Sections.Count(s => s.SectionId != V4SectionIds.Unused));
    }

    [Fact]
    public void Tracked_NewFile_HasReplicaIdAndVersion6()
    {
        using var tmp = new TempDb();
        Guid replica;
        using (var db = tmp.Open(changeTracking: Tracking))
        {
            Assert.True(db.IsChangeTrackingEnabled);
            replica = db.ReplicaId;
            Assert.NotEqual(Guid.Empty, replica);
        }

        var header = V4Header.Read(File.ReadAllBytes(tmp.Path), new FileInfo(tmp.Path).Length);
        Assert.Equal(V4Header.ChangeTrackingFormatVersion, header.Version);
        Assert.Equal(replica, header.ReplicaId);

        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.True(reopened.IsChangeTrackingEnabled);
        Assert.Equal(replica, reopened.ReplicaId);
    }

    [Fact]
    public void Tracked_ExplicitReplicaIdAndLogCapacity_AreHonoured()
    {
        using var tmp = new TempDb();
        var replica = Guid.NewGuid();
        using var db = tmp.Open(max: 100, changeTracking: new ChangeTrackingOptions { ReplicaId = replica, LogCapacity = 10 });

        Assert.Equal(replica, db.ReplicaId);
        Assert.Equal(10, db.ChangeLogCapacity);
    }

    [Fact]
    public void Tracked_InvalidOptions_Throw()
    {
        using var tmp = new TempDb();
        Assert.Throws<ArgumentOutOfRangeException>(() => tmp.Open(changeTracking: new ChangeTrackingOptions { LogCapacity = 0 }));
        Assert.Throws<ArgumentException>(() => tmp.Open(changeTracking: new ChangeTrackingOptions { ReplicaId = Guid.Empty }));
    }

    [Fact]
    public void AddEntry_StampsVersionWithLocalOrigin()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);

        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        var b = db.AddEntry(Vec.Basis(8, 1), "b");

        Assert.True(db.TryGetVersion(a, out var va));
        Assert.True(db.TryGetVersion(b, out var vb));
        Assert.Equal(db.ReplicaId, va.Origin);
        Assert.Equal(db.ReplicaId, vb.Origin);
        Assert.True(va.Hlc > 0);
        Assert.True(vb > va, "later write must get a strictly newer version");
        Assert.Equal(vb.Hlc, db.LastHlc);
    }

    [Fact]
    public void AddEntries_StampsEveryRow_InStrictlyIncreasingOrder()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(max: 1000, changeTracking: Tracking);
        var rng = new Random(3);
        var inserts = Enumerable.Range(0, 500).Select(i => new QvecInsert(Vec.Random(8, rng), $"m{i}")).ToList();

        var ids = db.AddEntries(inserts, maxDegreeOfParallelism: 4);

        long previous = 0;
        foreach (var id in ids)
        {
            Assert.True(db.TryGetVersion(id, out var v));
            Assert.Equal(db.ReplicaId, v.Origin);
            Assert.True(v.Hlc > previous);
            previous = v.Hlc;
        }
        Assert.Equal(previous, db.LastHlc);
    }

    [Fact]
    public void UpdateMetadata_BumpsVersion()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "a");
        db.TryGetVersion(id, out var before);

        Assert.True(db.UpdateMetadata(id, "b"));

        Assert.True(db.TryGetVersion(id, out var after));
        Assert.True(after > before);
    }

    [Fact]
    public void UpdateVector_VersionFollowsGuidToNewRow()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "a");
        db.AddEntry(Vec.Basis(8, 1), "b");
        db.TryGetVersion(id, out var before);

        Assert.True(db.UpdateVector(id, Vec.Basis(8, 2)));

        Assert.True(db.TryGetVersion(id, out var after));
        Assert.True(after > before);
        Assert.Equal(db.ReplicaId, after.Origin);
    }

    [Fact]
    public void Update_BothPaths_BumpVersion()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "a");
        db.TryGetVersion(id, out var v0);

        Assert.True(db.Update(id, null, "meta only"));
        db.TryGetVersion(id, out var v1);
        Assert.True(v1 > v0);

        Assert.True(db.Update(id, Vec.Basis(8, 3), null));
        db.TryGetVersion(id, out var v2);
        Assert.True(v2 > v1);
    }

    [Fact]
    public void Delete_RemovesVersionLookup()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "a");

        Assert.True(db.Delete(id));

        Assert.False(db.TryGetVersion(id, out _));
    }

    [Fact]
    public void Versions_SurviveReopen_AndLastHlcIsMonotone()
    {
        using var tmp = new TempDb();
        Guid id;
        EntryVersion v;
        long lastHlc;
        using (var db = tmp.Open(changeTracking: Tracking))
        {
            id = db.AddEntry(Vec.Basis(8, 0), "a");
            db.TryGetVersion(id, out v);
            lastHlc = db.LastHlc;
        }

        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.Equal(lastHlc, reopened.LastHlc);
        Assert.True(reopened.TryGetVersion(id, out var again));
        Assert.Equal(v, again);

        var next = reopened.AddEntry(Vec.Basis(8, 1), "b");
        reopened.TryGetVersion(next, out var vn);
        Assert.True(vn > v);
    }

    [Fact]
    public void Versions_SurviveGrow()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(max: 4, changeTracking: Tracking);
        var versions = new Dictionary<Guid, EntryVersion>();
        for (int i = 0; i < 4; i++)
        {
            var id = db.AddEntry(Vec.Basis(8, i), $"m{i}");
            db.TryGetVersion(id, out var v);
            versions[id] = v;
        }
        var replica = db.ReplicaId;

        // Fifth insert forces a grow, which relays out every section.
        var extra = db.AddEntry(Vec.Basis(8, 4), "extra");

        Assert.True(db.MaxCount > 4);
        Assert.True(db.IsChangeTrackingEnabled);
        Assert.Equal(replica, db.ReplicaId);
        foreach (var (id, expected) in versions)
        {
            Assert.True(db.TryGetVersion(id, out var actual));
            Assert.Equal(expected, actual);
        }
        Assert.True(db.TryGetVersion(extra, out var ve));
        Assert.True(versions.Values.All(v => ve > v));
    }

    [Fact]
    public void Versions_SurviveVacuum()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(max: 50, changeTracking: new ChangeTrackingOptions { LogCapacity = 20 });
        var versions = new Dictionary<Guid, EntryVersion>();
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var id = db.AddEntry(Vec.Basis(8, i % 8), $"m{i}");
            ids.Add(id);
            db.TryGetVersion(id, out var v);
            versions[id] = v;
        }
        db.Delete(ids[2]);
        db.Delete(ids[7]);
        var replica = db.ReplicaId;
        long lastHlc = db.LastHlc;

        db.Vacuum();

        Assert.True(db.IsChangeTrackingEnabled);
        Assert.Equal(replica, db.ReplicaId);
        Assert.Equal(20, db.ChangeLogCapacity);
        Assert.True(db.LastHlc >= lastHlc);
        Assert.Equal(0, db.DeletedCount);
        foreach (var id in ids)
        {
            if (id == ids[2] || id == ids[7])
            {
                Assert.False(db.TryGetVersion(id, out _));
                continue;
            }
            Assert.True(db.TryGetVersion(id, out var actual));
            Assert.Equal(versions[id], actual);
        }

        db.Dispose();
        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.Equal(replica, reopened.ReplicaId);
        Assert.True(reopened.TryGetVersion(ids[0], out var v0));
        Assert.Equal(versions[ids[0]], v0);
    }

    [Fact]
    public void EnableChangeTracking_OnEmptyFile_Works()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Assert.False(db.IsChangeTrackingEnabled);

        db.EnableChangeTracking();

        Assert.True(db.IsChangeTrackingEnabled);
        Assert.NotEqual(Guid.Empty, db.ReplicaId);
        var id = db.AddEntry(Vec.Basis(8, 0), "a");
        Assert.True(db.TryGetVersion(id, out var v));
        Assert.Equal(db.ReplicaId, v.Origin);
    }

    [Fact]
    public void EnableChangeTracking_OnExistingFile_StampsAllLiveRowsAndPreservesData()
    {
        using var tmp = new TempDb();
        var ids = new List<Guid>();
        using (var db = tmp.Open(max: 20))
        {
            var rng = new Random(5);
            for (int i = 0; i < 15; i++) ids.Add(db.AddEntry(Vec.Random(8, rng), $"m{i}"));
            db.Delete(ids[4]);
        }

        var replica = Guid.NewGuid();
        using (var db = QvecDatabase.Open(tmp.Path))
        {
            db.EnableChangeTracking(new ChangeTrackingOptions { ReplicaId = replica, LogCapacity = 64 });

            Assert.True(db.IsChangeTrackingEnabled);
            Assert.Equal(replica, db.ReplicaId);
            Assert.Equal(64, db.ChangeLogCapacity);
            Assert.Equal(14, db.LiveCount);

            long previous = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (i == 4)
                {
                    Assert.False(db.TryGetVersion(ids[i], out _));
                    continue;
                }
                Assert.True(db.TryGetVersion(ids[i], out var v));
                Assert.Equal(replica, v.Origin);
                Assert.True(v.Hlc > previous);
                previous = v.Hlc;
                Assert.Equal($"m{i}", db.GetByGuid(ids[i])!.Value.Metadata);
            }

            var results = db.Search(db.GetByGuid(ids[0])!.Value.Vector, 1);
            Assert.Equal(ids[0], results[0].Id);
        }

        using (var reopened = QvecDatabase.Open(tmp.Path))
        {
            Assert.True(reopened.IsChangeTrackingEnabled);
            Assert.Equal(replica, reopened.ReplicaId);
            Assert.Equal(14, reopened.LiveCount);
            Assert.True(reopened.TryGetVersion(ids[0], out _));
        }
    }

    [Fact]
    public void EnableChangeTracking_Twice_Throws()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);

        Assert.Throws<InvalidOperationException>(() => db.EnableChangeTracking());
    }

    [Fact]
    public void Constructor_OnUntrackedFileWithTrackingRequested_ThrowsWithHint()
    {
        using var tmp = new TempDb();
        using (tmp.Open()) { }

        var ex = Assert.Throws<QvecFormatException>(() => tmp.Open(changeTracking: Tracking));
        Assert.Contains("EnableChangeTracking", ex.Message);
    }

    [Fact]
    public void Constructor_OnTrackedFileWithoutOptions_HonoursFile()
    {
        using var tmp = new TempDb();
        Guid replica;
        using (var db = tmp.Open(changeTracking: Tracking)) replica = db.ReplicaId;

        using var reopened = tmp.Open();
        Assert.True(reopened.IsChangeTrackingEnabled);
        Assert.Equal(replica, reopened.ReplicaId);
    }

    [Fact]
    public void Constructor_OnTrackedFileWithDifferentReplicaId_Throws()
    {
        using var tmp = new TempDb();
        using (tmp.Open(changeTracking: Tracking)) { }

        Assert.Throws<QvecFormatException>(() => tmp.Open(changeTracking: new ChangeTrackingOptions { ReplicaId = Guid.NewGuid() }));
    }

    [Fact]
    public void Tracked_Int8Rescored_StampsVersions()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(quantization: VectorQuantization.Int8Rescored, changeTracking: Tracking);

        var id = db.AddEntry(Vec.Basis(8, 1), "a");

        Assert.True(db.TryGetVersion(id, out var v));
        Assert.Equal(db.ReplicaId, v.Origin);
        Assert.Equal(VectorQuantization.Int8Rescored, db.Quantization);
    }

    [Fact]
    public void EntryVersion_OrdersByHlcThenOrigin()
    {
        var lo = new Guid("00000000-0000-0000-0000-000000000001");
        var hi = new Guid("00000000-0000-0000-0000-000000000002");

        Assert.True(new EntryVersion(1, hi) < new EntryVersion(2, lo));
        Assert.True(new EntryVersion(2, lo) < new EntryVersion(2, hi));
        Assert.True(new EntryVersion(2, hi) >= new EntryVersion(2, hi));
        Assert.Equal(0, new EntryVersion(2, hi).CompareTo(new EntryVersion(2, hi)));
    }
}
