using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// The failures the Table service reports, as the stores over a row store (the in-memory ones) raise them: the same status and
/// <see cref="RequestFailedException.ErrorCode"/> the SDK surfaces through <c>TableStorage&lt;T&gt;</c>, and a
/// failure inside an entity-group transaction wrapped in <see cref="TableTransactionFailedException"/> as the
/// SDK wraps it. None of them leaves a store as it is: like <c>TableStorage&lt;T&gt;</c>, the stores throw the
/// provider-neutral <see cref="StorageException"/> types (<see cref="Neutral"/>) and keep the service's
/// failure as the inner exception, so code that inspects either sees the same thing on both stores.
/// </summary>
internal static class ServiceErrors
{
    /// <summary>The message <c>TableStorage</c>'s callers saw on 0.8.3 for a lost conditional write.</summary>
    internal const string ModifiedByAnotherWriter = "Precondition Failed: the entity was modified by another writer.";

    /// <summary>409 <c>EntityAlreadyExists</c>: an insert of a key that is stored.</summary>
    internal static RequestFailedException AlreadyExists(string id) =>
        new(409, $"The specified entity already exists. Id: {id}", "EntityAlreadyExists", null);

    /// <summary>404 <c>ResourceNotFound</c>: a conditional write or merge of a key that is not stored.</summary>
    internal static RequestFailedException NotFound(string id) =>
        new(404, $"The specified resource does not exist. Id: {id}", "ResourceNotFound", null);

    /// <summary>412 <c>UpdateConditionNotSatisfied</c>: the stored version is not the one the write was conditional on.</summary>
    internal static RequestFailedException ConditionNotSatisfied(string message = ModifiedByAnotherWriter) =>
        new(412, message, "UpdateConditionNotSatisfied", null);

    /// <summary>400 <c>InvalidDuplicateRow</c>: one transaction names the same row twice.</summary>
    internal static RequestFailedException DuplicateRow(string id) =>
        new(400, $"InvalidDuplicateRow: duplicate key within the batch. Id: {id}", "InvalidDuplicateRow", null);

    /// <summary>A failure of an entity-group transaction, as the SDK reports it.</summary>
    internal static TableTransactionFailedException InTransaction(RequestFailedException failure) => new(failure);

    /// <summary>
    /// <paramref name="neutral"/> with the code of the service failure it carries as its
    /// <see cref="StorageException.ProviderErrorCode"/>, as <c>TableStorage&lt;T&gt;</c> sets it on a duplicate
    /// key or a conflict the service reported. <c>MayBeOwnWrite</c> stays false: this store never sends a
    /// request twice.
    /// </summary>
    internal static TException Coded<TException>(TException neutral) where TException : StorageException
    {
        neutral.ProviderErrorCode = (neutral.InnerException as RequestFailedException)?.ErrorCode;
        return neutral;
    }

    /// <summary>
    /// The provider-neutral exception <c>TableStorage&lt;T&gt;</c> throws for <paramref name="failure"/>, with
    /// the same message: a missing row (404) is <see cref="EntityNotFoundException"/>, a request the service
    /// refuses (400, 413) is <see cref="StorageRequestRejectedException"/>. <paramref name="failure"/> is the
    /// inner exception and its code the <see cref="StorageException.ProviderErrorCode"/>. This store never
    /// sends a request twice, so a duplicate or a conflict is never its own write
    /// (<see cref="DuplicateKeyException.MayBeOwnWrite"/> stays false).
    /// </summary>
    /// <param name="failure">The failure, as the service reports it.</param>
    /// <param name="entityType">The entity type name (the table name of a polymorphic store).</param>
    /// <param name="id">The id of the row the call addressed, when it addressed one.</param>
    internal static StorageException Neutral(RequestFailedException failure, string entityType, string? id)
    {
        var code = failure.ErrorCode;
        var described = string.IsNullOrEmpty(code)
            ? failure.Status.ToString(CultureInfo.InvariantCulture)
            : $"{failure.Status.ToString(CultureInfo.InvariantCulture)} {code}";
        return failure.Status switch
        {
            404 => id is null
                ? new EntityNotFoundException($"{entityType}: a row the request needs does not exist.", failure) { ProviderErrorCode = code }
                : new EntityNotFoundException(entityType, id, failure) { ProviderErrorCode = code },
            409 => id is null
                ? new DuplicateKeyException($"Duplicate key creating {entityType}: a row with this id already exists.", failure) { ProviderErrorCode = code }
                : new DuplicateKeyException(entityType, id, failure) { ProviderErrorCode = code },
            412 => id is null
                ? new ConcurrencyConflictException(
                    $"Concurrency conflict updating {entityType}: the row was modified by another writer since it was read.", failure) { ProviderErrorCode = code }
                : new ConcurrencyConflictException(entityType, id, failure) { ProviderErrorCode = code },
            400 or 413 => new StorageRequestRejectedException(
                $"{entityType}: the storage service rejected the request ({described}). Sending it again fails the same way.", code, failure),
            _ => new StorageException($"{entityType}: the storage request failed ({described}).", code, failure),
        };
    }

    /// <summary>
    /// 400 <c>InvalidInput</c> when <paramref name="partitionKey"/> or <paramref name="rowKey"/> is longer
    /// than <see cref="TableKeyRules.MaxKeyLength"/> characters or holds a character the service refuses in a
    /// key: <c>/</c>, <c>\</c>, <c>#</c>, <c>?</c>, or a control character (U+0000 to U+001F, U+007F to
    /// U+009F); null when both keys are accepted. These are the rules of <see cref="TableKeyRules"/>.
    /// </summary>
    internal static RequestFailedException? RefusedKey(string partitionKey, string rowKey)
    {
        var tooLong = partitionKey.Length > TableKeyRules.MaxKeyLength ? "PartitionKey" : rowKey.Length > TableKeyRules.MaxKeyLength ? "RowKey" : null;
        if (tooLong is not null)
            return new RequestFailedException(400,
                $"The '{tooLong}' is longer than the {TableKeyRules.MaxKeyLength.ToString(CultureInfo.InvariantCulture)} characters (1 KiB of UTF-16) a key holds.",
                "InvalidInput", null);
        var refused = HasRefusedCharacter(partitionKey) ? "PartitionKey" : HasRefusedCharacter(rowKey) ? "RowKey" : null;
        return refused is null
            ? null
            : new RequestFailedException(400,
                $"The '{refused}' holds a character the service refuses in a key ('/', '\\', '#', '?' or a control character).",
                "InvalidInput", null);
    }

    /// <summary>Throws <see cref="RefusedKey"/>, as the neutral exception, for a single-entity write.</summary>
    internal static void ThrowIfRefusedKey((string PartitionKey, string RowKey) keys, string entityType)
    {
        if (RefusedKey(keys.PartitionKey, keys.RowKey) is { } refused)
            throw Neutral(refused, entityType, id: null);
    }

    /// <summary>The largest property value the service accepts: 64 KiB (a string is UTF-16, so 32 K characters).</summary>
    internal const int MaxPropertyBytes = 64 * 1024;

    /// <summary>
    /// 400 <c>PropertyValueTooLarge</c> when a cell of <paramref name="row"/> holds a <c>byte[]</c> longer than
    /// 64 KiB or a string longer than 32 K characters, as the service refuses the write; null when every cell
    /// fits. (The serializer refuses, compresses or trims a string or JSON cell itself; a plain <c>byte[]</c>
    /// cell reaches the service as it is.)
    /// </summary>
    internal static RequestFailedException? OversizedProperty(TableEntity row)
    {
        foreach (var cell in row)
        {
            var bytes = cell.Value switch
            {
                byte[] b => b.Length,
                string text => text.Length * 2L,
                _ => 0L,
            };
            if (bytes > MaxPropertyBytes)
            {
                return new RequestFailedException(400,
                    "The property value exceeds the maximum allowed size (64KB). If the property value is a string, it is UTF-16 encoded and the maximum number of characters should be 32K or less.",
                    "PropertyValueTooLarge", null);
            }
        }
        return null;
    }

    /// <summary>
    /// 400 <c>EntityTooLarge</c> when <paramref name="row"/> is larger than the library's budget for one
    /// entity (<see cref="TableRequestSize.MaxEntityBytes"/>, 1,000,000 bytes by the estimate the library
    /// plans rows with, a margin below the service's 1 MiB); null when it fits.
    /// </summary>
    internal static RequestFailedException? OversizedRow(TableEntity row) =>
        TableRowSize.Estimate(row) > TableRequestSize.MaxEntityBytes
            ? new RequestFailedException(400, "The entity is larger than the maximum allowed size (1MB).", "EntityTooLarge", null)
            : null;

    /// <summary>
    /// What the service refuses about a row's content: <see cref="OversizedProperty"/>, then
    /// <see cref="OversizedRow"/>, then <see cref="TooManyProperties"/>; null when it accepts the row. A row that cannot be written at all is
    /// refused here with the exception the Azure provider meets before it sends anything: a
    /// <see cref="double"/> cell that is positive or negative infinity throws <see cref="ArgumentException"/>,
    /// as the SDK's JSON writer does (the service's payload has no form for it).
    /// </summary>
    internal static RequestFailedException? RefusedRow(TableEntity row)
    {
        foreach (var cell in row)
        {
            if (cell.Value is double value && double.IsInfinity(value))
                throw new ArgumentException(
                    $"The column '{cell.Key}' holds {(value > 0 ? "positive" : "negative")} infinity, which cannot be written to a table: .NET number values such as positive and negative infinity cannot be written as valid JSON.",
                    nameof(row));
        }
        return OversizedProperty(row) ?? OversizedRow(row) ?? TooManyProperties(row);
    }

    /// <summary>
    /// 400 <c>TooManyProperties</c> when <paramref name="row"/> has more than
    /// <see cref="TableRequestSize.MaxEntityProperties"/> (252) properties besides PartitionKey, RowKey and
    /// Timestamp, as the service refuses the write; null when it has no more. A null cell is not sent, so
    /// it is not counted.
    /// </summary>
    internal static RequestFailedException? TooManyProperties(TableEntity row)
    {
        var properties = 0;
        foreach (var cell in row)
        {
            if (cell.Value is not null && cell.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))
                properties++;
        }
        return properties > TableRequestSize.MaxEntityProperties
            ? new RequestFailedException(400,
                $"The entity has {properties} properties; at most {TableRequestSize.MaxEntityProperties} are allowed besides PartitionKey, RowKey and Timestamp.",
                "TooManyProperties", null)
            : null;
    }

    /// <summary>Throws <see cref="RefusedRow"/>, as the neutral exception, for a single-entity write.</summary>
    internal static void ThrowIfRefusedRow(TableEntity row, string entityType)
    {
        if (RefusedRow(row) is { } refused)
            throw Neutral(refused, entityType, id: null);
    }

    /// <summary>
    /// The neutral exception for a table that does not exist, as <c>TableStorage&lt;T&gt;</c> reports the
    /// service's 404 <c>TableNotFound</c>: never a missing row.
    /// </summary>
    internal static TableNotFoundException TableMissing(string tableName, Exception? cause) =>
        new($"Table '{tableName}' does not exist.", tableName,
            new RequestFailedException(404, "The table specified does not exist.", "TableNotFound", cause)) { ProviderErrorCode = "TableNotFound" };

    private static bool HasRefusedCharacter(string key)
    {
        foreach (var c in key)
        {
            if (c is '/' or '\\' or '#' or '?' or <= '\u001f' or (>= '\u007f' and <= '\u009f'))
                return true;
        }
        return false;
    }
}
