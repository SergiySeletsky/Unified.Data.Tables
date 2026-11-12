using System.Runtime.Serialization;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// <see cref="TableRequestSize"/>: the public row-size estimate for callers that build their own
/// transactions. It is the computation the large-value planner uses, and it is never below what the real
/// Azure SDK (Azure.Data.Tables 12.11.0) sends, which a capturing transport measures here.
/// </summary>
public class TableRequestSizeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A row with one property of every type the SDK writes, plus many doubles (each carries an Edm.Double annotation).</summary>
    internal static TableEntity EveryType(string pk = "pk", string rk = "rk", int doubles = 200)
    {
        var row = new TableEntity(pk, rk)
        {
            ["S"] = "plain <&> жж 😀",
            ["I"] = 42,
            ["L"] = long.MaxValue,
            ["B"] = true,
            ["G"] = Guid.NewGuid(),
            ["T"] = DateTimeOffset.UtcNow,
            ["Bin"] = new byte[1_000],
            ["Max"] = double.MaxValue,
            ["Min"] = double.Epsilon,
            ["Neg"] = -1.0 / 3,
        };
        for (var i = 0; i < doubles; i++)
            row["D" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)] = (i + 1) / 3.0;
        return row;
    }

    private static TableClient Client(CapturingTransport transport) =>
        new(new Uri("https://acct.table.core.windows.net/"), "t", new TableSharedKeyCredential("acct", Convert.ToBase64String(new byte[32])),
            new TableClientOptions { Transport = transport, Retry = { MaxRetries = 0 } });

    [Fact]
    public async Task EntityWireBytes_IsNeverBelowTheBodyTheSdkSends()
    {
        var transport = new CapturingTransport();
        var row = EveryType();

        await Client(transport).AddEntityAsync(row, Ct);

        var body = Assert.Single(transport.Bodies);
        var json = Encoding.UTF8.GetString(body);
        Assert.Contains("\"D000@odata.type\":\"Edm.Double\"", json, StringComparison.Ordinal);
        Assert.True(TableRequestSize.EstimateEntityWireBytes(row) >= body.Length,
            $"{TableRequestSize.EstimateEntityWireBytes(row)} < {body.Length}");
    }

    [Fact]
    public void DoubleProperty_CountsItsTypeAnnotation()
    {
        // "D":0.3333333333333333 plus "D@odata.type":"Edm.Double", as Azure.Data.Tables writes it.
        var expected = "\"D\":0.3333333333333333,\"D@odata.type\":\"Edm.Double\",".Length;
        Assert.True(TableRowSize.PropertyWireSize("D", 1.0 / 3) >= expected);
    }

    [Fact]
    public void Estimates_AreThePlannersComputation()
    {
        var row = EveryType(doubles: 3);
        Assert.Equal(TableRowSize.Estimate(row), TableRequestSize.EstimateEntityBytes(row));
        Assert.Equal(TableRowSize.WireSize(row), TableRequestSize.EstimateEntityWireBytes(row));
        Assert.Equal(TableRowSize.ActionWireSize(row),
            TableRequestSize.EstimateActionWireBytes(new TableTransactionAction(TableTransactionActionType.UpsertReplace, row)));
        Assert.Equal(TableRowSize.DeleteActionWireSize("pk", "rk"),
            TableRequestSize.EstimateActionWireBytes(new TableTransactionAction(TableTransactionActionType.Delete, row)));
    }

    [Fact]
    public void Limits_AreTheServiceLimits()
    {
        Assert.Equal(100, TableRequestSize.MaxTransactionActions);
        Assert.Equal(BatchPlanner.DefaultMaxCount, TableRequestSize.MaxTransactionActions);
        Assert.Equal(4_194_304, TableRequestSize.MaxTransactionPayloadBytes);
        Assert.Equal(TableRowSize.MaxTransactionWireBytes, TableRequestSize.MaxTransactionWireBytes);
        Assert.Equal(BatchPlanner.DefaultMaxBytes, TableRequestSize.MaxTransactionEntityBytes);
        Assert.Equal(1_000_000, TableRequestSize.MaxEntityBytes);
        Assert.Equal(252, TableRequestSize.MaxEntityProperties);
    }

    [Fact]
    public void FitsOneTransaction_AppliesCountBytesWireAndPartition()
    {
        static TableTransactionAction Add(string pk, string rk, object? value = null) =>
            new(TableTransactionActionType.Add, new TableEntity(pk, rk) { ["V"] = value ?? "x" });

        Assert.True(TableRequestSize.FitsOneTransaction(Enumerable.Range(0, 100).Select(i => Add("p", "r" + i))));
        Assert.False(TableRequestSize.FitsOneTransaction(Enumerable.Range(0, 101).Select(i => Add("p", "r" + i))));
        Assert.False(TableRequestSize.FitsOneTransaction([Add("p", "a"), Add("q", "b")]));
        Assert.True(TableRequestSize.FitsOneTransaction([]));

        // 25 rows of four 7,500-char Cyrillic cells: 1.5 MB stored, but 4.5 MB escaped on the wire.
        var cyrillic = Enumerable.Range(0, 25).Select(i =>
        {
            var e = new TableEntity("p", "c" + i);
            for (var c = 0; c < 4; c++) e["C" + c] = new string('ж', 7_500);
            return new TableTransactionAction(TableTransactionActionType.UpsertReplace, e);
        }).ToList();
        Assert.True(cyrillic.Sum(a => TableRequestSize.EstimateEntityBytes(a.Entity)) <= TableRequestSize.MaxTransactionEntityBytes);
        Assert.False(TableRequestSize.FitsOneTransaction(cyrillic));

        // Four 800,000-byte binaries: 3.2 MB stored, over the 3,000,000 B planner budget.
        Assert.False(TableRequestSize.FitsOneTransaction(Enumerable.Range(0, 4).Select(i => Add("p", "b" + i, new byte[800_000]))));
    }

    [Fact]
    public void FitsOneTransaction_RefusesTwoActionsOnOneRow()
    {
        static TableTransactionAction Act(TableTransactionActionType type, string rk) => new(type, new TableEntity("p", rk) { ["V"] = "x" });

        Assert.False(TableRequestSize.FitsOneTransaction([Act(TableTransactionActionType.Add, "a"), Act(TableTransactionActionType.UpsertMerge, "a")]));
        Assert.False(TableRequestSize.FitsOneTransaction([Act(TableTransactionActionType.Delete, "a"), Act(TableTransactionActionType.Add, "b"), Act(TableTransactionActionType.Delete, "a")]));
        // Row keys compare ordinally, as the service compares them: different case is a different row.
        Assert.True(TableRequestSize.FitsOneTransaction([Act(TableTransactionActionType.Add, "a"), Act(TableTransactionActionType.Add, "A")]));
    }

    [Fact]
    public void Arguments_AreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => TableRequestSize.EstimateEntityBytes(null!));
        Assert.Throws<ArgumentNullException>(() => TableRequestSize.EstimateEntityWireBytes(null!));
        Assert.Throws<ArgumentNullException>(() => TableRequestSize.EstimateActionWireBytes(null!));
        Assert.Throws<ArgumentNullException>(() => TableRequestSize.FitsOneTransaction(null!));
    }

    public enum Shade
    {
        Light,
        Dark,
    }

    public sealed class Poco : ITableEntity
    {
        public string PartitionKey { get; set; } = "pk";
        public string RowKey { get; set; } = "rk";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string Name { get; set; } = "<name> жж";
        public double Ratio { get; set; } = 1.0 / 7;
        public long Big { get; set; } = long.MinValue;
        public Shade Shade { get; set; } = Shade.Dark;
        public byte[] Data { get; set; } = new byte[300];

        [DataMember(Name = "renamed_column")]
        public int Renamed { get; set; } = 7;

        [IgnoreDataMember]
        public string Ignored { get; set; } = new('x', 10_000);
    }

    [Fact]
    public async Task ModelEntity_IsMeasuredAsTheSdkSerializesIt()
    {
        var transport = new CapturingTransport();
        var poco = new Poco();

        await Client(transport).AddEntityAsync(poco, Ct);

        var body = Assert.Single(transport.Bodies);
        Assert.True(TableRequestSize.EstimateEntityWireBytes(poco) >= body.Length,
            $"{TableRequestSize.EstimateEntityWireBytes(poco)} < {body.Length}");
        // The ignored property is not counted, a renamed one is counted under its column name.
        Assert.True(TableRequestSize.EstimateEntityBytes(poco) < 10_000);
        Assert.True(TableRequestSize.EstimateEntityWireBytes(poco) < body.Length + 2_000);
    }
}
