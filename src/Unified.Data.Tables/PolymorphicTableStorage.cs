using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// Azure Table Storage implementation of <see cref="IPolymorphicStorage{TBase}"/>: many concrete
/// types in one table, discriminated by <see cref="SystemColumnNames.TypeName"/>.
/// </summary>
/// <remarks>
/// The table name is supplied explicitly rather than derived from <typeparamref name="TBase"/>: a
/// base type says nothing about which of several tables holds it, and one base commonly addresses
/// more than one table.
/// <para>
/// There is no cache. <c>TableStorage&lt;T&gt;</c> keys its cache on <c>typeof(T).FullName</c>, so
/// two stores over one table would never invalidate each other, and its snapshot round-trips
/// through the base-typed read — silently downcasting a derived instance and dropping its data.
/// Rather than mitigate three coupled hazards, this store has none, which also suits the
/// append-only fact tables it is designed for.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class PolymorphicTableStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly TableClient client;
    private readonly TableInitializer initializer;
    private readonly ITypeDiscriminator discriminator;
    private readonly ILogger logger;
    private readonly string tableName;

    /// <summary>Creates a store over one named table.</summary>
    /// <param name="serviceClient">The Azure Tables service client.</param>
    /// <param name="tableName">The table this store owns.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceClient"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public PolymorphicTableStorage(
        TableServiceClient serviceClient,
        string tableName,
        ILogger<PolymorphicTableStorage<TBase>> logger,
        UnifiedTableStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        this.tableName = tableName;
        this.logger = logger;
        discriminator = (options ?? new UnifiedTableStorageOptions()).ResolveTypeDiscriminator();
        client = serviceClient.GetTableClient(tableName);
        initializer = new TableInitializer(client);
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => initializer.EnsureAsync(ct);

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);

        try
        {
            await client.AddEntityAsync(row, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new DuplicateKeyException(tableName, write.Key.ToId(), ex);
        }

        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);
        await client.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task MergeColumnsAsync(
        TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        var response = await client.GetEntityIfExistsAsync<TableEntity>(
            key.PartitionKey, key.RowKey, cancellationToken: ct);

        return response.HasValue ? ToEntry(response.Value!) : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null, int? take = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public async Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        try
        {
            await client.DeleteEntityAsync(key.PartitionKey, key.RowKey, ETag.All, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Idempotent by contract: deleting what is already gone is the caller's desired state.
            logger.LogDebug("Delete of missing row {Key} in {Table} treated as a no-op.", key, tableName);
        }
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    // Build the row: serialize the object with persistType FALSE, then stamp the discriminator
    // ourselves. Composing this way rather than extending ToTableEntity is what gives the
    // ITypeDiscriminator seam for free, with zero change to the existing write path.
    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is null)
            return row;

        foreach (var column in write.SystemColumns)
        {
            ValidateSystemColumn(column.Key);
            row[column.Key] = column.Value;
        }

        return row;
    }

    private PolymorphicEntry<TBase> ToEntry(TableEntity row)
    {
        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        // Throws for a discriminator that is present but broken; returns false only for a row that
        // never carried one.
        var item = row.TryFromTableEntity<TBase>(discriminator, out var materialized) ? materialized : null;

        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            columns[cell.Key] = cell.Value;
        }

        return new PolymorphicEntry<TBase>(
            new TableKey(row.PartitionKey, row.RowKey),
            item,
            storedDiscriminator,
            row.ETag == default ? null : row.ETag.ToString(),
            row.Timestamp,
            columns);
    }

    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }
}
