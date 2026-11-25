using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// Size estimates and service limits for callers that build their own entity-group transactions with the
/// Azure SDK (<see cref="TableClient.SubmitTransactionAsync"/>). The estimates are the ones the library's
/// own planners use: <see cref="EstimateEntityBytes"/> is the stored size the plain batch planner budgets
/// (<see cref="BatchPlanner"/>), and <see cref="EstimateEntityWireBytes"/> is an upper bound of the JSON
/// body Azure.Data.Tables sends: every non-ASCII character and <c>" \ &lt; &gt; &amp; ' + `</c> escaped as
/// <c>\uXXXX</c> (six bytes), binary base64-encoded, and an <c>@odata.type</c> annotation for every value
/// that is not a string, <c>bool</c> or <c>int</c> (doubles included).
/// </summary>
/// <remarks>
/// A <see cref="TableEntity"/> is measured as is. Any other <see cref="ITableEntity"/> is measured as the SDK
/// serializes a model: its public readable instance properties except <see cref="ITableEntity.ETag"/> and
/// those marked <see cref="IgnoreDataMemberAttribute"/>, under their <see cref="DataMemberAttribute.Name"/>
/// when one is set, enums as their names.
/// </remarks>
public static class TableRequestSize
{
    /// <summary>The service limit on the actions of one entity-group transaction (100).</summary>
    public const int MaxTransactionActions = BatchPlanner.DefaultMaxCount;

    /// <summary>The service limit on the payload of one entity-group transaction (4 MiB).</summary>
    public const long MaxTransactionPayloadBytes = 4_194_304;

    /// <summary>
    /// The wire budget the library plans one transaction on: <see cref="MaxTransactionPayloadBytes"/> less
    /// 64 KiB for the multipart envelope and request headers. Compare the sum of
    /// <see cref="EstimateActionWireBytes"/> with it.
    /// </summary>
    public const long MaxTransactionWireBytes = MaxTransactionPayloadBytes - 65_536;

    /// <summary>
    /// The stored-size budget of one transaction (<see cref="BatchPlanner.DefaultMaxBytes"/>, 3,000,000 B).
    /// Compare the sum of <see cref="EstimateEntityBytes"/> with it.
    /// </summary>
    public const long MaxTransactionEntityBytes = BatchPlanner.DefaultMaxBytes;

    /// <summary>The service limit on one entity's stored size (1 MB), as <see cref="EstimateEntityBytes"/> measures it.</summary>
    public const long MaxEntityBytes = 1_000_000;

    /// <summary>The service limit on one entity's custom properties (252, besides PartitionKey, RowKey and Timestamp).</summary>
    public const int MaxEntityProperties = 252;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, string Column)[]> ModelColumns = new();

    /// <summary>The stored size of <paramref name="entity"/>, for the 1 MB entity limit and the 3,000,000 B transaction budget.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The estimate in bytes: about 88 B per entity and 8 B per property, two bytes per string character and one per binary byte.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static long EstimateEntityBytes(ITableEntity entity) => TableRowSize.Estimate(AsRow(entity));

    /// <summary>An upper bound of the JSON body the Azure SDK sends for <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The estimate in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static long EstimateEntityWireBytes(ITableEntity entity) => TableRowSize.WireSize(AsRow(entity));

    /// <summary>
    /// An upper bound of what <paramref name="action"/> adds to a transaction on the wire: its multipart
    /// framing and headers, its keys in the request URL and, unless it is a delete, the entity's body.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>The estimate in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    public static long EstimateActionWireBytes(TableTransactionAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.ActionType == TableTransactionActionType.Delete
            ? TableRowSize.DeleteActionWireSize(action.Entity.PartitionKey, action.Entity.RowKey)
            : TableRowSize.ActionWireSize(AsRow(action.Entity));
    }

    /// <summary>
    /// True when <paramref name="actions"/> fit one entity-group transaction as the library plans one: one
    /// partition, each row at most once (the service refuses a transaction with two actions on one row,
    /// 400 <c>InvalidDuplicateRow</c>; row keys compare ordinally), at most <see cref="MaxTransactionActions"/>
    /// actions, at most <see cref="MaxTransactionEntityBytes"/> stored and at most
    /// <see cref="MaxTransactionWireBytes"/> on the wire.
    /// </summary>
    /// <param name="actions">The actions of the transaction.</param>
    /// <returns>Whether they fit; an empty set fits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="actions"/> is null.</exception>
    public static bool FitsOneTransaction(IEnumerable<TableTransactionAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        string? partitionKey = null;
        var count = 0;
        long stored = 0;
        long wire = 0;
        var rowKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            if (++count > MaxTransactionActions)
                return false;
            if (count == 1)
                partitionKey = action.Entity.PartitionKey;
            else if (!string.Equals(partitionKey, action.Entity.PartitionKey, StringComparison.Ordinal))
                return false;
            if (!rowKeys.Add(action.Entity.RowKey ?? string.Empty))
                return false;
            var row = action.ActionType == TableTransactionActionType.Delete
                ? new TableEntity(action.Entity.PartitionKey, action.Entity.RowKey)
                : AsRow(action.Entity);
            stored += TableRowSize.Estimate(row);
            wire += action.ActionType == TableTransactionActionType.Delete
                ? TableRowSize.DeleteActionWireSize(row.PartitionKey, row.RowKey)
                : TableRowSize.ActionWireSize(row);
            if (stored > MaxTransactionEntityBytes || wire > MaxTransactionWireBytes)
                return false;
        }
        return true;
    }

    private static TableEntity AsRow(ITableEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is TableEntity row)
            return row;
        var model = new TableEntity(entity.PartitionKey, entity.RowKey);
        if (entity.Timestamp is { } timestamp)
            model.Timestamp = timestamp;
        foreach (var (property, column) in ModelColumns.GetOrAdd(entity.GetType(), ColumnsOf))
        {
            var value = property.GetValue(entity);
            model[column] = value is Enum e ? e.ToString() : value;
        }
        return model;
    }

    private static (PropertyInfo Property, string Column)[] ColumnsOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                && p.Name is not (nameof(ITableEntity.PartitionKey) or nameof(ITableEntity.RowKey)
                    or nameof(ITableEntity.Timestamp) or nameof(ITableEntity.ETag))
                && p.GetCustomAttribute<IgnoreDataMemberAttribute>() is null)
            .Select(p => (p, p.GetCustomAttribute<DataMemberAttribute>()?.Name is { Length: > 0 } name ? name : p.Name))
            .ToArray();
}
