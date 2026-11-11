using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The blob cleanup that follows a committed write, delete or partition delete: the caller's token does
/// not stop it at once (a committed change is never reported as failed, nor left uncleaned), but once the
/// caller has cancelled, cleanup gets only <see cref="LargeValueTable.CancelledCleanupBudget"/> more time,
/// then stops and leaves the remaining blobs to the orphan sweep, so a cancelled call (a host shutting
/// down) is never held for as long as a whole partition's blobs take to delete.
/// </summary>
public class PostCommitCleanupTests
{
    private static readonly string Sha = new('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // One blob per column of the row p|r: released blobs are always the written row's own.
    private static string Name(int i) => $"T/p/r/C{i}/{Sha}";

    private static LargeValueTable Table(IOffloadBlobStore blobs, TimeSpan budget) =>
        new("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero }, blobs) { CancelledCleanupBudget = budget };

    [Fact]
    public async Task ReleasedBlobs_CallerCancelled_StopAfterTheBudget_AndReportTheRestOnce()
    {
        var blobs = new HangingBlobStore();
        var failures = new List<(string Path, Exception Error)>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var watch = Stopwatch.StartNew();

        await Table(blobs, TimeSpan.FromMilliseconds(100))
            .DeleteReleasedBlobsAsync("p", "r", Enumerable.Range(0, 3).Select(Name).ToList(), DateTimeOffset.UtcNow, onForeign: null, (p, e) => failures.Add((p, e)), cts.Token);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"cleanup took {watch.Elapsed}");
        Assert.Equal(1, blobs.Deletes);
        var (path, error) = Assert.Single(failures);
        Assert.Equal(Name(0), path);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Contains("3 blob(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasedBlobs_CallerCancelled_WithinTheBudget_AreAllDeleted()
    {
        var blobs = new HangingBlobStore { Hang = false };
        var failures = new List<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Table(blobs, TimeSpan.FromSeconds(30))
            .DeleteReleasedBlobsAsync("p", "r", Enumerable.Range(0, 3).Select(Name).ToList(), serviceNow: null, onForeign: null, (p, _) => failures.Add(p), cts.Token);

        Assert.Equal(3, blobs.Deletes);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ReleasedBlobs_NotCancelled_AreAllDeleted_HoweverLongItTakes()
    {
        var blobs = new HangingBlobStore { Hang = false, DeleteDelay = TimeSpan.FromMilliseconds(50) };

        await Table(blobs, TimeSpan.FromMilliseconds(10))
            .DeleteReleasedBlobsAsync("p", "r", Enumerable.Range(0, 4).Select(Name).ToList(), serviceNow: null, onForeign: null, onFailure: null, Ct);

        Assert.Equal(4, blobs.Deletes);
    }

    [Fact]
    public async Task PartitionBlobs_CallerCancelled_StopAfterTheBudget_AndReportTheRestOnce()
    {
        var blobs = new HangingBlobStore();
        for (var i = 0; i < 5; i++)
            blobs.Listed.Add(Name(i));
        var failures = new List<(string Path, Exception Error)>();
        var listFailures = new List<Exception>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var watch = Stopwatch.StartNew();

        await Table(blobs, TimeSpan.FromMilliseconds(100))
            .DeletePartitionBlobsAsync("p", listFailures.Add, (p, e) => failures.Add((p, e)), cts.Token);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"cleanup took {watch.Elapsed}");
        Assert.Empty(listFailures);
        var (path, error) = Assert.Single(failures);
        Assert.Equal(Name(0), path);
        Assert.Contains("5 blob(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartitionBlobs_CallerCancelled_WithinTheBudget_AreAllDeleted()
    {
        var blobs = new HangingBlobStore { Hang = false };
        for (var i = 0; i < 5; i++)
            blobs.Listed.Add(Name(i));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Table(blobs, TimeSpan.FromSeconds(30)).DeletePartitionBlobsAsync("p", null, null, cts.Token);

        Assert.Equal(5, blobs.Deletes);
    }

    [Fact]
    public void CancelledCleanupBudget_DefaultsToFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new LargeValueTable("T", new BlobOffloadOptions(), new HangingBlobStore()).CancelledCleanupBudget);
    }

    /// <summary>A blob store whose deletes wait until their token is cancelled (when <see cref="Hang"/>).</summary>
    private sealed class HangingBlobStore : IOffloadBlobStore
    {
        private int deletes;

        public bool Hang { get; init; } = true;

        public TimeSpan DeleteDelay { get; init; }

        public List<string> Listed { get; } = [];

        public int Deletes => Volatile.Read(ref deletes);

        public async Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
        {
            Interlocked.Increment(ref deletes);
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
            else if (DeleteDelay > TimeSpan.Zero)
                await Task.Delay(DeleteDelay, ct);
            return true;
        }

        public Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
        }

        public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var name in Listed)
            {
                ct.ThrowIfCancellationRequested();
                yield return (name, DateTimeOffset.UtcNow.AddHours(-1));
            }
            await Task.CompletedTask;
        }

        public Task<BlobState?> GetStateAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TouchAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct) => throw new NotSupportedException();
    }
}
