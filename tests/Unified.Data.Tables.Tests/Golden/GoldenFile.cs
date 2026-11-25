using System.Text;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// The checked-in expectations of the golden corpus (<c>Golden/Data</c>): what release 0.9.2 puts on the
/// wire. They were generated once from 0.9.2 and are asserted verbatim; a test never overwrites one.
/// </summary>
/// <remarks>
/// A file is written only when it does not exist yet and <see cref="WriteVariable"/> is set, which is how
/// a new case gets its expectation. To change an existing expectation, delete the file and generate it
/// again: the deletion and the new content then show in the diff under review, which is the point.
/// </remarks>
internal static class GoldenFile
{
    /// <summary>Set to <c>1</c> to write the expectation of a case that has none yet.</summary>
    internal const string WriteVariable = "UNIFIED_TABLES_GOLDEN_WRITE";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static bool Writing =>
        Environment.GetEnvironmentVariable(WriteVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Asserts that <paramref name="actual"/> is, character for character, the checked-in file <paramref name="name"/>.</summary>
    /// <param name="name">The file under <c>Golden/Data</c>, with forward slashes (<c>Rows/scalars.txt</c>).</param>
    /// <param name="actual">What the code under test produced, with <c>\n</c> line ends.</param>
    internal static void AssertMatches(string name, string actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var expected = ReadOrNull(name);
        if (expected is null)
        {
            if (!Writing)
                Assert.Fail(
                    $"The golden file Golden/Data/{name} does not exist. For a new case, run the test once with " +
                    $"{WriteVariable}=1 and check the file in.");
            Write(name, actual);
            return;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return;

        var actualPath = Path.Combine(AppContext.BaseDirectory, "Golden", "Actual", name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, actual, Utf8NoBom);
        Assert.Fail(
            $"Golden/Data/{name} no longer matches what is produced: the stored form changed.{Environment.NewLine}" +
            FirstDifference(expected, actual) + Environment.NewLine + $"Produced output: {actualPath}");
    }

    /// <summary>The lines of the checked-in file <paramref name="name"/> that are neither blank nor a <c>#</c> comment.</summary>
    internal static IReadOnlyList<string> Lines(string name)
    {
        var text = ReadOrNull(name) ?? throw new FileNotFoundException($"The golden file Golden/Data/{name} does not exist.");
        return [.. text.Split('\n').Where(line => line.Length > 0 && line[0] != '#')];
    }

    /// <summary>The checked-in file <paramref name="name"/> with <c>\n</c> line ends, or null when there is none.</summary>
    internal static string? ReadOrNull(string name)
    {
        // The copy next to the test assembly; while a new case is being generated, the source tree,
        // where the other target framework may just have written it.
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "Data", name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path) && Writing)
            path = SourcePath(name);
        // A checkout may turn line ends into CRLF; no value holds a raw line break (they are JSON-escaped).
        return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
    }

    private static void Write(string name, string content)
    {
        var path = SourcePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(Utf8NoBom.GetBytes(content));
        }
        catch (IOException) when (File.Exists(path))
        {
            // The other target framework wrote it first: hold this run to that file like any other.
            AssertMatches(name, content);
        }
    }

    private static string SourcePath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "tests", "Unified.Data.Tables.Tests");
            if (Directory.Exists(project) && File.Exists(Path.Combine(dir.FullName, "Unified.Data.Tables.slnx")))
                return Path.Combine(project, "Golden", "Data", name.Replace('/', Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("The source tree was not found above " + AppContext.BaseDirectory + ".");
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end of file>";
            var al = i < a.Length ? a[i] : "<end of file>";
            if (!string.Equals(el, al, StringComparison.Ordinal))
                return $"First difference at line {i + 1}:{Environment.NewLine}  expected: {Clip(el)}{Environment.NewLine}  produced: {Clip(al)}";
        }
        return "The files differ only in their line ends.";
    }

    private static string Clip(string line) => line.Length <= 400 ? line : line[..400] + "…";
}
