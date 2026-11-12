using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables.InMemory;
using Xunit.v3;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="TableEntitySerializer.OversizedCellPolicy"/> is process-wide state, and xUnit runs test
/// classes in parallel unless they share a collection. A test class that writes the policy outside the
/// <c>OversizedCellPolicy</c> collection (directly, or through <c>AddUnifiedTableStorage</c> or
/// <c>AddUnifiedInMemoryStorage</c>, which apply their options to it) resets it underneath the policy
/// tests while they run, and those then fail now and then. These tests read the compiled assemblies and
/// fail for any such class, so the rule no longer depends on somebody noticing.
/// </summary>
public class OversizedCellPolicyCollectionTests
{
    private const string PolicyCollection = "OversizedCellPolicy";

    private static readonly Assembly TestAssembly = typeof(OversizedCellPolicyCollectionTests).Assembly;

    // The test assembly and the library assemblies it references: the only primitive is the policy's
    // setter, so a library method that starts applying the policy is picked up without a change here.
    private static readonly Lazy<PolicyWriteScan> Scan = new(() => new PolicyWriteScan(
        TestAssembly.GetReferencedAssemblies()
            .Where(name => name.Name!.StartsWith("Unified.Data.Tables", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .Prepend(TestAssembly)));

    private static bool IsTestClass(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(method => method.GetCustomAttributes(inherit: true).OfType<IFactAttribute>().Any());

    // Declared on the class itself: the check does not lean on how the runner treats a base class's attribute.
    private static bool IsInPolicyCollection(Type type) =>
        type.GetCustomAttributes(inherit: false).OfType<ICollectionAttribute>()
            .Any(collection => collection.Name == PolicyCollection);

    [Fact]
    public void EveryTestClassThatWritesTheProcessWidePolicy_IsInThePolicyCollection()
    {
        var outside = TestAssembly.GetTypes()
            .Where(type => IsTestClass(type) && Scan.Value.Writes(type) && !IsInPolicyCollection(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(outside.Count == 0,
            $"These test classes write TableEntitySerializer.OversizedCellPolicy (directly, or through AddUnifiedTableStorage or AddUnifiedInMemoryStorage) "
            + $"and so race the policy tests; add [Collection(\"{PolicyCollection}\")] to each: {string.Join(", ", outside)}");
    }

    // ── the scan itself ───────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(PolicyWriteSamples.Setter))]
    [InlineData(typeof(PolicyWriteSamples.TableRegistration))]
    [InlineData(typeof(PolicyWriteSamples.InMemoryRegistration))]
    [InlineData(typeof(PolicyWriteSamples.InALambda))]
    [InlineData(typeof(PolicyWriteSamples.InAnAsyncMethod))]
    [InlineData(typeof(PolicyWriteSamples.InAnIterator))]
    [InlineData(typeof(PolicyWriteSamples.InANestedHelper))]
    [InlineData(typeof(PolicyWriteSamples.ThroughAnotherClass))]
    [InlineData(typeof(PolicyWriteSamples.ThroughABaseClass))]
    public void Scan_FindsAWrite_HoweverTheCodeReachesIt(Type sample)
    {
        Assert.True(Scan.Value.Writes(sample));
    }

    [Theory]
    [InlineData(typeof(PolicyWriteSamples.Reader))]
    [InlineData(typeof(PolicyWriteSamples.OtherRegistration))]
    [InlineData(typeof(SplitCodecTests))]
    public void Scan_IgnoresCodeThatDoesNotWrite(Type sample)
    {
        Assert.False(Scan.Value.Writes(sample));
    }

    [Theory]
    [InlineData(typeof(OversizedCellPolicyTests))]          // sets the policy
    [InlineData(typeof(OffloadRegistrationTests))]          // AddUnifiedTableStorage
    [InlineData(typeof(InMemoryStorageTests))]              // AddUnifiedInMemoryStorage
    [InlineData(typeof(AzuriteLargeValueIntegrationTests))] // AddUnifiedTableStorage inside async tests
    [InlineData(typeof(OrphanBlobSweepServiceTests))]       // AddUnifiedInMemoryStorage inside a nested helper's constructor
    public void Scan_FindsTheKnownWriters(Type testClass)
    {
        Assert.True(IsTestClass(testClass));
        Assert.True(Scan.Value.Writes(testClass));
    }
}

/// <summary>
/// Finds, in the compiled IL, the code that writes <see cref="TableEntitySerializer.OversizedCellPolicy"/>:
/// a method that calls the setter, or that calls, constructs through or takes a delegate to a method
/// that does, across the assemblies it is given.
/// </summary>
internal sealed class PolicyWriteScan
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static readonly MethodInfo PolicySetter =
        typeof(TableEntitySerializer).GetProperty(nameof(TableEntitySerializer.OversizedCellPolicy))!.SetMethod!;

    private readonly HashSet<Module> modules;
    private readonly HashSet<(Module Module, int Token)> writers = [];

    public PolicyWriteScan(IEnumerable<Assembly> assemblies)
    {
        var scanned = assemblies.ToList();
        modules = scanned.Select(assembly => assembly.ManifestModule).ToHashSet();

        var callers = new Dictionary<(Module, int), List<(Module, int)>>();
        var found = new Stack<(Module, int)>();
        foreach (var method in scanned.SelectMany(assembly => assembly.GetTypes()).SelectMany(Methods))
        {
            var caller = Key(method);
            foreach (var target in Targets(method))
            {
                if (Key(target) == Key(PolicySetter))
                {
                    if (writers.Add(caller))
                        found.Push(caller);
                }
                else if (modules.Contains(target.Module) && target.DeclaringType is { IsArray: false })
                {
                    if (!callers.TryGetValue(Key(target), out var list))
                        callers[Key(target)] = list = [];
                    list.Add(caller);
                }
            }
        }

        // Whatever calls a writer is a writer.
        while (found.TryPop(out var writer))
        {
            if (!callers.TryGetValue(writer, out var list))
                continue;
            foreach (var caller in list)
            {
                if (writers.Add(caller))
                    found.Push(caller);
            }
        }
    }

    /// <summary>
    /// Whether code declared in <paramref name="type"/>, in a base class of it or in a type nested in
    /// either (lambdas and state machines included) writes the policy.
    /// </summary>
    public bool Writes(Type type) => Family(type).SelectMany(Methods).Any(method => writers.Contains(Key(method)));

    // A generic instantiation has the token of its definition, so one key covers every instantiation.
    private static (Module, int) Key(MethodBase method) => (method.Module, method.MetadataToken);

    private static IEnumerable<MethodBase> Methods(Type type) =>
        type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));

    private IEnumerable<Type> Family(Type type)
    {
        for (var current = type; current is not null && modules.Contains(current.Module); current = current.BaseType)
        {
            foreach (var member in WithNested(current))
                yield return member;
        }
    }

    private static IEnumerable<Type> WithNested(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested).Prepend(type);

    // The methods a method's body refers to: calls, constructor calls and delegates.
    private static IEnumerable<MethodBase> Targets(MethodBase method)
    {
        // An async or iterator method only starts its state machine; its body, calls included, is compiled
        // into that type, which the method's IL names only as a type.
        foreach (var machine in method.GetCustomAttributes<StateMachineAttribute>())
        {
            foreach (var moved in Methods(machine.StateMachineType))
                yield return moved;
        }

        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            yield break;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var at = 0; at < il.Length;)
        {
            var code = il[at] == 0xFE ? OpCodesByValue[unchecked((short)(0xFE00 | il[at + 1]))] : OpCodesByValue[il[at]];
            at += code.Size;
            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    if (method.Module.ResolveMember(BitConverter.ToInt32(il, at), typeArguments, methodArguments) is MethodBase target)
                        yield return target;
                    at += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    at += 1;
                    break;
                case OperandType.InlineVar:
                    at += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    at += 8;
                    break;
                case OperandType.InlineSwitch:
                    at += 4 + (4 * BitConverter.ToInt32(il, at));
                    break;
                default: // a 4-byte token, branch target, int32 or float32
                    at += 4;
                    break;
            }
        }
    }
}

/// <summary>
/// The shapes <see cref="PolicyWriteScan"/> has to see through. Nothing runs these (so nothing here
/// changes the policy): they exist to be read as IL.
/// </summary>
internal static class PolicyWriteSamples
{
    internal static class Setter
    {
        internal static void Run() => TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;
    }

    internal static class TableRegistration
    {
        internal static void Run(IServiceCollection services) => services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
    }

    internal static class InMemoryRegistration
    {
        internal static void Run(IServiceCollection services) => services.AddUnifiedInMemoryStorage();
    }

    internal static class InALambda
    {
        internal static Action<IServiceCollection> Run() => services => services.AddUnifiedInMemoryStorage();
    }

    internal static class InAnAsyncMethod
    {
        internal static async Task Run(IServiceCollection services)
        {
            await Task.Yield();
            services.AddUnifiedInMemoryStorage();
        }
    }

    internal static class InAnIterator
    {
        internal static IEnumerable<int> Run(IServiceCollection services)
        {
            services.AddUnifiedInMemoryStorage();
            yield return 1;
        }
    }

    internal static class InANestedHelper
    {
        internal static object Run(IServiceCollection services) => new Rig(services);

        private sealed class Rig
        {
            public Rig(IServiceCollection services) => services.AddUnifiedInMemoryStorage();
        }
    }

    internal static class ThroughAnotherClass
    {
        internal static void Run(IServiceCollection services) => InMemoryRegistration.Run(services);
    }

    internal abstract class RegisteringBase
    {
        protected static void Register(IServiceCollection services) => services.AddUnifiedInMemoryStorage();
    }

    internal sealed class ThroughABaseClass : RegisteringBase;

    internal static class Reader
    {
        internal static OversizedCellPolicy Run() => TableEntitySerializer.OversizedCellPolicy;
    }

    internal static class OtherRegistration
    {
        internal static void Run(IServiceCollection services) => services.AddUnifiedOrphanBlobSweep<SplitTextEntity>();
    }
}
