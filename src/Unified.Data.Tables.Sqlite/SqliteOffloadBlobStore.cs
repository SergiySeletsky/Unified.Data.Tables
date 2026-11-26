using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// The blob container of offloaded values, kept in the <c>__blobs</c> table of the database, with the
/// create-only, content-addressed contract of the Azure one: an existing blob with a matching hash is
/// success, a different hash throws, a touch refreshes the last-modified time, and a conditional delete
/// loses to a newer touch. Like the Azure store it refuses a name the Blobs SDK would not address as
/// given, so a naming regression fails here too.
/// </summary>
/// <remarks>
/// A blob is one row and is read and written whole, so a value is limited by what SQLite stores in one
/// row (1 GB by default) and by memory. The upload of a blob is a transaction of its own, like the
/// request to the blob service it stands in for: it is not part of the commit of the row that refers to
/// it, which is why the engine uploads first and sweeps orphans.
/// </remarks>
/// <param name="database">The database.</param>
/// <param name="container">The container the store's blobs are kept apart by (<see cref="OffloadOptions.ContainerName"/>).</param>
internal sealed class SqliteOffloadBlobStore(SqliteDatabase database, string container) : IOffloadBlobStore
{
    private const string Table = SqliteDatabase.BlobsTable;

    private SqliteCommand Command(SqliteConnection connection, string sql, string? name = null)
    {
        var command = SqliteDatabase.Command(connection, sql);
        command.Parameters.AddWithValue("$container", container);
        if (name is not null)
            command.Parameters.AddWithValue("$name", name);
        return command;
    }

    /// <inheritdoc />
    public async Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        OffloadBlobNames.ThrowIfNotAddressable(name);
        return await database.ReadAsync(connection =>
        {
            using var command = Command(connection, $"SELECT sha256, length, modified FROM {Table} WHERE container = $container AND name = $name", name);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new BlobState(reader.GetString(0), reader.GetInt64(1), Time(reader.GetInt64(2))) : null;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reference);
        OffloadBlobNames.ThrowIfNotAddressable(name);
        // Read before the write lock is taken: the stream may be slow, the lock is the database's only one.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        await database.WriteAsync(connection =>
        {
            if (Stored(connection, name) is { } existing)
            {
                if (!string.Equals(existing.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TableValueIntegrityException(name, $"blob already exists with SHA-256 {existing.Sha256}, not {reference.Sha256}.");
                Touch(connection, name, existing.Modified);
                return true;
            }
            using var insert = Command(connection,
                $"INSERT INTO {Table} (container, name, sha256, length, content_type, encoding, content_encoding, modified, content) " +
                "VALUES ($container, $name, $sha256, $length, $contentType, $encoding, $contentEncoding, $modified, $content)", name);
            insert.Parameters.AddWithValue("$sha256", reference.Sha256);
            insert.Parameters.AddWithValue("$length", reference.Length);
            insert.Parameters.AddWithValue("$contentType", reference.ContentType);
            insert.Parameters.AddWithValue("$encoding", reference.Encoding);
            insert.Parameters.AddWithValue("$contentEncoding", (object?)contentEncoding ?? DBNull.Value);
            insert.Parameters.AddWithValue("$modified", database.NextBlobTime().UtcTicks);
            insert.Parameters.AddWithValue("$content", bytes);
            insert.ExecuteNonQuery();
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TouchAsync(string name, CancellationToken ct)
    {
        OffloadBlobNames.ThrowIfNotAddressable(name);
        await database.WriteAsync(connection =>
        {
            if (Stored(connection, name) is not { } existing)
                throw new TableValueIntegrityException(name, "blob missing");
            Touch(connection, name, existing.Modified);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The stream is over the stored bytes as they were when it was opened: a touch, and a delete, of the
    /// blob while it is read do not affect it.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        OffloadBlobNames.ThrowIfNotAddressable(name);
        var bytes = await database.ReadAsync(connection =>
        {
            using var command = Command(connection, $"SELECT content FROM {Table} WHERE container = $container AND name = $name", name);
            using var reader = command.ExecuteReader();
            return reader.Read() ? reader.GetFieldValue<byte[]>(0) : null;
        }, ct).ConfigureAwait(false);
        return bytes is null
            ? throw new TableValueIntegrityException(column, "blob missing")
            : new MemoryStream(bytes, writable: false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        OffloadBlobNames.ThrowIfNotAddressable(name);
        return await database.WriteAsync(connection =>
        {
            using var command = Command(connection,
                $"DELETE FROM {Table} WHERE container = $container AND name = $name AND ($since IS NULL OR modified <= $since)", name);
            command.Parameters.AddWithValue("$since", unmodifiedSince is { } since ? since.UtcTicks : (object)DBNull.Value);
            return command.ExecuteNonQuery() > 0;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The clock that stamps the last-modified times: never behind any time it has stamped.</remarks>
    public Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<DateTimeOffset?>(database.NextBlobTime());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // Read whole, then handed out: no statement, and no connection, is held while the caller works.
        var found = await database.ReadAsync(connection =>
        {
            var blobs = new List<(string Name, DateTimeOffset LastModified)>();
            // The index narrows the scan to the names at or above the prefix (and, when the prefix ends
            // in a character that has a next one in ASCII, below the first name past it); which of them
            // start with the prefix, and their order, is decided here, on the strings.
            var upper = prefix.Length > 0 && prefix[^1] < '\u007f' ? string.Concat(prefix.AsSpan(0, prefix.Length - 1), ((char)(prefix[^1] + 1)).ToString()) : null;
            using var command = Command(connection,
                $"SELECT name, modified FROM {Table} WHERE container = $container AND name >= $prefix" + (upper is null ? "" : " AND name < $upper"));
            command.Parameters.AddWithValue("$prefix", prefix);
            if (upper is not null)
                command.Parameters.AddWithValue("$upper", upper);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    blobs.Add((name, Time(reader.GetInt64(1))));
            }
            blobs.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return blobs;
        }, ct).ConfigureAwait(false);
        foreach (var blob in found)
        {
            ct.ThrowIfCancellationRequested();
            yield return blob;
        }
    }

    private (string Sha256, DateTimeOffset Modified)? Stored(SqliteConnection connection, string name)
    {
        using var command = Command(connection, $"SELECT sha256, modified FROM {Table} WHERE container = $container AND name = $name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), Time(reader.GetInt64(1))) : null;
    }

    // Inside a write transaction: a last-modified time after the one the blob has, whoever set that.
    private void Touch(SqliteConnection connection, string name, DateTimeOffset modified)
    {
        using var command = Command(connection, $"UPDATE {Table} SET modified = $modified WHERE container = $container AND name = $name", name);
        command.Parameters.AddWithValue("$modified", database.NextBlobTime(after: modified).UtcTicks);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset Time(long utcTicks) => new(utcTicks, TimeSpan.Zero);
}
