namespace Unified.Data.Tables.Tests;

/// <summary>
/// 0.10.0: <see cref="TableKeyRules"/>, the public check of what Azure Table storage accepts as a
/// <c>PartitionKey</c> or a <c>RowKey</c>.
/// </summary>
public class TableKeyRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("orders|2024")]
    [InlineData("with space and ~ and ' and \" and % and + and é and 日本")]
    [InlineData(" ")] // the first character after the refused C1 range
    [InlineData("~")]
    public void AKeyTheServiceAccepts_IsValid(string key)
    {
        Assert.True(TableKeyRules.IsValid(key));
        Assert.Null(TableKeyRules.FindViolation(key));
        TableKeyRules.Validate(key, key);
        TableKeyRules.Validate(new TableKey(key, key));
    }

    [Theory]
    [InlineData("a/b", "'/' at index 1")]
    [InlineData("a\\b", "'\\' at index 1")]
    [InlineData("#a", "'#' at index 0")]
    [InlineData("a?", "'?' at index 1")]
    [InlineData("a\tb", "U+0009 at index 1")]
    [InlineData("a\nb", "U+000A at index 1")]
    [InlineData("\0", "U+0000 at index 0")]
    [InlineData("a\u001f", "U+001F at index 1")]
    [InlineData("a\u007f", "U+007F at index 1")]
    [InlineData("a\u009f", "U+009F at index 1")]
    public void AKeyWithARefusedCharacter_IsNamedWithTheCharacterAndItsIndex(string key, string names)
    {
        Assert.False(TableKeyRules.IsValid(key));
        Assert.Contains(names, TableKeyRules.FindViolation(key), StringComparison.Ordinal);

        var partition = Assert.Throws<ArgumentException>(() => TableKeyRules.Validate(key, "ok"));
        Assert.Equal("partitionKey", partition.ParamName);
        Assert.StartsWith("The PartitionKey is not a key Azure Table storage accepts: ", partition.Message, StringComparison.Ordinal);
        var row = Assert.Throws<ArgumentException>(() => TableKeyRules.Validate(new TableKey("ok", key)));
        Assert.Equal("rowKey", row.ParamName);
        Assert.StartsWith("The RowKey is not a key Azure Table storage accepts: ", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullKey_AndAKeyOverTheLimit_AreRefused()
    {
        Assert.False(TableKeyRules.IsValid(null));
        Assert.Equal("it is null", TableKeyRules.FindViolation(null));
        // A default TableKey has null keys.
        Assert.Throws<ArgumentException>(() => TableKeyRules.Validate(default(TableKey)));

        Assert.True(TableKeyRules.IsValid(new string('k', TableKeyRules.MaxKeyLength)));
        Assert.False(TableKeyRules.IsValid(new string('k', TableKeyRules.MaxKeyLength + 1)));
        Assert.Contains("513 characters long", TableKeyRules.FindViolation(new string('k', 513)), StringComparison.Ordinal);
        Assert.Equal(512, TableKeyRules.MaxKeyLength);
    }

    [Fact]
    public void NormalizingAnId_RemovesNothingTheRulesRefuse()
    {
        // The check is on the key as stored; an id that fails before normalization fails after it too.
        foreach (var id in new[] { "A/B", "a#b ", " a?b", "a\\B" })
            Assert.False(TableKeyRules.IsValid(EntityId.Normalize(id)));
    }
}
