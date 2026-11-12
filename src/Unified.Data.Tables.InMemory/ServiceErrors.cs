using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// The failures the Table service reports, as the in-memory stores raise them: the same status and
/// <see cref="RequestFailedException.ErrorCode"/> the SDK surfaces through <c>TableStorage&lt;T&gt;</c>, and a
/// failure inside an entity-group transaction wrapped in <see cref="TableTransactionFailedException"/> as the
/// SDK wraps it, so code that inspects either sees the same thing on both stores.
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
    /// 400 <c>InvalidInput</c> when <paramref name="partitionKey"/> or <paramref name="rowKey"/> holds a
    /// character the service refuses in a key: <c>/</c>, <c>\</c>, <c>#</c>, <c>?</c>, or a control character
    /// (U+0000 to U+001F, U+007F to U+009F); null when both keys are accepted.
    /// </summary>
    internal static RequestFailedException? RefusedKey(string partitionKey, string rowKey)
    {
        var refused = HasRefusedCharacter(partitionKey) ? "PartitionKey" : HasRefusedCharacter(rowKey) ? "RowKey" : null;
        return refused is null
            ? null
            : new RequestFailedException(400,
                $"The '{refused}' holds a character the service refuses in a key ('/', '\\', '#', '?' or a control character).",
                "InvalidInput", null);
    }

    /// <summary>Throws <see cref="RefusedKey"/> for a single-entity write.</summary>
    internal static void ThrowIfRefusedKey((string PartitionKey, string RowKey) keys)
    {
        if (RefusedKey(keys.PartitionKey, keys.RowKey) is { } refused)
            throw refused;
    }

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
