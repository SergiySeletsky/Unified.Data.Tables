namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// The storage connection string every Azurite test uses: <see cref="DevelopmentStorage"/> (Azurite on
/// 127.0.0.1:10000/10002) unless the environment variable <see cref="Variable"/> names another, for
/// example an emulator on other ports, or an unused port to exercise the skip path.
/// </summary>
internal static class AzuriteConnection
{
    /// <summary>Overrides the storage connection string of the Azurite tests.</summary>
    internal const string Variable = "UNIFIED_TABLES_TEST_STORAGE";

    /// <summary>The emulator shortcut used when <see cref="Variable"/> is unset or empty.</summary>
    internal const string DevelopmentStorage = "UseDevelopmentStorage=true";

    /// <summary>The connection string the Azurite tests use.</summary>
    internal static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured ? configured : DevelopmentStorage;
}
