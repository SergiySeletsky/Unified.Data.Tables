using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Cleanup and sweep cutoffs are compared with blob <c>Last-Modified</c> times, which the storage
/// service stamps. They are taken from the service's clock (the commit response's <c>Date</c>, or the
/// blob store's service time), never from a client clock that may run ahead of it, so a blob a
/// concurrent writer has just touched is never deleted. Here the service runs an hour behind the client.
/// </summary>
public class ServiceClockTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    private static readonly TimeSpan Behind = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Whole seconds, as the Date header carries them.
    private static DateTimeOffset ServiceNow()
    {
        var now = DateTimeOffset.UtcNow - Behind;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static UnifiedTableStorageOptions Offload() => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.FromMinutes(5) },
        Cache = CachePolicy.Disabled,
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A response with a <c>Date</c> (and an <c>ETag</c>) header.</summary>
    private sealed class DatedResponse(DateTimeOffset? date, string etag = "W/\"v2\"") : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get => null; set { } }

        public override string ClientRequestId { get => string.Empty; set { } }

        public override void Dispose()
        {
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = name.ToUpperInvariant() switch
            {
                "DATE" => date?.ToString("R", CultureInfo.InvariantCulture),
                "ETAG" => etag,
                _ => null,
            };
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = TryGetHeader(name, out var value) ? [value] : null;
            return values is not null;
        }

        protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    }

    /// <summary>A row (written by the store's own serializer) that references one blob per value, each stamped <paramref name="lastModified"/>.</summary>
    private static async Task<TableEntity> StoredRow(InMemoryOffloadBlobStore blobs, object entity, params DateTimeOffset[] lastModified)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, Offload().BlobOffload, entity.GetType().Name);
        var (row, _, pending) = coordinator.Serialize(entity, Pk, Rk, 0);
        await coordinator.UploadAsync(pending, Ct);
        var references = ValueOffloadCoordinator.References(row);
        Assert.Equal(lastModified.Length, references.Count);
        for (var i = 0; i < references.Count; i++)
        {
            await using var stream = await blobs.OpenReadAsync("x", references[i].Path, Ct);
            blobs.Seed(references[i].Path, await ReadAllAsync(stream), references[i].Sha256, lastModified[i]);
        }
        row.ETag = new ETag("W/\"v1\"");
        return row;
    }

    [Fact]
    public async Task Replace_CleanupCutoff_ComesFromTheCommitResponseDate()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var service = ServiceNow();
        // Touched a minute ago on the service clock (an hour and a minute ago on the client's).
        var prior = await StoredRow(blobs, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") }, service - TimeSpan.FromMinutes(1));
        var oldBlob = Assert.Single(blobs.Names);
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = () => null };
        using var h = new StorageHarness<FileEntity>(options: Offload(), blobs: recording);
        h.SetupGet(Pk, Rk, prior);
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(new DatedResponse(service));

        await h.Store.UpsertAsync(new FileEntity { Id = Id, File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct);

        // Within the grace on the service clock: kept for the sweep. The client clock would have deleted it.
        Assert.Contains(oldBlob, blobs.Names);
        Assert.Equal(0, recording.ServiceTimeCalls); // the commit's own Date was used
    }

    [Fact]
    public async Task Replace_CleanupStillDeletes_ABlobOlderThanTheGraceOnTheServiceClock()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var service = ServiceNow();
        var prior = await StoredRow(blobs, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") }, service - TimeSpan.FromMinutes(6));
        var oldBlob = Assert.Single(blobs.Names);
        using var h = new StorageHarness<FileEntity>(options: Offload(), blobs: blobs);
        h.SetupGet(Pk, Rk, prior);
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(new DatedResponse(service));

        await h.Store.UpsertAsync(new FileEntity { Id = Id, File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct);

        Assert.DoesNotContain(oldBlob, blobs.Names);
    }

    [Fact]
    public async Task Delete_WithoutACommitDate_UsesTheBlobStoreServiceTime()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var service = ServiceNow();
        var prior = await StoredRow(blobs, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") }, service - TimeSpan.FromMinutes(1));
        var oldBlob = Assert.Single(blobs.Names);
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = () => service };
        using var h = new StorageHarness<FileEntity>(options: Offload(), blobs: recording);
        h.SetupGet(Pk, Rk, prior);
        h.SetupDelete(); // its response has no Date

        await h.Store.DeleteAsync(Id, Ct);

        Assert.Contains(oldBlob, blobs.Names);
        Assert.Equal(1, recording.ServiceTimeCalls);
    }

    [Fact]
    public async Task NoServiceTime_FallsBackToTheClientClockLessAMargin()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var client = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromMinutes(5);
        // Body: just past the grace on the client clock, but inside the margin; Content: past both.
        var prior = await StoredRow(blobs,
            new TextAndBlobEntity { Id = Id, Body = Ascii(40_000), Content = TableBlobContent.FromBytes([1], "application/octet-stream") },
            client - grace - TimeSpan.FromMinutes(1), client - grace - LargeValueTable.ClientClockMargin - TimeSpan.FromMinutes(1));
        var references = ValueOffloadCoordinator.References(prior);
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = () => null };
        using var h = new StorageHarness<TextAndBlobEntity>(options: Offload(), blobs: recording);
        h.Store.LargeValueClock = new FixedClock(client);
        h.SetupGet(Pk, Rk, prior);
        h.SetupDelete();

        await h.Store.DeleteAsync(Id, Ct);

        Assert.Contains(references.Single(r => r.Path.Contains("/Body/", StringComparison.Ordinal)).Path, blobs.Names);
        Assert.DoesNotContain(references.Single(r => r.Path.Contains("/Content/", StringComparison.Ordinal)).Path, blobs.Names);
    }

    [Fact]
    public async Task Sweep_MeasuresAgeOnTheServiceClock()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var service = ServiceNow();
        var fresh = $"{nameof(SplitTextEntity)}/{Pk}/a/Body/{Sha(Utf8("a"))}";
        var old = $"{nameof(SplitTextEntity)}/{Pk}/b/Body/{Sha(Utf8("b"))}";
        blobs.Seed(fresh, Utf8("a"), Sha(Utf8("a")), service - TimeSpan.FromMinutes(1));
        blobs.Seed(old, Utf8("b"), Sha(Utf8("b")), service - TimeSpan.FromMinutes(30));
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = () => service };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: recording);
        h.SetupQueryByFilter();

        var deleted = await h.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(10), Ct);

        Assert.Equal(1, deleted);
        Assert.Equal([fresh], blobs.Names);
    }

    [Fact]
    public async Task PartitionDelete_CleanupCutoff_ComesFromTheServiceClock()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var service = ServiceNow();
        var fresh = $"{nameof(SplitTextEntity)}/{Pk}/a/Body/{Sha(Utf8("a"))}";
        var old = $"{nameof(SplitTextEntity)}/{Pk}/b/Body/{Sha(Utf8("b"))}";
        blobs.Seed(fresh, Utf8("a"), Sha(Utf8("a")), service - TimeSpan.FromMinutes(1));
        blobs.Seed(old, Utf8("b"), Sha(Utf8("b")), service - TimeSpan.FromMinutes(30));
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = () => service };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: recording);
        h.SetupQueryByFilter();
        h.SetupTransaction();

        await h.Store.DeletePartitionAsync(Pk, Ct);

        Assert.Equal([fresh], blobs.Names);
    }

    [Fact]
    public async Task AzureStore_ServiceTime_IsTheResponseDate()
    {
        var container = Substitute.For<BlobContainerClient>();
        var date = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        container.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(BlobsModelFactory.BlobContainerProperties(lastModified: date, eTag: new ETag("e")), new DatedResponse(date)));
        var store = new AzureOffloadBlobStore(container);

        Assert.Equal(date, await store.GetServiceTimeAsync(Ct));

        container.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(404, "ContainerNotFound", "ContainerNotFound", null));
        Assert.Null(await store.GetServiceTimeAsync(Ct)); // no response to read a Date from: the caller falls back
    }
}
