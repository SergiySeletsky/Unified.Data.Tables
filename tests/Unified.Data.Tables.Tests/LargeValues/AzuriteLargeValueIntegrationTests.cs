using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Split values and blob offload end to end against a LOCAL Azurite (tables on :10002, blobs on
/// :10000) through <see cref="TableStorage{T}"/> and the real Azure SDK clients. Every test works in its
/// own partition <c>lv-{guid}</c> and its own blob container <c>lv-{guid}</c> and deletes both when it
/// ends; no other table row, partition or container is touched.
/// <para>
/// The tests self-skip when the table or the blob endpoint is unreachable. The connection string
/// defaults to <c>UseDevelopmentStorage=true</c>; the environment variable
/// <see cref="ConnectionStringVariable"/> overrides it (for example to point at another emulator, or at
/// an unused port to exercise the skip path).
/// </para>
/// </summary>
[Collection("OversizedCellPolicy")] // one test registers through AddUnifiedTableStorage
public sealed class AzuriteLargeValueIntegrationTests : IAsyncLifetime
{
    /// <summary>Overrides the storage connection string the tests use.</summary>
    internal const string ConnectionStringVariable = "UNIFIED_TABLES_TEST_STORAGE";

    private const string DevelopmentStorage = "UseDevelopmentStorage=true";
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set " + ConnectionStringVariable + " or start Azurite";
    private const int TextCellChars = 32_000;
    private const long DefaultThreshold = 3_145_728;

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured ? configured : DevelopmentStorage;

    private static readonly Lazy<Task<bool>> Probe = new(ProbeAsync);

    private readonly string pk = "lv-" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "lv-" + Guid.NewGuid().ToString("N");
    private readonly List<(string Service, string Method, string Path)> requests = [];
    private readonly HashSet<string> tables = new(StringComparer.Ordinal);
    private readonly List<MemoryCache> caches = [];

    private TableServiceClient tableService = default!;
    private BlobServiceClient blobService = default!;
    private BlobContainerClient container = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        var tableOptions = new TableClientOptions();
        tableOptions.AddPolicy(new RequestRecorder("table", requests), HttpPipelinePosition.PerCall);
        var blobOptions = new BlobClientOptions();
        blobOptions.AddPolicy(new RequestRecorder("blob", requests), HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString, tableOptions);
        blobService = new BlobServiceClient(ConnectionString, blobOptions);
        container = blobService.GetBlobContainerClient(containerName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        foreach (var table in tables)
        {
            var client = tableService.GetTableClient(table);
            try
            {
                var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
                var keys = new List<string>();
                await foreach (var row in client.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                    keys.Add(row.RowKey);
                foreach (var rk in keys)
                    await client.DeleteEntityAsync(pk, rk, ETag.All, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // The table was never created, or a row is already gone.
            }
        }
        await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    // Reachability of both endpoints, once per test run, without retries (an absent emulator skips fast).
    private static async Task<bool> ProbeAsync()
    {
        try
        {
            var tableOptions = new TableClientOptions();
            tableOptions.Retry.MaxRetries = 0;
            tableOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            var blobOptions = new BlobClientOptions();
            blobOptions.Retry.MaxRetries = 0;
            blobOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            await foreach (var _ in new TableServiceClient(ConnectionString, tableOptions).QueryAsync(maxPerPage: 1))
                break;
            await foreach (var _ in new BlobServiceClient(ConnectionString, blobOptions).GetBlobContainersAsync())
                break;
            return true;
        }
#pragma warning disable CA1031 // any failure (refused connection, bad string, auth) means "not available here"
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private BlobOffloadOptions Offload(TimeSpan? grace = null)
    {
        var options = new BlobOffloadOptions { ContainerName = containerName };
        if (grace is { } g) options.CleanupGrace = g;
        return options;
    }

    private TableStorage<T> Store<T>(BlobOffloadOptions? offload = null) where T : class, IEntity, new()
    {
        tables.Add(typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(tableService, cache, NullLogger<TableStorage<T>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { BlobOffload = offload ?? Offload() }, blobService);
    }

    private string Id(string rk) => pk + "|" + rk;

    private TableClient Table<T>() => tableService.GetTableClient(typeof(T).Name);

    private string Prefix<T>() => $"{typeof(T).Name}/{pk}/";

    /// <summary>Every stored row of this test's partition, continuation rows included, in key order.</summary>
    private async Task<List<TableEntity>> RawRowsAsync<T>()
    {
        var rows = new List<TableEntity>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
        await foreach (var row in Table<T>().QueryAsync<TableEntity>(filter, cancellationToken: Ct))
            rows.Add(row);
        return rows.OrderBy(r => r.RowKey, StringComparer.Ordinal).ToList();
    }

    /// <summary>The blobs under this test's partition of <typeparamref name="T"/>'s table (none when the container does not exist).</summary>
    private async Task<List<BlobItem>> BlobsAsync<T>()
    {
        var items = new List<BlobItem>();
        if (!(await container.ExistsAsync(Ct)).Value)
            return items;
        await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions { Prefix = Prefix<T>(), Traits = BlobTraits.Metadata }, Ct))
            items.Add(item);
        return items;
    }

    private static BlobReference Reference(TableEntity row, string column) =>
        BlobReference.Parse(column, (string)row[column + "__blob"]);

    private static IEnumerable<string> ChunkKeys(TableEntity row, string column) =>
        row.Keys.Where(k => k.StartsWith(column + "__c", StringComparison.Ordinal)
            && int.TryParse(k.AsSpan(column.Length + 3), NumberStyles.None, CultureInfo.InvariantCulture, out _));

    private static int ChunkIndex(string key, string column) =>
        int.Parse(key.AsSpan(column.Length + 3), NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The text chunk cells of <paramref name="column"/> across all rows, concatenated in index order.</summary>
    private static string JoinTextChunks(IEnumerable<TableEntity> rows, string column)
    {
        var chunks = new SortedDictionary<int, string>();
        foreach (var row in rows)
            foreach (var key in ChunkKeys(row, column))
                chunks.Add(ChunkIndex(key, column), Assert.IsType<string>(row[key]));
        return string.Concat(chunks.Values);
    }

    private static string TextWithEmojiAtBoundaries(int length)
    {
        var chars = Ascii(length, seed: 5).ToCharArray();
        for (var boundary = TextCellChars - 1; boundary + 1 < length; boundary += TextCellChars)
        {
            chars[boundary] = '\uD83D'; // U+1F600 straddles the cell boundary
            chars[boundary + 1] = '\uDE00';
        }
        return new string(chars);
    }

    private static async Task<string> HashAsync(Stream stream)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
            sha.AppendData(buffer, 0, n);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    // Ages are measured on the service clock, whose Date header and Last-Modified have one-second
    // resolution: a blob written this second is not yet older than zero. Wait for the next second.
    private static Task PastTheServiceSecondAsync() => Task.Delay(TimeSpan.FromMilliseconds(1_100), Ct);

    private int BlobRequestCount()
    {
        lock (requests) return requests.Count(r => r.Service == "blob");
    }

    private void ClearRequests()
    {
        lock (requests) requests.Clear();
    }

    // ── tier boundaries ────────────────────────────────────────────────────

    [Theory]
    [InlineData(32_000, 1, 0, 0)]      // one cell
    [InlineData(32_001, 1, 2, 0)]      // two cells
    [InlineData(254_000, 1, 8, 0)]     // one entity
    [InlineData(600_000, 2, 19, 0)]    // two entities, one transaction
    [InlineData(3_200_000, 1, 0, 1)]   // above the threshold: offloaded
    public async Task TextBoundaries_RoundTrip(int length, int rows, int chunks, int blobs)
    {
        Assert.SkipUnless(available, SkipReason);
        var text = Ascii(length, seed: length);

        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("r1"), Body = text, Title = "t" }, Ct);
        var read = await Store<SplitTextEntity>().OneAsync(Id("r1"), Ct);

        Assert.NotNull(read);
        Assert.Equal(text, read!.Body, StringComparer.Ordinal);
        Assert.Equal("t", read.Title);

        var raw = await RawRowsAsync<SplitTextEntity>();
        Assert.Equal(rows, raw.Count);
        Assert.Equal("r1", raw[0].RowKey);
        for (var i = 1; i < raw.Count; i++)
            Assert.Equal("r1~" + i.ToString("D4", CultureInfo.InvariantCulture), raw[i].RowKey);
        Assert.Equal(chunks, raw.Sum(r => ChunkKeys(r, "Body").Count()));
        if (length <= TextCellChars)
            Assert.Equal(text, Assert.IsType<string>(raw[0]["Body"]), StringComparer.Ordinal);
        else if (chunks > 0)
        {
            Assert.False(raw[0].ContainsKey("Body"));
            Assert.Equal(chunks, raw[0]["Body__chunks"]);
            Assert.Equal("text", raw[0]["Body__enc"]);
            Assert.Equal(Sha(Utf8(text)), raw[0]["Body__sha256"]);
            Assert.Equal(text, JoinTextChunks(raw, "Body"), StringComparer.Ordinal);
        }

        var stored = await BlobsAsync<SplitTextEntity>();
        Assert.Equal(blobs, stored.Count);
        if (blobs == 1)
        {
            var reference = Reference(raw[0], "Body");
            var blob = Assert.Single(stored);
            Assert.Equal(reference.Path, blob.Name);
            Assert.Equal($"SplitTextEntity/{pk}/r1/Body/{Sha(Utf8(text))}", blob.Name);
            Assert.Equal("text/plain; charset=utf-8", blob.Properties.ContentType);
            Assert.Equal(Sha(Utf8(text)), blob.Metadata["sha256"]);
            Assert.Equal(length.ToString(CultureInfo.InvariantCulture), blob.Metadata["length"]);
            Assert.Equal(length, blob.Properties.ContentLength);
            Assert.Equal("text", reference.Encoding);
        }
    }

    // Text the SDK JSON-escapes character by character (\uXXXX, six bytes each on the wire): Cyrillic,
    // and the HTML-sensitive ASCII '<', '>', '&'. Sized by the storage estimate alone, 700K of either
    // was planned into one transaction and the service refused it (HTTP 413).
    [Theory]
    [InlineData("cyrillic", 600_000, false)]  // 3.6 MB on the wire: continuation rows, one transaction
    [InlineData("cyrillic", 700_000, true)]   // 4.2 MB on the wire: offloaded although below the byte threshold
    [InlineData("cyrillic", 1_200_000, true)]
    [InlineData("markup", 600_000, false)]
    [InlineData("markup", 700_000, true)]
    [InlineData("markup", 1_200_000, true)]
    public async Task EscapedText_RoundTripsExactly_SplitOrOffloaded(string alphabet, int length, bool offloaded)
    {
        Assert.SkipUnless(available, SkipReason);
        var letters = alphabet == "cyrillic" ? "абвгдежзийклмнопрстуфхцчшщъыьэюяЖЁ" : "<<<<>&";
        var random = new Random(length);
        var text = string.Create(length, letters, (span, set) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = set[random.Next(set.Length)];
        });
        Assert.True(Utf8(text).Length < DefaultThreshold, "below the byte threshold: only the wire size decides");

        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("x1"), Body = text, Title = "t" }, Ct);
        var read = await Store<SplitTextEntity>().OneAsync(Id("x1"), Ct);

        Assert.Equal(text, read!.Body, StringComparer.Ordinal);
        var raw = await RawRowsAsync<SplitTextEntity>();
        var blobs = await BlobsAsync<SplitTextEntity>();
        if (offloaded)
        {
            Assert.Single(raw);
            Assert.Equal(Sha(Utf8(text)), Reference(raw[0], "Body").Sha256);
            Assert.Single(blobs);
        }
        else
        {
            Assert.True(raw.Count > 1, $"{raw.Count} rows");
            Assert.Empty(blobs);
            Assert.Equal(text, JoinTextChunks(raw, "Body"), StringComparer.Ordinal);
        }

        // An upsert of the same value takes the same tier, and so does a replace that grows it.
        await Store<SplitTextEntity>().UpsertAsync(new SplitTextEntity { Id = Id("x1"), Body = text + text[..1000] }, Ct);
        Assert.Equal(text + text[..1000], (await Store<SplitTextEntity>().OneAsync(Id("x1"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task FullTransaction_StaysInTables_OneMoreCharIsOffloaded()
    {
        Assert.SkipUnless(available, SkipReason);
        var offload = Offload();

        // The largest Text value whose row and continuation rows fit one entity-group transaction.
        bool Fits(int n)
        {
            var ctx = new ValueWriteContext(offload, nameof(SplitTextEntity), new HashSet<string>(StringComparer.Ordinal), IsClone: false);
            var row = TableEntitySerializer.ToTableEntity(new SplitTextEntity { Id = Id("a1"), Body = new string('a', n) }, pk, "a1", ctx, []);
            return SplitRowLayout.FitsOneTransaction(SplitRowLayout.Plan(row), 0);
        }
        int lo = 600_000, hi = 3_000_000; // Fits(lo) holds, Fits(hi) does not
        Assert.True(Fits(lo));
        Assert.False(Fits(hi));
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            if (Fits(mid)) lo = mid; else hi = mid;
        }
        Assert.True(lo < DefaultThreshold, "the largest one-transaction value is below the byte threshold");

        var largest = Ascii(lo, seed: 21);
        var oneMore = Ascii(lo + 1, seed: 22);
        await Store<SplitTextEntity>(offload).CreateAsync(new SplitTextEntity { Id = Id("a1"), Body = largest }, Ct);
        Assert.Empty(await BlobsAsync<SplitTextEntity>());
        var parts = (await RawRowsAsync<SplitTextEntity>()).Count;
        Assert.True(parts >= 2, $"{parts} rows");

        await Store<SplitTextEntity>(offload).CreateAsync(new SplitTextEntity { Id = Id("a2"), Body = oneMore }, Ct);
        var blob = Assert.Single(await BlobsAsync<SplitTextEntity>());
        Assert.StartsWith($"SplitTextEntity/{pk}/a2/Body/", blob.Name, StringComparison.Ordinal);
        Assert.Equal(parts + 1, (await RawRowsAsync<SplitTextEntity>()).Count); // a2 is one row with a reference

        var store = Store<SplitTextEntity>(offload);
        Assert.Equal(largest, (await store.OneAsync(Id("a1"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(oneMore, (await store.OneAsync(Id("a2"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task JustAboveThreshold_Bytes_IsOffloaded()
    {
        Assert.SkipUnless(available, SkipReason);
        var image = RandomBytes(3_145_729, seed: 3);

        await Store<SplitBinaryEntity>().CreateAsync(new SplitBinaryEntity { Id = Id("b1"), Image = image }, Ct);
        var read = await Store<SplitBinaryEntity>().OneAsync(Id("b1"), Ct);

        Assert.Equal(image, read!.Image);
        var row = Assert.Single(await RawRowsAsync<SplitBinaryEntity>());
        var reference = Reference(row, "Image");
        Assert.Equal("binary", reference.Encoding);
        Assert.Equal(image.LongLength, reference.Length);
        var blob = Assert.Single(await BlobsAsync<SplitBinaryEntity>());
        Assert.Equal(reference.Path, blob.Name);
        Assert.Equal(Sha(image), blob.Metadata["sha256"]);
        Assert.Equal("3145729", blob.Metadata["length"]);
        Assert.Null(blob.Properties.ContentEncoding);
    }

    [Fact]
    public async Task SplitBytes_AreRawBinaryCells()
    {
        Assert.SkipUnless(available, SkipReason);
        var image = RandomBytes(200_000, seed: 4);

        await Store<SplitBinaryEntity>().CreateAsync(new SplitBinaryEntity { Id = Id("b2"), Image = image }, Ct);
        var read = await Store<SplitBinaryEntity>().OneAsync(Id("b2"), Ct);

        Assert.Equal(image, read!.Image);
        var row = Assert.Single(await RawRowsAsync<SplitBinaryEntity>());
        var keys = ChunkKeys(row, "Image").OrderBy(k => ChunkIndex(k, "Image")).ToList();
        Assert.Equal(4, keys.Count); // 65,536-byte cells
        Assert.Equal(image, keys.SelectMany(k => row.GetBinary(k)!).ToArray());
        Assert.Empty(await BlobsAsync<SplitBinaryEntity>());
    }

    [Fact]
    public async Task EmptyValues_RoundTripAsEmpty_NotNull()
    {
        Assert.SkipUnless(available, SkipReason);

        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("e1"), Body = string.Empty }, Ct);
        await Store<SplitBinaryEntity>().CreateAsync(new SplitBinaryEntity { Id = Id("e2"), Image = [] }, Ct);
        await Store<SplitGzipEntity>().CreateAsync(new SplitGzipEntity { Id = Id("e3"), Body = string.Empty }, Ct);

        var text = await Store<SplitTextEntity>().OneAsync(Id("e1"), Ct);
        var binary = await Store<SplitBinaryEntity>().OneAsync(Id("e2"), Ct);
        var gzip = await Store<SplitGzipEntity>().OneAsync(Id("e3"), Ct);

        Assert.Equal(string.Empty, Assert.IsType<string>(text!.Body));
        Assert.Empty(Assert.IsType<byte[]>(binary!.Image));
        Assert.Equal(string.Empty, Assert.IsType<string>(gzip!.Body));

        // The service returns the empty cells as empty values, not as missing ones.
        Assert.Equal(string.Empty, Assert.Single(await RawRowsAsync<SplitTextEntity>())["Body"]);
        var binaryRow = Assert.Single(await RawRowsAsync<SplitBinaryEntity>());
        Assert.True(binaryRow.ContainsKey("Image"));
        Assert.Empty(binaryRow.GetBinary("Image")!);
    }

    [Fact]
    public async Task TextWithSurrogatePairsAtEveryBoundary_RoundTrips()
    {
        Assert.SkipUnless(available, SkipReason);
        var text = TextWithEmojiAtBoundaries(600_000);

        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("s1"), Body = text }, Ct);
        var read = await Store<SplitTextEntity>().OneAsync(Id("s1"), Ct);

        Assert.Equal(text, read!.Body, StringComparer.Ordinal);
        var raw = await RawRowsAsync<SplitTextEntity>();
        Assert.Equal(2, raw.Count);
        foreach (var row in raw)
            foreach (var key in ChunkKeys(row, "Body"))
            {
                var chunk = Assert.IsType<string>(row[key]);
                Assert.False(char.IsHighSurrogate(chunk[^1]), $"{key} ends inside a surrogate pair");
                Assert.False(char.IsLowSurrogate(chunk[0]), $"{key} starts inside a surrogate pair");
            }
        Assert.Equal(text, JoinTextChunks(raw, "Body"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task IllFormedText_RoundTrips()
    {
        Assert.SkipUnless(available, SkipReason);
        var small = "abc" + '\uD800' + "def";
        var split = Ascii(100_000, seed: 8) + '\uDC00' + Ascii(100_000, seed: 9);
        var offloaded = Ascii(1_700_000, seed: 10) + '\uD800' + Ascii(1_700_000, seed: 12);

        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("i1"), Body = small }, Ct);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("i2"), Body = split }, Ct);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("i3"), Body = offloaded }, Ct);

        var store = Store<SplitTextEntity>();
        Assert.Equal(small, (await store.OneAsync(Id("i1"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(split, (await store.OneAsync(Id("i2"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(offloaded, (await store.OneAsync(Id("i3"), Ct))!.Body, StringComparer.Ordinal);

        var rows = (await RawRowsAsync<SplitTextEntity>()).ToDictionary(r => r.RowKey, StringComparer.Ordinal);
        Assert.Equal("utf16le", rows["i1"]["Body__enc"]);
        Assert.Equal("utf16le", rows["i2"]["Body__enc"]);
        Assert.All(ChunkKeys(rows["i2"], "Body"), k => Assert.IsType<byte[]>(rows["i2"][k]));
        var reference = Reference(rows["i3"], "Body");
        Assert.Equal("utf16le", reference.Encoding);
        var blob = Assert.Single(await BlobsAsync<SplitTextEntity>());
        Assert.Equal("text/plain; charset=utf-16le", blob.Properties.ContentType);
        Assert.Equal(offloaded.Length * 2L, blob.Properties.ContentLength);
    }

    [Fact]
    public async Task GzipEncoding_RoundTrips_BlobHasContentEncodingGzip()
    {
        Assert.SkipUnless(available, SkipReason);
        var text = Ascii(4_000_000, seed: 13);

        await Store<SplitGzipEntity>().CreateAsync(new SplitGzipEntity { Id = Id("g1"), Body = text }, Ct);
        var read = await Store<SplitGzipEntity>().OneAsync(Id("g1"), Ct);

        Assert.Equal(text, read!.Body, StringComparer.Ordinal);
        var reference = Reference(Assert.Single(await RawRowsAsync<SplitGzipEntity>()), "Body");
        Assert.Equal("gzip", reference.Encoding);
        var item = Assert.Single(await BlobsAsync<SplitGzipEntity>());
        Assert.Equal("gzip", item.Properties.ContentEncoding);
        Assert.Equal(reference.Sha256, item.Metadata["sha256"]);

        // The SDK hands back the stored gzip bytes: the transport does not decompress them.
        var downloaded = (await container.GetBlobClient(reference.Path).DownloadContentAsync(Ct)).Value;
        var bytes = downloaded.Content.ToArray();
        Assert.Equal("gzip", downloaded.Details.ContentEncoding);
        Assert.Equal(reference.Length, bytes.LongLength);
        Assert.Equal(new byte[] { 0x1f, 0x8b }, bytes[..2]);
        Assert.Equal(reference.Sha256, Sha(bytes));
        using var unzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        Assert.Equal(text, Encoding.UTF8.GetString(await ReadAllAsync(unzip)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task TableBlobContent_100MB_StreamsBothWays()
    {
        Assert.SkipUnless(available, SkipReason);
        const long size = 100_000_000;
        string expected;
        await using (var source = new GeneratedStream(size, seekable: false))
            expected = await HashAsync(source);

        var entity = new FileEntity { Id = Id("f1"), Title = "big", File = TableBlobContent.FromStream(new GeneratedStream(size, seekable: false), "application/octet-stream") };
        await Store<FileEntity>().CreateAsync(entity, Ct);
        Assert.Equal(size, entity.File!.Length);
        Assert.Equal(expected, entity.File.Sha256);

        ClearRequests();
        var read = await Store<FileEntity>().OneAsync(Id("f1"), Ct);
        Assert.Equal(0, BlobRequestCount()); // reading the entity never downloads the content
        Assert.Equal("big", read!.Title);
        Assert.Equal(size, read.File!.Length);
        Assert.Equal(expected, read.File.Sha256);

        await using (var stream = await read.File.OpenReadAsync(Ct))
            Assert.Equal(expected, await HashAsync(stream));
        var blob = Assert.Single(await BlobsAsync<FileEntity>());
        Assert.Equal(size, blob.Properties.ContentLength);
        Assert.Equal("application/octet-stream", blob.Properties.ContentType);
    }

    // ── integrity ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CorruptChunk_Throws()
    {
        Assert.SkipUnless(available, SkipReason);
        var text = Ascii(254_000, seed: 14);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("c1"), Body = text }, Ct);

        var row = Assert.Single(await RawRowsAsync<SplitTextEntity>());
        var chunk = (string)row["Body__c3"];
        var patch = new TableEntity(pk, "c1") { ["Body__c3"] = (chunk[0] == 'x' ? 'y' : 'x') + chunk[1..] };
        await Table<SplitTextEntity>().UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, Ct);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => Store<SplitTextEntity>().OneAsync(Id("c1"), Ct));
    }

    [Fact]
    public async Task MissingPart_Throws()
    {
        Assert.SkipUnless(available, SkipReason);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("m1"), Body = Ascii(600_000, seed: 15) }, Ct);
        Assert.Equal(2, (await RawRowsAsync<SplitTextEntity>()).Count);

        await Table<SplitTextEntity>().DeleteEntityAsync(pk, "m1~0001", ETag.All, Ct);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => Store<SplitTextEntity>().OneAsync(Id("m1"), Ct));
    }

    [Fact]
    public async Task MissingBlob_Throws()
    {
        Assert.SkipUnless(available, SkipReason);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("m2"), Body = Ascii(3_200_000, seed: 16) }, Ct);
        var blob = Assert.Single(await BlobsAsync<SplitTextEntity>());

        await container.DeleteBlobAsync(blob.Name, cancellationToken: Ct);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => Store<SplitTextEntity>().OneAsync(Id("m2"), Ct));
    }

    [Fact]
    public async Task TamperedBlob_Throws()
    {
        Assert.SkipUnless(available, SkipReason);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("t1"), Body = Ascii(3_200_000, seed: 17) }, Ct);
        var item = Assert.Single(await BlobsAsync<SplitTextEntity>());

        // Same length, same metadata, different bytes.
        var tampered = Utf8(Ascii(3_200_000, seed: 18));
        await container.GetBlobClient(item.Name).UploadAsync(new BinaryData(tampered), new BlobUploadOptions
        {
            Metadata = item.Metadata,
            HttpHeaders = new BlobHttpHeaders { ContentType = item.Properties.ContentType },
        }, Ct);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => Store<SplitTextEntity>().OneAsync(Id("t1"), Ct));
    }

    // ── replace, delete, sweep ─────────────────────────────────────────────

    [Fact]
    public async Task Replace_DeletesOldBlob_AfterSwap()
    {
        Assert.SkipUnless(available, SkipReason);
        // CleanupGrace = 0: the old blob is deleted by the cleanup right after the swap (the default
        // grace of 5 minutes would leave it for SweepOrphanBlobsAsync).
        var offload = Offload(TimeSpan.Zero);
        var before = Ascii(3_200_000, seed: 19);
        var after = Ascii(3_200_000, seed: 20);
        var created = await Store<SplitTextEntity>(offload).CreateAsync(new SplitTextEntity { Id = Id("u1"), Body = before }, Ct);
        var oldName = Assert.Single(await BlobsAsync<SplitTextEntity>()).Name;

        created.Body = after;
        ClearRequests();
        await Store<SplitTextEntity>(offload).UpdateAsync(created, Ct);

        var blob = Assert.Single(await BlobsAsync<SplitTextEntity>());
        Assert.NotEqual(oldName, blob.Name);
        Assert.Equal(Reference(Assert.Single(await RawRowsAsync<SplitTextEntity>()), "Body").Path, blob.Name);
        Assert.Equal(after, (await Store<SplitTextEntity>(offload).OneAsync(Id("u1"), Ct))!.Body, StringComparer.Ordinal);

        // The old blob was deleted only after the row swap.
        List<(string Service, string Method, string Path)> log;
        lock (requests) log = [.. requests];
        var swap = log.FindIndex(r => r.Service == "table" && r.Method == "PUT");
        var delete = log.FindIndex(r => r.Service == "blob" && r.Method == "DELETE");
        Assert.True(swap >= 0 && delete > swap, string.Join(", ", log.Select(r => r.Service + ":" + r.Method)));
    }

    [Fact]
    public async Task Delete_RemovesRowThenBlobs()
    {
        Assert.SkipUnless(available, SkipReason);
        // CleanupGrace = 0 so the cleanup after the delete removes the just-written blobs.
        var offload = Offload(TimeSpan.Zero);
        var store = Store<TextAndBlobEntity>(offload);
        await store.CreateAsync(new TextAndBlobEntity
        {
            Id = Id("d1"),
            Body = Ascii(3_200_000, seed: 23),
            Content = TableBlobContent.FromBytes(RandomBytes(1_000, seed: 24), "application/octet-stream"),
        }, Ct);
        Assert.Equal(2, (await BlobsAsync<TextAndBlobEntity>()).Count);

        ClearRequests();
        await Store<TextAndBlobEntity>(offload).DeleteAsync(Id("d1"), Ct);

        Assert.Empty(await RawRowsAsync<TextAndBlobEntity>());
        Assert.Empty(await BlobsAsync<TextAndBlobEntity>());
        List<(string Service, string Method, string Path)> log;
        lock (requests) log = [.. requests];
        var rowDelete = log.FindIndex(r => r.Service == "table" && r.Method == "DELETE");
        var firstBlobDelete = log.FindIndex(r => r.Service == "blob" && r.Method == "DELETE");
        Assert.True(rowDelete >= 0 && firstBlobDelete > rowDelete, string.Join(", ", log.Select(r => r.Service + ":" + r.Method)));
        Assert.Equal(2, log.Count(r => r.Service == "blob" && r.Method == "DELETE"));
    }

    [Fact]
    public async Task CrashBeforeEntityWrite_ThenSweep()
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store<SplitTextEntity>();
        await store.EnsureCreatedAsync(Ct);

        // A write that uploaded its blob and then crashed before the entity write.
        var bytes = Utf8(Ascii(3_200_000, seed: 25));
        var sha = Sha(bytes);
        var name = $"SplitTextEntity/{pk}/x1/Body/{sha}";
        var blobs = new AzureOffloadBlobStore(container);
        await using (var content = new MemoryStream(bytes))
            await blobs.UploadCreateOnlyAsync(name, content, new BlobReference(name, sha, bytes.LongLength, "text/plain; charset=utf-8", "text"), null, Ct);
        Assert.Single(await BlobsAsync<SplitTextEntity>());
        Assert.Empty(await RawRowsAsync<SplitTextEntity>());

        await PastTheServiceSecondAsync();
        Assert.Equal(1, await store.SweepOrphanBlobsAsync(pk, TimeSpan.Zero, Ct));
        Assert.Empty(await BlobsAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task ConcurrentReplace_OneWins_SweepLeavesOnlyWinnersBlob()
    {
        Assert.SkipUnless(available, SkipReason);
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("w1"), Body = Ascii(3_200_000, seed: 26) }, Ct);
        var first = (await Store<SplitTextEntity>().OneAsync(Id("w1"), Ct))!;
        var second = (await Store<SplitTextEntity>().OneAsync(Id("w1"), Ct))!;
        Assert.Equal(first.ETag, second.ETag);
        first.Body = Ascii(3_200_000, seed: 27);
        second.Body = Ascii(3_200_000, seed: 28);

        var outcomes = await Task.WhenAll(
            Outcome(Store<SplitTextEntity>().UpdateAsync(first, Ct)),
            Outcome(Store<SplitTextEntity>().UpdateAsync(second, Ct)));

        Assert.Single(outcomes, o => o is null);
        Assert.IsType<ConcurrencyConflictException>(Assert.Single(outcomes, o => o is not null));
        var winner = outcomes[0] is null ? first.Body : second.Body;
        var winnerName = $"SplitTextEntity/{pk}/w1/Body/{Sha(Utf8(winner))}";
        Assert.Equal(3, (await BlobsAsync<SplitTextEntity>()).Count); // original, winner, loser

        await PastTheServiceSecondAsync();
        Assert.Equal(2, await Store<SplitTextEntity>().SweepOrphanBlobsAsync(pk, TimeSpan.Zero, Ct));

        Assert.Equal(winnerName, Assert.Single(await BlobsAsync<SplitTextEntity>()).Name);
        Assert.Equal(winnerName, Reference(Assert.Single(await RawRowsAsync<SplitTextEntity>()), "Body").Path);
        Assert.Equal(winner, (await Store<SplitTextEntity>().OneAsync(Id("w1"), Ct))!.Body, StringComparer.Ordinal);

        static async Task<Exception?> Outcome(Task task)
        {
            try
            {
                await task;
                return null;
            }
#pragma warning disable CA1031 // the test inspects which exception each writer got
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        }
    }

    // ── registration ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectionStringRegistration_EmulatorShortcut_RegistersBlobClient_AndOffloads()
    {
        // The emulator shortcut is not an Azure Storage host name, yet it must still get a blob client.
        var shortcut = new ServiceCollection();
        shortcut.AddUnifiedTableStorage(DevelopmentStorage);
        using (var provider = shortcut.BuildServiceProvider())
            Assert.Equal("devstoreaccount1", provider.GetRequiredService<BlobServiceClient>().AccountName);

        Assert.SkipUnless(available, SkipReason);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage(ConnectionString, o => o.BlobOffload = Offload());
        await using var sp = services.BuildServiceProvider();
        tables.Add(nameof(SplitTextEntity));
        var store = sp.GetRequiredService<IStorage<SplitTextEntity>>();
        var text = Ascii(3_200_000, seed: 29);

        await store.CreateAsync(new SplitTextEntity { Id = Id("di1"), Body = text }, Ct);

        Assert.Equal(text, (await sp.GetRequiredService<IStorage<SplitTextEntity>>().OneAsync(Id("di1"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Single(await BlobsAsync<SplitTextEntity>());
    }

    /// <summary>Records every request a client sends, in order, labelled with the service.</summary>
    private sealed class RequestRecorder(string service, List<(string Service, string Method, string Path)> log) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            lock (log) log.Add((service, message.Request.Method.Method, message.Request.Uri.Path));
        }
    }
}
