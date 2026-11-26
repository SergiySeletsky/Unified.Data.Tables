using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Unified.Data.Tables.Analyzers;

namespace Unified.Data.Tables.Tests.Analyzers;

/// <summary>
/// The upgrade analyzer of 0.10.0 (UDT1001): a <c>catch (RequestFailedException)</c> whose try block
/// calls a storage member of this library and no Azure SDK member has caught nothing since 0.10.0,
/// because storage members throw the neutral <see cref="StorageException"/> family. It is a hint for
/// the upgrade and must never be more than that: every consumer builds with warnings as errors.
/// </summary>
public class ProviderExceptionCatchAnalyzerTests
{
    private const string Usings = """
        using System;
        using System.Threading.Tasks;
        using Azure;
        using Azure.Data.Tables;
        using Unified.Data.Tables;

        public sealed class Note : Entity { public string? Text { get; set; } }

        """;

    // Everything the test process itself has loaded or can load: the BCL, the Azure SDK, this library.
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body, bool warningsAsErrors = false)
    {
        var source = Usings + "public static class Program\n{\n" + body + "\n}\n";
        var compilation = CSharpCompilation.Create(
            "UpgradeSample",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                // What <TreatWarningsAsErrors> hands the compiler.
                generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));

        // A sample that does not compile would prove nothing about what the analyzer sees.
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "The sample does not compile: " + string.Join(Environment.NewLine, errors));

        return await compilation
            .WithAnalyzers([new ProviderExceptionCatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheRule_IsInfo_SoThatWarningsAsErrorsNeverFailsABuildOnIt()
    {
        var rule = Assert.Single(new ProviderExceptionCatchAnalyzer().SupportedDiagnostics);

        Assert.Equal("UDT1001", rule.Id);
        Assert.Equal(DiagnosticSeverity.Info, rule.DefaultSeverity);
        Assert.True(rule.IsEnabledByDefault);
        Assert.Contains("InnerException", rule.MessageFormat.ToString(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatchOfRequestFailedException_AroundAnIStorageMember_IsReported()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task<bool> RenameAsync(IStorage<Note> notes)
            {
                try
                {
                    await notes.UpdateAsync("p|a", b => b.SetProperty(n => n.Text, "x"));
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return false;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("UDT1001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.False(diagnostic.IsWarningAsError);
        Assert.Contains("RequestFailedException", diagnostic.GetMessage(null), StringComparison.Ordinal);
        Assert.Contains("UpdateAsync", diagnostic.GetMessage(null), StringComparison.Ordinal);
        // Reported on the caught type, where the edit is made.
        Assert.Equal("RequestFailedException", diagnostic.Location.SourceTree!.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public async Task UnderWarningsAsErrors_TheDiagnosticStaysInfo()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task DeleteAsync(IStorage<Note> notes)
            {
                try { await notes.DeleteAsync("p|a"); }
                catch (RequestFailedException) { }
            }
            """, warningsAsErrors: true);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.False(diagnostic.IsWarningAsError);
    }

    [Fact]
    public async Task ACatchOfTheTransactionFailure_AroundABatch_IsReported()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task SaveAsync(IStorage<Note> notes, Note[] batch)
            {
                try { await notes.UpsertBatchAsync(batch); }
                catch (TableTransactionFailedException) { }
            }
            """);

        Assert.Equal("UDT1001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task AnExtensionVerb_AndAPolymorphicMember_AndAConcreteStore_AreStorageCalls()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task VerbAsync(IStorage<Note> notes)
            {
                try { await notes.MutateAsync("p|a", n => n.Text = "x"); }
                catch (RequestFailedException) { }
            }

            public static async Task MergeAsync(IPolymorphicStorage<object> events)
            {
                try { await events.MergeColumnsAsync(new TableKey("p", "r"), new System.Collections.Generic.Dictionary<string, object>()); }
                catch (RequestFailedException) { }
            }

            public static async Task ConcreteAsync(TableStorage<Note> notes)
            {
                try { await notes.DeleteAsync("p|a"); }
                catch (RequestFailedException) { }
            }
            """);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("UDT1001", d.Id));
    }

    [Fact]
    public async Task ATryBlockThatAlsoCallsTheAzureSdk_IsLeftAlone()
    {
        // The catch still guards the SDK call: changing it would be wrong, so nothing is said.
        var diagnostics = await AnalyzeAsync("""
            public static async Task MixedAsync(IStorage<Note> notes, TableClient raw)
            {
                try
                {
                    await notes.DeleteAsync("p|a");
                    await raw.DeleteEntityAsync("p", "b");
                }
                catch (RequestFailedException) { }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ACatchAroundTheAzureSdkAlone_OrAroundNoStorageCall_IsLeftAlone()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task RawAsync(TableClient raw)
            {
                try { await raw.DeleteEntityAsync("p", "b"); }
                catch (RequestFailedException) { }
            }

            public static void Nothing()
            {
                try { Console.WriteLine("no storage here"); }
                catch (RequestFailedException) { }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ACatchOfANeutralOrUnrelatedType_IsLeftAlone()
    {
        var diagnostics = await AnalyzeAsync("""
            public static async Task NeutralAsync(IStorage<Note> notes)
            {
                try { await notes.CreateAsync(new Note { Id = "p|a" }); }
                catch (DuplicateKeyException) { }
                catch (StorageException) { }
                catch (InvalidOperationException) { }
                catch (Exception) { }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AStorageCallInsideALambdaOfTheTryBlock_IsNotCounted()
    {
        // The lambda runs wherever it is invoked; the catch around its declaration guards nothing of it.
        var diagnostics = await AnalyzeAsync("""
            public static Func<Task> Later(IStorage<Note> notes)
            {
                try { return () => notes.DeleteAsync("p|a"); }
                catch (RequestFailedException) { return () => Task.CompletedTask; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TheAnalyzerAssembly_IsNotReferencedByAnyLibraryPackage()
    {
        // Opt-in only: a consumer gets the analyzer by referencing its package, never transitively.
        var name = typeof(ProviderExceptionCatchAnalyzer).Assembly.GetName().Name;
        var libraries = new[]
        {
            typeof(IStorage<>).Assembly, typeof(TableEntitySerializer).Assembly, typeof(TableStorage<>).Assembly,
            typeof(Unified.Data.Tables.InMemory.InMemoryStorage<>).Assembly, typeof(Unified.Data.Tables.Sqlite.SqliteStorage<>).Assembly,
            typeof(Unified.Data.Tables.Conformance.ConformanceSuite).Assembly,
        };

        Assert.All(libraries, library =>
            Assert.DoesNotContain(library.GetReferencedAssemblies(), reference => reference.Name == name));
    }
}
