#pragma warning disable UDT0001 // the experimental raw-table API is what is under test

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// The experimental raw-table surface declared in 0.10.0 (<see cref="ITableStore"/>, <see cref="IRawTable"/>
/// and their row model): untyped physical rows with typed cells, with no Azure type in sight. No provider
/// implements it yet; these tests hold the value types and the experimental marking.
/// </summary>
public class RawTableDeclarationTests
{
    private static readonly Type[] RawTypes =
    [
        typeof(ITableStore), typeof(IRawTable), typeof(RawRow), typeof(RawCell), typeof(RawCellType),
        typeof(RawQuery), typeof(RawOperation), typeof(RawOperationKind),
    ];

    [Fact]
    public void EveryRawType_IsMarkedExperimental_UDT0001()
    {
        Assert.All(RawTypes, type =>
        {
            var attribute = type.GetCustomAttribute<ExperimentalAttribute>();
            Assert.True(attribute is not null, type.Name + " must be [Experimental]");
            Assert.Equal("UDT0001", attribute.DiagnosticId);
        });
    }

    [Fact]
    public void NoOtherPublicType_OfTheAbstractions_IsExperimental()
    {
        var experimental = typeof(IStorage<>).Assembly.GetExportedTypes()
            .Where(t => t.GetCustomAttribute<ExperimentalAttribute>() is not null);

        Assert.Equal(RawTypes.OrderBy(t => t.Name), experimental.OrderBy(t => t.Name));
    }

    [Fact]
    public void TheRawSurface_NamesNoBackendType()
    {
        var abstractions = typeof(IStorage<>).Assembly;
        var mentioned = RawTypes
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m switch
            {
                MethodBase method => method.GetParameters().Select(p => p.ParameterType)
                    .Concat(method is MethodInfo info ? [info.ReturnType] : []),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                _ => [],
            })
            .SelectMany(Flatten)
            .Distinct();

        Assert.All(mentioned, type => Assert.True(
            type.Assembly == abstractions || type.Namespace!.StartsWith("System", StringComparison.Ordinal), type.FullName));
        Assert.DoesNotContain(abstractions.GetReferencedAssemblies(), a => a.Name!.StartsWith("Azure", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Flatten(Type type) =>
        type.IsGenericParameter ? []
        : type.HasElementType ? Flatten(type.GetElementType()!)
        : type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten).Append(type.GetGenericTypeDefinition())
        : [type];

    // ── cells ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACell_KeepsItsValue_AndTheTypeItIsStoredAs()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var instant = new DateTimeOffset(2025, 3, 14, 15, 9, 26, TimeSpan.FromHours(2));

        Assert.Equal((RawCellType.String, "text"), (RawCell.FromString("text").Type, RawCell.FromString("text").AsString()));
        Assert.Equal(RawCellType.Binary, RawCell.FromBinary(bytes).Type);
        Assert.Same(bytes, RawCell.FromBinary(bytes).AsBinary());
        Assert.Equal((RawCellType.Boolean, true), (RawCell.FromBoolean(true).Type, RawCell.FromBoolean(true).AsBoolean()));
        Assert.Equal((RawCellType.Double, 1.5), (RawCell.FromDouble(1.5).Type, RawCell.FromDouble(1.5).AsDouble()));
        Assert.Equal((RawCellType.Guid, guid), (RawCell.FromGuid(guid).Type, RawCell.FromGuid(guid).AsGuid()));
        Assert.Equal((RawCellType.Int32, 42), (RawCell.FromInt32(42).Type, RawCell.FromInt32(42).AsInt32()));
        Assert.Equal((RawCellType.Int64, 42L), (RawCell.FromInt64(42).Type, RawCell.FromInt64(42).AsInt64()));

        var date = RawCell.FromDateTime(instant);
        Assert.Equal(RawCellType.DateTime, date.Type);
        Assert.Equal(instant, date.AsDateTime());          // the same instant
        Assert.Equal(TimeSpan.Zero, date.AsDateTime().Offset); // kept in UTC, as the table stores it
        Assert.Equal("text", RawCell.FromString("text").Value);
        Assert.IsType<long>(RawCell.FromInt64(1).Value);
    }

    [Fact]
    public void ACell_IsNeverNull()
    {
        Assert.Throws<ArgumentNullException>(() => RawCell.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => RawCell.FromBinary(null!));
    }

    [Fact]
    public void ReadingACell_AsAnotherType_Throws_AndNeverConverts()
    {
        var int32 = RawCell.FromInt32(1);

        var ex = Assert.Throws<InvalidOperationException>(() => int32.AsInt64());
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => int32.AsString());
        Assert.Throws<InvalidOperationException>(() => int32.AsDouble());
        Assert.Throws<InvalidOperationException>(() => RawCell.FromString("x").AsBinary());
        Assert.Throws<InvalidOperationException>(() => RawCell.FromString("true").AsBoolean());
        Assert.Throws<InvalidOperationException>(() => RawCell.FromString("x").AsGuid());
        Assert.Throws<InvalidOperationException>(() => RawCell.FromString("x").AsDateTime());
        Assert.Throws<InvalidOperationException>(() => RawCell.FromInt64(1).AsInt32());
    }

    [Fact]
    public void CellEquality_IsTypeAndValue_BinaryByContent()
    {
        Assert.Equal(RawCell.FromInt32(1), RawCell.FromInt32(1));
        Assert.Equal(RawCell.FromInt32(1).GetHashCode(), RawCell.FromInt32(1).GetHashCode());
        Assert.NotEqual(RawCell.FromInt32(1), RawCell.FromInt32(2));
        // The stored type is part of the value: Int32 1 and Int64 1 are different cells.
        Assert.NotEqual(RawCell.FromInt32(1), RawCell.FromInt64(1));
        Assert.NotEqual(RawCell.FromString("1"), RawCell.FromInt32(1));
        Assert.Equal(RawCell.FromBinary([1, 2, 3]), RawCell.FromBinary([1, 2, 3]));
        Assert.Equal(RawCell.FromBinary([1, 2, 3]).GetHashCode(), RawCell.FromBinary([1, 2, 3]).GetHashCode());
        Assert.NotEqual(RawCell.FromBinary([1, 2, 3]), RawCell.FromBinary([1, 2, 4]));
        Assert.NotEqual(RawCell.FromBinary([1, 2, 3]), RawCell.FromBinary([1, 2]));
        Assert.Equal(RawCell.FromBinary([]), RawCell.FromBinary([]));
        Assert.Equal(RawCell.FromDouble(double.NaN), RawCell.FromDouble(double.NaN));
        Assert.False(RawCell.FromInt32(1).Equals(null));
        Assert.False(RawCell.FromInt32(1).Equals((object)1));
        Assert.True(RawCell.FromString("a").Equals((object)RawCell.FromString("a")));
        Assert.NotEqual(RawCell.FromString("a"), RawCell.FromString("A"));
    }

    [Fact]
    public void CellToString_ShowsTypeAndValue_ButNotTheBytes()
    {
        Assert.Equal("Int32:42", RawCell.FromInt32(42).ToString());
        Assert.Equal("Double:1.5", RawCell.FromDouble(1.5).ToString());
        Assert.Equal("Binary:3 bytes", RawCell.FromBinary([1, 2, 3]).ToString());
    }

    // ── rows, queries, operations ─────────────────────────────────────────

    [Fact]
    public void ARow_HoldsItsKeysAsGiven_AndItsCells()
    {
        var cells = new Dictionary<string, RawCell> { ["Name"] = RawCell.FromString("n") };
        var row = new RawRow("Tenant A", " Row 1 ", cells) { ETag = "W/\"1\"", Timestamp = DateTimeOffset.UnixEpoch };

        Assert.Equal("Tenant A", row.PartitionKey); // physical: no id normalization
        Assert.Equal(" Row 1 ", row.RowKey);
        Assert.Same(cells, row.Cells);
        Assert.Equal("W/\"1\"", row.ETag);
        Assert.Equal(DateTimeOffset.UnixEpoch, row.Timestamp);
    }

    [Fact]
    public void ARow_WithoutCells_HasNone_AndNoVersion()
    {
        var row = new RawRow("p", "r");

        Assert.Empty(row.Cells);
        Assert.Null(row.ETag);
        Assert.Null(row.Timestamp);
        Assert.Throws<ArgumentNullException>(() => new RawRow(null!, "r"));
        Assert.Throws<ArgumentNullException>(() => new RawRow("p", null!));
    }

    [Fact]
    public void AQuery_DefaultsToTheWholePartition_LowerInclusive_UpperExclusive()
    {
        var query = new RawQuery("p");

        Assert.Equal("p", query.PartitionKey);
        Assert.Null(query.FromRowKey);
        Assert.True(query.FromInclusive);
        Assert.Null(query.ToRowKey);
        Assert.False(query.ToInclusive);
        Assert.Null(query.Take);
        Assert.Equal(query, new RawQuery("p"));
        Assert.NotEqual(query, query with { ToRowKey = "z" });
        Assert.Throws<ArgumentNullException>(() => new RawQuery(null!));
    }

    [Fact]
    public void Operations_CarryTheirKind_Row_AndCondition()
    {
        var row = new RawRow("p", "r");

        var insert = RawOperation.Insert(row);
        var replace = RawOperation.Replace(row, "W/\"1\"");
        var merge = RawOperation.Merge(row);
        var delete = RawOperation.Delete("p", "r", "W/\"2\"");

        Assert.Equal((RawOperationKind.Insert, row, null), (insert.Kind, insert.Row, insert.IfMatch));
        Assert.Equal((RawOperationKind.Replace, row, "W/\"1\""), (replace.Kind, replace.Row, replace.IfMatch));
        Assert.Equal((RawOperationKind.Merge, row, null), (merge.Kind, merge.Row, merge.IfMatch));
        Assert.Equal((RawOperationKind.Delete, "p", "r", "W/\"2\""), (delete.Kind, delete.Row.PartitionKey, delete.Row.RowKey, delete.IfMatch));
        Assert.Null(RawOperation.Delete("p", "r").IfMatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    public void AnOperation_HasOneSpellingOfNoCondition_Null(string ifMatch)
    {
        var row = new RawRow("p", "r");

        Assert.Throws<ArgumentException>(() => RawOperation.Replace(row, ifMatch));
        Assert.Throws<ArgumentException>(() => RawOperation.Merge(row, ifMatch));
        Assert.Throws<ArgumentException>(() => RawOperation.Delete("p", "r", ifMatch));
    }

    [Fact]
    public void AnOperation_NeedsARow()
    {
        Assert.Throws<ArgumentNullException>(() => RawOperation.Insert(null!));
        Assert.Throws<ArgumentNullException>(() => RawOperation.Replace(null!));
        Assert.Throws<ArgumentNullException>(() => RawOperation.Merge(null!));
        Assert.Throws<ArgumentNullException>(() => RawOperation.Delete(null!, "r"));
    }
}
