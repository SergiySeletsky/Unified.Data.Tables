using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Sqlite;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.Sqlite;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// 0.10.0: a handler migrated from <c>catch (RequestFailedException ex) when (ex.Status == 404)</c> reads
/// the status from <see cref="Exception.InnerException"/> or the code from
/// <see cref="StorageException.ProviderErrorCode"/>. A whole-entity update of a missing row of a type
/// with large values is found missing by the store's own read; it carries the service's 404 all the
/// same, on every provider (the in-memory and Azure cases are in
/// <see cref="InMemoryNeutralFailureParityTests"/>).
/// </summary>
public sealed class ProviderExceptionRidesAlongTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    [Theory]
    [InlineData(ConcurrencyMode.LastWriterWins)]
    [InlineData(ConcurrencyMode.Strict)]
    public async Task Sqlite_Update_OfAMissingRow_OfATypeWithLargeValues_CarriesThe404(ConcurrencyMode mode)
    {
        var store = new SqliteStorage<SplitTextEntity>(database.Open());

        var ex = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => store.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "b", ETag = "W/\"e\"" }, mode, Ct));

        Assert.Equal("p|r", ex.Id);
        Assert.Equal(nameof(SplitTextEntity), ex.EntityType);
        Assert.Equal("ResourceNotFound", ex.ProviderErrorCode);
        var inner = Assert.IsType<RequestFailedException>(ex.InnerException);
        Assert.Equal(404, inner.Status);
        Assert.Equal("ResourceNotFound", inner.ErrorCode);
    }

    [Fact]
    public async Task Sqlite_Update_OfAMissingRow_HasTheSameMessage_WithAndWithoutLargeValues()
    {
        var account = database.Open();
        var plain = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => new SqliteStorage<PlainEntity>(account).UpdateAsync(new PlainEntity { Id = "p|r" }, ConcurrencyMode.LastWriterWins, Ct));
        var large = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => new SqliteStorage<SplitTextEntity>(account).UpdateAsync(new SplitTextEntity { Id = "p|r" }, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Equal(plain.Message.Replace(nameof(PlainEntity), nameof(SplitTextEntity), StringComparison.Ordinal), large.Message);
        Assert.Equal(plain.ProviderErrorCode, large.ProviderErrorCode);
    }
}

/// <summary>
/// An all-or-nothing batch the serializer refuses before a request exists leaves the caller's entities as
/// they were on the Azure provider, as on the row-store providers: nothing was written, so no stamp the
/// call set in advance stays behind.
/// </summary>
// The oversized-cell policy is process-wide; the tests that change it share this collection.
[Collection("OversizedCellPolicy")]
public sealed class AtomicBatchRefusedBeforeSendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A string no compression brings under the 64 KB of a cell: the serializer refuses it.
    private static string Incompressible()
    {
        var random = new Random(42);
        var chars = new char[600_000];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)random.Next(0x21, 0x7F);
        return new string(chars);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Azure_ABatchTheSerializerRefuses_SendsNothing_AndLeavesTheEntities(bool upsert)
    {
        using var h = new StorageHarness<TestEntity>();
        var created = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        List<TestEntity> batch =
        [
            new() { Id = " P|First ", Name = "fits", CreatedAt = created, UpdatedAt = created, ETag = "W/\"v1\"" },
            new() { Id = "p|second", Name = Incompressible(), ETag = "W/\"v2\"" },
        ];
        var stamps = batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)).ToList();

        await Assert.ThrowsAsync<OversizedCellException>(
            () => upsert ? h.Store.UpsertAtomicAsync(batch, Ct) : h.Store.CreateAtomicAsync(batch, Ct));

        Assert.Equal(stamps, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task Sqlite_ABatchTheSerializerRefuses_LeavesTheEntities_AsTheAzureProviderDoes()
    {
        using var database = new TempDatabase();
        var store = new SqliteStorage<TestEntity>(database.Open());
        List<TestEntity> batch =
        [
            new() { Id = " P|First ", Name = "fits", ETag = "W/\"v1\"" },
            new() { Id = "p|second", Name = Incompressible() },
        ];
        var stamps = batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)).ToList();

        await Assert.ThrowsAsync<OversizedCellException>(() => store.CreateAtomicAsync(batch, Ct));

        Assert.Equal(stamps, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
        Assert.Equal(0, await store.CountAsync(ct: Ct));
    }
}
