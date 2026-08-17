using Azure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The polymorphic contract against the fake. The repo's guarantee is that a green test here holds
/// against Azure Tables, so this file deliberately re-runs the same behaviours as
/// <see cref="PolymorphicStorageTests"/> rather than testing the fake's internals.
/// </summary>
public class InMemoryPolymorphicStorageTests
{
    private static InMemoryPolymorphicStorage<TestMessage> NewStore() => new();

    [Fact]
    public async Task InsertAsync_ThenGetAsync_ReturnsTrueDerivedInstance()
    {
        var store = NewStore();
        var key = new TableKey("agg-1", "000000001");

        await store.InsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.IsType<TestCreatedEvent>(entry!.Item).Version);
    }

    [Fact]
    public async Task InsertAsync_DuplicateKey_ThrowsDuplicateKeyException()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        await store.InsertAsync(key, new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DuplicateKeyException>(() =>
            store.InsertAsync(key, new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InsertAsync_DuplicateKey_NamesTheTable_ByteIdenticallyToAzure()
    {
        // The fake used to name typeof(TBase).Name, which can never equal a real polymorphic table
        // name — so every duplicate-key message and EntityType diverged from production.
        var fake = new InMemoryPolymorphicStorage<TestMessage>("TestTable");
        await fake.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        using var harness = new PolymorphicHarness<TestMessage>();
        harness.Table
            .AddEntityAsync(Arg.Any<Azure.Data.Tables.TableEntity>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new RequestFailedException(409, "conflict"));

        var fromFake = await Assert.ThrowsAsync<DuplicateKeyException>(() =>
            fake.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
                TestContext.Current.CancellationToken));
        var fromReal = await Assert.ThrowsAsync<DuplicateKeyException>(() =>
            harness.Store.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
                TestContext.Current.CancellationToken));

        Assert.Equal(fromReal.Message, fromFake.Message);
        Assert.Equal(fromReal.EntityType, fromFake.EntityType);
        Assert.Equal("TestTable", fromFake.EntityType);
    }

    [Fact]
    public async Task InsertAsync_ReturnsAnETagAndNoTimestamp_LikeAzure()
    {
        var store = NewStore();

        var entry = await store.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry.ETag);
        // Azure's write response carries no service last-write time, so the fake must not invent one
        // — a test asserting on it would be green here and null in production.
        Assert.Null(entry.Timestamp);

        var read = await store.GetAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken);
        Assert.NotNull(read!.Timestamp);
    }

    [Fact]
    public async Task UpsertAsync_ExistingKey_Replaces()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        await store.InsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 1 },
            TestContext.Current.CancellationToken);

        await store.UpsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 2 },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.IsType<TestCreatedEvent>(entry!.Item).Version);
    }

    [Fact]
    public async Task InsertBatchAsync_HeterogeneousPlusMarker_AllReadBack()
    {
        var store = NewStore();

        var written = await store.InsertBatchAsync(
            [
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "001"), new TestCreatedEvent { Id = "e1" }),
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "002"), new TestArchivedEvent { Id = "e2" }),
                PolymorphicWrite<TestMessage>.Marker(
                    new TableKey("t1", "FlagEntity"),
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false }),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, written);

        var entries = await store.QueryAsync("t1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, entries.Count);
        Assert.Single(entries, e => e.Item is null);
        Assert.Single(entries.ItemsOfType<TestMessage, TestArchivedEvent>());
    }

    [Fact]
    public async Task InsertBatchAsync_SameKeyTwiceInOneBatch_ThrowsDuplicateKeyException()
    {
        var store = NewStore();

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.InsertBatchAsync(
            [
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "same"), new TestCreatedEvent { Id = "e1" }),
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "same"), new TestArchivedEvent { Id = "e2" }),
            ],
            TestContext.Current.CancellationToken));

        // Azure rejects the whole transaction; nothing may be left behind. Before the fix the fake
        // stored ONE row and reported 2.
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task InsertBatchAsync_ExistingKey_ThrowsDuplicateKeyException()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("t1", "001"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.InsertBatchAsync(
            [new PolymorphicWrite<TestMessage>(new TableKey("t1", "001"), new TestCreatedEvent { Id = "e1" })],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InsertBatchAsync_WithinBatchDuplicate_ThrowsTheSameTypeOnBothImplementations()
    {
        var fake = new InMemoryPolymorphicStorage<TestMessage>("TestTable");

        using var harness = new PolymorphicHarness<TestMessage>();
        // Azure only reports a within-batch duplicate from the service, as 400 InvalidDuplicateRow.
        harness.Table.SubmitTransactionAsync(
                Arg.Any<IEnumerable<Azure.Data.Tables.TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw new Azure.Data.Tables.TableTransactionFailedException(
                new RequestFailedException(400, "InvalidDuplicateRow", "InvalidDuplicateRow", null)));

        PolymorphicWrite<TestMessage>[] writes =
        [
            new(new TableKey("t1", "same"), new TestCreatedEvent { Id = "e1" }),
            new(new TableKey("t1", "same"), new TestArchivedEvent { Id = "e2" }),
        ];

        var fromFake = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => fake.InsertBatchAsync(writes, TestContext.Current.CancellationToken));
        var fromReal = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => harness.Store.InsertBatchAsync(writes, TestContext.Current.CancellationToken));

        Assert.Equal(fromReal.GetType(), fromFake.GetType());
        Assert.Equal(fromReal.EntityType, fromFake.EntityType);
    }

    [Fact]
    public async Task DeletePartitionAsync_EmptyPartition_ThrowsIdenticallyOnBothImplementations()
    {
        var fake = NewStore();
        using var harness = new PolymorphicHarness<TestMessage>();

        var fromFake = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.DeletePartitionAsync("", TestContext.Current.CancellationToken));
        var fromReal = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.DeletePartitionAsync("", TestContext.Current.CancellationToken));

        Assert.Equal(fromReal.Message, fromFake.Message);
        Assert.Equal(fromReal.ParamName, fromFake.ParamName);
    }

    [Fact]
    public async Task EveryOperation_CancelledToken_Throws()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        var cancelled = new CancellationToken(canceled: true);
        var key = new TableKey("p", "r");
        var columns = new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true };

        Func<Task>[] operations =
        [
            () => store.InsertAsync(new TableKey("p", "r2"), new TestCommand { Id = "c2" }, cancelled),
            () => store.UpsertAsync(key, new TestCommand { Id = "c1" }, cancelled),
            () => store.InsertBatchAsync(
                [new PolymorphicWrite<TestMessage>(new TableKey("p", "r3"), new TestCommand { Id = "c3" })],
                cancelled),
            () => store.MergeColumnsAsync(key, columns, cancelled),
            () => store.GetAsync(key, cancelled),
            () => store.QueryAsync("p", ct: cancelled),
            () => Drain(store.QueryStreamAsync("p", ct: cancelled)),
            () => store.DeleteAsync(key, cancelled),
            () => store.DeletePartitionAsync("p", cancelled),
            () => store.CountAsync("p", cancelled),
        ];

        foreach (var operation in operations)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);

        return;

        static async Task Drain(IAsyncEnumerable<PolymorphicEntry<TestMessage>> source)
        {
            await foreach (var _ in source)
            {
                // Enumeration alone is the assertion; the token is checked before the first item.
            }
        }
    }

    [Fact]
    public async Task MergeColumnsAsync_FlipsASentinel_WithoutTouchingThePayload()
    {
        var store = NewStore();
        var key = new TableKey("agg-1", "000000001");
        await store.InsertAsync(
            new PolymorphicWrite<TestMessage>(
                key,
                new TestIntegrationEvent { Id = "e1", Topic = "orders" },
                new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = false }),
            TestContext.Current.CancellationToken);

        await store.MergeColumnsAsync(
            key,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.True(entry!.Column<bool>("_IsPublished"));
        Assert.Equal("orders", Assert.IsType<TestIntegrationEvent>(entry.Item).Topic);
    }

    [Fact]
    public async Task MergeColumnsAsync_MissingRow_Throws404LikeAzure()
    {
        var store = NewStore();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => store.MergeColumnsAsync(
            new TableKey("p", "r"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true },
            TestContext.Current.CancellationToken));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task QueryAsync_OrdersLexicallyByPartitionThenRow()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("b", "2"), new TestCommand { Id = "3" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("a", "2"), new TestCommand { Id = "2" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("a", "1"), new TestCommand { Id = "1" },
            TestContext.Current.CancellationToken);

        var entries = await store.QueryAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], entries.Select(e => e.Item!.Id));
    }

    [Fact]
    public async Task Keys_AreUsedVerbatim_AndAreCaseSensitive()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("Agg 1", "MiXeD"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetAsync(new TableKey("Agg 1", "MiXeD"), TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(new TableKey("agg-1", "mixed"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsANoOp()
    {
        var store = NewStore();

        await store.DeleteAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeletePartitionAsync_RemovesOnlyThatPartition()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("a", "1"), new TestCommand { Id = "1" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("b", "1"), new TestCommand { Id = "2" },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.DeletePartitionAsync("a", TestContext.Current.CancellationToken));
        Assert.Equal(1, await store.CountAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RowsRoundTripThroughTheRealSerializer()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        var payload = new TestCtorlessEvent("large-" + new string('x', 200)) { Id = "e1" };

        await store.InsertAsync(key, payload, TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.StartsWith("large-", Assert.IsType<TestCtorlessEvent>(entry!.Item).Payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IsPublished")]
    [InlineData("_TypeName")]
    public async Task Fake_And_Real_ThrowByteIdenticalMessages_ForBadSystemColumns(string columnName)
    {
        var fake = NewStore();
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupMerge();

        var columns = new Dictionary<string, object>(StringComparer.Ordinal) { [columnName] = true };
        var key = new TableKey("p", "r");

        var fromFake = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.MergeColumnsAsync(key, columns, TestContext.Current.CancellationToken));
        var fromReal = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.MergeColumnsAsync(key, columns, TestContext.Current.CancellationToken));

        Assert.Equal(fromReal.Message, fromFake.Message);
    }

    [Fact]
    public async Task Fake_And_Real_ThrowByteIdenticalMessages_ForUnassignableType()
    {
        var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
        var row = new Azure.Data.Tables.TableEntity("p", "r")
        {
            [SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(UnrelatedType)),
        };

        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupGet(row);

        var fromReal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Store.GetAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken));

        Assert.Equal(PolymorphicMessages.NotAssignable(
            discriminator.ToDiscriminator(typeof(UnrelatedType)), typeof(TestMessage)), fromReal.Message);
    }
}
