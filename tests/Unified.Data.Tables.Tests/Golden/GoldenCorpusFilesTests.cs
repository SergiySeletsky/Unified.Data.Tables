namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// The golden corpus is a fixed set of files. A golden test reads its expectation from the source tree
/// (see <see cref="GoldenFile.ReadOrNull"/>), and this test holds the set itself: a file deleted from
/// <c>Golden/Data</c> fails here by name, and so does a file nobody asserts.
/// </summary>
public class GoldenCorpusFilesTests
{
    // Every file a golden test names. A new case adds its file here in the same change.
    private static readonly string[] Expected =
    [
        "Cursors/azure-paging.txt",
        "Cursors/in-memory-paging.txt",
        "Cursors/page-cursor.txt",
        "Filters/store-queries.txt",
        "Filters/translator.txt",
        "Rows/collections.txt",
        "Rows/column-aliases.legacy-row.txt",
        "Rows/column-aliases.txt",
        "Rows/decimal-beyond-double-precision.txt",
        "Rows/interface-entity.txt",
        "Rows/json-and-gzip-fallbacks.txt",
        "Rows/nested.txt",
        "Rows/nullable-scalars-set.txt",
        "Rows/nullable-scalars-unset.txt",
        "Rows/persist-type.txt",
        "Rows/polymorphic-default-discriminator.txt",
        "Rows/polymorphic-mapped-discriminator.txt",
        "Rows/polymorphic-marker.txt",
        "Rows/scalar-edges.txt",
        "Rows/scalars.txt",
        "Rows/single-key-id.txt",
        "Rows/split-blob-offload.txt",
        "Rows/split-cells.txt",
        "Rows/split-continuation-rows.txt",
        "Rows/split-inline.txt",
    ];

    [Fact]
    public void TheFilesUnderGoldenData_AreExactlyTheOnesTheTestsName()
    {
        // The source tree when the tests run from a build of it (always, in CI and locally); the copy
        // next to the assembly only when there is no tree to look at.
        var root = GoldenFile.SourceDataDirectory() ?? Path.Combine(AppContext.BaseDirectory, "Golden", "Data");
        Assert.True(Directory.Exists(root), $"{root} does not exist.");

        var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Expected.Order(StringComparer.Ordinal), found);
    }

    [Fact]
    public void AGoldenTest_ReadsTheSourceTree_NotTheCopyNextToTheAssembly()
    {
        var source = GoldenFile.SourceDataDirectory();
        Assert.NotNull(source);
        Assert.False(
            Path.GetFullPath(source).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
            "The golden files must be read from the checked-in tree.");

        Assert.All(Expected, name =>
            Assert.Equal(
                File.ReadAllText(Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal),
                GoldenFile.ReadOrNull(name)));
        // A file that is only in the copy (deleted from the tree) is a missing file.
        Assert.Null(GoldenFile.ReadOrNull("Rows/no-such-case.txt"));
    }
}
