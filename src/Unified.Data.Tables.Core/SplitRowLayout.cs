using System.Globalization;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>A logical row planned onto one primary row and zero or more continuation rows.</summary>
/// <param name="Primary">The row that carries ordinary cells, manifests and blob references.</param>
/// <param name="Parts">Continuation rows, in index order (the first has index 1).</param>
internal sealed record SplitRowPlan(TableEntity Primary, IReadOnlyList<TableEntity> Parts);

/// <summary>
/// Why a logical row cannot be planned: the cells that must stay on its primary row (every cell but the
/// chunks of split values, and the part count) exceed one entity.
/// </summary>
/// <param name="Properties">How many properties those cells are.</param>
/// <param name="Bytes">Their estimated size, as <see cref="TableRowSize.Estimate"/> measures the row.</param>
/// <param name="LargestColumn">The column of the largest of them.</param>
/// <param name="LargestBytes">The stored size of that cell's value.</param>
internal sealed record PrimaryOverflow(int Properties, long Bytes, string LargestColumn, long LargestBytes)
{
    /// <summary>How many split values (a manifest of <see cref="SplitRowLayout.ManifestCells"/> cells each) those cells hold.</summary>
    internal int SplitValues { get; init; }

    /// <summary>The column of the largest of those split values (by its stored size), or null when there is none.</summary>
    internal string? LargestSplitColumn { get; init; }

    /// <summary>The stored (encoded) size of that value, from its <c>__len</c> cell.</summary>
    internal long LargestSplitBytes { get; init; }
}

/// <summary>
/// Pure layout of a logical entity bag onto a primary row plus continuation rows
/// (<c>{RowKey}~0001</c>...), and the inverse assembly. No I/O.
/// </summary>
internal static class SplitRowLayout
{
    internal const int MaxPropertiesPerRow = TableRequestSize.MaxEntityProperties;
    internal const long MaxEntityBytes = TableRequestSize.MaxEntityBytes;

    /// <summary>
    /// The budget of one row's JSON body (<see cref="TableRowSize.WireSize"/>) when split cells are
    /// placed on it. The service limits an entity by its stored size, which <see cref="MaxEntityBytes"/>
    /// bounds; the storage emulator limits it by its request body instead, where escaped text is up to
    /// six bytes per character and binary is base64. Split cells are placed within both, so a split row
    /// is accepted by either. Cells that cannot be split are still held only to <see cref="MaxEntityBytes"/>,
    /// and so is a row that fits one entity as it is: only a row whose opted-in values had to be moved
    /// to split form to fit (see <see cref="ChoosePromotions(PrimaryOverflow, IEnumerable{ValueTuple{string, long}})"/>)
    /// is brought within this budget as well, as far as moving such values can.
    /// </summary>
    internal const long MaxEntityWireBytes = 1_000_000;
    internal const int MaxRowKeyLength = 480;

    /// <summary>Separates an entity's row key from a continuation row's index.</summary>
    internal const char PartSeparator = '~';

    /// <summary>Fixed width of a continuation row's index (<c>~0001</c>).</summary>
    internal const int PartIndexDigits = 4;

    private static bool IsSystemKey(string key) =>
        key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag";

    private static bool IsChunk(string column) =>
        SplitCodec.TryParseLargeValueColumn(column, out _, out var kind, out _) && kind == LargeValueCellKind.Chunk;

    private static long CellCost(string key, object? value) =>
        8L + (key.Length * 2L) + ValueBytes(value);

    private static long ValueBytes(object? value) => value switch
    {
        byte[] binary => binary.Length,
        BinaryData binary => binary.ToMemory().Length,
        string text => text.Length * 2L,
        _ => 8L,
    };

    /// <summary>The cells a split value keeps on the primary row: <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>, <c>__len</c>.</summary>
    internal const int ManifestCells = 4;

    // The estimated size of those four cells for a column: their names (the column and the suffixes,
    // 26 characters in all), an int, a 64-character hash, the encoding tag and a long.
    private static long ManifestCost(string column, string enc) =>
        (ManifestCells * 8L) + (2L * ((ManifestCells * column.Length) + 26)) + 8 + 128 + (2L * enc.Length) + 8;

    private static readonly string Sha256Sample = new('0', 64);

    // The same four cells as the request sends them.
    private static long ManifestWire(string column, string enc) =>
        TableRowSize.PropertyWireSize(column + SplitCodec.ChunksSuffix, 0)
        + TableRowSize.PropertyWireSize(column + "__sha256", Sha256Sample)
        + TableRowSize.PropertyWireSize(column + "__enc", enc)
        + TableRowSize.PropertyWireSize(column + "__len", 0L);

    /// <summary>
    /// What moving <paramref name="value"/> to split form frees on the primary row: its one cell goes
    /// (its chunk can then sit on a continuation row) and four manifest cells come. In estimated bytes
    /// (<see cref="TableRowSize.Estimate"/>) and in bytes of the request (<see cref="TableRowSize.WireSize"/>);
    /// not positive for a value smaller than its manifest.
    /// </summary>
    internal static (long Bytes, long Wire) PromotionGain(TableEntity row, InlineValue value)
    {
        if (!row.TryGetValue(value.Cell, out var cell))
            return (0, 0);
        // The tag SplitCodec gives the value once it is split (a value that fits one cell is well-formed text).
        var enc = cell is byte[] or BinaryData ? "binary" : value.Encoding == SplitEncoding.Gzip ? "gzip" : "text";
        return (CellCost(value.Cell, cell) - ManifestCost(value.Column, enc),
            TableRowSize.PropertyWireSize(value.Cell, cell) - ManifestWire(value.Column, enc));
    }

    /// <summary>
    /// The opted-in values to move from one cell each to split form so that the cells left on the first
    /// row fit one entity (<see cref="MaxEntityBytes"/>), chosen as the other overload chooses them.
    /// Empty when the property count is what does not fit: moving a value adds properties.
    /// </summary>
    internal static List<string> ChoosePromotions(PrimaryOverflow overflow, IEnumerable<(string Column, long Gain)> candidates) =>
        overflow.Properties > MaxPropertiesPerRow ? [] : ChoosePromotions(overflow.Properties, overflow.Bytes - MaxEntityBytes, candidates);

    /// <summary>
    /// The opted-in values to move from one cell each to split form to free <paramref name="excess"/>
    /// bytes on a primary row of <paramref name="properties"/> properties: the largest gain first (the
    /// first of equal ones), as many as it takes and as the row has properties for, since a moved value
    /// leaves <see cref="ManifestCells"/> cells where its one cell was. Empty when nothing frees bytes
    /// or the row has no property left.
    /// </summary>
    internal static List<string> ChoosePromotions(int properties, long excess, IEnumerable<(string Column, long Gain)> candidates)
    {
        var moved = new List<string>();
        foreach (var (column, gain) in candidates.Where(c => c.Gain > 0).OrderByDescending(c => c.Gain))
        {
            if (excess <= 0 || properties + ManifestCells - 1 > MaxPropertiesPerRow)
                break;
            moved.Add(column);
            excess -= gain;
            properties += ManifestCells - 1;
        }
        return moved;
    }

    /// <summary>
    /// How far the cells that stay on the primary row of <paramref name="plan"/> (every cell but the
    /// chunks, which are only placed there within the budget) exceed <see cref="MaxEntityWireBytes"/>,
    /// and how many properties they are. Not positive when they are within it.
    /// </summary>
    internal static (int Properties, long Excess) FirstRowWireExcess(SplitRowPlan plan)
    {
        var properties = 0;
        long wire = 2;
        foreach (var (key, value) in plan.Primary)
        {
            if (IsChunk(key))
                continue;
            wire += TableRowSize.PropertyWireSize(key, value);
            if (!IsSystemKey(key))
                properties++;
        }
        return (properties, wire - MaxEntityWireBytes);
    }

    /// <summary>
    /// The error of a row whose first-row cells exceed one entity after everything that can leave the
    /// row has left it. It names the largest cell that is left, which is a real column, and both limits.
    /// When the properties are what does not fit and the row holds split values, it names the largest
    /// split value instead, with its own encoded size, the properties the split values' cells need and
    /// the number the rest of the row leaves, and remedies that apply (fewer properties, or a lower
    /// offload threshold so the value takes one cell).
    /// </summary>
    /// <param name="overflow">What is in the way.</param>
    /// <param name="movable">
    /// How many opted-in values of one cell each are still on the row because it has no property left
    /// for their manifests.
    /// </param>
    internal static OversizedValueException PrimaryOverflowError(PrimaryOverflow overflow, int movable)
    {
        if (overflow is { SplitValues: > 0, LargestSplitColumn: { } splitColumn } && overflow.Properties > MaxPropertiesPerRow
            && overflow.Bytes <= MaxEntityBytes)
            return PropertyBudgetError(overflow, splitColumn, movable);

        var reason = string.Create(CultureInfo.InvariantCulture,
            $"the cells that must stay on the entity's main row ({overflow.Properties} properties, {overflow.Bytes:N0} estimated bytes) "
            + $"exceed what one entity holds ({MaxPropertiesPerRow} properties, {MaxEntityBytes:N0} estimated bytes), "
            + $"and '{overflow.LargestColumn}' is the largest of them");
        if (movable > 0)
            reason += string.Create(CultureInfo.InvariantCulture,
                $"; {movable} more [TableValueSplit] value(s) on that row fit one cell each, but a value moved to continuation rows "
                + $"leaves {ManifestCells} cells behind and the row has no property left for them");
        return new OversizedValueException(overflow.LargestColumn, overflow.LargestBytes, reason,
            "Only the chunks of [TableValueSplit] values can go to continuation rows: mark the large properties (or the class) "
            + "[TableValueSplit], or store less in one entity.");
    }

    // The main row has the bytes for its cells but not the properties: its split values keep a manifest
    // each, and the row its part count. The error names the largest split value with its own encoded size,
    // the properties its cells need and those the rest of the row leaves, and only remedies that apply:
    // the value is already [TableValueSplit], and offloaded it would take one cell. No value is quoted.
    private static OversizedValueException PropertyBudgetError(PrimaryOverflow overflow, string column, int movable)
    {
        var values = overflow.SplitValues;
        var needed = (values * ManifestCells) + 1;
        var others = overflow.Properties - needed;
        var left = Math.Max(0, MaxPropertiesPerRow - others);
        var who = values == 1
            ? $"'{column}' is too large for one cell, so its cells"
            : $"its {values} split values ('{column}' the largest) are too large for one cell each, so their cells";
        var reason = string.Create(CultureInfo.InvariantCulture,
            $"the entity's main row has no room for the cells of {(values == 1 ? "its split value" : "its split values")}: {who} "
            + $"need {needed} properties on the main row ({ManifestCells} for each value besides its chunks, and 1 for the row's part count), "
            + $"and the entity's other {others} properties leave {left} of the {MaxPropertiesPerRow} one entity holds "
            + $"(255 less PartitionKey, RowKey and Timestamp)");
        if (movable > 0)
            reason += string.Create(CultureInfo.InvariantCulture,
                $"; {movable} more [TableValueSplit] value(s) on that row fit one cell each and stay there");
        var remedy = string.Create(CultureInfo.InvariantCulture,
            $"Store fewer properties in one entity, or have the value offloaded to blob storage, where it takes one cell: with "
            + $"UnifiedTableStorageOptions.BlobOffload configured and the property's Offload not BlobOffload.Never, set "
            + $"BlobOffloadOptions.ThresholdBytes below {overflow.LargestSplitBytes:N0} B (the encoded size of '{column}').");
        return new OversizedValueException(column, overflow.LargestSplitBytes, reason, remedy);
    }

    /// <summary>
    /// The error of a row that has no split value left to move to a blob and still does not fit one
    /// entity-group transaction: the deletes of the continuation rows stored under its key are what is
    /// in the way. It names the largest cell of the row, which is a real column.
    /// </summary>
    internal static OversizedValueException TransactionOverflowError(SplitRowPlan plan, int extraActions)
    {
        var (column, bytes) = LargestCell(plan.Primary);
        return new OversizedValueException(column, bytes,
            string.Create(CultureInfo.InvariantCulture,
                $"the row and the deletes of the {extraActions} continuation rows stored under its key do not fit one entity-group "
                + $"transaction ({BatchPlanner.DefaultMaxCount} actions, {BatchPlanner.DefaultMaxBytes:N0} estimated bytes)"),
            "Remove the leftover continuation rows of this key, then write the entity again.");
    }

    // How many split values the row's manifests describe, and the largest of them by its __len cell (the
    // first of equal ones, in the row's order).
    private static (int Count, string? Column, long Bytes) SplitValuesOf(TableEntity row)
    {
        var count = 0;
        string? largest = null;
        long bytes = -1;
        foreach (var (key, value) in row)
        {
            if (!SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _) || kind != LargeValueCellKind.Length)
                continue;
            count++;
            var length = value switch { long l => l, int i => i, _ => 0L };
            if (length > bytes)
                (largest, bytes) = (column, length);
        }
        return (count, largest, Math.Max(bytes, 0));
    }

    // The column and the stored size of the cell with the largest value on the row (the first of equal
    // ones), other than the keys and the part count.
    private static (string Column, long Bytes) LargestCell(TableEntity row)
    {
        string? largest = null;
        long bytes = -1;
        foreach (var (key, value) in row)
        {
            if (IsSystemKey(key) || key == SystemColumnNames.SplitParts)
                continue;
            var valueBytes = ValueBytes(value);
            if (valueBytes > bytes)
                (largest, bytes) = (key, valueBytes);
        }
        return largest is null ? ("(none)", 0) : (ColumnOf(largest), bytes);
    }

    // The column a cell belongs to: the base column of a large-value cell, or the name without the
    // suffix of a JSON, GZip or truncation-marker cell.
    private static string ColumnOf(string cell)
    {
        if (SplitCodec.TryParseLargeValueColumn(cell, out var column, out _, out _))
            return column;
        foreach (var suffix in CellSuffixes)
        {
            if (cell.Length > suffix.Length && cell.EndsWith(suffix, StringComparison.Ordinal))
                return cell[..^suffix.Length];
        }
        return cell;
    }

    private static readonly string[] CellSuffixes = ["__Json", "__GZip", "__Truncated"];

    private static int CustomCount(TableEntity row)
    {
        var n = 0;
        foreach (var key in row.Keys)
            if (!IsSystemKey(key)) n++;
        return n;
    }

    /// <summary>Plans <paramref name="logical"/>; returns it unchanged (no parts) when it fits one row.</summary>
    /// <exception cref="OversizedValueException">
    /// The cells that cannot go to continuation rows exceed one entity (<see cref="PrimaryOverflowError"/>).
    /// </exception>
    internal static SplitRowPlan Plan(TableEntity logical) =>
        TryPlan(logical, out var overflow) ?? throw PrimaryOverflowError(overflow!, movable: 0);

    /// <summary>
    /// <see cref="Plan"/> without the exception: null, with what is in the way, when the cells that
    /// cannot go to continuation rows (every cell but the chunks of split values) exceed one entity.
    /// </summary>
    internal static SplitRowPlan? TryPlan(TableEntity logical, out PrimaryOverflow? overflow)
    {
        overflow = null;
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
        {
            var (column, largest) = LargestCell(primary);
            var (splitValues, splitColumn, splitBytes) = SplitValuesOf(primary);
            overflow = new PrimaryOverflow(props, bytes, column, largest)
            {
                SplitValues = splitValues,
                LargestSplitColumn = splitColumn,
                LargestSplitBytes = splitBytes,
            };
            return null;
        }

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
    internal static bool FitsOneTransaction(SplitRowPlan plan, int extraActions) =>
        Fits(TransactionCost.Of(plan, extraActions));

    /// <summary>
    /// Packs the write units (each a plan and the number of its stale-part deletes), in order, into
    /// entity-group transactions: a unit is indivisible, so its rows never straddle two transactions, and
    /// a transaction holds units of one partition only, within the limits <see cref="FitsOneTransaction"/>
    /// applies. A unit that does not fit one transaction on its own gets a transaction of its own.
    /// </summary>
    internal static IReadOnlyList<BatchRange> PackTransactions(IReadOnlyList<(SplitRowPlan Plan, int ExtraActions)> units)
    {
        var ranges = new List<BatchRange>();
        var start = 0;
        var total = TransactionCost.Zero;
        for (var i = 0; i < units.Count; i++)
        {
            var cost = TransactionCost.Of(units[i].Plan, units[i].ExtraActions);
            if (i > start
                && (!string.Equals(units[i].Plan.Primary.PartitionKey, units[start].Plan.Primary.PartitionKey, StringComparison.Ordinal)
                    || !Fits(total + cost)))
            {
                ranges.Add(new BatchRange(start, i - start, total.Bytes));
                start = i;
                total = TransactionCost.Zero;
            }
            total += cost;
        }
        if (units.Count > start)
            ranges.Add(new BatchRange(start, units.Count - start, total.Bytes));
        return ranges;
    }

    private static bool Fits(TransactionCost cost) =>
        cost.Actions <= BatchPlanner.DefaultMaxCount
        && cost.Bytes <= BatchPlanner.DefaultMaxBytes
        && cost.Wire <= TableRowSize.MaxTransactionWireBytes;

    /// <summary>
    /// What a unit adds to a transaction: its actions, its payload as <see cref="TableRowSize.Estimate"/>
    /// measures it (the plain batch planner's budget), and its size as the SDK sends it.
    /// </summary>
    private readonly record struct TransactionCost(int Actions, long Bytes, long Wire)
    {
        internal static TransactionCost Zero => default;

        internal static TransactionCost Of(SplitRowPlan plan, int extraActions)
        {
            // A stale delete carries no body, but its keys and framing still count toward the payload.
            var staleKey = PartRowKey(plan.Primary.RowKey, 0);
            var bytes = TableRowSize.Estimate(plan.Primary)
                + (extraActions * TableRowSize.Estimate(new TableEntity(plan.Primary.PartitionKey, staleKey)));
            var wire = TableRowSize.ActionWireSize(plan.Primary)
                + (extraActions * TableRowSize.DeleteActionWireSize(plan.Primary.PartitionKey, staleKey));
            foreach (var part in plan.Parts)
            {
                bytes += TableRowSize.Estimate(part);
                wire += TableRowSize.ActionWireSize(part);
            }
            return new TransactionCost(1 + plan.Parts.Count + extraActions, bytes, wire);
        }

        public static TransactionCost operator +(TransactionCost a, TransactionCost b) =>
            new(a.Actions + b.Actions, a.Bytes + b.Bytes, a.Wire + b.Wire);
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
    internal static string PartRangeLower(string rowKey) => rowKey + PartSeparator;

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

    /// <summary>
    /// True when <paramref name="rowKey"/> has the shape of a continuation row's key: it ends with
    /// <see cref="PartSeparator"/> followed by exactly <see cref="PartIndexDigits"/> ASCII digits. Only
    /// such a key can collide with a part (every part key is <c>{RowKey}~{index:D4}</c>, and an entity
    /// has at most 99 parts, one transaction's worth); any other <c>'~'</c> is an ordinary character.
    /// </summary>
    internal static bool IsReservedRowKey(string rowKey)
    {
        var separator = rowKey.Length - PartIndexDigits - 1;
        if (separator < 0 || rowKey[separator] != PartSeparator)
            return false;
        for (var i = separator + 1; i < rowKey.Length; i++)
            if (!char.IsAsciiDigit(rowKey[i]))
                return false;
        return true;
    }

    /// <summary>Rejects row keys that would collide with, or overflow, the continuation-row key space.</summary>
    internal static void ValidateRowKey(string rowKey)
    {
        if (IsReservedRowKey(rowKey))
            throw new ArgumentException(
                $"The row key '{rowKey}' ends with '~' followed by exactly {PartIndexDigits} digits, the shape reserved for "
                + "continuation rows of split values; any other '~' is allowed.", nameof(rowKey));
        if (rowKey.Length > MaxRowKeyLength)
            throw new ArgumentException($"A row key must not exceed {MaxRowKeyLength} characters.", nameof(rowKey));
    }

    /// <summary>
    /// Rejects the keys of a split-aware write: the row key as <see cref="ValidateRowKey"/> does, and either
    /// key when it is not well-formed UTF-16. A key names the entity's blobs, and an unpaired surrogate
    /// escapes to the same blob-name segment as U+FFFD, so two such keys would share blobs.
    /// </summary>
    internal static void ValidateKeys(string partitionKey, string rowKey)
    {
        RefuseUnpairedSurrogate(partitionKey, nameof(partitionKey));
        RefuseUnpairedSurrogate(rowKey, nameof(rowKey));
        ValidateRowKey(rowKey);
    }

    private static void RefuseUnpairedSurrogate(string key, string paramName)
    {
        if (!SplitCodec.IsWellFormedUtf16(key))
            throw new ArgumentException(
                $"The {(paramName == "rowKey" ? "row" : "partition")} key of an entity with split or offloaded values must be well-formed UTF-16: "
                + "it has an unpaired surrogate, which would name the same blobs as a key with U+FFFD in its place.", paramName);
    }

    /// <summary>The key of continuation row <paramref name="index"/> of the entity at <paramref name="rowKey"/>.</summary>
    internal static string PartRowKey(string rowKey, int index) =>
        rowKey + PartSeparator + index.ToString("D" + PartIndexDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static TableEntity NewPart(string pk, string rk, int index) =>
        new(pk, PartRowKey(rk, index))
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
