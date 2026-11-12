namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A class-level <see cref="TableValueSplitAttribute"/> covers the string, byte[] and JSON-stored members of the class,
/// but not a scalar the serializer happens to store as JSON (<c>float</c>, <c>short</c>, <c>char</c>, <c>DateOnly</c>,
/// <c>TimeOnly</c>, <c>Half</c>, <c>Int128</c> and the like, nullable or not): its JSON is a few characters, so it can
/// never be split, and covering it only refused builder updates a plain type makes (rv-serializer, PR #18 review). Such
/// a member is stored, updated and filtered as it is on a plain type; the members the attribute is for stay covered.
/// </summary>
[Collection("OversizedCellPolicy")]
public class ClassLevelSplitBoundedScalarTests
{
    private const string AwareTable = "BoundedScalarAware";
    private const string PlainTable = "BoundedScalarPlain";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class Aware : Entity
    {
        public float F { get; set; }

        public short S { get; set; }

        public ushort U { get; set; }

        public sbyte B { get; set; }

        public char C { get; set; }

        public DateOnly D { get; set; }

        public TimeOnly T { get; set; }

        public Half H { get; set; }

        public Int128 I { get; set; }

        public float? NF { get; set; }

        public DateOnly? ND { get; set; }

        public string? Text { get; set; }

        public List<string>? Items { get; set; }
    }

    public sealed class Plain : Entity
    {
        public float F { get; set; }

        public short S { get; set; }

        public ushort U { get; set; }

        public sbyte B { get; set; }

        public char C { get; set; }

        public DateOnly D { get; set; }

        public TimeOnly T { get; set; }

        public Half H { get; set; }

        public Int128 I { get; set; }

        public float? NF { get; set; }

        public DateOnly? ND { get; set; }
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task BuilderUpdates_OfBoundedScalars_AreWrittenAsOnAPlainType(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var aware = stores.Store<Aware>(AwareTable);
        var id = stores.Id("r");
        await aware.CreateAsync(new Aware { Id = id }, Ct);
        var date = new DateOnly(2026, 11, 12);
        var time = new TimeOnly(1, 2, 3);

        await aware.UpdateAsync(id, b => b
            .SetProperty(x => x.F, 1.5f)
            .SetProperty(x => x.S, (short)-2)
            .SetProperty(x => x.U, (ushort)3)
            .SetProperty(x => x.B, (sbyte)-4)
            .SetProperty(x => x.C, 'c')
            .SetProperty(x => x.D, date)
            .SetProperty(x => x.T, time)
            .SetProperty(x => x.H, (Half)0.5)
            .SetProperty(x => x.NF, 6.5f)
            .SetProperty(x => x.ND, date), Ct);

        var read = (await aware.OneAsync(id, Ct))!;
        Assert.Equal(1.5f, read.F);
        Assert.Equal(-2, read.S);
        Assert.Equal(3, read.U);
        Assert.Equal(-4, read.B);
        Assert.Equal('c', read.C);
        Assert.Equal(date, read.D);
        Assert.Equal(time, read.T);
        Assert.Equal((Half)0.5, read.H);
        Assert.Equal(6.5f, read.NF);
        Assert.Equal(date, read.ND);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task TheCoveredMembers_StayCovered(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var aware = stores.Store<Aware>(AwareTable);
        var id = stores.Id("c");
        await aware.CreateAsync(new Aware { Id = id }, Ct);

        await Assert.ThrowsAsync<NotSupportedException>(() => aware.UpdateAsync(id, b => b.SetProperty(x => x.Text, "t"), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => aware.UpdateAsync(id, b => b.SetProperty(x => x.Items, ["i"]), Ct));
    }

    /// <summary>The bounded scalars are stored in the columns a plain type stores them in.</summary>
    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task BoundedScalars_AreStoredInThePlainColumns(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("s");
        await stores.Store<Aware>(AwareTable).CreateAsync(new Aware { Id = id, F = 1.5f, C = 'x', D = new DateOnly(2026, 1, 2) }, Ct);
        await stores.Store<Plain>(PlainTable).CreateAsync(new Plain { Id = id, F = 1.5f, C = 'x', D = new DateOnly(2026, 1, 2) }, Ct);

        var awareRow = (await stores.RawRowsAsync<Aware>(AwareTable)).Single();
        var plainRow = (await stores.RawRowsAsync<Plain>(PlainTable)).Single();
        foreach (var column in new[] { "F__Json", "S__Json", "U__Json", "B__Json", "C__Json", "D__Json", "T__Json", "H__Json", "I__Json" })
        {
            Assert.True(plainRow.TryGetValue(column, out var plain), column);
            Assert.True(awareRow.TryGetValue(column, out var aware), column);
            Assert.Equal(plain, aware);
        }
    }

    [Fact]
    public void LinqFilter_OnABoundedScalar_IsRefusedForItsType_NotAsALargeValue()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate<Aware>(x => x.F == 1.5f));

        Assert.DoesNotContain("large value", ex.Message, StringComparison.Ordinal);
    }
}
