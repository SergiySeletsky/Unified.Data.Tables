using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// 0.10.0, break B2: the read-modify-write verbs when another writer deletes the row between a verb's
/// read and its write. Until 0.10.0 the store's raw not-found error escaped all four. Now
/// <c>TryMutateAsync</c> and <c>TryTransitionAsync</c> return <see cref="MutationStatus.NotFound"/>,
/// <c>MutateOrCreateAsync</c> goes back to create, and <c>MutateAsync</c> keeps its
/// <see cref="InvalidOperationException"/> with the store's <see cref="EntityNotFoundException"/> inside.
/// Each runs on the in-memory store, on <see cref="TableStorage{T}"/> over a stubbed table and on a
/// third-party <see cref="IStorage{T}"/>.
/// </summary>
public class RowVanishedBetweenReadAndWriteTests
{
    private const string Id = "p|r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A store, and what deletes the row behind the verb's back (run from inside the mutation).</summary>
    private sealed record Rig(IStorage<TestEntity> Store, Action DeleteRow, Func<Task<TestEntity?>> Stored, IDisposable? Owned = null) : IDisposable
    {
        public void Dispose() => Owned?.Dispose();
    }

    public static TheoryData<string> Stores => ["memory", "azure", "third party"];

    private static async Task<Rig> RigAsync(string kind)
    {
        switch (kind)
        {
            case "memory":
            {
                var store = new InMemoryStorage<TestEntity>();
                await store.CreateAsync(new TestEntity { Id = Id, Value = 1 }, Ct);
                return new Rig(store, () => store.DeleteAsync(Id, Ct).GetAwaiter().GetResult(), () => store.OneAsync(Id, Ct));
            }
            case "azure":
            {
                // TableStorage over a table double that holds one row: a point read finds it until it is
                // deleted, a replace of a row that is gone gets the service's 404, an insert stores it again.
                var h = new StorageHarness<TestEntity>(options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
                TableEntity? row = Mocks.Row("p", "r", value: 1);
                NullableResponse<TableEntity> pointRead = new CurrentRow(() => row);
                h.Table.GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                    .Returns(pointRead);
                h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
                    .Returns<Response>(call =>
                    {
                        if (row is null)
                            throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
                        row = Stamped(call.Arg<TableEntity>(), "W/\"etag2\"");
                        return Mocks.EtagResponse("W/\"etag2\"");
                    });
                h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
                    .Returns<Response>(call =>
                    {
                        row = Stamped(call.Arg<TableEntity>(), "W/\"etag3\"");
                        return Mocks.EtagResponse("W/\"etag3\"");
                    });
                return new Rig(h.Store, () => row = null, () => h.Store.OneAsync(Id, Ct), h);
            }
            default:
            {
                // A third-party implementation that reports the missing row with the neutral type.
                var inner = new InMemoryStorage<TestEntity>();
                await inner.CreateAsync(new TestEntity { Id = Id, Value = 1 }, Ct);
                var store = Substitute.For<IStorage<TestEntity>>();
                store.OneAsync(Id, Arg.Any<CancellationToken>()).Returns(call => inner.OneAsync(Id, call.Arg<CancellationToken>()));
                store.CreateAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>())
                    .Returns(call => inner.CreateAsync(call.Arg<TestEntity>(), call.Arg<CancellationToken>()));
                store.UpdateAsync(Arg.Any<TestEntity>(), ConcurrencyMode.Strict, Arg.Any<CancellationToken>())
                    .Returns(async call =>
                    {
                        if (!await inner.ExistsAsync(Id, Ct))
                            throw new EntityNotFoundException("Custom", Id, null);
                        return await inner.UpdateAsync(call.Arg<TestEntity>(), ConcurrencyMode.Strict, call.Arg<CancellationToken>());
                    });
                return new Rig(store, () => inner.DeleteAsync(Id, Ct).GetAwaiter().GetResult(), () => inner.OneAsync(Id, Ct));
            }
        }
    }

    private static TableEntity Stamped(TableEntity written, string etag) => new(written) { ETag = new ETag(etag) };

    /// <summary>The answer of a point read: the row the table double holds at the moment it is asked.</summary>
    private sealed class CurrentRow(Func<TableEntity?> row) : NullableResponse<TableEntity>
    {
        public override bool HasValue => row() is not null;

        public override TableEntity? Value => row();

        public override Response GetRawResponse() => new FakeResponse();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TryMutate_RowDeletedBetweenTheReadAndTheWrite_ReturnsNotFound(string kind)
    {
        using var rig = await RigAsync(kind);
        var mutations = 0;

        var result = await rig.Store.TryMutateAsync(Id, e =>
        {
            mutations++;
            e.Value++;
            rig.DeleteRow(); // another writer deletes the row after the verb read it
        }, ct: Ct);

        Assert.Equal(MutationStatus.NotFound, result.Status);
        Assert.Null(result.Entity);
        Assert.Equal(1, mutations); // a row that is gone is not a race to retry
        Assert.Null(await rig.Stored());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TryTransition_RowDeletedBetweenTheReadAndTheWrite_ReturnsNotFound(string kind)
    {
        using var rig = await RigAsync(kind);
        var applied = 0;

        var result = await rig.Store.TryTransitionAsync(Id, e => e.Value == 1, e =>
        {
            applied++;
            e.Value = 2;
            rig.DeleteRow();
        }, ct: Ct);

        Assert.Equal(MutationStatus.NotFound, result.Status);
        Assert.Null(result.Entity);
        Assert.Equal(1, applied);
        Assert.Null(await rig.Stored());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task MutateOrCreate_RowDeletedBetweenTheReadAndTheUpdate_GoesBackToCreate(string kind)
    {
        using var rig = await RigAsync(kind);
        var created = 0;
        var mutations = 0;

        var persisted = await rig.Store.MutateOrCreateAsync(Id,
            () =>
            {
                created++;
                return new TestEntity { Value = 100 };
            },
            e =>
            {
                e.Value++;
                if (++mutations == 1)
                    rig.DeleteRow(); // deleted after the first attempt's read
            }, ct: Ct);

        // The second attempt found no row, created the initial state and applied the delta to it once.
        Assert.Equal(1, created);
        Assert.Equal(2, mutations);
        Assert.Equal(101, persisted.Value);
        Assert.Equal(101, (await rig.Stored())!.Value);
    }

    [Fact]
    public async Task MutateOrCreate_RowDeletedOnTheLastAttempt_SurfacesTheDocumentedConflict_WithTheMissingRowInside()
    {
        using var rig = await RigAsync("memory");

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => rig.Store.MutateOrCreateAsync(Id,
            () => new TestEntity(),
            _ => rig.DeleteRow(), maxAttempts: 1, ct: Ct));

        // Never the raw missing-row failure: the verb's contract names one exception for a spent budget.
        Assert.Equal(Id, ex.Id);
        Assert.IsType<EntityNotFoundException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Mutate_RowDeletedBetweenTheReadAndTheWrite_KeepsItsInvalidOperation_WithTheNeutralFailureInside(string kind)
    {
        using var rig = await RigAsync(kind);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.MutateAsync(Id, e =>
        {
            e.Value++;
            rig.DeleteRow();
        }, ct: Ct));

        // The same exception and message as for a row the read does not find.
        Assert.Equal($"Cannot mutate '{Id}': the entity does not exist.", ex.Message);
        var missing = Assert.IsType<EntityNotFoundException>(ex.InnerException);
        Assert.Equal(Id, missing.Id);
        if (kind == "azure")
            Assert.Equal(404, Assert.IsType<RequestFailedException>(missing.InnerException).Status);
    }

    [Fact]
    public async Task Mutate_RowThatTheReadDoesNotFind_ThrowsAsBefore_WithNoInnerException()
    {
        var store = new InMemoryStorage<TestEntity>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MutateAsync(Id, e => e.Value++, ct: Ct));

        Assert.Equal($"Cannot mutate '{Id}': the entity does not exist.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task TheTryVerbs_StillThrowAFailureThatIsNeitherAConflictNorAMissingRow()
    {
        var store = Substitute.For<IStorage<TestEntity>>();
        store.OneAsync(Id, Arg.Any<CancellationToken>()).Returns(new TestEntity { Id = Id, ETag = "e" });
        var outage = new TransientStorageException("unavailable");
        store.UpdateAsync(Arg.Any<TestEntity>(), ConcurrencyMode.Strict, Arg.Any<CancellationToken>())
            .Returns<Task<TestEntity>>(_ => throw outage);

        Assert.Same(outage, await Assert.ThrowsAsync<TransientStorageException>(() => store.TryMutateAsync(Id, e => e.Value++, ct: Ct)));
        Assert.Same(outage, await Assert.ThrowsAsync<TransientStorageException>(
            () => store.TryTransitionAsync(Id, _ => true, e => e.Value++, ct: Ct)));
    }
}
