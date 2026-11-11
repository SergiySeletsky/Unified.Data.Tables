using System.Diagnostics;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A plain entity type (its declared types opt into nothing: no <c>[TableValueSplit]</c>, no
/// <c>TableBlobContent</c>) runs exactly the user code it ran on the published 0.8.3: the library constructs
/// it, runs its static constructor and calls its getters only when a read materializes a row and when an
/// entity the caller passed in is serialized. Constructing a store (also through DI), a delete, an
/// existence check, a count, a partition delete and a builder partial update construct nothing, and nothing
/// of the entity runs inside a type initializer of the library, so whatever its constructor does (throw,
/// wait, take a lock, count) cannot change, break or delay the store. Whether a type is large-value aware is
/// decided from its declared types by reflection alone. (Review findings SERIALIZER-1, SERIALIZER-L3 and
/// COMPLETENESS-5 of fix wave 2; ruling R3 of fix wave 3.)
/// </summary>
public class PlainEntityUserCodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── models: all plain ──────────────────────────────────────────────────

    public sealed class Address
    {
        private static int constructed;

        public Address() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? City { get; set; }
    }

    /// <summary>A plain entity with a nested object property, the commonest plain shape.</summary>
    public sealed class Customer : Entity
    {
        private static int constructed;
        private static int getterCalls;

        /// <summary>Every instance ever constructed, as an "identity map" or a registration would keep it.</summary>
        public static readonly List<Customer> Registry = [];

        private Address address = new();

        public Customer()
        {
            Interlocked.Increment(ref constructed);
            lock (Registry) Registry.Add(this);
        }

        public static int Constructed => Volatile.Read(ref constructed);

        public static int GetterCalls => Volatile.Read(ref getterCalls);

        public Address Address
        {
            get
            {
                Interlocked.Increment(ref getterCalls);
                return address;
            }
            set => address = value;
        }

        public string? Name { get; set; }
    }

    /// <summary>The same shape, for the store over a mocked <c>TableClient</c>.</summary>
    public sealed class Supplier : Entity
    {
        private static int constructed;

        public Supplier() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public Address Address { get; set; } = new();

        public string? Name { get; set; }
    }

    /// <summary>A plain entity with scalar properties only.</summary>
    public sealed class Counter : Entity
    {
        private static int constructed;

        public Counter() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Name { get; set; }

        public int Value { get; set; }
    }

    public sealed class Detail
    {
        private static int constructed;

        public Detail() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Note { get; set; }
    }

    /// <summary>A plain entity whose nested object is absent on a new instance.</summary>
    public sealed class Ticket : Entity
    {
        private static int constructed;

        public Ticket() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public Detail? Detail { get; set; }
    }

    /// <summary>Process-wide state a host sets once at startup, before it creates its first entity.</summary>
    public static class HostSettings
    {
        public static string? Currency { get; set; }

        public static string? Region { get; set; }
    }

    public sealed class Money
    {
        public decimal Amount { get; set; }
    }

    /// <summary>A plain entity whose static constructor captures host state for its defaults.</summary>
    public sealed class Invoice : Entity
    {
        private static readonly string DefaultCurrency;

        static Invoice() => DefaultCurrency = HostSettings.Currency ?? "UNSET";

        public string Currency { get; set; } = DefaultCurrency;

        public Money Total { get; set; } = new();
    }

    /// <summary>A plain entity whose static constructor fails while the host state is missing.</summary>
    public sealed class Shipment : Entity
    {
        private static readonly string HomeRegion;

        static Shipment() => HomeRegion = HostSettings.Region ?? throw new InvalidOperationException("the host has not set its region yet");

        public string Region { get; set; } = HomeRegion;

        public Money Freight { get; set; } = new();
    }

    /// <summary>A plain entity whose constructor waits for something the host provides later.</summary>
    public sealed class Reservation : Entity
    {
        public static readonly ManualResetEventSlim HostIsUp = new(false);

        public Reservation() => HostIsUp.Wait();

        public Money Deposit { get; set; } = new();
    }

    public sealed class LedgerTotals
    {
        public decimal Sum { get; set; }
    }

    /// <summary>A plain entity whose constructor takes a lock of the application (a registry, an id generator).</summary>
    public sealed class Ledger : Entity
    {
        /// <summary>The application's own lock.</summary>
        public static readonly object Registry = new();

        public static readonly ManualResetEventSlim InConstructor = new(false);

        private static int gaveUp;

        public Ledger()
        {
            InConstructor.Set();
            // An ordinary `lock (Registry) { }` never returns when the cycle forms: the wait is bounded
            // here only so that the test ends.
            if (Monitor.TryEnter(Registry, TimeSpan.FromSeconds(6)))
                Monitor.Exit(Registry);
            else
                Interlocked.Increment(ref gaveUp);
        }

        public static int GaveUp => Volatile.Read(ref gaveUp);

        public LedgerTotals Totals { get; set; } = new();
    }

    public sealed class CountedInner
    {
        public string? Label { get; set; }
    }

    /// <summary>A plain entity with a nested object, counted (TableStorage).</summary>
    public sealed class CountedDoc : Entity
    {
        private static int constructed;

        public CountedDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Name { get; set; }

        public string? Title { get; set; }

        public CountedInner Inner { get; set; } = new();
    }

    /// <summary>A plain entity without any nested-object property, counted the same way (TableStorage).</summary>
    public sealed class CountedFlatDoc : Entity
    {
        private static int constructed;

        public CountedFlatDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Name { get; set; }
    }

    /// <summary>The same, for the in-memory store (a type of its own, so the two tests do not share a counter).</summary>
    public sealed class CountedMemoryDoc : Entity
    {
        private static int constructed;

        public CountedMemoryDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Name { get; set; }
    }

    /// <summary>A plain entity with a nested object, for the operations that return no entity on TableStorage.</summary>
    public sealed class Warehouse : Entity
    {
        private static int constructed;

        public Warehouse() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public Address Address { get; set; } = new();

        public string? Name { get; set; }
    }

    public interface ILocation
    {
    }

    /// <summary>An object with a large-value member that sits in an interface-typed property: it opts its owner into nothing.</summary>
    public sealed class DepotSite : ILocation
    {
        private static int constructed;

        public DepotSite() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>The same shape for the aware entities below (a counter of its own).</summary>
    public sealed class Site : ILocation
    {
        private static int constructed;

        public Site() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Plain by its declared types: its only large-value member is below an interface-typed property. The
    /// library must not construct it (nor the <see cref="Site"/> its constructor creates) to find that out.
    /// </summary>
    public sealed class Depot : Entity
    {
        private static int constructed;
        private static int getterCalls;

        private ILocation location = new DepotSite();

        public Depot() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public static int GetterCalls => Volatile.Read(ref getterCalls);

        public ILocation Location
        {
            get
            {
                Interlocked.Increment(ref getterCalls);
                return location;
            }
            set => location = value;
        }

        public Detail? Detail { get; set; }
    }

    /// <summary>The same entity opted in by the class-level attribute: aware, so the library may construct it once.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInDepot : Entity
    {
        private static int constructed;

        public OptedInDepot() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public ILocation Location { get; set; } = new Site();
    }

    /// <summary>Opted in by the class-level attribute; its constructor fails until the host has started.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class LateDepot : Entity
    {
        public static bool Started { get; set; }

        public LateDepot()
        {
            if (!Started)
                throw new InvalidOperationException("the host has not started yet");
        }

        public ILocation Location { get; set; } = new Site();
    }

    // ── store construction and operations that return no entity ────────────

    [Fact]
    public async Task ConstructingAStore_AndOperationsThatReturnNoEntity_NeverConstructThePlainEntity_AsOn083()
    {
        var store = new InMemoryStorage<Customer>();

        await store.DeleteAsync("p|r", Ct);
        Assert.False(await store.ExistsAsync("p|r", Ct));
        Assert.Equal(0, await store.CountAsync("p", Ct));
        Assert.Equal(0, await store.DeletePartitionAsync("p", Ct));

        // 0.8.3: no Customer is ever constructed here, and no getter is called.
        int registered;
        lock (Customer.Registry) registered = Customer.Registry.Count;
        Assert.True(Customer.Constructed == 0 && Customer.GetterCalls == 0 && registered == 0,
            $"the store constructed {Customer.Constructed} Customer(s), called the Address getter "
            + $"{Customer.GetterCalls} time(s), and the type's registry holds {registered} instance(s) the caller never created.");
    }

    [Fact]
    public async Task TableStorage_ConstructingAStore_NeverConstructsThePlainEntity_AsOn083()
    {
        using var h = new StorageHarness<Supplier>();
        h.SetupDelete();

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.True(Supplier.Constructed == 0, $"new TableStorage<Supplier>() and a delete constructed {Supplier.Constructed} Supplier(s).");
    }

    [Fact]
    public async Task TableStorage_CountPartitionDeleteDeleteAndExists_NeverConstructThePlainEntity_AsOn083()
    {
        using var rig = new CountingRig<Warehouse>();
        await rig.Table.AddEntityAsync(new TableEntity("p", "r") { ["Id"] = "p|r", ["Name"] = "n", ["Address_City"] = "c" }, Ct);
        await rig.Table.AddEntityAsync(new TableEntity("p", "s") { ["Id"] = "p|s", ["Name"] = "n", ["Address_City"] = "c" }, Ct);

        Assert.Equal(2, await rig.Store.CountAsync("p", Ct));
        await rig.Store.DeleteAsync("p|s", Ct);
        Assert.Equal(1, await rig.Store.DeletePartitionAsync("p", Ct));
        Assert.False(await rig.Store.ExistsAsync("p|r", Ct));
        Assert.Equal(0, await rig.NewStore().CountAsync("p", Ct));

        Assert.Empty(rig.Table.StoredRows());
        Assert.True(Warehouse.Constructed == 0, $"constructed {Warehouse.Constructed} Warehouse(s).");
    }

    [Fact]
    public async Task ConstructingAStore_DoesNotWaitForTheConstructorOfThePlainEntity_AsOn083()
    {
        // Constructing the store runs no user code, so it returns at once whatever the entity's constructor does.
        var constructing = Task.Run(() =>
        {
            using var h = new StorageHarness<Reservation>();
        }, Ct);

        var finished = await Task.WhenAny(constructing, Task.Delay(TimeSpan.FromSeconds(3), Ct)) == constructing;
        Reservation.HostIsUp.Set(); // let a blocked initializer go, whatever the outcome
        await constructing;

        Assert.True(finished, "new TableStorage<Reservation>() did not return within 3 seconds: it was waiting in the constructor of the entity.");
    }

    [Fact]
    public void ConstructingAStore_DoesNotRunTheStaticConstructorOfThePlainEntity_AsOn083()
    {
        // The host builds its stores first (as a DI container does when a service that depends on
        // IStorage<Invoice> is resolved) ...
        using var h = new StorageHarness<Invoice>();
        _ = new InMemoryStorage<Invoice>();

        // ... then finishes its own startup, and only then creates entities: their defaults see its settings.
        HostSettings.Currency = "EUR";
        var invoice = new Invoice { Id = "p|r" };

        Assert.Equal("EUR", invoice.Currency);
        Assert.Equal("EUR", invoice.ToTableEntity("p", "r")["Currency"]);
    }

    [Fact]
    public void ConstructingAStore_DoesNotBreakAPlainEntityWhoseStaticConstructorNeedsHostState_AsOn083()
    {
        using var h = new StorageHarness<Shipment>();

        HostSettings.Region = "eu-west";

        // The store ran no static constructor of the type, so the type works once the host has its state.
        var shipment = new Shipment { Id = "p|r" };
        Assert.Equal("eu-west", shipment.Region);
    }

    /// <summary>
    /// Thread B holds the application's lock and then needs the store; thread A is the first to use the
    /// store type. Neither waits for the other: constructing a store runs no code of the entity, so no
    /// entity constructor ever runs inside the store's type initializer.
    /// </summary>
    [Fact]
    public async Task AThreadThatHoldsALockTheEntityConstructorTakes_IsNotBlockedByAnotherThreadsFirstUseOfTheStore_AsOn083()
    {
        using var bHoldsTheLock = new ManualResetEventSlim(false);
        using var aHasItsStore = new ManualResetEventSlim(false);
        var bWaited = TimeSpan.Zero;

        var b = Task.Run(() =>
        {
            lock (Ledger.Registry)
            {
                bHoldsTheLock.Set();
                // Wait until A is inside the entity's constructor or has its store.
                WaitHandle.WaitAny([Ledger.InConstructor.WaitHandle, aHasItsStore.WaitHandle], TimeSpan.FromSeconds(30));
                var clock = Stopwatch.StartNew();
                using var h = new StorageHarness<Ledger>();
                bWaited = clock.Elapsed;
            }
        }, Ct);

        var a = Task.Run(() =>
        {
            bHoldsTheLock.Wait(Ct);
            try
            {
                using var h = new StorageHarness<Ledger>(); // the first use of TableStorage<Ledger>
            }
            finally
            {
                aHasItsStore.Set();
            }
        }, Ct);

        await Task.WhenAll(a, b);

        Assert.True(bWaited < TimeSpan.FromSeconds(2) && Ledger.GaveUp == 0,
            $"The thread that held the application's lock waited {bWaited.TotalSeconds:N1} s for new TableStorage<Ledger>(), until the entity "
            + $"constructor running inside the store's type initializer on the other thread gave up waiting for that lock ({Ledger.GaveUp} time(s)).");
    }

    // ── builder partial updates ────────────────────────────────────────────

    [Fact]
    public async Task BuilderPartialUpdate_OfAPlainEntity_ConstructsNothing_AsOn083()
    {
        var store = new InMemoryStorage<Counter>();
        await store.CreateAsync(new Counter { Id = "p|r", Name = "n", Value = 1 }, Ct);
        var before = Counter.Constructed; // the one entity this test created itself

        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Value, 2), Ct);
        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "m"), Ct);

        Assert.True(before == Counter.Constructed,
            $"two partial updates (of Value and of Name) constructed {Counter.Constructed - before} Counter(s).");
    }

    [Fact]
    public async Task BuilderPartialUpdate_OfANestedPath_ConstructsNeitherTheEntityNorTheNestedType_AsOn083()
    {
        var store = new InMemoryStorage<Ticket>();
        await store.CreateAsync(new Ticket { Id = "p|r" }, Ct);
        var entities = Ticket.Constructed;
        var details = Detail.Constructed;

        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Detail!.Note, "patched"), Ct);

        Assert.True(entities == Ticket.Constructed && details == Detail.Constructed,
            $"the partial update constructed {Ticket.Constructed - entities} Ticket(s) and {Detail.Constructed - details} Detail(s).");
    }

    [Fact]
    public async Task TableStorage_BuilderUpdates_OfAPlainEntityWithANestedObject_ConstructNothing_AsOn083()
    {
        using var rig = new CountingRig<CountedDoc>();
        await rig.Table.AddEntityAsync(new TableEntity("p", "r") { ["Id"] = "p|r", ["Name"] = "n" }, Ct);
        var afterTheStore = CountedDoc.Constructed;

        await rig.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Inner.Label, "x"), Ct);
        await rig.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "y"), Ct);
        var afterTwoBuilderUpdates = CountedDoc.Constructed;

        Assert.Equal((0, 0), (afterTheStore, afterTwoBuilderUpdates));
        var row = rig.Table.Stored("p", "r")!;
        Assert.Equal("x", row["Inner_Label"]);
        Assert.Equal("y", row["Name"]);
    }

    [Fact]
    public async Task TableStorage_BuilderUpdate_OfAScalarOnlyPlainEntity_ConstructsNothing_AsOn083()
    {
        using var rig = new CountingRig<CountedFlatDoc>();
        await rig.Table.AddEntityAsync(new TableEntity("p", "r") { ["Id"] = "p|r", ["Name"] = "n" }, Ct);

        await rig.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "y"), Ct);

        Assert.Equal(0, CountedFlatDoc.Constructed);
    }

    [Fact]
    public async Task InMemoryBuilderUpdate_OfAPlainEntity_ConstructsNothing_AsOn083()
    {
        var store = new InMemoryStorage<CountedMemoryDoc>();
        await store.CreateAsync(new CountedMemoryDoc { Id = "p|r", Name = "n" }, Ct);
        var before = CountedMemoryDoc.Constructed;

        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "y"), Ct);

        Assert.Equal(0, CountedMemoryDoc.Constructed - before);
    }

    // ── what the library asks about a type ─────────────────────────────────

    [Fact]
    public void DecidingAboutAPlainType_ConstructsNothing_AndCallsNoGetter()
    {
        // Plain by its declared types although the object its constructor creates has a large-value member.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(Depot)));
        Assert.False(LargeValueTable.IsLargeValueAware(typeof(Depot)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(Depot)));
        Assert.False(TypeMetadataCache.IsLargeValueOnRead(typeof(Depot), "Location_Notes"));
        Assert.False(TypeMetadataCache.IsLargeValueOnRead(typeof(Depot), "Detail_Note"));
        _ = TypeMetadataCache.ReadTarget(typeof(Depot), "Detail_Note");
        _ = TypeMetadataCache.ReadsBack(typeof(Depot), "Detail_Note");
        _ = TypeMetadataCache.ReadsBack(typeof(Depot), "Location_Notes");
        Assert.Equal("v", TableEntitySerializer.FlattenProperty(typeof(Depot), "Detail_Note", "v")["Detail_Note"]);
        Assert.Equal(7, TableEntitySerializer.FlattenProperty(typeof(Depot), "Location_Count", 7)["Location_Count"]);
        _ = new InMemoryStorage<Depot>();
        using (new StorageHarness<Depot>())
        {
        }

        Assert.True(Depot.Constructed == 0 && Depot.GetterCalls == 0 && DepotSite.Constructed == 0 && Detail.Constructed == 0,
            $"constructed {Depot.Constructed} Depot(s), {DepotSite.Constructed} DepotSite(s), {Detail.Constructed} Detail(s); "
            + $"called the Location getter {Depot.GetterCalls} time(s).");
    }

    [Fact]
    public void ClassLevelAttribute_OnTheEntity_MakesItAware_WithoutConstructingIt()
    {
        // The class-level attribute covers none of the entity's own properties: it is the opt-in for a
        // large value below a property whose type is known at run time.
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(OptedInDepot)));
        Assert.True(LargeValueTable.IsLargeValueAware(typeof(OptedInDepot)));
        _ = new InMemoryStorage<OptedInDepot>();
        Assert.Equal(0, OptedInDepot.Constructed);

        // The columns of the objects its constructor creates are learnt from one instance, once.
        Assert.Equal(["Location_Notes"], TypeMetadataCache.LargeValueColumns(typeof(OptedInDepot)));
        Assert.Equal(["Location_Notes"], TypeMetadataCache.LargeValueColumns(typeof(OptedInDepot)));
        Assert.Equal(1, OptedInDepot.Constructed);
    }

    [Fact]
    public void AwareType_WhoseConstructorFailsAtFirstUse_StaysAware_AndListsItsColumnsOnceItCanBeConstructed()
    {
        LateDepot.Started = false;
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(LateDepot)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(LateDepot)));

        LateDepot.Started = true;

        // The failure is not remembered: the same question now gets the full answer.
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(LateDepot)));
        Assert.Equal(["Location_Notes"], TypeMetadataCache.LargeValueColumns(typeof(LateDepot)));
    }

    /// <summary>A <see cref="TableStorage{T}"/> over the suite's in-memory <see cref="RacingTableClient"/>, counting its requests.</summary>
    private sealed class CountingRig<T> : IDisposable where T : class, IEntity, new()
    {
        private readonly List<MemoryCache> caches = [];

        public CountingRig() => Store = NewStore();

        public RacingTableClient Table { get; } = new();

        public TableStorage<T> Store { get; }

        public TableStorage<T> NewStore()
        {
            var service = Substitute.For<TableServiceClient>();
            service.GetTableClient(typeof(T).Name).Returns(Table);
            var cache = new MemoryCache(new MemoryCacheOptions());
            caches.Add(cache);
            return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, new UnifiedTableStorageOptions());
        }

        public void Dispose()
        {
            foreach (var cache in caches)
                cache.Dispose();
        }
    }
}
