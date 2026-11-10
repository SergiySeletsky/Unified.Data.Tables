using System.Globalization;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>A logical row planned onto one primary row and zero or more continuation rows.</summary>
/// <param name="Primary">The row that carries ordinary cells, manifests and blob references.</param>
/// <param name="Parts">Continuation rows, in index order (the first has index 1).</param>
internal sealed record SplitRowPlan(TableEntity Primary, IReadOnlyList<TableEntity> Parts);

/// <summary>
/// Pure layout of a logical entity bag onto a primary row plus continuation rows
/// (<c>{RowKey}~0001</c>...), and the inverse assembly. No I/O.
/// </summary>
internal static class SplitRowLayout
{
    internal const int MaxPropertiesPerRow = 252;
    internal const long MaxEntityBytes = 1_000_000;

    /// <summary>
    /// The budget of one row's JSON body (<see cref="TableRowSize.WireSize"/>) when split cells are
    /// placed on it. The service limits an entity by its stored size, which <see cref="MaxEntityBytes"/>
    /// bounds; the storage emulator limits it by its request body instead, where escaped text is up to
    /// six bytes per character and binary is base64. Split cells are placed within both, so a split row
    /// is accepted by either. Cells that cannot be split are still held only to <see cref="MaxEntityBytes"/>.
    /// </summary>
    internal const long MaxEntityWireBytes = 1_000_000;
    internal const int MaxRowKeyLength = 480;

    private static bool IsSystemKey(string key) =>
        key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag";

    private static bool IsChunk(string column) =>
        SplitCodec.TryParseLargeValueColumn(column, out _, out var kind, out _) && kind == LargeValueCellKind.Chunk;

    private static long CellCost(string key, object? value) =>
        8L + (key.Length * 2L) + value switch
        {
            byte[] binary => binary.Length,
            BinaryData binary => binary.ToMemory().Length,
            string text => text.Length * 2L,
            _ => 8L,
        };

    private static int CustomCount(TableEntity row)
    {
        var n = 0;
        foreach (var key in row.Keys)
            if (!IsSystemKey(key)) n++;
        return n;
    }

    /// <summary>Plans <paramref name="logical"/>; returns it unchanged (no parts) when it fits one row.</summary>
    internal static SplitRowPlan Plan(TableEntity logical)
    {
        if (CustomCount(logical) <= MaxPropertiesPerRow && TableRowSize.Estimate(logical) <= MaxEntityBytes
            && (TableRowSize.WireSize(logical) <= MaxEntityWireBytes || !logical.Keys.Any(IsChunk)))
            return new SplitRowPlan(logical, []);

        var primary = new TableEntity(logical.PartitionKey, logical.RowKey)
        {
            ETag = logical.ETag,
            Timestamp = logical.Timestamp,
        };
        var chunks = new List<KeyValuePair<string, object>>();
        foreach (var key in logical.Keys)
        {
            if (IsSystemKey(key)) continue;
            if (IsChunk(key)) chunks.Add(new(key, logical[key]));
            else primary[key] = logical[key];
        }

        // Reserve the cell now (its value is rewritten once the part count is known) so it is budgeted.
        primary[SystemColumnNames.SplitParts] = 0;

        var parts = new List<TableEntity>();
        var row = primary;
        var props = CustomCount(primary);
        var bytes = TableRowSize.Estimate(primary);
        var wire = TableRowSize.WireSize(primary);
        var placedInRow = 0;

        if (props > MaxPropertiesPerRow || bytes > MaxEntityBytes)
            throw new OversizedValueException(
                logical.RowKey ?? "(row)",
                bytes,
                "the cells that cannot be split onto continuation rows exceed the entity limit of "
                + $"{MaxPropertiesPerRow} properties and {MaxEntityBytes:N0} estimated bytes");

        foreach (var (key, value) in chunks)
        {
            var cost = CellCost(key, value);
            var wireCost = TableRowSize.PropertyWireSize(key, value);
            // Only a fresh continuation row (one that holds nothing yet) may exceed its budget; the
            // primary must always open a part instead.
            var freshPart = !ReferenceEquals(row, primary) && placedInRow == 0;
            if (!freshPart && (props + 1 > MaxPropertiesPerRow || bytes + cost > MaxEntityBytes || wire + wireCost > MaxEntityWireBytes))
            {
                row = NewPart(logical.PartitionKey, logical.RowKey, parts.Count + 1);
                parts.Add(row);
                props = 2;
                bytes = TableRowSize.Estimate(row);
                wire = TableRowSize.WireSize(row);
                placedInRow = 0;
            }
            row[key] = value;
            props++;
            bytes += cost;
            wire += wireCost;
            placedInRow++;
        }

        primary[SystemColumnNames.SplitParts] = parts.Count;
        return new SplitRowPlan(primary, parts);
    }

    /// <summary>
    /// True when primary plus parts plus <paramref name="extraActions"/> (deletes of stale continuation
    /// rows) fit one entity-group transaction: within <see cref="BatchPlanner.DefaultMaxCount"/> actions,
    /// within <see cref="BatchPlanner.DefaultMaxBytes"/> as <see cref="TableRowSize.Estimate"/> measures,
    /// and within <see cref="TableRowSize.MaxTransactionWireBytes"/> as the SDK will actually send it
    /// (JSON-escaped text is up to six bytes per character on the wire).
    /// </summary>
    internal static bool FitsOneTransaction(SplitRowPlan plan, int extraActions)
    {
        if (1 + plan.Parts.Count + extraActions > BatchPlanner.DefaultMaxCount) return false;
        // A stale delete carries no body, but its keys and framing still count toward the payload.
        var staleKey = plan.Primary.RowKey + "~0000";
        var deleteEstimate = TableRowSize.Estimate(new TableEntity(plan.Primary.PartitionKey, staleKey));
        var sizes = new List<long>(1 + plan.Parts.Count + extraActions) { TableRowSize.Estimate(plan.Primary) };
        foreach (var part in plan.Parts) sizes.Add(TableRowSize.Estimate(part));
        for (var i = 0; i < extraActions; i++) sizes.Add(deleteEstimate);
        try
        {
            if (BatchPlanner.Plan(sizes).Count != 1) return false;
        }
        catch (BatchPayloadTooLargeException)
        {
            return false;
        }

        var wire = TableRowSize.ActionWireSize(plan.Primary)
            + (extraActions * TableRowSize.DeleteActionWireSize(plan.Primary.PartitionKey, staleKey));
        foreach (var part in plan.Parts) wire += TableRowSize.ActionWireSize(part);
        return wire <= TableRowSize.MaxTransactionWireBytes;
    }

    /// <summary>
    /// True for a continuation row (it carries a non-null <c>_SplitOf</c>). A projection that names
    /// <c>_SplitOf</c> can return it as null on a row that lacks it, so null means "not a part".
    /// </summary>
    internal static bool IsPartRow(TableEntity row) =>
        row.TryGetValue(SystemColumnNames.SplitOf, out var of) && of is not null;

    /// <summary>Number of continuation rows a primary declares (0 when none).</summary>
    internal static int PartCount(TableEntity primary) =>
        primary.TryGetValue(SystemColumnNames.SplitParts, out var v) && v is not null
            ? (ToInt(v) is var n and >= 0
                ? n
                : throw new TableValueIntegrityException(SystemColumnNames.SplitParts, "the part count is not a non-negative integer"))
            : 0;

    /// <summary>Inclusive lower bound of the continuation-row range of <paramref name="rowKey"/>.</summary>
    internal static string PartRangeLower(string rowKey) => rowKey + "~";

    /// <summary>Exclusive upper bound of the continuation-row range of <paramref name="rowKey"/>.</summary>
    internal static string PartRangeUpper(string rowKey) => rowKey + "\u007f";

    /// <summary>Merges the primary with its own continuation rows into one logical bag.</summary>
    internal static TableEntity Assemble(TableEntity primary, IEnumerable<TableEntity> candidateParts)
    {
        var declared = PartCount(primary);
        var byIndex = new Dictionary<int, TableEntity>();
        foreach (var candidate in candidateParts)
        {
            if (!candidate.TryGetValue(SystemColumnNames.SplitOf, out var of)
                || !string.Equals(of as string, primary.RowKey, StringComparison.Ordinal)) continue;
            if (!candidate.TryGetValue(SystemColumnNames.SplitIndex, out var idx)) continue;
            var i = ToInt(idx);
            if (i < 1 || i > declared) continue;
            byIndex[i] = candidate;
        }

        var result = new TableEntity(primary.PartitionKey, primary.RowKey)
        {
            ETag = primary.ETag,
            Timestamp = primary.Timestamp,
        };
        foreach (var key in primary.Keys)
            if (!IsSystemKey(key) && key != SystemColumnNames.SplitParts) result[key] = primary[key];
        for (var i = 1; i <= declared; i++)
        {
            if (!byIndex.TryGetValue(i, out var part))
                throw new TableValueIntegrityException(SystemColumnNames.SplitParts, $"part {i} of {declared} missing");
            // A part carries only chunk cells; any other cell on it (a stray merge) is never surfaced.
            foreach (var key in part.Keys)
                if (IsChunk(key))
                    result[key] = part[key];
        }
        return result;
    }

    /// <summary>Rejects row keys that would collide with, or overflow, the continuation-row key space.</summary>
    internal static void ValidateRowKey(string rowKey)
    {
        if (rowKey.Contains('~', StringComparison.Ordinal))
            throw new ArgumentException("A row key must not contain '~', which is reserved for continuation rows.", nameof(rowKey));
        if (rowKey.Length > MaxRowKeyLength)
            throw new ArgumentException($"A row key must not exceed {MaxRowKeyLength} characters.", nameof(rowKey));
    }

    private static TableEntity NewPart(string pk, string rk, int index) =>
        new(pk, rk + "~" + index.ToString("D4", CultureInfo.InvariantCulture))
        {
            [SystemColumnNames.SplitOf] = rk,
            [SystemColumnNames.SplitIndex] = index,
        };

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => -1,
    };
}
