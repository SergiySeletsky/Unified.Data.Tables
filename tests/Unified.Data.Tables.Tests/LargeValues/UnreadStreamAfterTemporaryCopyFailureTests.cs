using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>The tests that point the process's temporary directory elsewhere; they never run beside another test.</summary>
[CollectionDefinition("TemporaryDirectory", DisableParallelization = true)]
public sealed class TemporaryDirectoryCollection;

/// <summary>
/// STREAMS-3 of the fix-wave-4 review: when the temporary copy of a forward-only stream cannot be created,
/// the write throws before it reads a byte, and the stream is left as it was before that write: neither the
/// content nor a new content over the stream is refused once a temporary copy can be made again.
/// </summary>
[Collection("TemporaryDirectory")]
public class UnreadStreamAfterTemporaryCopyFailureTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static async Task<byte[]> ReadFileAsync(InMemoryStorage<FileEntity> store, string id)
    {
        var read = await store.OneAsync(id, Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // Runs the write while TMP and TEMP name a directory that does not exist, so no temporary file can be created.
    private static async Task<Exception?> WithoutATemporaryDirectoryAsync(Func<Task> write)
    {
        var saved = (Tmp: Environment.GetEnvironmentVariable("TMP"), Temp: Environment.GetEnvironmentVariable("TEMP"));
        var missing = Path.Combine(Path.GetTempPath(), "unified-tables-" + Guid.NewGuid().ToString("N"), "missing");
        try
        {
            Environment.SetEnvironmentVariable("TMP", missing);
            Environment.SetEnvironmentVariable("TEMP", missing);
            return await Record.ExceptionAsync(write);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", saved.Tmp);
            Environment.SetEnvironmentVariable("TEMP", saved.Temp);
        }
    }

    [Fact]
    public async Task AStreamNothingWasReadFrom_BecauseTheTemporaryCopyCouldNotBeCreated_IsStoredWithTheSameContent()
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(100_000, seed: 74);
        body.Load(data);
        var content = TableBlobContent.FromStream(body, "application/octet-stream");

        var first = await WithoutATemporaryDirectoryAsync(() => store.UpsertAsync(new FileEntity { Id = "p|1", File = content }, Ct));
        output.WriteLine($"first write (temp dir unusable): {first?.GetType().Name}: {first?.Message}");
        Assert.IsAssignableFrom<IOException>(first);

        await store.UpsertAsync(new FileEntity { Id = "p|1", File = content }, Ct);
        Assert.Equal(data, await ReadFileAsync(store, "p|1"));
    }

    [Fact]
    public async Task AStreamNothingWasReadFrom_BecauseTheTemporaryCopyCouldNotBeCreated_IsStoredWithANewContent()
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(100_000, seed: 78);
        body.Load(data);

        var first = await WithoutATemporaryDirectoryAsync(() => store.UpsertAsync(
            new FileEntity { Id = "p|1", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct));
        Assert.IsAssignableFrom<IOException>(first);

        await store.UpsertAsync(new FileEntity { Id = "p|2", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct);
        Assert.Equal(data, await ReadFileAsync(store, "p|2"));
    }

    // A stream an earlier write read to its end stays listed so after such a failure: the next content over it
    // that delivers nothing is still refused, and one that delivers a new payload is stored.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConsumedStream_WhoseNextWriteCouldNotCreateItsTemporaryCopy_StaysReadToItsEnd(bool newPayload)
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        body.Load(RandomBytes(100_000, seed: 79));
        await store.UpsertAsync(new FileEntity { Id = "p|1", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct);

        var next = RandomBytes(100_000, seed: 80);
        body.Load(newPayload ? next : []);
        var failed = await WithoutATemporaryDirectoryAsync(() => store.UpsertAsync(
            new FileEntity { Id = "p|2", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct));
        Assert.IsAssignableFrom<IOException>(failed);

        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(
            new FileEntity { Id = "p|2", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct));
        output.WriteLine(outcome is null ? "stored" : $"{outcome.GetType().Name}: {outcome.Message}");
        if (newPayload)
        {
            Assert.Null(outcome);
            Assert.Equal(next, await ReadFileAsync(store, "p|2"));
        }
        else
        {
            RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(outcome));
            Assert.Null(await store.OneAsync("p|2", Ct));
        }
    }
}
