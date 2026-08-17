using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="PolymorphicTableStorage{TBase}"/> over NSubstitute mocks of the Azure Tables
/// SDK. Cannot reuse <see cref="StorageHarness{T}"/>: its <c>where T : class, IEntity, new()</c> is
/// the exact constraint the polymorphic contract exists to escape.
/// </summary>
public sealed class PolymorphicHarness<TBase> : IDisposable
    where TBase : class
{
    public TableServiceClient Service { get; }

    public TableClient Table { get; }

    public PolymorphicTableStorage<TBase> Store { get; }

    /// <summary>The last entity handed to Add/Upsert/Update — the write-side assertion hook.</summary>
    public TableEntity? LastWrittenEntity { get; private set; }

    /// <summary>The last OData filter the store emitted — the read-side assertion hook.</summary>
    public string? LastQueryFilter { get; private set; }

    /// <summary>Every transaction the store submitted, in order — the batch assertion hook.</summary>
    public List<IReadOnlyList<TableTransactionAction>> Transactions { get; } = [];

    public PolymorphicHarness(string tableName = "TestTable", UnifiedTableStorageOptions? options = null)
    {
        Service = Substitute.For<TableServiceClient>();
        Table = Substitute.For<TableClient>();
        Service.GetTableClient(tableName).Returns(Table);

        // Table creation is lazy (the first operation awaits it); an unmocked substitute returns a
        // null Task and NREs on await, so every harness store gets a completed init by default.
        Table.CreateIfNotExistsAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<Azure.Data.Tables.Models.TableItem>>(null!));

        Store = new PolymorphicTableStorage<TBase>(
            Service, tableName, NullLogger<PolymorphicTableStorage<TBase>>.Instance, options);
    }

    public void SetupAdd() =>
        Table.AddEntityAsync(Arg.Do<TableEntity>(e => LastWrittenEntity = e), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupUpsert() =>
        Table.UpsertEntityAsync(
                 Arg.Do<TableEntity>(e => LastWrittenEntity = e),
                 Arg.Any<TableUpdateMode>(),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupMerge() =>
        Table.UpdateEntityAsync(
                 Arg.Do<TableEntity>(e => LastWrittenEntity = e),
                 Arg.Any<ETag>(),
                 Arg.Any<TableUpdateMode>(),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupGet(TableEntity row)
    {
        // Build the (substitute-backed) response BEFORE calling Returns() — a nested Substitute.For
        // call inside the Returns() chain corrupts NSubstitute's last-call context (see
        // StorageHarness.SetupGet, which hit the same trap first).
        var response = Mocks.Found(row);
        Table.GetEntityIfExistsAsync<TableEntity>(
                 row.PartitionKey, row.RowKey, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(response));
    }

    public void SetupNotFound()
    {
        var response = Mocks.NotFound();
        Table.GetEntityIfExistsAsync<TableEntity>(
                 Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(response));
    }

    public void SetupTransaction() =>
        Table.SubmitTransactionAsync(
                 Arg.Do<IEnumerable<TableTransactionAction>>(a => Transactions.Add(a.ToList())),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<IReadOnlyList<Response>>>(null!));

    public void SetupDelete() =>
        Table.DeleteEntityAsync(
                 Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    /// <summary>Arranges a single-page query result and captures the emitted filter.</summary>
    public void SetupQuery(params TableEntity[] rows) =>
        Table.QueryAsync<TableEntity>(
                 Arg.Do<string>(f => LastQueryFilter = f),
                 Arg.Any<int?>(),
                 Arg.Any<IEnumerable<string>>(),
                 Arg.Any<CancellationToken>())
             .Returns(Mocks.Pageable(rows));

    /// <summary>Arranges a MULTI-page query result, to prove continuation tokens are followed.</summary>
    public void SetupPagedQuery(TableEntity[] firstPage, TableEntity[] secondPage)
    {
        var pages = new[]
        {
            Page<TableEntity>.FromValues(firstPage, continuationToken: "next", new FakeResponse()),
            Page<TableEntity>.FromValues(secondPage, continuationToken: null, new FakeResponse()),
        };

        Table.QueryAsync<TableEntity>(
                 Arg.Do<string>(f => LastQueryFilter = f),
                 Arg.Any<int?>(),
                 Arg.Any<IEnumerable<string>>(),
                 Arg.Any<CancellationToken>())
             .Returns(AsyncPageable<TableEntity>.FromPages(pages));
    }

    public void Dispose()
    {
        // Nothing owned; the method exists so tests can use `using var` uniformly with StorageHarness.
    }
}
