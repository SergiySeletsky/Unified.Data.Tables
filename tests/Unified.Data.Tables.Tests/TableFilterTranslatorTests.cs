using System.Linq.Expressions;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Unit tests for the LINQ → server-side OData <c>$filter</c> translation: the exact filter string
/// must map columns and values to their STORED representation (enum → string name, decimal → double,
/// nested x.A.B → "A_B"), and reject anything it cannot translate rather than translate it wrong.
/// </summary>
public class TableFilterTranslatorTests
{
    public enum DocStatus { Open, Closed, Archived }

    public sealed class Meta
    {
        public string Tag { get; set; } = string.Empty;
    }

    public sealed class Doc : Entity
    {
        public string Owner { get; set; } = string.Empty;
        public int Count { get; set; }
        public long Big { get; set; }
        public ulong UBig { get; set; }
        public decimal Price { get; set; }
        public bool Active { get; set; }
        public DocStatus Status { get; set; }
        public DateTimeOffset When { get; set; }
        public Guid Ref { get; set; }
        public TimeSpan Duration { get; set; }
        public int? MaybeCount { get; set; }
        public Meta Info { get; set; } = new();

        // Computed / get-only — has NO stored column.
        public bool IsOpen => Status == DocStatus.Open;
    }

    private static string Translate(Expression<Func<Doc, bool>> predicate) =>
        TableFilterTranslator.Translate(predicate);

    // ── Supported grammar ─────────────────────────────────────────────────────

    [Fact]
    public void Enum_MapsToStoredStringName() =>
        Assert.Equal("Status eq 'Closed'", Translate(d => d.Status == DocStatus.Closed));

    [Fact]
    public void Int_Comparison() =>
        Assert.Equal("Count ge 5", Translate(d => d.Count >= 5));

    [Fact]
    public void Long_MapsToInt64Literal() =>
        Assert.Equal("Big eq 9000000000L", Translate(d => d.Big == 9_000_000_000L));

    [Fact]
    public void Bool_BareMember_BecomesEqTrue() =>
        Assert.Equal("Active eq true", Translate(d => d.Active));

    [Fact]
    public void Not_NegatesTheOperand() =>
        Assert.Equal("not (Active eq true)", Translate(d => !d.Active));

    [Fact]
    public void String_Value_EscapesApostrophe() =>
        Assert.Equal("Owner eq 'O''Brien'", Translate(d => d.Owner == "O'Brien"));

    [Fact]
    public void And_Or_Nest_WithParentheses() =>
        Assert.Equal(
            "(Status eq 'Open' and (Count gt 1 or Active eq true))",
            Translate(d => d.Status == DocStatus.Open && (d.Count > 1 || d.Active)));

    [Fact]
    public void ValueOnLeft_FlipsTheOperator() =>
        // 5 < d.Count  is  d.Count > 5
        Assert.Equal("Count gt 5", Translate(d => 5 < d.Count));

    [Fact]
    public void NestedMember_FlattensToUnderscoreColumn() =>
        Assert.Equal("Info_Tag eq 'x'", Translate(d => d.Info.Tag == "x"));

    [Fact]
    public void CapturedLocal_IsEvaluated()
    {
        var min = 3;
        Assert.Equal("Count gt 3", Translate(d => d.Count > min));
    }

    [Fact]
    public void Decimal_MapsToStoredDouble()
    {
        // decimal is stored as double, so the literal must be the double form (never a decimal 'm').
        var filter = Translate(d => d.Price >= 20m);
        Assert.StartsWith("Price ge 20", filter);
        Assert.False(filter.Contains('m'), filter); // never the decimal 'm' suffix
    }

    [Fact]
    public void Guid_MapsToGuidLiteral()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal("Ref eq guid'11111111-1111-1111-1111-111111111111'", Translate(d => d.Ref == id));
    }

    [Fact]
    public void DateTimeOffset_MapsToDatetimeLiteral()
    {
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var filter = Translate(d => d.When > when);
        Assert.StartsWith("When gt datetime'2026-01-02T03:04:05", filter);
    }

    // ── Rejected (throws instead of translating wrong) ────────────────────────

    [Fact]
    public void MethodCall_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Owner.StartsWith("prefix")));

    [Fact]
    public void ColumnToColumn_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Owner == d.Info.Tag));

    [Fact]
    public void NullComparison_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Owner == null));

    [Fact]
    public void UnpersistedColumn_ETag_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.ETag == "x"));

    [Fact]
    public void JsonBackedType_TimeSpan_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Duration == TimeSpan.Zero));

    [Fact]
    public void Enum_OrderingComparison_IsRejected() =>
        // Enums are stored by NAME (lexical), not by numeric value, so a server-side ordering
        // compare would diverge from the in-memory fake's numeric comparison — reject it.
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Status > DocStatus.Open));

    [Fact]
    public void Enum_Equality_IsStillAllowed() =>
        Assert.Equal("Status ne 'Open'", Translate(d => d.Status != DocStatus.Open));

    [Fact]
    public void ULong_OrderingComparison_IsRejected() =>
        // ulong wraps to a signed Int64 in storage, so ordering diverges above long.MaxValue — reject.
        Assert.Throws<NotSupportedException>(() => Translate(d => d.UBig > 1UL));

    [Fact]
    public void ULong_Equality_IsStillAllowed() =>
        // The (long)<->(ulong) wrap is bijective, so equality is safe and maps to an Int64 literal.
        Assert.Equal("UBig eq 1L", Translate(d => d.UBig == 1UL));

    [Fact]
    public void ComputedProperty_IsRejected() =>
        // IsOpen is get-only -> no stored column; the server would match nothing while the fake recomputes it.
        Assert.Throws<NotSupportedException>(() => Translate(d => d.IsOpen));

    [Fact]
    public void NotEqual_OnReferenceColumn_IsRejected() =>
        // Azure excludes absent-column (null) rows from `ne`; the fake's `null != x` includes them.
        Assert.Throws<NotSupportedException>(() => Translate(d => d.Owner != "x"));

    [Fact]
    public void NotEqual_OnNonNullableValue_IsAllowed() =>
        // An int is always a present column, so `ne` matches CLR `!=` with no divergence.
        Assert.Equal("Count ne 5", Translate(d => d.Count != 5));

    [Fact]
    public void NegatedEquality_OnReferenceColumn_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => !(d.Owner == "x")));

    [Fact]
    public void NegatedOrdering_OnNullableColumn_IsRejected() =>
        // Consistent with negated equality: over an absent column not(eq) and not(ge) behave identically,
        // so both are rejected on nullable/reference columns.
        Assert.Throws<NotSupportedException>(() => Translate(d => !(d.MaybeCount >= 5)));

    [Fact]
    public void NegatedComparison_OnNonNullableValue_IsAllowed() =>
        // A non-nullable value column is always present, so a negated comparison is safe.
        Assert.Equal("not (Count ge 5)", Translate(d => !(d.Count >= 5)));

    [Fact]
    public void NullableValueAccess_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.MaybeCount!.Value == 5));

    [Fact]
    public void NullableEquality_IsAllowed() =>
        Assert.Equal("MaybeCount eq 5", Translate(d => d.MaybeCount == 5));

    [Fact]
    public void NotEqual_OnNullableValue_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => Translate(d => d.MaybeCount != 5));

    [Fact]
    public void NullPredicate_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TableFilterTranslator.Translate<Doc>(null!));
}
