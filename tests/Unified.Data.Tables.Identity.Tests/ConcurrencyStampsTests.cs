using NSubstitute;
using Unified.Data.Tables.Identity.Models;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// The conditional replace the user and role stores share. A row deleted between the store's read and
    /// its write lost the race like any concurrent change. Since 0.10.0 a store of this library reports
    /// that as <see cref="EntityNotFoundException"/>, so the replace no longer has to guess from an
    /// unknown exception by reading the row again; that probe is kept only for a third-party
    /// <see cref="IStorage{T}"/> that still throws a type of its own.
    /// </summary>
    public class ConcurrencyStampsTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IdentityRoleModel Role() => new() { Id = "role|r1", Name = "admins" };

        private static IStorage<IdentityRoleModel> StoreThatFailsTheReplaceWith(Exception failure)
        {
            var storage = Substitute.For<IStorage<IdentityRoleModel>>();
            storage.UpdateAsync(Arg.Any<IdentityRoleModel>(), ConcurrencyMode.Strict, Arg.Any<CancellationToken>())
                .Returns<Task<IdentityRoleModel>>(_ => throw failure);
            return storage;
        }

        [Fact]
        public async Task ARowDeletedAfterTheRead_LosesTheRace_WithoutAProbe()
        {
            var storage = StoreThatFailsTheReplaceWith(new EntityNotFoundException("IdentityRoleModel", "role|r1", null));

            var replaced = await ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct);

            Assert.False(replaced);
            await storage.DidNotReceiveWithAnyArgs().ExistsAsync(default!, default);
        }

        [Fact]
        public async Task AConflict_LosesTheRace_WithoutAProbe()
        {
            var storage = StoreThatFailsTheReplaceWith(new ConcurrencyConflictException());

            Assert.False(await ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct));
            await storage.DidNotReceiveWithAnyArgs().ExistsAsync(default!, default);
        }

        [Fact]
        public async Task ASuccessfulReplace_ReturnsTrue_AndSendsTheETagOfTheRead()
        {
            var storage = Substitute.For<IStorage<IdentityRoleModel>>();
            var role = Role();
            storage.UpdateAsync(role, ConcurrencyMode.Strict, Arg.Any<CancellationToken>()).Returns(role);

            Assert.True(await ConcurrencyStamps.TryReplaceAsync(storage, role, "W/\"7\"", Ct));
            Assert.Equal("W/\"7\"", role.ETag);
        }

        public static TheoryData<Exception> StorageFailures =>
        [
            new TransientStorageException("the service is busy"),
            new StorageRequestRejectedException("refused"),
            new TableNotFoundException("no table"),
            new StorageException("forbidden"),
        ];

        [Theory]
        [MemberData(nameof(StorageFailures))]
        public async Task AnyOtherStorageFailure_IsThrownAsItIs_WithoutAProbe(Exception failure)
        {
            // Even when the row would be found missing: an outage is not a lost race.
            var storage = StoreThatFailsTheReplaceWith(failure);
            storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

            var thrown = await Record.ExceptionAsync(() => ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct));

            Assert.Same(failure, thrown);
            await storage.DidNotReceiveWithAnyArgs().ExistsAsync(default!, default);
        }

        // ── a third-party store that still throws its own type ──────────────────────────────────

        private sealed class VendorNotFoundException(string message) : Exception(message);

        [Fact]
        public async Task AThirdPartyStoresOwnFailure_WhenTheRowIsGone_LosesTheRace()
        {
            var storage = StoreThatFailsTheReplaceWith(new VendorNotFoundException("404"));
            storage.ExistsAsync("role|r1", Arg.Any<CancellationToken>()).Returns(false);

            Assert.False(await ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct));
            await storage.Received(1).ExistsAsync("role|r1", Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AThirdPartyStoresOwnFailure_WhenTheRowIsStillThere_IsThrown()
        {
            var failure = new VendorNotFoundException("something else");
            var storage = StoreThatFailsTheReplaceWith(failure);
            storage.ExistsAsync("role|r1", Arg.Any<CancellationToken>()).Returns(true);

            Assert.Same(failure, await Assert.ThrowsAsync<VendorNotFoundException>(
                () => ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct)));
        }

        [Fact]
        public async Task AThirdPartyStoresOwnFailure_WhenTheProbeFailsToo_ReportsTheOriginalFailure()
        {
            var failure = new VendorNotFoundException("down");
            var storage = StoreThatFailsTheReplaceWith(failure);
            storage.ExistsAsync("role|r1", Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new VendorNotFoundException("probe"));

            Assert.Same(failure, await Assert.ThrowsAsync<VendorNotFoundException>(
                () => ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct)));
        }

        [Fact]
        public async Task Cancellation_IsNeverTakenForALostRace()
        {
            var storage = StoreThatFailsTheReplaceWith(new OperationCanceledException());
            storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

            await Assert.ThrowsAsync<OperationCanceledException>(() => ConcurrencyStamps.TryReplaceAsync(storage, Role(), "W/\"1\"", Ct));
            await storage.DidNotReceiveWithAnyArgs().ExistsAsync(default!, default);
        }
    }
}
