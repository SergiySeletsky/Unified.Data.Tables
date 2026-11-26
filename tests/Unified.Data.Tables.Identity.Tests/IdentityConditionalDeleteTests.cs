using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.Identity.Models;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// How the user store deletes an external login, whose row key does not name its owner. Since 0.10.0
    /// the delete is conditional on the version the ownership check was made on when the store underneath
    /// has a conditional delete (<see cref="IConditionalDelete{T}"/>, found through the capability probe),
    /// and exactly the read-then-delete of 0.9.2 when it has not: a third-party <see cref="IStorage{T}"/>,
    /// an older decorator or a mock must keep working. The same rows on real providers are in
    /// <see cref="IdentityConcurrencyTests"/>.
    /// </summary>
    public sealed class IdentityConditionalDeleteTests : IDisposable
    {
        private const string Provider = "Google";
        private const string Key = "k1";

        private readonly IdentityStoreFixture _fixture = new();

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string LoginId => IdentityKeys.UserLogin(Provider, Key);

        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            _fixture.Dispose();
        }

        private static IdentityUserLoginModel Login(string userId, string? etag)
        {
            var row = IdentityUserLoginModel.FromIdentity(
                new IdentityUserLogin<string> { UserId = userId, LoginProvider = Provider, ProviderKey = Key });
            row.ETag = etag;
            return row;
        }

        /// <summary>A third-party store: <see cref="IStorage{T}"/> and nothing else.</summary>
        private static IStorage<IdentityUserLoginModel> PlainStore(IdentityUserLoginModel? row)
        {
            var storage = Substitute.For<IStorage<IdentityUserLoginModel>>();
            Holds(storage, row);
            return storage;
        }

        /// <summary>A store with a conditional delete, found by its type as on any store without a probe.</summary>
        private static IStorage<IdentityUserLoginModel> ConditionalStore(IdentityUserLoginModel? row, out IConditionalDelete<IdentityUserLoginModel> conditional)
        {
            var storage = Substitute.For<IStorage<IdentityUserLoginModel>, IConditionalDelete<IdentityUserLoginModel>>();
            Holds(storage, row);
            conditional = (IConditionalDelete<IdentityUserLoginModel>)storage;
            return storage;
        }

        private static void Holds(IStorage<IdentityUserLoginModel> storage, IdentityUserLoginModel? row)
        {
            storage.OneAsync(LoginId, Arg.Any<CancellationToken>()).Returns(row);
            IReadOnlyList<IdentityUserLoginModel> rows = row is null ? [] : [row];
            storage.QueryAsync(Arg.Any<Expression<Func<IdentityUserLoginModel, bool>>>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(rows);
        }

        private UserStore StoreOver(IStorage<IdentityUserLoginModel> logins) =>
            new(_fixture.UserRows, _fixture.RoleRows, logins, _fixture.UserClaimRows, _fixture.UserRoleRows, _fixture.UserTokenRows);

        private static IdentityUser User(string id = "u1") => new() { Id = id, UserName = "alice" };

        // ── a store without the capability: exactly the path of 0.9.2 ───────────────────────────

        [Fact]
        public async Task RemoveLogin_OverAStoreWithoutConditionalDelete_ReadsThenDeletes()
        {
            var storage = PlainStore(Login("u1", "W/\"1\""));

            var result = await StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct);

            Assert.True(result.Succeeded);
            await storage.Received(1).OneAsync(LoginId, Arg.Any<CancellationToken>());
            await storage.Received(1).DeleteAsync(LoginId, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task UserDelete_OverAStoreWithoutConditionalDelete_QueriesThenDeletes()
        {
            await _fixture.SeedUserAsync("u1", "alice", Ct);
            var user = await _fixture.ReadUserAsync("u1", Ct);
            var storage = PlainStore(Login("u1", "W/\"1\""));

            var result = await StoreOver(storage).DeleteAsync(user, Ct);

            Assert.True(result.Succeeded);
            await storage.Received(1).DeleteAsync(LoginId, Arg.Any<CancellationToken>());
            await storage.DidNotReceiveWithAnyArgs().OneAsync(default!, default);
        }

        [Fact]
        public async Task RemoveLogin_OverADecoratorThatHidesTheCapability_ReadsThenDeletes()
        {
            // The probe's answer is final: a decorator that says no is not cast behind its back.
            var inner = ConditionalStore(Login("u1", "W/\"1\""), out var conditional);
            var hiding = new HidingStorage(inner);

            var result = await StoreOver(hiding).RemoveLoginCoreAsync(User(), Provider, Key, Ct);

            Assert.True(result.Succeeded);
            await inner.Received(1).DeleteAsync(LoginId, Arg.Any<CancellationToken>());
            await conditional.DidNotReceiveWithAnyArgs().DeleteIfMatchAsync(default!, default!, default);
        }

        [Fact]
        public async Task RemoveLogin_WhenTheRowCarriesNoVersion_ReadsThenDeletes()
        {
            // A store that has the capability but hands out rows without an ETag gives nothing to match on.
            var storage = ConditionalStore(Login("u1", etag: null), out var conditional);

            var result = await StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct);

            Assert.True(result.Succeeded);
            await storage.Received(1).DeleteAsync(LoginId, Arg.Any<CancellationToken>());
            await conditional.DidNotReceiveWithAnyArgs().DeleteIfMatchAsync(default!, default!, default);
        }

        // ── a store with the capability ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(DeleteStatus.Deleted)]
        [InlineData(DeleteStatus.NotFound)]
        // Gone, possibly by an earlier attempt of this very request: the login is removed either way, and
        // the store runs nothing that depends on who removed it.
        [InlineData(DeleteStatus.Indeterminate)]
        public async Task RemoveLogin_DeletesTheVersionItChecked_AndEveryStatusIsARemovedLogin(DeleteStatus status)
        {
            var storage = ConditionalStore(Login("u1", "W/\"7\""), out var conditional);
            conditional.DeleteIfMatchAsync(LoginId, "W/\"7\"", Arg.Any<CancellationToken>()).Returns(status);

            var result = await StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct);

            Assert.True(result.Succeeded);
            await conditional.Received(1).DeleteIfMatchAsync(LoginId, "W/\"7\"", Arg.Any<CancellationToken>());
            await storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        }

        [Theory]
        [InlineData(DeleteStatus.Deleted)]
        [InlineData(DeleteStatus.NotFound)]
        [InlineData(DeleteStatus.Indeterminate)]
        public async Task UserDelete_DeletesTheLoginVersionItFound_AndEveryStatusLetsTheDeleteFinish(DeleteStatus status)
        {
            await _fixture.SeedUserAsync("u1", "alice", Ct);
            var user = await _fixture.ReadUserAsync("u1", Ct);
            var storage = ConditionalStore(Login("u1", "W/\"7\""), out var conditional);
            conditional.DeleteIfMatchAsync(LoginId, "W/\"7\"", Arg.Any<CancellationToken>()).Returns(status);

            var result = await StoreOver(storage).DeleteAsync(user, Ct);

            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserRows.Count);
            await conditional.Received(1).DeleteIfMatchAsync(LoginId, "W/\"7\"", Arg.Any<CancellationToken>());
            await storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        }

        [Fact]
        public async Task RemoveLogin_ALoginOfAnotherUser_IsNotDeletedAtAll()
        {
            var storage = ConditionalStore(Login("u2", "W/\"7\""), out var conditional);

            var result = await StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct);

            Assert.Equal("LoginNotFound", Assert.Single(result.Errors).Code);
            await conditional.DidNotReceiveWithAnyArgs().DeleteIfMatchAsync(default!, default!, default);
            await storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        }

        [Fact]
        public async Task RemoveLogin_ARowThatKeepsChanging_ThrowsTheConflict_AfterFiveReads()
        {
            // Every read is still this user's row and every delete finds another version. Nothing rewrites
            // a login row in this library, so this is a store that misbehaves: the conflict is reported,
            // never swallowed, because IUserLoginStore.RemoveLoginAsync has no result to carry it.
            var storage = ConditionalStore(Login("u1", "W/\"7\""), out var conditional);
            conditional.DeleteIfMatchAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new ConcurrencyConflictException());

            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct));

            await storage.Received(5).OneAsync(LoginId, Arg.Any<CancellationToken>());
            await storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        }

        [Fact]
        public async Task RemoveLogin_AnyOtherStorageFailure_IsThrownAsItIs()
        {
            var failure = new TransientStorageException("the service is busy");
            var storage = ConditionalStore(Login("u1", "W/\"7\""), out var conditional);
            conditional.DeleteIfMatchAsync(default!, default!, default).ThrowsAsyncForAnyArgs(failure);

            var thrown = await Record.ExceptionAsync(() => StoreOver(storage).RemoveLoginCoreAsync(User(), Provider, Key, Ct));

            Assert.Same(failure, thrown);
            await storage.Received(1).OneAsync(LoginId, Arg.Any<CancellationToken>());
        }

        /// <summary>A decorator whose probe answers no, whatever it wraps.</summary>
        private sealed class HidingStorage(IStorage<IdentityUserLoginModel> inner) : DelegatingStorage<IdentityUserLoginModel>(inner)
        {
            public override bool TryGetCapability<TCap>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TCap? capability) where TCap : class
            {
                capability = null;
                return false;
            }
        }
    }
}
