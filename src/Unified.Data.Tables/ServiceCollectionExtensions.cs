using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// DI helpers for wiring the unified Azure Table Storage layer.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// The DI key of the <see cref="BlobServiceClient"/> blob offload uses. The library registers it when
    /// the registered options set <see cref="UnifiedTableStorageOptions.BlobOffload"/>, and, beside options
    /// the host registered through a factory or a type (which cannot be read at registration), whenever a
    /// later call has a client to offer (a connection-string or credential overload, or its own
    /// <see cref="BlobOffloadOptions"/> naming one), also when those options set no offload. On
    /// a container that follows Microsoft's keyed-service rules the host's own (unkeyed)
    /// <see cref="BlobServiceClient"/> registration is never taken or shadowed; on Lamar and the DryIoc
    /// adapter that last case changes what the host's unkeyed client resolves to (see the README).
    /// </summary>
    internal const string OffloadBlobServiceClientKey = "Unified.Data.Tables.BlobOffload";

    /// <summary>
    /// Registers the in-memory cache, the configured <see cref="UnifiedTableStorageOptions"/>, and
    /// the open-generic <see cref="IStorage{T}"/> → <see cref="TableStorage{T}"/> mapping. The
    /// caller must separately register a <see cref="TableServiceClient"/> (and, if protected
    /// properties are used, an <see cref="IProtectedPropertyAuthorizer"/>). With
    /// <see cref="UnifiedTableStorageOptions.BlobOffload"/> set, the blob client is
    /// <see cref="BlobOffloadOptions.ServiceClient"/>, or the host's registered <see cref="BlobServiceClient"/>
    /// when <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/> is true; without one, resolving a
    /// store of a type with <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> members
    /// throws <see cref="InvalidOperationException"/> (other types resolve as usual).
    /// </summary>
    /// <remarks>
    /// The first call's options are the registered ones; the options of a later call (of any overload) are
    /// not used. The blob client every store uses follows from the registered (unkeyed) options, whoever
    /// registered them (a call of the library, or the host as an instance, a factory or a type): their
    /// <see cref="BlobOffloadOptions.ServiceClient"/>; else, when they set
    /// <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>, the host's registered client and no other
    /// one; only when they choose neither, the client a call supplied. A later call therefore still does one
    /// thing: when the registered options set <see cref="UnifiedTableStorageOptions.BlobOffload"/>, choose no
    /// client, and no call has supplied one yet (for example because the first call was this overload,
    /// which derives none), it supplies that client if it can, by the same rule as a first call: its own
    /// <see cref="BlobOffloadOptions.ServiceClient"/>, else the host's registered client when it sets
    /// <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>, else the client its connection string or
    /// endpoint derives. A client an earlier call supplied is never replaced, and the registered options
    /// object is not changed. Options registered through a factory or a type are read when the container
    /// builds them; beside such options a later call offers its client only when it has one to offer, so
    /// options of that kind that set <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/> get the
    /// host's client only after a call that offers a client of its own (a connection-string or credential
    /// overload, or a call that sets <see cref="UnifiedTableStorageOptions.BlobOffload"/> with a
    /// <see cref="BlobOffloadOptions.ServiceClient"/> or <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>);
    /// otherwise a large-value store fails to resolve with <see cref="InvalidOperationException"/>.
    /// </remarks>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, Action<UnifiedTableStorageOptions>? configure = null) =>
        AddCore(services, configure, derivedBlobClient: null);

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> built from <paramref name="connectionString"/> alongside
    /// the cache, options, and the open-generic <see cref="IStorage{T}"/> mapping. A client already
    /// registered is kept. When <see cref="UnifiedTableStorageOptions.BlobOffload"/> is set (and names no
    /// <see cref="BlobOffloadOptions.ServiceClient"/> and does not set
    /// <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>), blob offload uses a
    /// <see cref="BlobServiceClient"/> built from the same string if it has a blob endpoint (a
    /// <c>BlobEndpoint</c>, <c>UseDevelopmentStorage=true</c>, or an <c>AccountName</c> without a table
    /// endpoint or on an Azure Storage table host). That client is private to the library: nothing is
    /// registered as the <see cref="BlobServiceClient"/> service. When options already registered (by an
    /// earlier call or by the host) set <see cref="UnifiedTableStorageOptions.BlobOffload"/>, choose no blob
    /// client and no call has supplied one, this call supplies it the same way; a client the registered
    /// options choose is never replaced (see the remarks on
    /// <see cref="AddUnifiedTableStorage(IServiceCollection, Action{UnifiedTableStorageOptions}?)"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Blob offload is configured, this call is the one to supply its client (the registered options, this
    /// call's own or an instance registered before it, ask for blob offload, choose no client, and no call
    /// has supplied one), and the string names a blob endpoint the Blobs SDK rejects.
    /// </exception>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, string connectionString,
        Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new TableServiceClient(connectionString));
        return AddCore(services, configure,
            () => HasBlobEndpoint(connectionString) ? BlobClientFrom(connectionString) : null);
    }

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> authenticated with a <see cref="TokenCredential"/>
    /// (e.g. a managed identity via <c>DefaultAzureCredential</c>) alongside the cache, options,
    /// and the open-generic <see cref="IStorage{T}"/> mapping. A client already registered is kept. When
    /// <see cref="UnifiedTableStorageOptions.BlobOffload"/> is set (and names no
    /// <see cref="BlobOffloadOptions.ServiceClient"/> and does not set
    /// <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>) and the endpoint's host is an Azure
    /// Storage table host (<c>{account}.table.core.{suffix}</c>, in any cloud), blob offload uses a
    /// <see cref="BlobServiceClient"/> for the same account (<c>.blob.core.</c>) and credential, private to
    /// the library. For any other host (Cosmos DB Table API, an emulator, a custom domain) supply the blob
    /// client through <see cref="BlobOffloadOptions.ServiceClient"/>. When options already registered (by an
    /// earlier call or by the host) set <see cref="UnifiedTableStorageOptions.BlobOffload"/>, choose no blob
    /// client and no call has supplied one, this call supplies it the same way; a client the registered
    /// options choose is never replaced (see the remarks on
    /// <see cref="AddUnifiedTableStorage(IServiceCollection, Action{UnifiedTableStorageOptions}?)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The table endpoint, e.g. <c>https://{account}.table.core.windows.net</c>.</param>
    /// <param name="credential">The Azure credential to authenticate with.</param>
    /// <param name="configure">Optional options configuration.</param>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, Uri endpoint, TokenCredential credential,
        Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        services.TryAddSingleton(_ => new TableServiceClient(endpoint, credential));
        return AddCore(services, configure,
            () => BlobEndpointFor(endpoint) is { } blobEndpoint ? new BlobServiceClient(blobEndpoint, credential) : null);
    }

    private static IServiceCollection AddCore(
        IServiceCollection services, Action<UnifiedTableStorageOptions>? configure, Func<BlobServiceClient?>? derivedBlobClient)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new UnifiedTableStorageOptions();
        configure?.Invoke(options);

        // The serializer is static, so its oversized-cell policy is process-wide state. Mirror
        // TryAddSingleton's first-registration-wins semantics: only the call whose options actually
        // register also applies the policy, so a later bare AddUnifiedTableStorage() from another
        // module cannot silently reset an explicitly configured policy. Only an unkeyed registration
        // counts, as for TryAddSingleton: a keyed one of the host is never what a store receives.
        var optionsAlreadyRegistered = services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(UnifiedTableStorageOptions));
        if (!optionsAlreadyRegistered)
        {
            TableEntitySerializer.OversizedCellPolicy = options.OversizedCells;
            // The offload client follows the options that register: only when blob offload is
            // configured, and only under the library's own key.
            if (options.BlobOffload is { } offload)
                RegisterOffloadClient(services, SuppliedClient(offload, derivedBlobClient));
        }
        else if (!OffloadClientRegistered(services))
        {
            // The registered options stay as they are and still decide everything else, the blob client
            // first: the order of choice applies to THEM, whoever registered them. Only when they ask for
            // blob offload and choose no client does this call supply one, by its own rule (dropping it
            // left every large-value store unresolvable).
            var registered = RegisteredOptions(services);
            if (registered?.ImplementationInstance is UnifiedTableStorageOptions known)
            {
                // Options registered as an instance can be read now. Their ServiceClient needs no
                // registration (TableStorage<T> takes it from them); UseRegisteredServiceClient means the
                // host's client and no other one, so this call derives nothing.
                if (known.BlobOffload is { ServiceClient: null } offload)
                    RegisterOffloadClient(services, offload.UseRegisteredServiceClient
                        ? HostsClient
                        : SuppliedClient(options.BlobOffload, derivedBlobClient));
            }
            else if (registered is not null)
            {
                // A factory or a type: the options cannot be read before the container builds them, so
                // the choice is made then (ChooseOffloadClient). This call only offers its own client;
                // one that cannot be built is not offered, as those options may never need it.
                RegisterOffloadClient(services, SuppliedClientIfItCanBeBuilt(options.BlobOffload, derivedBlobClient));
            }
        }

        services.AddMemoryCache();
        services.TryAddSingleton(options);
        services.TryAddSingleton(typeof(IStorage<>), typeof(TableStorage<>));
        return services;
    }

    // The registration of the options TableStorage<T> will be given: the last unkeyed one, as the
    // container resolves it. Its instance is null when the host registered them as a type or a factory,
    // which cannot be read before the container is built.
    private static ServiceDescriptor? RegisteredOptions(IServiceCollection services) =>
        services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == typeof(UnifiedTableStorageOptions));

    // Whether a call has already registered the offload client (an instance, or the factory that
    // resolves the host's client): that choice is kept, so a later call never replaces it.
    private static bool OffloadClientRegistered(IServiceCollection services) =>
        services.Any(d => d.IsKeyedService && d.ServiceType == typeof(BlobServiceClient)
            && OffloadBlobServiceClientKey.Equals(d.ServiceKey));

    // What one call supplies, by the rule, in order: its BlobOffloadOptions.ServiceClient; the host's
    // registered BlobServiceClient when it sets UseRegisteredServiceClient (resolved lazily, so the host
    // may register it after this call); the client its overload derives from its connection string or
    // endpoint (built now, so a malformed blob endpoint fails this call). Null when it supplies none. A
    // call whose own options set no BlobOffload (offload is null) can only supply the derived client.
    private static Func<IServiceProvider, BlobServiceClient?>? SuppliedClient(
        BlobOffloadOptions? offload, Func<BlobServiceClient?>? derivedBlobClient)
    {
        if (offload?.ServiceClient is { } explicitClient)
            return _ => explicitClient;
        if (offload is { UseRegisteredServiceClient: true })
            return HostsClient;
        return derivedBlobClient?.Invoke() is { } derived ? _ => derived : null;
    }

    // The same, for a call beside options that cannot be read yet: a client its string cannot build is
    // not offered (and fails nothing), since those options may name their own client or none at all.
    private static Func<IServiceProvider, BlobServiceClient?>? SuppliedClientIfItCanBeBuilt(
        BlobOffloadOptions? offload, Func<BlobServiceClient?>? derivedBlobClient)
    {
        try
        {
            return SuppliedClient(offload, derivedBlobClient);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static BlobServiceClient? HostsClient(IServiceProvider provider) => provider.GetService<BlobServiceClient>();

    // Registers the offload client under the library's key, once: the first client a call supplies is
    // kept. Nothing is ever registered as the unkeyed BlobServiceClient. The order of choice is applied
    // when the container builds the client, to the options every store receives (whoever registered
    // them, as an instance, a factory or a type): their ServiceClient; the host's client when they set
    // UseRegisteredServiceClient, and then no other one; else the client a call supplied. A null result
    // leaves TableStorage<T> without one; only a large-value-aware T refuses that.
    private static void RegisterOffloadClient(IServiceCollection services, Func<IServiceProvider, BlobServiceClient?>? supplied)
    {
        if (supplied is null)
            return;
        services.TryAddKeyedSingleton<BlobServiceClient>(OffloadBlobServiceClientKey,
            (sp, _) => ChooseOffloadClient(sp, supplied)!);
    }

    private static BlobServiceClient? ChooseOffloadClient(IServiceProvider provider, Func<IServiceProvider, BlobServiceClient?> supplied)
    {
        var offload = provider.GetService<UnifiedTableStorageOptions>()?.BlobOffload;
        if (offload?.ServiceClient is { } named)
            return named;
        if (offload is { UseRegisteredServiceClient: true })
            return HostsClient(provider);
        return supplied(provider);
    }

    // True for an Azure Storage connection string that names or implies a blob endpoint: an explicit
    // BlobEndpoint, the emulator shortcut, or an account name whose table endpoint (if any) is an Azure
    // Storage table host, from which the SDK derives {account}.blob.core.{suffix}. A table-only string (an
    // endpoint and a SAS), a Cosmos DB Table API one or a custom table domain has none. Parsing only.
    internal static bool HasBlobEndpoint(string connectionString)
    {
        string? tableEndpoint = null;
        var accountName = false;
        foreach (var setting in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = setting[..eq].Trim();
            var value = setting[(eq + 1)..].Trim();
            if (key.Equals("BlobEndpoint", StringComparison.OrdinalIgnoreCase) && value.Length > 0) return true;
            if (key.Equals("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
                && value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.Equals("AccountName", StringComparison.OrdinalIgnoreCase) && value.Length > 0) accountName = true;
            else if (key.Equals("TableEndpoint", StringComparison.OrdinalIgnoreCase)) tableEndpoint = value;
        }
        return accountName
            && (tableEndpoint is null
                || (Uri.TryCreate(tableEndpoint, UriKind.Absolute, out var table) && IsAzureStorageTableHost(table.Host)));
    }

    // Blob offload is configured and the string names a blob endpoint: a string the SDK refuses is a
    // configuration error worth failing registration over, not a reason to drop offload silently.
    private static BlobServiceClient BlobClientFrom(string connectionString)
    {
        try
        {
            return new BlobServiceClient(connectionString);
        }
#pragma warning disable CA1031 // the SDK throws several types (even NullReferenceException) for a malformed string; all are rethrown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ArgumentException(
                "UnifiedTableStorageOptions.BlobOffload is configured, but no BlobServiceClient can be built from the connection string's blob endpoint ("
                + ex.Message + "). Supply one through BlobOffloadOptions.ServiceClient.", nameof(connectionString), ex);
        }
    }

    private const string StorageTableHostPart = ".table.core.";

    // An Azure Storage table host in any cloud: {account}.table.core.windows.net, .core.chinacloudapi.cn,
    // .core.usgovcloudapi.net, ... A Cosmos DB Table API host ({account}.table.cosmos.azure.com) is not one.
    private static bool IsAzureStorageTableHost(string host) =>
        host.Contains(StorageTableHostPart, StringComparison.OrdinalIgnoreCase);

    // https://{account}.table.core.{suffix} -> https://{account}.blob.core.{suffix}; null for any other
    // host (Cosmos DB, an emulator, a custom domain), whose blob endpoint cannot be derived.
    private static Uri? BlobEndpointFor(Uri tableEndpoint)
    {
        var host = tableEndpoint.Host;
        var at = host.IndexOf(StorageTableHostPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        var builder = new UriBuilder(tableEndpoint)
        {
            Host = host[..at] + ".blob.core." + host[(at + StorageTableHostPart.Length)..],
        };
        return builder.Uri;
    }

    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> over <paramref name="tableName"/>,
    /// KEYED by that table name. Requires a registered <see cref="TableServiceClient"/> — call an
    /// <c>AddUnifiedTableStorage</c> overload first.
    /// </summary>
    /// <remarks>
    /// Keyed rather than open-generic because one base type routinely addresses several tables (an
    /// event base is both the state-event store and the transaction store), and an open-generic
    /// registration can only bind one. Resolve with
    /// <c>[FromKeyedServices("TableName")] IPolymorphicStorage&lt;TBase&gt;</c>. A key attribute needs
    /// a compile-time constant, so a table name only known at runtime must be resolved imperatively
    /// with <c>IServiceProvider.GetRequiredKeyedService&lt;IPolymorphicStorage&lt;TBase&gt;&gt;(name)</c>.
    /// <para>
    /// <b>One token namespace for the whole process.</b> Unless <paramref name="configure"/> is
    /// supplied, every polymorphic store resolves the single registered
    /// <see cref="UnifiedTableStorageOptions"/> and therefore shares ONE
    /// <see cref="ITypeDiscriminator"/> — tokens are global, not per-table. So
    /// <c>new TypeDiscriminatorMap().MapAssignableTo&lt;IEvent&gt;(asm).MapAssignableTo&lt;ICommand&gt;(asm)</c>
    /// throws at startup on any simple name the two hierarchies share, and two different tables
    /// cannot each map their own <c>Created</c>. Give the colliding types explicit tokens, pass a
    /// naming function, or hand one table its own options through <paramref name="configure"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The table this store owns; also the DI key.</param>
    /// <param name="configure">
    /// Options for this table only — chiefly its <see cref="ITypeDiscriminator"/>. Null resolves the
    /// registered <see cref="UnifiedTableStorageOptions"/>, or the defaults when none is registered.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedPolymorphicTable<TBase>(
        this IServiceCollection services,
        string tableName,
        Action<UnifiedTableStorageOptions>? configure = null)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        // A per-table options instance rather than a registration: the discriminator is the only
        // setting a polymorphic store reads, and one table's type map has no business becoming the
        // process-wide default that every IStorage<T> also resolves.
        UnifiedTableStorageOptions? local = null;
        if (configure is not null)
        {
            local = new UnifiedTableStorageOptions();
            configure(local);
        }

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new PolymorphicTableStorage<TBase>(
                sp.GetRequiredService<TableServiceClient>(),
                tableName,
                sp.GetRequiredService<ILogger<PolymorphicTableStorage<TBase>>>(),
                local ?? sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
}
