using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

/// <summary>
/// Replicas that mutate offline and then exchange change batches must end up with the same
/// documents, versions, metadata and vectors, regardless of the interleaving. Seeds are
/// reported on failure so a run can be reproduced.
/// </summary>
public class ConvergenceTests
{
    private const int Dim = 8;

    [Theory]
    [InlineData(2, 20)]
    [InlineData(3, 20)]
    public void OfflineMutations_ThenExchange_Converge(int replicas, int iterations)
        => RunMany(replicas, iterations, baseSeed: 1000);

    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void OfflineMutations_ThenExchange_Converge_1000Iterations()
        => RunMany(replicas: 2, iterations: 1000, baseSeed: 50_000);

    private static void RunMany(int replicas, int iterations, int baseSeed)
    {
        for (int i = 0; i < iterations; i++)
        {
            int seed = baseSeed + i;
            try { RunOne(replicas, seed); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"Convergence failed for seed {seed}: {ex.Message}", ex); }
        }
    }

    private static void RunOne(int replicaCount, int seed)
    {
        var rng = new Random(seed);
        var tmps = new TempDb[replicaCount];
        var dbs = new QvecDatabase[replicaCount];
        try
        {
            for (int r = 0; r < replicaCount; r++)
            {
                tmps[r] = new TempDb();
                dbs[r] = tmps[r].Open(dim: Dim, max: 64, changeTracking: new ChangeTrackingOptions { LogCapacity = 4096 });
            }

            // cursors[i, j] = how far replica i has read replica j's log.
            var cursors = new long[replicaCount, replicaCount];
            var pool = new List<Guid>();
            for (int i = 0; i < 12; i++) pool.Add(Guid.NewGuid());

            int rounds = rng.Next(1, 4);
            for (int round = 0; round < rounds; round++)
            {
                foreach (var db in dbs)
                {
                    int ops = rng.Next(0, 8);
                    for (int k = 0; k < ops; k++) Mutate(db, pool, rng);
                }

                Exchange(dbs, cursors, rng);
            }

            for (int r = 1; r < replicaCount; r++)
                ApplyChangesTests.AssertSameContent(dbs[0], dbs[r]);
            foreach (var db in dbs) Assert.True(db.IsHealthy());
        }
        finally
        {
            foreach (var db in dbs) db?.Dispose();
            foreach (var tmp in tmps) tmp?.Dispose();
        }
    }

    private static void Mutate(QvecDatabase db, List<Guid> pool, Random rng)
    {
        var id = pool[rng.Next(pool.Count)];
        bool exists = db.GetByGuid(id) is not null;
        int roll = rng.Next(10);
        if (!exists || roll < 4)
        {
            if (exists) db.Update(id, Vec.Random(Dim, rng), $"u{rng.Next(1000)}");
            else db.AddEntry(Vec.Random(Dim, rng), $"a{rng.Next(1000)}", id);
        }
        else if (roll < 7) db.UpdateMetadata(id, $"m{rng.Next(1000)}");
        else db.Delete(id);
    }

    /// <summary>Random pairwise pulls until a full pass moves nothing.</summary>
    private static void Exchange(QvecDatabase[] dbs, long[,] cursors, Random rng)
    {
        int n = dbs.Length;
        bool moved = true;
        int guard = 0;
        while (moved)
        {
            moved = false;
            Assert.True(++guard < 100, "exchange did not settle");

            var pairs = new List<(int To, int From)>();
            for (int to = 0; to < n; to++)
                for (int from = 0; from < n; from++)
                    if (to != from) pairs.Add((to, from));
            pairs = pairs.OrderBy(_ => rng.Next()).ToList();

            foreach (var (to, from) in pairs)
            {
                int maxItems = rng.Next(1, 6);
                long before = cursors[to, from];
                var batch = dbs[from].GetChanges(before, maxItems, excludeOrigin: dbs[to].ReplicaId);
                if (batch.Items.Count > 0)
                {
                    var result = dbs[to].ApplyChanges(batch);
                    Assert.Equal(0, result.Rejected);
                }
                cursors[to, from] = batch.ToSeq;
                if (batch.ToSeq != before) moved = true;
            }
        }
    }
}
