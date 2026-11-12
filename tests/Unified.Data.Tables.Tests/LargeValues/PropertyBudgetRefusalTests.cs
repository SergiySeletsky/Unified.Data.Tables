using System.Globalization;
using System.Reflection;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// One entity holds 255 properties, 252 besides PartitionKey, RowKey and Timestamp. A value too large for one
/// cell keeps four cells on the main row besides its chunks (<c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>,
/// <c>__len</c>), and a row with split values keeps its part count there too; an offloaded value keeps one
/// reference cell. When the main row has no room for the cells of its split values the entity is refused
/// (decided: the planner does not fall back to a blob reference below the offload threshold), and the
/// <see cref="OversizedValueException"/> is accurate: it names the property and the real encoded size of its
/// value, says the row has no room for the value's cells with the number they need and the number left, and
/// advises only what applies (fewer properties, or a lower offload threshold so the value takes one cell),
/// never marking a property that is already marked, and it carries no user data.
/// (Review finding SERIALIZER-9 of fix wave 2.)
/// </summary>
public class PropertyBudgetRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    /// <summary>246 ordinary columns (with Id, CreatedAt and UpdatedAt: 249) and one opted-in value.</summary>
    public sealed class WideRecord : Entity
    {
        public int N000 { get; set; }
        public int N001 { get; set; }
        public int N002 { get; set; }
        public int N003 { get; set; }
        public int N004 { get; set; }
        public int N005 { get; set; }
        public int N006 { get; set; }
        public int N007 { get; set; }
        public int N008 { get; set; }
        public int N009 { get; set; }
        public int N010 { get; set; }
        public int N011 { get; set; }
        public int N012 { get; set; }
        public int N013 { get; set; }
        public int N014 { get; set; }
        public int N015 { get; set; }
        public int N016 { get; set; }
        public int N017 { get; set; }
        public int N018 { get; set; }
        public int N019 { get; set; }
        public int N020 { get; set; }
        public int N021 { get; set; }
        public int N022 { get; set; }
        public int N023 { get; set; }
        public int N024 { get; set; }
        public int N025 { get; set; }
        public int N026 { get; set; }
        public int N027 { get; set; }
        public int N028 { get; set; }
        public int N029 { get; set; }
        public int N030 { get; set; }
        public int N031 { get; set; }
        public int N032 { get; set; }
        public int N033 { get; set; }
        public int N034 { get; set; }
        public int N035 { get; set; }
        public int N036 { get; set; }
        public int N037 { get; set; }
        public int N038 { get; set; }
        public int N039 { get; set; }
        public int N040 { get; set; }
        public int N041 { get; set; }
        public int N042 { get; set; }
        public int N043 { get; set; }
        public int N044 { get; set; }
        public int N045 { get; set; }
        public int N046 { get; set; }
        public int N047 { get; set; }
        public int N048 { get; set; }
        public int N049 { get; set; }
        public int N050 { get; set; }
        public int N051 { get; set; }
        public int N052 { get; set; }
        public int N053 { get; set; }
        public int N054 { get; set; }
        public int N055 { get; set; }
        public int N056 { get; set; }
        public int N057 { get; set; }
        public int N058 { get; set; }
        public int N059 { get; set; }
        public int N060 { get; set; }
        public int N061 { get; set; }
        public int N062 { get; set; }
        public int N063 { get; set; }
        public int N064 { get; set; }
        public int N065 { get; set; }
        public int N066 { get; set; }
        public int N067 { get; set; }
        public int N068 { get; set; }
        public int N069 { get; set; }
        public int N070 { get; set; }
        public int N071 { get; set; }
        public int N072 { get; set; }
        public int N073 { get; set; }
        public int N074 { get; set; }
        public int N075 { get; set; }
        public int N076 { get; set; }
        public int N077 { get; set; }
        public int N078 { get; set; }
        public int N079 { get; set; }
        public int N080 { get; set; }
        public int N081 { get; set; }
        public int N082 { get; set; }
        public int N083 { get; set; }
        public int N084 { get; set; }
        public int N085 { get; set; }
        public int N086 { get; set; }
        public int N087 { get; set; }
        public int N088 { get; set; }
        public int N089 { get; set; }
        public int N090 { get; set; }
        public int N091 { get; set; }
        public int N092 { get; set; }
        public int N093 { get; set; }
        public int N094 { get; set; }
        public int N095 { get; set; }
        public int N096 { get; set; }
        public int N097 { get; set; }
        public int N098 { get; set; }
        public int N099 { get; set; }
        public int N100 { get; set; }
        public int N101 { get; set; }
        public int N102 { get; set; }
        public int N103 { get; set; }
        public int N104 { get; set; }
        public int N105 { get; set; }
        public int N106 { get; set; }
        public int N107 { get; set; }
        public int N108 { get; set; }
        public int N109 { get; set; }
        public int N110 { get; set; }
        public int N111 { get; set; }
        public int N112 { get; set; }
        public int N113 { get; set; }
        public int N114 { get; set; }
        public int N115 { get; set; }
        public int N116 { get; set; }
        public int N117 { get; set; }
        public int N118 { get; set; }
        public int N119 { get; set; }
        public int N120 { get; set; }
        public int N121 { get; set; }
        public int N122 { get; set; }
        public int N123 { get; set; }
        public int N124 { get; set; }
        public int N125 { get; set; }
        public int N126 { get; set; }
        public int N127 { get; set; }
        public int N128 { get; set; }
        public int N129 { get; set; }
        public int N130 { get; set; }
        public int N131 { get; set; }
        public int N132 { get; set; }
        public int N133 { get; set; }
        public int N134 { get; set; }
        public int N135 { get; set; }
        public int N136 { get; set; }
        public int N137 { get; set; }
        public int N138 { get; set; }
        public int N139 { get; set; }
        public int N140 { get; set; }
        public int N141 { get; set; }
        public int N142 { get; set; }
        public int N143 { get; set; }
        public int N144 { get; set; }
        public int N145 { get; set; }
        public int N146 { get; set; }
        public int N147 { get; set; }
        public int N148 { get; set; }
        public int N149 { get; set; }
        public int N150 { get; set; }
        public int N151 { get; set; }
        public int N152 { get; set; }
        public int N153 { get; set; }
        public int N154 { get; set; }
        public int N155 { get; set; }
        public int N156 { get; set; }
        public int N157 { get; set; }
        public int N158 { get; set; }
        public int N159 { get; set; }
        public int N160 { get; set; }
        public int N161 { get; set; }
        public int N162 { get; set; }
        public int N163 { get; set; }
        public int N164 { get; set; }
        public int N165 { get; set; }
        public int N166 { get; set; }
        public int N167 { get; set; }
        public int N168 { get; set; }
        public int N169 { get; set; }
        public int N170 { get; set; }
        public int N171 { get; set; }
        public int N172 { get; set; }
        public int N173 { get; set; }
        public int N174 { get; set; }
        public int N175 { get; set; }
        public int N176 { get; set; }
        public int N177 { get; set; }
        public int N178 { get; set; }
        public int N179 { get; set; }
        public int N180 { get; set; }
        public int N181 { get; set; }
        public int N182 { get; set; }
        public int N183 { get; set; }
        public int N184 { get; set; }
        public int N185 { get; set; }
        public int N186 { get; set; }
        public int N187 { get; set; }
        public int N188 { get; set; }
        public int N189 { get; set; }
        public int N190 { get; set; }
        public int N191 { get; set; }
        public int N192 { get; set; }
        public int N193 { get; set; }
        public int N194 { get; set; }
        public int N195 { get; set; }
        public int N196 { get; set; }
        public int N197 { get; set; }
        public int N198 { get; set; }
        public int N199 { get; set; }
        public int N200 { get; set; }
        public int N201 { get; set; }
        public int N202 { get; set; }
        public int N203 { get; set; }
        public int N204 { get; set; }
        public int N205 { get; set; }
        public int N206 { get; set; }
        public int N207 { get; set; }
        public int N208 { get; set; }
        public int N209 { get; set; }
        public int N210 { get; set; }
        public int N211 { get; set; }
        public int N212 { get; set; }
        public int N213 { get; set; }
        public int N214 { get; set; }
        public int N215 { get; set; }
        public int N216 { get; set; }
        public int N217 { get; set; }
        public int N218 { get; set; }
        public int N219 { get; set; }
        public int N220 { get; set; }
        public int N221 { get; set; }
        public int N222 { get; set; }
        public int N223 { get; set; }
        public int N224 { get; set; }
        public int N225 { get; set; }
        public int N226 { get; set; }
        public int N227 { get; set; }
        public int N228 { get; set; }
        public int N229 { get; set; }
        public int N230 { get; set; }
        public int N231 { get; set; }
        public int N232 { get; set; }
        public int N233 { get; set; }
        public int N234 { get; set; }
        public int N235 { get; set; }
        public int N236 { get; set; }
        public int N237 { get; set; }
        public int N238 { get; set; }
        public int N239 { get; set; }
        public int N240 { get; set; }
        public int N241 { get; set; }
        public int N242 { get; set; }
        public int N243 { get; set; }
        public int N244 { get; set; }
        public int N245 { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    /// <summary>Class-level opt-in: 64 text values.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ManyTexts : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
        public string? P20 { get; set; }
        public string? P21 { get; set; }
        public string? P22 { get; set; }
        public string? P23 { get; set; }
        public string? P24 { get; set; }
        public string? P25 { get; set; }
        public string? P26 { get; set; }
        public string? P27 { get; set; }
        public string? P28 { get; set; }
        public string? P29 { get; set; }
        public string? P30 { get; set; }
        public string? P31 { get; set; }
        public string? P32 { get; set; }
        public string? P33 { get; set; }
        public string? P34 { get; set; }
        public string? P35 { get; set; }
        public string? P36 { get; set; }
        public string? P37 { get; set; }
        public string? P38 { get; set; }
        public string? P39 { get; set; }
        public string? P40 { get; set; }
        public string? P41 { get; set; }
        public string? P42 { get; set; }
        public string? P43 { get; set; }
        public string? P44 { get; set; }
        public string? P45 { get; set; }
        public string? P46 { get; set; }
        public string? P47 { get; set; }
        public string? P48 { get; set; }
        public string? P49 { get; set; }
        public string? P50 { get; set; }
        public string? P51 { get; set; }
        public string? P52 { get; set; }
        public string? P53 { get; set; }
        public string? P54 { get; set; }
        public string? P55 { get; set; }
        public string? P56 { get; set; }
        public string? P57 { get; set; }
        public string? P58 { get; set; }
        public string? P59 { get; set; }
        public string? P60 { get; set; }
        public string? P61 { get; set; }
        public string? P62 { get; set; }
        public string? P63 { get; set; }
    }

    /// <summary>Class-level opt-in: 62 text values, the most an entity with no other property holds in split form.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class SixtyTwoTexts : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
        public string? P20 { get; set; }
        public string? P21 { get; set; }
        public string? P22 { get; set; }
        public string? P23 { get; set; }
        public string? P24 { get; set; }
        public string? P25 { get; set; }
        public string? P26 { get; set; }
        public string? P27 { get; set; }
        public string? P28 { get; set; }
        public string? P29 { get; set; }
        public string? P30 { get; set; }
        public string? P31 { get; set; }
        public string? P32 { get; set; }
        public string? P33 { get; set; }
        public string? P34 { get; set; }
        public string? P35 { get; set; }
        public string? P36 { get; set; }
        public string? P37 { get; set; }
        public string? P38 { get; set; }
        public string? P39 { get; set; }
        public string? P40 { get; set; }
        public string? P41 { get; set; }
        public string? P42 { get; set; }
        public string? P43 { get; set; }
        public string? P44 { get; set; }
        public string? P45 { get; set; }
        public string? P46 { get; set; }
        public string? P47 { get; set; }
        public string? P48 { get; set; }
        public string? P49 { get; set; }
        public string? P50 { get; set; }
        public string? P51 { get; set; }
        public string? P52 { get; set; }
        public string? P53 { get; set; }
        public string? P54 { get; set; }
        public string? P55 { get; set; }
        public string? P56 { get; set; }
        public string? P57 { get; set; }
        public string? P58 { get; set; }
        public string? P59 { get; set; }
        public string? P60 { get; set; }
        public string? P61 { get; set; }
    }

    /// <summary>Class-level opt-in: 63 text values, one more than fit in split form.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class SixtyThreeTexts : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
        public string? P20 { get; set; }
        public string? P21 { get; set; }
        public string? P22 { get; set; }
        public string? P23 { get; set; }
        public string? P24 { get; set; }
        public string? P25 { get; set; }
        public string? P26 { get; set; }
        public string? P27 { get; set; }
        public string? P28 { get; set; }
        public string? P29 { get; set; }
        public string? P30 { get; set; }
        public string? P31 { get; set; }
        public string? P32 { get; set; }
        public string? P33 { get; set; }
        public string? P34 { get; set; }
        public string? P35 { get; set; }
        public string? P36 { get; set; }
        public string? P37 { get; set; }
        public string? P38 { get; set; }
        public string? P39 { get; set; }
        public string? P40 { get; set; }
        public string? P41 { get; set; }
        public string? P42 { get; set; }
        public string? P43 { get; set; }
        public string? P44 { get; set; }
        public string? P45 { get; set; }
        public string? P46 { get; set; }
        public string? P47 { get; set; }
        public string? P48 { get; set; }
        public string? P49 { get; set; }
        public string? P50 { get; set; }
        public string? P51 { get; set; }
        public string? P52 { get; set; }
        public string? P53 { get; set; }
        public string? P54 { get; set; }
        public string? P55 { get; set; }
        public string? P56 { get; set; }
        public string? P57 { get; set; }
        public string? P58 { get; set; }
        public string? P59 { get; set; }
        public string? P60 { get; set; }
        public string? P61 { get; set; }
        public string? P62 { get; set; }
    }

    private static T Filled<T>(int size) where T : Entity, new()
    {
        var entity = new T { Id = "p|r" };
        var properties = Texts<T>();
        for (var i = 0; i < properties.Length; i++)
            properties[i].SetValue(entity, Ascii(size, i));
        return entity;
    }

    private static PropertyInfo[] Texts<T>() =>
        typeof(T).GetProperties().Where(p => p.Name.Length == 3 && p.Name[0] == 'P').OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    private static InMemoryStorage<T> Memory<T>(long? threshold = null) where T : class, IEntity, new()
    {
        var offload = new BlobOffloadOptions();
        if (threshold is { } t) offload.ThresholdBytes = t;
        return new InMemoryStorage<T>(null, new UnifiedTableStorageOptions { BlobOffload = offload });
    }

    // The message states the cause and the numbers, names an applicable remedy, and quotes no value.
    private static void AssertAccurate(OversizedValueException ex, string column, long encodedBytes, int needed, int left, string? value)
    {
        Assert.Equal(column, ex.ColumnName);
        Assert.Equal(encodedBytes, ex.EncodedBytes);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"Property '{column}' encodes to {encodedBytes:N0} B"), ex.Message, StringComparison.Ordinal);
        Assert.Contains("no room for the cells of", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"need {needed} properties", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"leave {left} of the 252", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fewer properties", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BlobOffloadOptions.ThresholdBytes", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("encodes to 128 B", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mark the large properties", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Configure UnifiedTableStorageOptions.BlobOffload so", ex.Message, StringComparison.Ordinal);
        if (value is not null)
            Assert.DoesNotContain(value[..40], ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 1_200, ex.Message);
    }

    [Theory]
    [InlineData(1_000)]      // one cell: 250 properties
    [InlineData(32_000)]     // one cell
    [InlineData(3_500_000)]  // above the threshold: a blob reference, 250 properties
    public async Task EntityWithManyOrdinaryColumns_StoresItsLargeValue_WhereItsCellsFit(int size)
    {
        var store = Memory<WideRecord>();
        var body = Ascii(size, size);

        await store.CreateAsync(new WideRecord { Id = "p|r", Body = body }, Ct);

        Assert.Equal(body, (await store.OneAsync("p|r", Ct))!.Body);
    }

    [Theory]
    [InlineData(32_001)]   // split: 249 + 4 manifest cells + the part count = 254 > 252
    [InlineData(700_000)]
    public async Task EntityWithManyOrdinaryColumns_RefusesASplitValue_ItsMainRowHasNoRoomFor_Accurately(int size)
    {
        var store = Memory<WideRecord>();
        var body = Ascii(size, size);

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() => store.CreateAsync(new WideRecord { Id = "p|r", Body = body }, Ct));

        AssertAccurate(ex, "Body", size, needed: 5, left: 3, body);
        Assert.Null(await store.OneAsync("p|r", Ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheRefusal_IsTheSameOnBothStores_AndNothingIsStored(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var body = Ascii(700_000, 1);

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() =>
            stores.Store<WideRecord>().CreateAsync(new WideRecord { Id = stores.Id("r"), Body = body }, Ct));

        AssertAccurate(ex, "Body", 700_000, needed: 5, left: 3, body);
        Assert.Empty(await stores.RawRowsAsync<WideRecord>());
        Assert.Empty(await stores.BlobNamesAsync<WideRecord>());
    }

    [Fact]
    public async Task TheRefusal_DoesNotMisstateTheValue_NorAdviseWhatIsAlreadyDone()
    {
        var store = Memory<WideRecord>();

        var ex = await Record.ExceptionAsync(() => store.CreateAsync(new WideRecord { Id = "p|r", Body = Ascii(700_000, 1) }, Ct));
        if (ex is null)
            return; // stored: nothing to misstate

        var oversized = Assert.IsType<OversizedValueException>(ex);
        Assert.Equal("Body", oversized.ColumnName);
        // The value encodes to 700,000 B. 128 B is its SHA-256 cell.
        Assert.True(oversized.EncodedBytes != 128 && !oversized.Message.Contains("encodes to 128 B", StringComparison.Ordinal), oversized.Message);
        Assert.False(oversized.Message.Contains("mark the large properties (or the class) [TableValueSplit]", StringComparison.Ordinal), oversized.Message);
    }

    /// <summary>The remedy the refusal names works: with the threshold below the value's size the value is offloaded, one cell.</summary>
    [Theory]
    [InlineData(32_001)]
    [InlineData(700_000)]
    public async Task LoweringTheOffloadThreshold_AsTheRefusalAdvises_StoresTheValueAsABlobReference(int size)
    {
        var store = Memory<WideRecord>(threshold: 32_000);
        var body = Ascii(size, size);

        await store.CreateAsync(new WideRecord { Id = "p|r", Body = body }, Ct);

        Assert.Equal(body, (await store.OneAsync("p|r", Ct))!.Body);
        Assert.True(store.PhysicalRow("p", "r")!.ContainsKey("Body__blob"));
    }

    [Fact]
    public async Task SixtyFourOptedInValuesThatEachFitOneCell_AreStored()
    {
        var store = Memory<ManyTexts>();
        var entity = Filled<ManyTexts>(32_000);

        await store.CreateAsync(entity, Ct);

        var read = await store.OneAsync("p|r", Ct);
        foreach (var p in Texts<ManyTexts>())
            Assert.Equal((string?)p.GetValue(entity), (string?)p.GetValue(read));
    }

    [Fact]
    public async Task SixtyFourOptedInValuesThatEachNeedSplitCells_AreRefused_Accurately()
    {
        var store = Memory<ManyTexts>();

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() => store.CreateAsync(Filled<ManyTexts>(33_000), Ct));

        // 64 x 4 manifest cells and the part count: 257; Id, CreatedAt and UpdatedAt leave 249.
        AssertAccurate(ex, "P00", 33_000, needed: 257, left: 249, value: null);
        Assert.Contains("64 split values", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The limit, measured: an entity with no other property than those of <see cref="Entity"/> holds 62 split values, not 63.</summary>
    [Fact]
    public async Task AnEntityHoldsSixtyTwoSplitValues_NotSixtyThree()
    {
        var fits = Memory<SixtyTwoTexts>();
        var entity = Filled<SixtyTwoTexts>(33_000);
        await fits.CreateAsync(entity, Ct);
        var read = await fits.OneAsync("p|r", Ct);
        foreach (var p in Texts<SixtyTwoTexts>())
            Assert.Equal((string?)p.GetValue(entity), (string?)p.GetValue(read));

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() => Memory<SixtyThreeTexts>().CreateAsync(Filled<SixtyThreeTexts>(33_000), Ct));
        AssertAccurate(ex, "P00", 33_000, needed: 253, left: 249, value: null);
    }
}
