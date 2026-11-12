using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>What the read-cache race tests need from an entity: a value and the version that wrote it.</summary>
public interface IRaceDoc
{
    string? Body { get; set; }

    int Version { get; set; }
}

/// <summary>A split-aware entity: its body may be one cell, continuation rows or a blob.</summary>
public class RaceDoc : Entity, IRaceDoc
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public int Version { get; set; }
}

/// <summary>A type that opts into nothing.</summary>
public class RacePlainDoc : Entity, IRaceDoc
{
    public string? Body { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// A plain entity whose body getter calls back into the running test, so the test can act at the moment
/// the store copies the entity for its cache (a serializer round trip, which reads the getter).
/// </summary>
public class RaceProbeDoc : Entity, IRaceDoc
{
    /// <summary>The probe of the test that is running (one per async flow).</summary>
    public static readonly AsyncLocal<RaceProbe?> Probe = new();

    private string? body;

    public string? Body
    {
        get
        {
            Probe.Value?.BodyRead();
            return body;
        }
        set => body = value;
    }

    public int Version { get; set; }
}

/// <summary>Runs an action once, at the next read of a <see cref="RaceProbeDoc"/> body.</summary>
public sealed class RaceProbe
{
    private Action? armed;

    public void Arm(Action action) => armed = action;

    public void BodyRead() => Interlocked.Exchange(ref armed, null)?.Invoke();
}

/// <summary>One request a store sent to <see cref="RacingTableClient"/>.</summary>
internal sealed record TableRequest(string Operation, string? PartitionKey, string? RowKey, string? Filter, IReadOnlyList<string>? Select)
{
    /// <summary>A point read of a whole row (the read paths project nothing; the write paths project).</summary>
    public bool IsWholeRowPointRead => Operation == "get" && Select is null;

    public bool IsQuery => Operation == "query";

    /// <summary>The range query for an entity's continuation rows.</summary>
    public bool IsContinuationRowQuery =>
        IsQuery && Filter is not null && Filter.Contains("_SplitOf eq", StringComparison.Ordinal);

    /// <summary>A query for entities (a partition, a page, a predicate, the whole table).</summary>
    public bool IsEntityQuery => IsQuery && !IsContinuationRowQuery;
}

/// <summary>
/// An in-memory <see cref="TableClient"/> for the read-cache race tests. It keeps real rows with real
/// versions (every write changes the ETag, conditions are enforced, a transaction is atomic), so the
/// store's own write and delete paths run against it unchanged. A test can hold a read's response "in
/// flight" (<see cref="ResponseInFlight"/>: the rows were read, the caller has not received them yet)
/// and run code right after a write committed, before its response returns (<see cref="Committed"/>).
/// </summary>
internal sealed partial class RacingTableClient : TableClient
{
    private readonly object gate = new();
    private readonly Dictionary<(string PartitionKey, string RowKey), TableEntity> rows = [];
    private readonly List<TableRequest> requests = [];
    private long version;

    /// <summary>Awaited after a read captured its rows and before the caller receives them.</summary>
    public Func<TableRequest, Task>? ResponseInFlight { get; set; }

    /// <summary>Called right after a write or delete committed, before its response returns.</summary>
    public Action<TableRequest>? Committed { get; set; }

    /// <summary>Every request so far, in order.</summary>
    public IReadOnlyList<TableRequest> Requests
    {
        get
        {
            lock (gate) return [.. requests];
        }
    }

    public int Count(Func<TableRequest, bool> predicate)
    {
        lock (gate) return requests.Count(predicate);
    }

    /// <summary>A copy of the stored row; null when absent.</summary>
    public TableEntity? Stored(string partitionKey, string rowKey)
    {
        lock (gate) return rows.TryGetValue((partitionKey, rowKey), out var row) ? Copy(row, null) : null;
    }

    /// <summary>Copies of every stored row, continuation rows included, in key order.</summary>
    public IReadOnlyList<TableEntity> StoredRows()
    {
        lock (gate)
        {
            return rows.Values.OrderBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal)
                .Select(r => Copy(r, null)).ToList();
        }
    }

    public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Response<TableItem>>(null!);

    // ── reads ──────────────────────────────────────────────────────────────

    public override async Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(
        string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var request = new TableRequest("get", partitionKey, rowKey, null, select?.ToList());
        TableEntity? found;
        lock (gate)
        {
            found = rows.TryGetValue((partitionKey, rowKey), out var row) ? Copy(row, request.Select) : null;
            requests.Add(request);
        }
        if (ResponseInFlight is { } hook)
            await hook(request);
        return found is null
            ? new NoValue<T>()
            : Response.FromValue((T)(object)found, new FakeResponse(found.ETag.ToString()));
    }

    public override AsyncPageable<T> QueryAsync<T>(
        Expression<Func<T, bool>> filter, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var matches = ((Expression<Func<TableEntity, bool>>)(object)filter).Compile();
        return new Pages<T>(this, matches, maxPerPage, new TableRequest("query", null, null, filter.ToString(), select?.ToList()));
    }

    public override AsyncPageable<T> QueryAsync<T>(
        string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default) =>
        new Pages<T>(this, Matcher(filter), maxPerPage, new TableRequest("query", null, null, filter, select?.ToList()));

    private sealed class Pages<T>(RacingTableClient table, Func<TableEntity, bool> matches, int? maxPerPage, TableRequest request)
        : AsyncPageable<T> where T : notnull
    {
        public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            var size = pageSizeHint ?? maxPerPage ?? 1000;
            var start = continuationToken is null ? 0 : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            while (true)
            {
                // One request: the rows are read now, and reach the caller after the response's flight.
                List<T> page;
                bool more;
                lock (table.gate)
                {
                    var all = table.rows.Values.Where(matches)
                        .OrderBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal).ToList();
                    page = all.Skip(start).Take(size).Select(r => (T)(object)Copy(r, request.Select)).ToList();
                    more = start + size < all.Count;
                    table.requests.Add(request);
                }
                if (table.ResponseInFlight is { } hook)
                    await hook(request);
                start += size;
                yield return Page<T>.FromValues(page, more ? start.ToString(CultureInfo.InvariantCulture) : null, new FakeResponse());
                if (!more)
                    yield break;
            }
        }
    }

    // ── writes ─────────────────────────────────────────────────────────────

    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default) =>
        Commit("add", entity.PartitionKey, entity.RowKey, () => Apply(new TableTransactionAction(TableTransactionActionType.Add, entity)));

    public override Task<Response> UpsertEntityAsync<T>(T entity, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) =>
        Commit("upsert", entity.PartitionKey, entity.RowKey, () => Apply(new TableTransactionAction(
            mode == TableUpdateMode.Replace ? TableTransactionActionType.UpsertReplace : TableTransactionActionType.UpsertMerge, entity)));

    public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) =>
        Commit("update", entity.PartitionKey, entity.RowKey, () => Apply(new TableTransactionAction(
            mode == TableUpdateMode.Replace ? TableTransactionActionType.UpdateReplace : TableTransactionActionType.UpdateMerge, entity, ifMatch)));

    public override Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch = default, CancellationToken cancellationToken = default) =>
        Commit("delete", partitionKey, rowKey, () =>
        {
            // Like the SDK, deleting a row that is not there is not an error.
            if (!rows.ContainsKey((partitionKey, rowKey)))
                return default;
            return Apply(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(partitionKey, rowKey), ifMatch));
        });

    public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
        IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
    {
        var actions = transactionActions.ToList();
        var request = new TableRequest("transaction", actions[0].Entity.PartitionKey, null, null, null);
        var responses = new List<Response>(actions.Count);
        lock (gate)
        {
            // An entity-group transaction is atomic: every condition holds before anything is applied.
            foreach (var action in actions)
                Check(action);
            foreach (var action in actions)
                responses.Add(new FakeResponse(Apply(action).ToString()));
            requests.Add(request);
        }
        Committed?.Invoke(request);
        return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>(responses, new FakeResponse()));
    }

    private Task<Response> Commit(string operation, string partitionKey, string rowKey, Func<ETag> apply)
    {
        var request = new TableRequest(operation, partitionKey, rowKey, null, null);
        ETag etag;
        lock (gate)
        {
            etag = apply();
            requests.Add(request);
        }
        Committed?.Invoke(request);
        return Task.FromResult<Response>(new FakeResponse(etag.ToString()));
    }

    // Applies one action under the gate and returns the row's new version (default for a delete).
    private ETag Apply(TableTransactionAction action)
    {
        Check(action);
        var key = (action.Entity.PartitionKey, action.Entity.RowKey);
        if (action.ActionType == TableTransactionActionType.Delete)
        {
            rows.Remove(key);
            return default;
        }

        var merge = action.ActionType is TableTransactionActionType.UpdateMerge or TableTransactionActionType.UpsertMerge;
        var stored = merge && rows.TryGetValue(key, out var existing) ? Copy(existing, null) : new TableEntity(key.PartitionKey, key.RowKey);
        foreach (var (name, value) in (TableEntity)action.Entity)
        {
            if (!IsSystemCell(name))
                stored[name] = value;
        }
        stored.ETag = new ETag($"W/\"{(++version).ToString(CultureInfo.InvariantCulture)}\"");
        stored.Timestamp = DateTimeOffset.UtcNow;
        rows[key] = stored;
        return stored.ETag;
    }

    private void Check(TableTransactionAction action)
    {
        var exists = rows.TryGetValue((action.Entity.PartitionKey, action.Entity.RowKey), out var stored);
        switch (action.ActionType)
        {
            case TableTransactionActionType.Add when exists:
                throw new RequestFailedException(409, "The specified entity already exists.", "EntityAlreadyExists", null);
            case TableTransactionActionType.UpdateMerge or TableTransactionActionType.UpdateReplace or TableTransactionActionType.Delete:
                if (!exists)
                    throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
                if (action.ETag != default && action.ETag != ETag.All && action.ETag != stored!.ETag)
                    throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
                break;
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static bool IsSystemCell(string name) => name is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag";

    // What the service returns: the row's version always, and only the selected cells of a projection.
    private static TableEntity Copy(TableEntity source, IReadOnlyList<string>? select)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey) { ETag = source.ETag };
        if (select is null || select.Contains("Timestamp"))
            copy.Timestamp = source.Timestamp;
        foreach (var (name, value) in source)
        {
            if (!IsSystemCell(name) && (select is null || select.Contains(name)))
                copy[name] = value;
        }
        return copy;
    }

    [GeneratedRegex(@"(?<column>[A-Za-z_][A-Za-z0-9_]*) (?<op>eq|ne|gt|ge|lt|le) (?:'(?<text>(?:[^']|'')*)'|(?<number>-?\d+))")]
    private static partial Regex Clause();

    // The filters the store sends in these tests: comparisons of text or integer columns joined by "and".
    private static Func<TableEntity, bool> Matcher(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return static _ => true;

        var clauses = Clause().Matches(filter);
        var rest = Clause().Replace(filter, string.Empty).Replace(" and ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal).Replace(")", string.Empty, StringComparison.Ordinal);
        if (clauses.Count == 0 || !string.IsNullOrWhiteSpace(rest))
            throw new NotSupportedException($"{nameof(RacingTableClient)} does not evaluate the filter '{filter}'.");

        return row => clauses.All(clause => Holds(row, clause));
    }

    private static bool Holds(TableEntity row, Match clause)
    {
        var column = clause.Groups["column"].Value;
        object? cell = column switch
        {
            "PartitionKey" => row.PartitionKey,
            "RowKey" => row.RowKey,
            _ => row.TryGetValue(column, out var value) ? value : null,
        };
        int order;
        if (clause.Groups["number"].Success)
        {
            if (cell is not (int or long))
                return false;
            order = Convert.ToInt64(cell, CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(clause.Groups["number"].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            if (cell is not string text)
                return false;
            order = string.CompareOrdinal(text, clause.Groups["text"].Value.Replace("''", "'", StringComparison.Ordinal));
        }
        return clause.Groups["op"].Value switch
        {
            "eq" => order == 0,
            "ne" => order != 0,
            "gt" => order > 0,
            "ge" => order >= 0,
            "lt" => order < 0,
            _ => order <= 0,
        };
    }

    private sealed class NoValue<T> : NullableResponse<T>
    {
        public override bool HasValue => false;

        public override T? Value => throw new InvalidOperationException("The entity does not exist.");

        public override Response GetRawResponse() => new FakeResponse();
    }
}

/// <summary>
/// A real <see cref="MemoryCache"/> that tells a test, on the calling thread, when an entry has just been
/// evicted (<c>"remove"</c>) or stored (<c>"set"</c>), so the test can run a reader to completion in the
/// middle of a writer's cache maintenance.
/// </summary>
internal sealed class ObservedMemoryCache : IMemoryCache
{
    private readonly MemoryCache inner = new(new MemoryCacheOptions());

    /// <summary>Called with the operation and the key right after it took effect.</summary>
    public Action<string, string>? After { get; set; }

    public ICacheEntry CreateEntry(object key) => new Entry(inner.CreateEntry(key), this);

    public void Remove(object key)
    {
        inner.Remove(key);
        After?.Invoke("remove", (string)key);
    }

    public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

    /// <summary>Evicts every entry (no <see cref="After"/> call).</summary>
    public void Clear() => inner.Clear();

    public void Dispose() => inner.Dispose();

    // An entry is stored when it is disposed (that is how IMemoryCache.Set commits it).
    private sealed class Entry(ICacheEntry entry, ObservedMemoryCache owner) : ICacheEntry
    {
        public object Key => entry.Key;

        public object? Value
        {
            get => entry.Value;
            set => entry.Value = value;
        }

        public DateTimeOffset? AbsoluteExpiration
        {
            get => entry.AbsoluteExpiration;
            set => entry.AbsoluteExpiration = value;
        }

        public TimeSpan? AbsoluteExpirationRelativeToNow
        {
            get => entry.AbsoluteExpirationRelativeToNow;
            set => entry.AbsoluteExpirationRelativeToNow = value;
        }

        public TimeSpan? SlidingExpiration
        {
            get => entry.SlidingExpiration;
            set => entry.SlidingExpiration = value;
        }

        public IList<IChangeToken> ExpirationTokens => entry.ExpirationTokens;

        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => entry.PostEvictionCallbacks;

        public CacheItemPriority Priority
        {
            get => entry.Priority;
            set => entry.Priority = value;
        }

        public long? Size
        {
            get => entry.Size;
            set => entry.Size = value;
        }

        public void Dispose()
        {
            entry.Dispose();
            owner.After?.Invoke("set", (string)entry.Key);
        }
    }
}
