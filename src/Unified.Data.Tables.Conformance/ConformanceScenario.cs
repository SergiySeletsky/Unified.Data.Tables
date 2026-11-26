namespace Unified.Data.Tables.Conformance;

/// <summary>
/// One rule of the storage contract, checked against a store: a name, the tier it belongs to, and a run.
/// Scenarios are listed by <see cref="ConformanceSuite"/>; each builds the stores it needs from the
/// factory, so they are independent of each other and of the order they run in.
/// </summary>
public sealed class ConformanceScenario
{
    private readonly Func<ConformanceContext, Task> body;

    internal ConformanceScenario(
        ConformanceTier tier, string name, string description, IReadOnlyList<string> leniencies, Func<ConformanceContext, Task> body)
    {
        Tier = tier;
        Name = name;
        Description = description;
        Leniencies = leniencies;
        this.body = body;
    }

    /// <summary>The tier the scenario belongs to.</summary>
    public ConformanceTier Tier { get; }

    /// <summary>
    /// The scenario's name, unique in the kit and stable across releases: the tier, the member or rule,
    /// and the case (<c>Logical.Update.StaleETag_ThrowsConflict_AndKeepsTheRow</c>). Also the name of the leniency that covers
    /// this scenario alone.
    /// </summary>
    public string Name { get; }

    /// <summary>What the contract says, in one sentence.</summary>
    public string Description { get; }

    /// <summary>
    /// The <see cref="ConformanceLeniencies"/> names that cover this scenario, besides its own
    /// <see cref="Name"/>.
    /// </summary>
    public IReadOnlyList<string> Leniencies { get; }

    /// <summary>
    /// Runs the scenario against a store built by <paramref name="factory"/>.
    /// </summary>
    /// <param name="factory">Describes the store under test.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>
    /// A result that is <see cref="ConformanceOutcome.Passed"/>, <see cref="ConformanceOutcome.NotSupported"/>
    /// (the store does not expose what the scenario is about) or <see cref="ConformanceOutcome.Lenient"/>
    /// (the store differs and the factory declared a leniency that covers the scenario). Never
    /// <see cref="ConformanceOutcome.Failed"/>: that is thrown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="ConformanceAssertionException">
    /// The store does not do what the contract says. The message names the store, the scenario, what was
    /// expected and what happened.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<ConformanceResult> RunAsync(IConformanceStoreFactory factory, CancellationToken ct = default)
    {
        var result = await TryRunAsync(factory, ct).ConfigureAwait(false);
        if (result.Outcome == ConformanceOutcome.Failed)
            throw result.Exception!;
        return result;
    }

    /// <summary>
    /// Runs the scenario like <see cref="RunAsync"/>, but returns a failure as a
    /// <see cref="ConformanceOutcome.Failed"/> result instead of throwing it.
    /// </summary>
    /// <param name="factory">Describes the store under test.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The result, whatever the outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<ConformanceResult> TryRunAsync(IConformanceStoreFactory factory, CancellationToken ct = default)
    {
        Arg.NotNull(factory, nameof(factory));

        Exception thrown;
        try
        {
            var context = new ConformanceContext(factory, ct);
            try
            {
                await body(context).ConfigureAwait(false);
            }
            finally
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            return new ConformanceResult(this, ConformanceOutcome.Passed, null, null);
        }
        catch (NotSupportedSignal signal)
        {
            return new ConformanceResult(this, ConformanceOutcome.NotSupported, signal.Message, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // whatever the store throws where the contract expects none is the finding
        catch (Exception ex)
#pragma warning restore CA1031
        {
            thrown = ex;
        }

        var failure = thrown is ConformanceAssertionException assertion
            ? new ConformanceAssertionException($"[{factory.Name}] {Name}: {assertion.Message}", assertion.InnerException)
            : new ConformanceAssertionException(
                $"[{factory.Name}] {Name}: the store threw {thrown.GetType().FullName} where the contract expects none: {thrown.Message}", thrown);

        var declared = factory.Leniencies;
        if (declared is not null)
        {
            foreach (var leniency in declared)
            {
                if (string.Equals(leniency, Name, StringComparison.Ordinal) || Covers(leniency))
                    return new ConformanceResult(this, ConformanceOutcome.Lenient, $"(leniency '{leniency}') {failure.Message}", failure);
            }
        }
        return new ConformanceResult(this, ConformanceOutcome.Failed, failure.Message, failure);
    }

    internal bool Covers(string leniency)
    {
        foreach (var own in Leniencies)
        {
            if (string.Equals(own, leniency, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The scenario's <see cref="Name"/>.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}

/// <summary>Thrown inside a scenario to end it as <see cref="ConformanceOutcome.NotSupported"/>.</summary>
internal sealed class NotSupportedSignal(string message) : Exception(message);

/// <summary>What a running scenario works with: the factory, the token, and the stores it has built.</summary>
internal sealed class ConformanceContext(IConformanceStoreFactory factory, CancellationToken ct)
{
    private readonly List<IAsyncDisposable> owned = [];

    public IConformanceStoreFactory Factory { get; } = factory;

    public CancellationToken Ct { get; } = ct;

    /// <summary>A partition key no other scenario uses, already in normalized form.</summary>
    public string Pk { get; } = "p" + Guid.NewGuid().ToString("N").Remove(12);

    /// <summary>The id of the row at <paramref name="rowKey"/> in <see cref="Pk"/>.</summary>
    public string Id(string rowKey) => Pk + "|" + rowKey;

    public Exception NotSupported(string why) => new NotSupportedSignal(why);

    public async Task<ConformanceStore<T>> StoreAsync<T>(ConformanceStoreSettings? settings = null) where T : class, IEntity, new()
    {
        ConformanceStore<T> store;
        try
        {
            store = await Factory.CreateStoreAsync<T>(settings ?? ConformanceStoreSettings.Default, Ct).ConfigureAwait(false)
                ?? throw new ConformanceAssertionException($"the factory returned no store of {typeof(T).Name}");
        }
        catch (NotSupportedException ex)
        {
            throw NotSupported($"the factory cannot build this store of {typeof(T).Name}: {ex.Message}");
        }
        owned.Add(store);
        return store;
    }

    public async Task<IStorage<ConformanceItem>> ItemsAsync(ConformanceStoreSettings? settings = null) =>
        (await StoreAsync<ConformanceItem>(settings).ConfigureAwait(false)).Storage;

    public async Task<IPolymorphicStorage<ConformanceEvent>> EventsAsync()
    {
        ConformancePolymorphicStore<ConformanceEvent> store;
        try
        {
            store = await Factory.CreatePolymorphicStoreAsync<ConformanceEvent>(Ct).ConfigureAwait(false)
                ?? throw new ConformanceAssertionException("the factory returned no polymorphic store");
        }
        catch (NotSupportedException ex)
        {
            throw NotSupported($"the factory has no polymorphic store: {ex.Message}");
        }
        owned.Add(store);
        return store.Storage;
    }

    /// <summary>The store's capability, or the end of the scenario as "not supported".</summary>
    public TCap Capability<T, TCap>(IStorage<T> storage) where T : class, IEntity, new() where TCap : class =>
        storage.TryGetCapability<T, TCap>(out var capability)
            ? capability
            : throw NotSupported($"the store exposes no {typeof(TCap).Name}");

    public TCap Capability<TBase, TCap>(IPolymorphicStorage<TBase> storage) where TBase : class where TCap : class =>
        storage.TryGetCapability<TBase, TCap>(out var capability)
            ? capability
            : throw NotSupported($"the store exposes no {typeof(TCap).Name}");

    public async ValueTask DisposeAsync()
    {
        // Newest first; a clean-up that fails must not keep the others from running.
        Exception? first = null;
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            try
            {
                await owned[i].DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // collected and rethrown below
            catch (Exception ex)
#pragma warning restore CA1031
            {
                first ??= ex;
            }
        }
        owned.Clear();
        if (first is not null)
            throw new InvalidOperationException("The store factory's clean-up failed: " + first.Message, first);
    }
}
