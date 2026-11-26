using System.Linq.Expressions;
using System.Reflection;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0 adds no member to <see cref="IStorage{T}"/> or <see cref="IPolymorphicStorage{TBase}"/>: a
/// hand-written implementer or decorator compiled against 0.9.2 must still satisfy the interface, at
/// compile time and when the runtime loads its type. The member lists below are the 0.9.2 ones; a new
/// operation belongs on a side interface (see <see cref="IStorageCapabilities"/>), never here.
/// </summary>
public class StoreContractShapeTests
{
    [Fact]
    public void IStorage_HasExactlyTheMembersOf092()
    {
        string[] expected =
        [
            "System.Threading.Tasks.Task DeleteAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] DeletePartitionAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[T] CreateAsync(T, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[T] UpsertAsync(T, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[T] UpdateAsync(T, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[T] UpdateAsync(T, Unified.Data.Tables.ConcurrencyMode, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.String] UpdateAsync(System.String, System.Action`1[Unified.Data.Tables.UpdateBuilder`1[T]], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[T] OneAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Boolean] ExistsAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Collections.Generic.IEnumerable`1[T]] QueryAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[T]] QueryAsync(Unified.Data.Tables.QueryOptions, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[T]] QueryAsync(System.Linq.Expressions.Expression`1[System.Func`2[T,System.Boolean]], System.String, System.Nullable`1[System.Int32], System.Threading.CancellationToken)",
            "System.Collections.Generic.IAsyncEnumerable`1[T] QueryStreamAsync(Unified.Data.Tables.QueryOptions, System.Threading.CancellationToken)",
            "System.Collections.Generic.IAsyncEnumerable`1[T] QueryStreamAsync(System.Linq.Expressions.Expression`1[System.Func`2[T,System.Boolean]], System.String, System.Nullable`1[System.Int32], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[Unified.Data.Tables.EntityPage`1[T]] QueryPageAsync(Unified.Data.Tables.QueryOptions, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Boolean] AnyAsync(System.Linq.Expressions.Expression`1[System.Func`2[T,System.Boolean]], System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] CreateBatchAsync(System.Collections.Generic.IReadOnlyCollection`1[T], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] UpsertBatchAsync(System.Collections.Generic.IReadOnlyCollection`1[T], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] CountAsync(System.String, System.Threading.CancellationToken)",
        ];

        AssertMembers(typeof(IStorage<>), expected);
    }

    [Fact]
    public void IPolymorphicStorage_HasExactlyTheMembersOf092()
    {
        string[] expected =
        [
            "System.Threading.Tasks.Task EnsureCreatedAsync(System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[Unified.Data.Tables.PolymorphicEntry`1[TBase]] InsertAsync(Unified.Data.Tables.PolymorphicWrite`1[TBase], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[Unified.Data.Tables.PolymorphicEntry`1[TBase]] UpsertAsync(Unified.Data.Tables.PolymorphicWrite`1[TBase], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] InsertBatchAsync(System.Collections.Generic.IReadOnlyCollection`1[Unified.Data.Tables.PolymorphicWrite`1[TBase]], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task MergeColumnsAsync(Unified.Data.Tables.TableKey, System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.Object], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[Unified.Data.Tables.PolymorphicEntry`1[TBase]] GetAsync(Unified.Data.Tables.TableKey, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Unified.Data.Tables.PolymorphicEntry`1[TBase]]] QueryAsync(System.String, System.Nullable`1[System.Int32], System.Threading.CancellationToken)",
            "System.Collections.Generic.IAsyncEnumerable`1[Unified.Data.Tables.PolymorphicEntry`1[TBase]] QueryStreamAsync(System.String, System.Nullable`1[System.Int32], System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task DeleteAsync(Unified.Data.Tables.TableKey, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] DeletePartitionAsync(System.String, System.Threading.CancellationToken)",
            "System.Threading.Tasks.Task`1[System.Int32] CountAsync(System.String, System.Threading.CancellationToken)",
        ];

        AssertMembers(typeof(IPolymorphicStorage<>), expected);
    }

    private static void AssertMembers(Type contract, string[] expected)
    {
        Assert.Empty(contract.GetInterfaces());
        // Methods only: no property, event or static member crept in either.
        Assert.Equal(contract.GetMethods().Length, contract.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length);
        Assert.Equal(expected.Order(StringComparer.Ordinal), contract.GetMethods().Select(m => m.ToString()!).Order(StringComparer.Ordinal));
        // No default body either: one would give the two target frameworks different surfaces.
        Assert.All(contract.GetMethods(), m => Assert.True(m.IsAbstract, m.Name));
    }

    [Fact]
    public void AnImplementerWrittenAgainst092_StillSatisfiesTheInterface_AndLoads()
    {
        // UneditedImplementer<T> below implements the 0.9.2 members and nothing else. That it compiles is
        // the compile-time half; instantiating it makes the runtime check its type against the interface
        // (a member it lacks would be a TypeLoadException here).
        IStorage<TestEntity> storage = new UneditedImplementer<TestEntity>();

        Assert.IsAssignableFrom<IStorage<TestEntity>>(storage);
        var map = typeof(UneditedImplementer<TestEntity>).GetInterfaceMap(typeof(IStorage<TestEntity>));
        Assert.Equal(19, map.InterfaceMethods.Length);
        Assert.All(map.TargetMethods, m => Assert.Equal(typeof(UneditedImplementer<TestEntity>), m.DeclaringType));
        // What 0.10.0 adds reaches such a store as "not supported" or "unknown", never as a crash.
        Assert.False(storage.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out _));
        Assert.Same(StorageInfo.Unknown, storage.GetInfo());
    }
}

/// <summary>
/// A third-party <see cref="IStorage{T}"/> as one was written against 0.9.2: the nineteen members of that
/// release, no probe, no side interface. Do not add a member here when the interface changes; this file
/// failing to compile is the signal that an implementer in the field breaks.
/// </summary>
internal class UneditedImplementer<T> : IStorage<T> where T : class, IEntity, new()
{
    public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<T> CreateAsync(T entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<T> UpsertAsync(T entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<T> UpdateAsync(T entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<T?> OneAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
}
