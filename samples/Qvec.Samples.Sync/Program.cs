// Two (or more) processes, each with its own Qvec file, converge through a shared folder.
//
//   dotnet run -- <db.qvec> <shared-dir> add "some text"   # insert a document and sync once
//   dotnet run -- <db.qvec> <shared-dir> watch              # keep syncing in the background, print arrivals
//   dotnet run -- <db.qvec> <shared-dir> list               # sync once and print everything this replica has
//
// Start `watch` in one terminal against a.qvec, then run `add` from another terminal against
// b.qvec with the same shared dir: the row shows up in the first terminal within a poll interval.
// The "vectors" here are a toy hash of the text so the sample has no embedding dependency.

using Qvec.Core;
using Qvec.Core.Sync;
using Qvec.Sync;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <db.qvec> <shared-dir> add <text> | watch | list");
    return 2;
}

const int Dim = 16;
string dbPath = args[0];
string sharedDir = args[1];
string command = args[2];

// Not `using`: a snapshot bootstrap may replace the instance, so dispose agent.Database at the end instead.
var db = File.Exists(dbPath)
    ? QvecDatabase.Open(dbPath)
    : new QvecDatabase(dbPath, Dim, max: 10_000, changeTracking: new ChangeTrackingOptions());

if (!db.IsChangeTrackingEnabled)
{
    Console.Error.WriteLine($"{dbPath} was created without change tracking; only tracked databases can sync.");
    return 1;
}

await using var peer = new DirectorySyncPeer(sharedDir);
await using var agent = new SyncAgent(db, peer, new SyncOptions
{
    PollInterval = TimeSpan.FromSeconds(1),
    BootstrapFromSnapshotIfBehind = true,
    SnapshotInterval = TimeSpan.FromMinutes(5),
});
agent.IterationFailed += ex => Console.Error.WriteLine($"sync failed: {ex.GetType().Name}: {ex.Message}");

Console.WriteLine($"replica {db.ReplicaId:D} · {db.LiveCount} docs · log seq {db.ChangeSeq}");

try
{
    return await RunAsync();
}
finally
{
    // The agent does not own the database; dispose whatever instance is live after a possible bootstrap.
    await agent.StopAsync();
    agent.Database.Dispose();
}

async Task<int> RunAsync()
{
    switch (command)
    {
        case "add":
            if (args.Length < 4) { Console.Error.WriteLine("add needs a text argument"); return 2; }
            string text = string.Join(' ', args[3..]);
            Guid id = agent.Database.AddEntry(ToyEmbedding(text), text);
            Console.WriteLine($"added {id:D}");
            Report(await agent.SyncOnceAsync());
            return 0;

        case "list":
            Report(await agent.SyncOnceAsync());
            foreach (var (docId, metadata) in agent.Database.Where(_ => true, maxResults: 1000).OrderBy(r => r.Metadata))
                Console.WriteLine($"  {docId:D}  {metadata}");
            return 0;

        case "watch":
            agent.IterationCompleted += r =>
            {
                if (r.Applied > 0) Console.WriteLine($"{DateTime.Now:T} received {r.Applied} change(s); now {agent.Database.LiveCount} docs");
                if (r.Bootstrapped) Console.WriteLine($"{DateTime.Now:T} bootstrapped from a snapshot; replica is now {agent.Database.ReplicaId:D}");
            };
            agent.Start();
            Console.WriteLine("watching; press Ctrl+C to stop");
            using (var stop = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
            }
            await agent.StopAsync();
            return 0;

        default:
            Console.Error.WriteLine($"unknown command '{command}'");
            return 2;
    }
}

static void Report(SyncIterationResult r)
    => Console.WriteLine($"pushed {r.PushedItems} item(s) in {r.PushedBatches} batch(es), pulled {r.PulledBatches} batch(es): applied {r.Applied}, skipped {r.Skipped}, rejected {r.Rejected}");

static float[] ToyEmbedding(string text)
{
    var v = new float[Dim];
    foreach (char c in text.ToLowerInvariant()) v[c % Dim] += 1f;
    float norm = MathF.Sqrt(v.Sum(x => x * x));
    if (norm > 0) for (int i = 0; i < Dim; i++) v[i] /= norm;
    return v;
}
