using Azure;
using Azure.Data.Tables;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the Azure-side polymorphic contract: the discriminator records the RUNTIME type, the read
/// gives back the true derived instance, and keys are used exactly as supplied.
/// </summary>
public class PolymorphicStorageTests
{
    [Fact]
    public async Task InsertAsync_DerivedInstance_WritesDiscriminatorForRuntimeType()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertAsync(
            new TableKey("agg-1", "000000001"),
            new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.Equal(
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestCreatedEvent)),
            written[SystemColumnNames.TypeName]);
    }

    [Fact]
    public async Task InsertAsync_ReturnsEntry_WithTrueDerivedInstance()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var entry = await harness.Store.InsertAsync(
            new TableKey("agg-1", "000000001"),
            new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.IsType<TestCreatedEvent>(entry.Item).Version);
    }

    [Fact]
    public async Task InsertAsync_Keys_AreUsedVerbatim_UnderDefaultNormalization()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertAsync(
            new TableKey("Agg 1", "MiXeD Case"),
            new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.Equal("Agg 1", written.PartitionKey);
        Assert.Equal("MiXeD Case", written.RowKey);
    }

    [Fact]
    public async Task InsertAsync_ExistingKey_ThrowsDuplicateKeyException()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.Table
            .AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new RequestFailedException(409, "conflict"));

        await Assert.ThrowsAsync<DuplicateKeyException>(() => harness.Store.InsertAsync(
            new TableKey("p", "r"), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InsertAsync_MarkerRow_WritesNoDiscriminator()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertMarkerAsync(
            new TableKey("t1", "FlagEntity"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.False(written.ContainsKey(SystemColumnNames.TypeName));
        Assert.False((bool)written["_IsCommitted"]);
    }

    [Fact]
    public async Task InsertAsync_UnprefixedSystemColumn_Throws()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var write = new PolymorphicWrite<TestMessage>(
            new TableKey("p", "r"),
            new TestCommand { Id = "c1" },
            new Dictionary<string, object>(StringComparer.Ordinal) { ["IsPublished"] = true });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.InsertAsync(write, TestContext.Current.CancellationToken));
        Assert.Contains("not a system column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertAsync_TypeNameAsSystemColumn_Throws()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var write = new PolymorphicWrite<TestMessage>(
            new TableKey("p", "r"),
            new TestCommand { Id = "c1" },
            new Dictionary<string, object>(StringComparer.Ordinal) { [SystemColumnNames.TypeName] = "x" });

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.InsertAsync(write, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", "r")]
    [InlineData("p", "")]
    public async Task InsertAsync_EmptyKey_Throws(string partition, string row)
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.InsertAsync(
            new TableKey(partition, row), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_MissingRow_ReturnsNull()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupNotFound();

        Assert.Null(await harness.Store.GetAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_DerivedRow_ReturnsTrueDerivedInstance()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        var row = new TestArchivedEvent { Id = "e2", Reason = "obsolete" }.ToTableEntity("agg-1", "000000002");
        row[SystemColumnNames.TypeName] =
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestArchivedEvent));
        harness.SetupGet(row);

        var entry = await harness.Store.GetAsync(
            new TableKey("agg-1", "000000002"), TestContext.Current.CancellationToken);

        Assert.Equal("obsolete", Assert.IsType<TestArchivedEvent>(entry!.Item).Reason);
    }

    [Fact]
    public async Task GetAsync_MarkerRow_ReturnsEntryWithNullItemAndColumnsIntact()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupGet(new TableEntity("t1", "FlagEntity") { ["_IsCommitted"] = true });

        var entry = await harness.Store.GetAsync(
            new TableKey("t1", "FlagEntity"), TestContext.Current.CancellationToken);

        Assert.Null(entry!.Item);
        Assert.Null(entry.Discriminator);
        Assert.True(entry.Column<bool>("_IsCommitted"));
    }

    [Fact]
    public async Task UpsertAsync_SendsReplaceMode()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupUpsert();

        await harness.Store.UpsertAsync(
            new TableKey("p", "r"), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken);

        await harness.Table.Received(1).UpsertEntityAsync(
            Arg.Any<TableEntity>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsANoOp()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.Table
            .DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new RequestFailedException(404, "not found"));

        await harness.Store.DeleteAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Constructor_ResolvesTheGivenTableName()
    {
        using var harness = new PolymorphicHarness<TestMessage>(tableName: "StateEventStore");

        harness.Service.Received(1).GetTableClient("StateEventStore");
    }
}
