namespace Qvec.Sync.Tests;

public class SyncCursorTests
{
    [Fact]
    public void UnknownReplica_IsZero()
    {
        var cursor = new SyncCursor(Guid.NewGuid());
        Assert.Equal(0, cursor[Guid.NewGuid()]);
        Assert.Empty(cursor.Positions);
    }

    [Fact]
    public void Advance_NeverMovesBackwards()
    {
        var cursor = new SyncCursor(Guid.NewGuid());
        var r = Guid.NewGuid();

        Assert.True(cursor.Advance(r, 10));
        Assert.False(cursor.Advance(r, 10));
        Assert.False(cursor.Advance(r, 3));
        Assert.True(cursor.Advance(r, 11));
        Assert.Equal(11, cursor[r]);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var self = Guid.NewGuid();
        var r = Guid.NewGuid();
        var cursor = new SyncCursor(self);
        cursor.Advance(r, 5);

        var copy = cursor.Clone();
        copy.Advance(r, 9);

        Assert.Equal(self, copy.Self);
        Assert.Equal(5, cursor[r]);
        Assert.Equal(9, copy[r]);
    }
}

public class SyncStateTests
{
    [Fact]
    public void Missing_YieldsEmptyStateForReplica()
    {
        using var tmp = new TempDir();
        var id = Guid.NewGuid();
        var state = SyncState.LoadOrCreate(tmp.File("x.sync"), id);

        Assert.Equal(id, state.ReplicaId);
        Assert.Equal(0, state.PushedSeq);
        Assert.Empty(state.Cursor);
        Assert.Null(state.LastSnapshotUtc);
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        using var tmp = new TempDir();
        string path = tmp.File("x.sync");
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        new SyncState { ReplicaId = id, PushedSeq = 42, Cursor = { [other] = 7 }, LastSnapshotUtc = when }.Save(path);
        var loaded = SyncState.LoadOrCreate(path, id);

        Assert.Equal(42, loaded.PushedSeq);
        Assert.Equal(7, loaded.Cursor[other]);
        Assert.Equal(when, loaded.LastSnapshotUtc);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupt_ThrowsSyncStateException_NamingThePath()
    {
        using var tmp = new TempDir();
        string path = tmp.File("x.sync");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<SyncStateException>(() => SyncState.LoadOrCreate(path, Guid.NewGuid()));
        Assert.Equal(path, ex.Path);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void OtherReplica_Throws()
    {
        using var tmp = new TempDir();
        string path = tmp.File("x.sync");
        new SyncState { ReplicaId = Guid.NewGuid(), PushedSeq = 1 }.Save(path);

        Assert.Throws<SyncStateException>(() => SyncState.LoadOrCreate(path, Guid.NewGuid()));
    }

    [Fact]
    public void NegativeSeq_Throws()
    {
        using var tmp = new TempDir();
        string path = tmp.File("x.sync");
        var id = Guid.NewGuid();
        File.WriteAllText(path, "{\"ReplicaId\":\"" + id + "\",\"PushedSeq\":-1,\"Cursor\":{}}");

        Assert.Throws<SyncStateException>(() => SyncState.LoadOrCreate(path, id));
    }
}
