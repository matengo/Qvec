using System.Collections.Concurrent;
using System.Text.Json;
using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Concurrency regression coverage for the public database surface. Each test
/// uses its own mapped file because xUnit may run this class beside other test
/// classes.
/// </summary>
[Collection(TestCollections.TimingSensitive)]
public class ConcurrencyTests
{
    /// <summary>
    /// Deadline for the cooperative worker loops. It exists to stop a genuine deadlock from
    /// hanging the suite, not to assert throughput: spinning readers can starve the thread pool
    /// on a loaded build agent, so the budget is deliberately far larger than the work needs.
    /// </summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentReaders_ReturnIdenticalSearchResults()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 128);
        for (int i = 0; i < 64; i++)
            db.AddEntry(VectorFor(i, 8), Metadata(i, "seed"));

        var query = Vec.Basis(8, 0);
        var expected = db.SearchSimple(query, 10);
        Assert.NotEmpty(expected);

        using var cts = new CancellationTokenSource(Watchdog);
        var exceptions = new ConcurrentBag<Exception>();
        int workers = Math.Max(2, Environment.ProcessorCount);

        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(() =>
            {
                CaptureExceptions(exceptions, () =>
                {
                    for (int i = 0; i < 100 && !cts.IsCancellationRequested; i++)
                    {
                        var actual = db.SearchSimple(query, 10);
                        AssertSameResults(expected, actual);
                    }
                });
            }, cts.Token))
            .ToArray();

        await WaitForTasks(tasks, cts);
        ThrowIfWorkerExceptions(exceptions);
    }

    [Fact]
    public async Task ReadersDuringWrites_OnlyObserveSaneCommittedResults()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 256);
        for (int i = 0; i < 32; i++)
            db.AddEntry(VectorFor(i, 8), Metadata(i, "seed"));

        using var cts = new CancellationTokenSource(Watchdog);
        var exceptions = new ConcurrentBag<Exception>();
        int writerDone = 0;
        int searchCount = 0;

        var writer = Task.Run(async () =>
        {
            try
            {
                for (int i = 32; i < 112 && !cts.IsCancellationRequested; i++)
                {
                    db.AddEntry(VectorFor(i, 8), Metadata(i, "added"));
                    await Task.Delay(1, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) { exceptions.Add(ex); }
            finally { Volatile.Write(ref writerDone, 1); }
        }, cts.Token);

        var readers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2))
            .Select(_ => Task.Run(() =>
            {
                CaptureExceptions(exceptions, () =>
                {
                    while (Volatile.Read(ref writerDone) == 0 && !cts.IsCancellationRequested)
                    {
                        foreach (var result in db.Search(Vec.Basis(8, 0), topK: 8))
                            AssertSaneResult(result);
                        Interlocked.Increment(ref searchCount);
                    }
                });
            }, cts.Token));

        await WaitForTasks(readers.Prepend(writer).ToArray(), cts);
        ThrowIfWorkerExceptions(exceptions);
        Assert.True(searchCount > 0, "Readers should have searched while the writer was active.");
    }

    [Fact]
    public async Task ConcurrentWriters_AddEveryEntryExactlyOnce()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 512);
        using var cts = new CancellationTokenSource(Watchdog);
        var exceptions = new ConcurrentBag<Exception>();
        var ids = new ConcurrentBag<Guid>();

        int writers = Math.Min(Math.Max(2, Environment.ProcessorCount), 8);
        const int entriesPerWriter = 30;

        var tasks = Enumerable.Range(0, writers)
            .Select(worker => Task.Run(() =>
            {
                CaptureExceptions(exceptions, () =>
                {
                    for (int i = 0; i < entriesPerWriter && !cts.IsCancellationRequested; i++)
                    {
                        int seq = worker * entriesPerWriter + i;
                        ids.Add(db.AddEntry(VectorFor(seq, 8), Metadata(seq, "writer")));
                    }
                });
            }, cts.Token))
            .ToArray();

        await WaitForTasks(tasks, cts);
        ThrowIfWorkerExceptions(exceptions);

        int expected = writers * entriesPerWriter;
        Assert.Equal(expected, ids.Count);
        Assert.Equal(expected, db.LiveCount);
        Assert.Equal(expected, ids.Distinct().Count());
        foreach (Guid id in ids)
            Assert.NotNull(db.GetByGuid(id));
    }

    [Fact]
    public async Task ConcurrentDeleteAndSearch_NeverReturnsPreviouslyDeletedEntries()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 160);
        var ids = new List<Guid>();
        for (int i = 0; i < 96; i++)
            ids.Add(db.AddEntry(VectorFor(i, 8), Metadata(i, "seed")));

        using var cts = new CancellationTokenSource(Watchdog);
        var exceptions = new ConcurrentBag<Exception>();
        var deleted = new ConcurrentDictionary<Guid, byte>();
        int deleterDone = 0;
        int searchCount = 0;

        var deleter = Task.Run(async () =>
        {
            try
            {
                foreach (Guid id in ids.Take(72))
                {
                    if (cts.IsCancellationRequested) break;
                    Assert.True(db.Delete(id));
                    deleted[id] = 0;
                    await Task.Delay(1, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) { exceptions.Add(ex); }
            finally { Volatile.Write(ref deleterDone, 1); }
        }, cts.Token);

        var searchers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2))
            .Select(_ => Task.Run(() =>
            {
                CaptureExceptions(exceptions, () =>
                {
                    while (Volatile.Read(ref deleterDone) == 0 && !cts.IsCancellationRequested)
                    {
                        var deletedBeforeSearch = deleted.Keys.ToHashSet();
                        foreach (var result in db.Search(Vec.Basis(8, 0), topK: 10))
                        {
                            Assert.DoesNotContain(result.Id, deletedBeforeSearch);
                            AssertSaneResult(result);
                        }
                        Interlocked.Increment(ref searchCount);
                    }
                });
            }, cts.Token));

        await WaitForTasks(searchers.Prepend(deleter).ToArray(), cts);
        ThrowIfWorkerExceptions(exceptions);
        Assert.True(searchCount > 0, "Searchers should have exercised tombstones while deletes ran.");
    }

    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public async Task MixedWorkloadSoak_LeavesDatabaseHealthyAndConsistent()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 1_000);
        var live = new ConcurrentDictionary<Guid, byte>();
        int physicalSlots = 0;

        for (int i = 0; i < 60; i++)
        {
            var id = db.AddEntry(VectorFor(i, 8), Metadata(i, "seed"));
            live[id] = 0;
            physicalSlots++;
        }

        using var cts = new CancellationTokenSource(Watchdog);
        var exceptions = new ConcurrentBag<Exception>();
        int nextSeq = 60;

        var tasks = Enumerable.Range(0, Math.Min(Math.Max(4, Environment.ProcessorCount), 8))
            .Select(worker => Task.Run(() =>
            {
                var rng = new Random(10_000 + worker);
                CaptureExceptions(exceptions, () =>
                {
                    for (int i = 0; i < 120 && !cts.IsCancellationRequested; i++)
                    {
                        switch (rng.Next(4))
                        {
                            case 0:
                                int addSeq = Interlocked.Increment(ref nextSeq);
                                Guid newId = db.AddEntry(VectorFor(addSeq, 8), Metadata(addSeq, "mixed-add"));
                                live[newId] = 0;
                                Interlocked.Increment(ref physicalSlots);
                                break;
                            case 1:
                                Guid? updateId = PickRandomKey(live, rng);
                                if (updateId is Guid idToUpdate)
                                {
                                    int updateSeq = Interlocked.Increment(ref nextSeq);
                                    if (db.Update(idToUpdate, VectorFor(updateSeq, 8), Metadata(updateSeq, "mixed-update")))
                                        Interlocked.Increment(ref physicalSlots);
                                }
                                break;
                            case 2:
                                Guid? deleteId = PickRandomKey(live, rng);
                                if (deleteId is Guid idToDelete && live.TryRemove(idToDelete, out _))
                                    Assert.True(db.Delete(idToDelete));
                                break;
                            default:
                                foreach (var result in db.Search(Vec.Basis(8, 0), topK: 8))
                                    AssertSaneResult(result);
                                break;
                        }
                    }
                });
            }, cts.Token))
            .ToArray();

        await WaitForTasks(tasks, cts);
        ThrowIfWorkerExceptions(exceptions);

        Assert.True(db.IsHealthy());
        // Tombstoned slots are reused, so physical slots consumed is bounded by (but no longer
        // equal to) the number of inserts performed.
        Assert.True(db.LiveCount + db.DeletedCount <= Volatile.Read(ref physicalSlots));
        Assert.Equal(live.Count, db.LiveCount);
        foreach (Guid id in live.Keys)
            Assert.NotNull(db.GetByGuid(id));
    }

    /// <summary>
    /// SearchSimpleParallel must serialize against writers the same way SearchSimple
    /// and Search do; it previously read the mapped file with no lock at all.
    /// </summary>
    [Fact]
    public async Task SearchSimpleParallel_BlocksWritersDuringConcurrentMutation()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 128);
        for (int i = 0; i < 64; i++)
            db.AddEntry(VectorFor(i, 8), Metadata(i, "seed"));

        using var cts = new CancellationTokenSource(Watchdog);
        using var filterEntered = new ManualResetEventSlim();
        using var releaseFilter = new ManualResetEventSlim();

        var search = Task.Run(() => db.SearchSimpleParallel(Vec.Basis(8, 0), 8, _ =>
        {
            filterEntered.Set();
            Assert.True(releaseFilter.Wait(TimeSpan.FromSeconds(15)), "Timed out waiting to release the search filter.");
            return true;
        }), cts.Token);

        Assert.True(filterEntered.Wait(TimeSpan.FromSeconds(10)), "SearchSimpleParallel did not enter the filter in time.");

        var writer = Task.Run(() => db.AddEntry(VectorFor(10_000, 8), Metadata(10_000, "concurrent-writer")), cts.Token);
        var writerCompletedEarly = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token)) == writer;

        releaseFilter.Set();
        await WaitForTasks(new Task[] { search, writer }, cts);

        Assert.False(writerCompletedEarly, "A writer committed while SearchSimpleParallel was still in progress.");
    }

    [Fact]
    public async Task DisposeDuringSearch_CompletesOrThrowsObjectDisposedExceptionWithoutHanging()
    {
        using var temp = new TempDb();
        var db = temp.Open(dim: 8, max: 128);
        for (int i = 0; i < 64; i++)
            db.AddEntry(VectorFor(i, 8), Metadata(i, "seed"));

        using var filterEntered = new ManualResetEventSlim();
        using var releaseFilter = new ManualResetEventSlim();
        Exception? observed = null;

        var search = Task.Run(() =>
        {
            try
            {
                db.SearchSimple(Vec.Basis(8, 0), 8, _ =>
                {
                    filterEntered.Set();
                    releaseFilter.Wait(TimeSpan.FromSeconds(10));
                    return true;
                });
            }
            catch (Exception ex)
            {
                observed = ex;
            }
        });

        Assert.True(filterEntered.Wait(TimeSpan.FromSeconds(10)), "Search did not start in time.");
        db.Dispose();
        releaseFilter.Set();

        var completed = await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(search, completed);

        if (observed is not null)
            Assert.IsType<ObjectDisposedException>(observed);

        using var reopened = temp.Open(dim: 8, max: 128);
        Assert.True(reopened.IsHealthy());
    }

    private static float[] VectorFor(int seq, int dim)
    {
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
            vector[i] = ((seq + 1) * (i + 3) % 97) / 97f;
        vector[0] = seq + 1;
        return vector;
    }

    private static string Metadata(int seq, string kind) => $"{{\"seq\":{seq},\"kind\":\"{kind}\"}}";

    private static void AssertSameResults(
        IReadOnlyList<(Guid Id, float Score, string Metadata)> expected,
        IReadOnlyList<(Guid Id, float Score, string Metadata)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].Metadata, actual[i].Metadata);
            Assert.True(Math.Abs(expected[i].Score - actual[i].Score) < 0.0001f);
        }
    }

    private static void AssertSaneResult((Guid Id, float Score, string Metadata) result)
    {
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.False(float.IsNaN(result.Score));
        Assert.False(float.IsInfinity(result.Score));
        Assert.InRange(result.Score, -1_000_000_000f, 1_000_000_000f);

        using var document = JsonDocument.Parse(result.Metadata);
        Assert.True(document.RootElement.TryGetProperty("seq", out var seq));
        Assert.Equal(JsonValueKind.Number, seq.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("kind", out var kind));
        Assert.Equal(JsonValueKind.String, kind.ValueKind);
    }

    private static Guid? PickRandomKey(ConcurrentDictionary<Guid, byte> keys, Random rng)
    {
        var snapshot = keys.Keys.ToArray();
        return snapshot.Length == 0 ? null : snapshot[rng.Next(snapshot.Length)];
    }

    private static void CaptureExceptions(ConcurrentBag<Exception> exceptions, Action action)
    {
        try { action(); }
        catch (Exception ex) { exceptions.Add(ex); }
    }

    private static void ThrowIfWorkerExceptions(ConcurrentBag<Exception> exceptions)
    {
        if (!exceptions.IsEmpty)
            throw new AggregateException(exceptions);
    }

    private static async Task WaitForTasks(Task[] tasks, CancellationTokenSource cts)
    {
        Task all = Task.WhenAll(tasks);
        Task completed = await Task.WhenAny(all, Task.Delay(Watchdog + TimeSpan.FromSeconds(15)));
        if (completed != all)
        {
            cts.Cancel();
            throw new TimeoutException("Concurrency test did not complete before its timeout.");
        }

        await all;
        Assert.False(cts.IsCancellationRequested, "Concurrency test exceeded its cancellation deadline.");
    }
}
