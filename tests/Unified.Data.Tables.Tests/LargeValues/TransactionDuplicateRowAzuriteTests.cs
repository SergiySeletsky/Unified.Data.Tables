using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="TableRequestSize.FitsOneTransaction"/> says no to actions the service refuses as one
/// entity-group transaction because two of them name the same row: checked against the local Azurite
/// through the real SDK, so the rule is the service's and not only the library's.
/// </summary>
public sealed class TransactionDuplicateRowAzuriteTests : AzuriteCleanupTestBase
{
    private const string TableName = "UdtDuplicateRowTx";

    [Fact]
    public async Task Azurite_TwoActionsOnOneRow_AreRefusedAsOneTransaction_AndFitOneTransactionSaysNo()
    {
        SkipUnlessAvailable();
        var table = Table(TableName);
        await table.CreateIfNotExistsAsync(Ct);
        TableTransactionAction[] actions =
        [
            new(TableTransactionActionType.UpsertReplace, new TableEntity(Pk, "r") { ["V"] = "1" }),
            new(TableTransactionActionType.UpsertReplace, new TableEntity(Pk, "r") { ["V"] = "2" }),
        ];

        var refused = await Assert.ThrowsAnyAsync<RequestFailedException>(() => table.SubmitTransactionAsync(actions, Ct));

        Assert.Equal(400, refused.Status);
        Assert.False(TableRequestSize.FitsOneTransaction(actions));
        Assert.Empty(await RawRowsAsync(TableName));
    }
}
