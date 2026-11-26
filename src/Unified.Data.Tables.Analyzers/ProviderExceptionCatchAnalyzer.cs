using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Unified.Data.Tables.Analyzers;

/// <summary>
/// UDT1001, an upgrade hint for 0.10.0: a <c>catch</c> of <c>Azure.RequestFailedException</c> (or a type
/// derived from it, such as <c>TableTransactionFailedException</c>) whose try block calls a storage
/// member of Unified.Data.Tables and no member of an Azure SDK assembly.
/// </summary>
/// <remarks>
/// Since 0.10.0 no storage member lets a provider exception out: a failure is a
/// <c>StorageException</c> (<c>EntityNotFoundException</c>, <c>TableNotFoundException</c>,
/// <c>DuplicateKeyException</c>, <c>ConcurrencyConflictException</c>,
/// <c>StorageRequestRejectedException</c>, <c>TransientStorageException</c>) and the provider's exception
/// is its <c>InnerException</c>. Such a catch still compiles and silently catches nothing, which is why
/// it is pointed at.
/// <para>
/// The severity is <see cref="DiagnosticSeverity.Info"/> on purpose and must stay so: consumers build
/// with warnings as errors, and a hint about code that may be right must not fail a build. For the same
/// reason a try block that also calls the Azure SDK directly (a <c>TableClient</c>, a
/// <c>BlobContainerClient</c>) is left alone: its catch is still needed for that call.
/// </para>
/// <para>
/// What counts as a storage member: a member of <c>IStorage&lt;T&gt;</c>,
/// <c>IPolymorphicStorage&lt;TBase&gt;</c> or one of the 0.10.0 side interfaces, a member of a type of a
/// Unified.Data.Tables assembly that implements one of the two (<c>TableStorage&lt;T&gt;</c>), and an
/// extension method of a Unified.Data.Tables assembly over one of the two (<c>MutateAsync</c>). A call
/// inside a lambda or a local function of the try block is not counted: it runs where it is invoked.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProviderExceptionCatchAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The id of the diagnostic.</summary>
    public const string DiagnosticId = "UDT1001";

    private const string LibraryAssembly = "Unified.Data.Tables";
    private const string LibraryNamespace = "Unified.Data.Tables";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "A catch of the Azure SDK's exception around a Unified.Data.Tables storage call catches nothing since 0.10.0",
        messageFormat: "'{1}' no longer throws '{0}': since Unified.Data.Tables 0.10.0 a storage failure is a StorageException "
            + "(EntityNotFoundException, DuplicateKeyException, ConcurrencyConflictException, TransientStorageException, ...) "
            + "and the provider exception is its InnerException. Catch the neutral type.",
        category: "Upgrade",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Storage members of Unified.Data.Tables throw provider-neutral exceptions since 0.10.0. A catch of "
            + "Azure.RequestFailedException around such a call compiles and never runs. Catch StorageException or the "
            + "specific neutral type; read the provider's status and error code from InnerException or ProviderErrorCode.",
        helpLinkUri: "https://github.com/SergiySeletsky/Unified.Data.Tables#upgrading-from-09x");

    // The storage contracts, by metadata name in the library's namespace.
    private static readonly ImmutableHashSet<string> StoreInterfaces =
        ImmutableHashSet.Create("IStorage`1", "IPolymorphicStorage`1");

    private static readonly ImmutableHashSet<string> SideInterfaces =
        ImmutableHashSet.Create("IConditionalDelete`1", "IAtomicBatch`1", "IAtomicInsert`1", "IRawTable", "ITableStore", "IOrphanBlobSweeper");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Without the Azure SDK's exception in the compilation there is no such catch to find.
            var providerException = start.Compilation.GetTypeByMetadataName("Azure.RequestFailedException");
            if (providerException is null)
                return;

            start.RegisterSyntaxNodeAction(node => AnalyzeCatch(node, providerException), SyntaxKind.CatchClause);
        });
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context, INamedTypeSymbol providerException)
    {
        var clause = (CatchClauseSyntax)context.Node;
        if (clause.Declaration is not { } declaration || clause.Parent is not TryStatementSyntax statement)
            return;

        var caught = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;
        if (!IsOrDerivesFrom(caught, providerException))
            return;

        string? storageCall = null;
        foreach (var node in statement.Block.DescendantNodes(descendIntoChildren: n => !IsDeferredCode(n)))
        {
            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
                continue;
            if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IMethodSymbol method)
                continue;

            // A direct call into an Azure SDK assembly: the catch is (also) for that call. Say nothing.
            if (IsAzureSdk(method.ContainingAssembly))
                return;

            if (storageCall is null && IsStorageMember(method))
                storageCall = method.Name;
        }

        if (storageCall is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            declaration.Type.GetLocation(),
            caught!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            storageCall));
    }

    // Code that is declared in the try block and runs elsewhere.
    private static bool IsDeferredCode(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    private static bool IsOrDerivesFrom(ITypeSymbol? type, INamedTypeSymbol target)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }

        return false;
    }

    private static bool IsAzureSdk(IAssemblySymbol? assembly) =>
        assembly is not null
        && (assembly.Name.StartsWith("Azure.", StringComparison.Ordinal) || assembly.Name.StartsWith("Microsoft.Azure.", StringComparison.Ordinal));

    private static bool IsLibrary(IAssemblySymbol? assembly) =>
        assembly is not null
        && (assembly.Name == LibraryAssembly || assembly.Name.StartsWith(LibraryAssembly + ".", StringComparison.Ordinal));

    private static bool IsLibraryInterface(ITypeSymbol? type, ImmutableHashSet<string> names) =>
        type is INamedTypeSymbol named
        && names.Contains(named.OriginalDefinition.MetadataName)
        && named.ContainingNamespace?.ToDisplayString() == LibraryNamespace
        && IsLibrary(named.ContainingAssembly);

    private static bool IsStore(ITypeSymbol? type)
    {
        if (type is null)
            return false;
        if (IsLibraryInterface(type, StoreInterfaces))
            return true;

        foreach (var contract in type.AllInterfaces)
        {
            if (IsLibraryInterface(contract, StoreInterfaces))
                return true;
        }

        return false;
    }

    private static bool IsStorageMember(IMethodSymbol method)
    {
        // An extension verb of the library over a store: storage.MutateAsync(...), store.DeleteIfExistsAsync(...).
        var definition = method.ReducedFrom ?? method;
        if (definition.IsExtensionMethod)
            return IsLibrary(definition.ContainingAssembly) && definition.Parameters.Length > 0 && IsStore(definition.Parameters[0].Type);

        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation))
            return false;

        var owner = method.ContainingType;
        if (IsLibraryInterface(owner, StoreInterfaces) || IsLibraryInterface(owner, SideInterfaces))
            return true;

        // A store of the library called by its own type (TableStorage<T>, PolymorphicTableStorage<TBase>).
        return IsLibrary(owner?.ContainingAssembly) && IsStore(owner);
    }
}
