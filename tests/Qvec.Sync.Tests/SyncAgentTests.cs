using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Sync.Tests;

public class SyncAgentTests
{
    private const int Dim = 8;

    private static Guid Add(QvecDatabase db, int basis, string metadata) => db.AddEntry(Vec.Basis(Dim, basis), metadata);

    private static SyncOptions Quick() => new() { PollInterval = TimeSpan.FromMilliseconds(50), InitialBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(100) };

    [Fact]
    public void Constructor_RequiresChangeTracking()
    {
        using var tmp = new TempDir();
        using var db = new QvecDatabase(tmp.File("plain.qvec"), Dim, 10);
        Assert.Throws<ArgumentException>(() => new SyncAgent(db, new InMemoryHubPeer(db)));
    }

    [Fact]
    public async Task TwoAgents_OverDirectory_Converge_AndPersistState()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        using var b = tmp.OpenDb("b.qvec");
        var idA = Add(a, 0, "from a");
        Add(a, 1, "also a");
        var idB = Add(b, 2, "from b");

        await using var agentA = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus")));
        await using var agentB = new SyncAgent(b, new DirectorySyncPeer(tmp.Dir("bus")));

        var r1 = await agentA.SyncOnceAsync();
        Assert.Equal(1, r1.PushedBatches);
        Assert.Equal(2, r1.PushedItems);
        Assert.Equal(0, r1.PulledBatches);

        var r2 = await agentB.SyncOnceAsync();
        Assert.Equal(1, r2.PushedBatches);
        Assert.Equal(1, r2.PulledBatches);
        Assert.Equal(2, r2.Applied);

        var r3 = await agentA.SyncOnceAsync();
        Assert.Equal(1, r3.PulledBatches);
        Assert.Equal(1, r3.Applied);

        DbAssert.SameContent(a, b);
        Assert.NotNull(a.GetByGuid(idB));
        Assert.NotNull(b.GetByGuid(idA));
        Assert.True(File.Exists(tmp.File("a.qvec.sync")));

        // On a bus, applied remote rows are re-announced under our own prefix and skipped by
        // everyone else; a couple of quiet rounds later nobody has anything left to do.
        for (int i = 0; i < 3; i++) { await agentA.SyncOnceAsync(); await agentB.SyncOnceAsync(); }
        Assert.Equal(a.ChangeSeq, agentA.PushedSeq);
        Assert.Equal(b.ChangeSeq, agentB.PushedSeq);
        Assert.False((await agentA.SyncOnceAsync()).DidWork);
        Assert.False((await agentB.SyncOnceAsync()).DidWork);
        DbAssert.SameContent(a, b);
    }

    [Fact]
    public async Task ThreeAgents_Bus_UpdatesAndDeletes_Converge()
    {
        using var tmp = new TempDir();
        var dbs = new[] { tmp.OpenDb("a.qvec"), tmp.OpenDb("b.qvec"), tmp.OpenDb("c.qvec") };
        try
        {
            var agents = dbs.Select(db => new SyncAgent(db, new DirectorySyncPeer(tmp.Dir("bus")))).ToArray();

            var shared = Add(dbs[0], 0, "v1");
            var doomed = Add(dbs[1], 1, "soon gone");
            Add(dbs[2], 2, "c");

            foreach (var agent in agents) await agent.SyncOnceAsync();
            foreach (var agent in agents) await agent.SyncOnceAsync();

            dbs[2].UpdateMetadata(shared, "v2 from c");
            dbs[0].Delete(doomed);
            Add(dbs[1], 3, "late b");

            for (int round = 0; round < 3; round++)
                foreach (var agent in agents) await agent.SyncOnceAsync();

            DbAssert.SameContent(dbs[0], dbs[1]);
            DbAssert.SameContent(dbs[1], dbs[2]);
            Assert.Equal("v2 from c", dbs[0].GetByGuid(shared)!.Value.Metadata);
            Assert.Null(dbs[2].GetByGuid(doomed));

            foreach (var agent in agents) await agent.DisposeAsync();
        }
        finally { foreach (var db in dbs) db.Dispose(); }
    }

    [Fact]
    public async Task Hub_ClientAndServer_Converge_WithoutEcho()
    {
        using var tmp = new TempDir();
        using var server = tmp.OpenDb("server.qvec");
        using var client = tmp.OpenDb("client.qvec");
        Add(server, 0, "s");
        Add(client, 1, "c");

        var hub = new InMemoryHubPeer(server);
        await using var agent = new SyncAgent(client, hub);

        var r = await agent.SyncOnceAsync();
        Assert.Equal(1, r.PushedBatches);
        Assert.Equal(1, r.Applied);
        DbAssert.SameContent(server, client);

        // The server now holds the client's change too; it must not be pushed back to the client.
        var again = await agent.SyncOnceAsync();
        Assert.Equal(0, again.PushedBatches);
        Assert.Equal(0, again.Applied);
        Assert.Equal(1, hub.Pushes);
    }

    [Fact]
    public async Task Restart_ResumesFromState_AndDeletedState_RepushesIdempotently()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        using var b = tmp.OpenDb("b.qvec");
        for (int i = 0; i < 5; i++) Add(a, i, "a" + i);

        await using (var agentA = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus"))))
            await agentA.SyncOnceAsync();
        await using (var agentB = new SyncAgent(b, new DirectorySyncPeer(tmp.Dir("bus"))))
            await agentB.SyncOnceAsync();

        // Resume: nothing to do.
        await using (var agentA = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus"))))
        {
            Assert.Equal(5, agentA.PushedSeq);
            Assert.False((await agentA.SyncOnceAsync()).DidWork);
        }

        // Lost state: A re-pushes from the start of the log (same segment name, so a no-op on
        // disk) and B, whose only log records are A's rows, skips every duplicate.
        File.Delete(tmp.File("a.qvec.sync"));
        File.Delete(tmp.File("b.qvec.sync"));
        await using (var agentA = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus"))))
        {
            var r = await agentA.SyncOnceAsync();
            Assert.Equal(1, r.PushedBatches);
            Assert.Equal(5, agentA.PushedSeq);
            Assert.Equal(0, r.Applied);
        }
        await using (var agentB = new SyncAgent(b, new DirectorySyncPeer(tmp.Dir("bus"))))
        {
            var r = await agentB.SyncOnceAsync();
            Assert.True(r.PulledBatches >= 1);
            Assert.Equal(0, r.Applied);
            Assert.Equal(5, r.Skipped);
        }
        DbAssert.SameContent(a, b);
    }

    [Fact]
    public async Task FailureMidPush_NextRunCompletes_WithoutDuplicates()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        using var b = tmp.OpenDb("b.qvec");
        for (int i = 0; i < 6; i++) Add(a, i, "a" + i);

        var flaky = new FlakyPeer(new DirectorySyncPeer(tmp.Dir("bus")), failOnPush: 2);
        await using var agentA = new SyncAgent(a, flaky, new SyncOptions { BatchSize = 2 });
        await Assert.ThrowsAsync<IOException>(() => agentA.SyncOnceAsync());
        Assert.Equal(2, agentA.PushedSeq);

        var r = await agentA.SyncOnceAsync();
        Assert.Equal(2, r.PushedBatches);
        Assert.Equal(6, agentA.PushedSeq);

        await using var agentB = new SyncAgent(b, new DirectorySyncPeer(tmp.Dir("bus")));
        var rb = await agentB.SyncOnceAsync();
        Assert.Equal(6, rb.Applied);
        DbAssert.SameContent(a, b);
    }

    [Fact]
    public async Task LocalRingOverrun_ThrowsSyncLogOverrun()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec", logCapacity: 4);
        for (int i = 0; i < 10; i++) Add(a, i % Dim, "a" + i);

        await using var agent = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus")));
        var ex = await Assert.ThrowsAsync<SyncLogOverrunException>(() => agent.SyncOnceAsync());
        Assert.Equal(0, ex.PushedSeq);
        Assert.Equal(a.OldestChangeSeq, ex.OldestAvailableSeq);
    }

    [Fact]
    public async Task StateAheadOfLog_ThrowsSyncStateException()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        Add(a, 0, "x");
        new SyncState { ReplicaId = a.ReplicaId, PushedSeq = 99 }.Save(tmp.File("a.qvec.sync"));

        await using var agent = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus")));
        await Assert.ThrowsAsync<SyncStateException>(() => agent.SyncOnceAsync());
    }

    [Fact]
    public async Task CorruptState_FailsFastInConstructor()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        File.WriteAllText(tmp.File("a.qvec.sync"), "garbage");
        Assert.Throws<SyncStateException>(() => new SyncAgent(a, new InMemoryHubPeer(a)));
    }

    [Fact]
    public async Task ClientBehindRing_WithoutBootstrap_Throws()
    {
        using var tmp = new TempDir();
        using var server = tmp.OpenDb("server.qvec", logCapacity: 4);
        using var client = tmp.OpenDb("client.qvec");
        for (int i = 0; i < 10; i++) Add(server, i % Dim, "s" + i);

        await using var agent = new SyncAgent(client, new InMemoryHubPeer(server));
        await Assert.ThrowsAsync<SyncCursorTooOldException>(() => agent.SyncOnceAsync());
    }

    [Fact]
    public async Task ClientBehindRing_BootstrapsFromSnapshot_ThenSyncsNormally()
    {
        using var tmp = new TempDir();
        using var server = tmp.OpenDb("server.qvec", logCapacity: 4);
        var client = tmp.OpenDb("client.qvec");
        try
        {
            Add(client, 7, "client-only, will be lost by design");
            var ids = new List<Guid>();
            for (int i = 0; i < 10; i++) ids.Add(Add(server, i % Dim, "s" + i));
            Guid oldClientId = client.ReplicaId;

            var options = new SyncOptions
            {
                BootstrapFromSnapshotIfBehind = true,
                FieldIndexExtractor = m => [("prefix", m[..1])],
            };
            await using var agent = new SyncAgent(client, new InMemoryHubPeer(server), options);
            QvecDatabase? replaced = null;
            agent.DatabaseReplaced += db => replaced = db;

            var r = await agent.SyncOnceAsync();
            Assert.True(r.Bootstrapped);
            Assert.NotNull(replaced);
            Assert.Same(replaced, agent.Database);
            client = agent.Database;

            Assert.NotEqual(oldClientId, client.ReplicaId);
            Assert.NotEqual(server.ReplicaId, client.ReplicaId);
            Assert.Equal(tmp.File("client.qvec"), client.FilePath);
            Assert.False(File.Exists(tmp.File("client.qvec.bootstrap")));
            Assert.Equal(10, ids.Count(id => client.GetByGuid(id) is not null));
            Assert.Equal(server.ChangeSeq, agent.PushedSeq);
            Assert.Equal(server.ChangeSeq, agent.Cursor[server.ReplicaId]);
            Assert.Equal(client.ReplicaId, SyncState.LoadOrCreate(tmp.File("client.qvec.sync"), client.ReplicaId).ReplicaId);

            // Field index was rebuilt with the configured extractor.
            Assert.Equal(10, client.WhereIndexed("prefix", "s").Count);

            // Normal delta traffic afterwards, both directions.
            var fromClient = Add(client, 3, "after bootstrap");
            var fromServer = Add(server, 4, "server after");
            var r2 = await agent.SyncOnceAsync();
            Assert.False(r2.Bootstrapped);
            Assert.Equal(1, r2.PushedBatches);
            Assert.NotNull(server.GetByGuid(fromClient));
            Assert.NotNull(client.GetByGuid(fromServer));
        }
        finally { client.Dispose(); }
    }

    [Fact]
    public async Task SnapshotInterval_PublishesOnceUntilElapsed()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        Add(a, 0, "x");
        var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await using var agent = new SyncAgent(a, peer, new SyncOptions { SnapshotInterval = TimeSpan.FromHours(1) });

        Assert.True((await agent.SyncOnceAsync()).SnapshotPublished);
        Assert.False((await agent.SyncOnceAsync()).SnapshotPublished);

        Assert.Equal(a.ChangeSeq, peer.ReadManifest(a.ReplicaId)!.SnapshotSeq);
        await using var s = await peer.OpenSnapshotAsync(new SyncCursor(Guid.NewGuid()), CancellationToken.None);
        Assert.NotNull(s);
        Assert.Equal(new FileInfo(a.FilePath).Length, s.Length);
    }

    [Fact]
    public async Task BackgroundLoop_PropagatesChanges_AndStops()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        using var b = tmp.OpenDb("b.qvec");

        await using var agentA = new SyncAgent(a, new DirectorySyncPeer(tmp.Dir("bus")), Quick());
        await using var agentB = new SyncAgent(b, new DirectorySyncPeer(tmp.Dir("bus")), Quick());
        int completed = 0;
        agentB.IterationCompleted += _ => Interlocked.Increment(ref completed);
        agentA.Start();
        agentB.Start();
        Assert.True(agentA.IsRunning);
        Assert.Throws<InvalidOperationException>(agentA.Start);

        var id = Add(a, 0, "hello");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (b.GetByGuid(id) is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(b.GetByGuid(id));

        await agentA.StopAsync();
        await agentB.StopAsync();
        Assert.False(agentA.IsRunning);
        Assert.True(completed > 0);
    }

    [Fact]
    public async Task BackgroundLoop_ReportsFailures_AndKeepsGoing()
    {
        using var tmp = new TempDir();
        using var a = tmp.OpenDb("a.qvec");
        Add(a, 0, "x");

        var flaky = new FlakyPeer(new DirectorySyncPeer(tmp.Dir("bus")), failOnPush: 1);
        await using var agent = new SyncAgent(a, flaky, Quick());
        Exception? failure = null;
        var pushed = new TaskCompletionSource();
        agent.IterationFailed += ex => failure ??= ex;
        agent.IterationCompleted += r => { if (r.PushedBatches > 0) pushed.TrySetResult(); };

        agent.Start();
        await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.StopAsync();

        Assert.IsType<IOException>(failure);
        Assert.Equal(1, agent.PushedSeq);
    }
}
