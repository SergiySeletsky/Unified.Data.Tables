using Unified.Data.Tables.Identity;
using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityRowModelTests
    {
        [Fact]
        public void Hash_IsLowercaseHex_AndDeterministic()
        {
            // Arrange / Act
            var a = IdentityKeys.Hash("Google-12345");
            var b = IdentityKeys.Hash("Google-12345");

            // Assert
            Assert.Equal(a, b);
            Assert.Equal(32, a.Length);
            Assert.Equal(a.ToLowerInvariant(), a);
        }

        [Fact]
        public void Hash_MatchesKnownLiteralDigests()
        {
            // These literals are the regression guard for the on-disk key format: they are the
            // lowercase-hex MD5 of the UTF-8 bytes of the input. Every persisted row's key is
            // derived from this function, so changing the digest, the text encoding, or the hex
            // casing orphans every existing row. If these assertions fail, the change is a
            // breaking data migration — do not "fix" the test by updating the literals.
            Assert.Equal("827ccb0eea8a706c4c34a16891f84e7b", IdentityKeys.Hash("12345"));
            Assert.Equal("fbbd4c210e88504da7a732c59084ef49", IdentityKeys.Hash("Google-12345"));
            Assert.Equal(string.Empty, IdentityKeys.Hash(string.Empty));
        }

        [Fact]
        public void Keys_ComposeExpectedLiteralIds()
        {
            // Fully literal expectations — no call to IdentityKeys.Hash on the expected side, so
            // both the separator layout AND the hash itself are pinned. These are the exact
            // strings written to storage; a failure here means existing rows become unreachable.
            Assert.Equal(
                "Google|827ccb0eea8a706c4c34a16891f84e7b",
                IdentityKeys.UserLogin("Google", "12345"));
            Assert.Equal(
                "u1|9872af6969e880b1438106f4a6e7cf32",
                IdentityKeys.UserClaim("u1", "friendly-name", "Bob"));
            Assert.Equal(
                "u1|a92660cffb43f1f88f52617e09118370",
                IdentityKeys.UserToken("u1", "Google", "AuthenticatorKey"));
            Assert.Equal(
                "r1|d95564ddfe8b558513601ba05961f3e4",
                IdentityKeys.RoleClaim("r1", "perm", "read"));
        }

        [Fact]
        public void Keys_ComposeExpectedIds()
        {
            // Assert
            Assert.Equal("User|abc", IdentityKeys.User("abc"));
            Assert.Equal("Role|r1", IdentityKeys.Role("r1"));
            Assert.Equal("u1|r1", IdentityKeys.UserRole("u1", "r1"));
            Assert.Equal($"u1|{IdentityKeys.Hash("friendly-name-Bob")}", IdentityKeys.UserClaim("u1", "friendly-name", "Bob"));
            Assert.Equal($"Google|{IdentityKeys.Hash("12345")}", IdentityKeys.UserLogin("Google", "12345"));
            Assert.Equal($"u1|{IdentityKeys.Hash("Google-AuthenticatorKey")}", IdentityKeys.UserToken("u1", "Google", "AuthenticatorKey"));
            Assert.Equal($"r1|{IdentityKeys.Hash("perm-read")}", IdentityKeys.RoleClaim("r1", "perm", "read"));
        }

        [Fact]
        public void UserModel_RoundTripsEveryProperty_IncludingLockoutEnd()
        {
            // Arrange — LockoutEnd is the property an earlier reflection-based serializer could
            // never read back
            var lockoutEnd = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var user = new IdentityUser
            {
                Id = "u1",
                UserName = "alice",
                NormalizedUserName = "ALICE",
                Email = "alice@example.com",
                NormalizedEmail = "ALICE@EXAMPLE.COM",
                EmailConfirmed = true,
                PasswordHash = "hash",
                SecurityStamp = "stamp",
                ConcurrencyStamp = "concurrency",
                PhoneNumber = "+380000000",
                PhoneNumberConfirmed = true,
                TwoFactorEnabled = true,
                LockoutEnd = lockoutEnd,
                LockoutEnabled = true,
                AccessFailedCount = 3
            };

            // Act
            var round = IdentityUserModel.FromIdentity(user).ToIdentity();

            // Assert
            Assert.Equal("u1", round.Id);
            Assert.Equal("alice", round.UserName);
            Assert.Equal("ALICE", round.NormalizedUserName);
            Assert.Equal("alice@example.com", round.Email);
            Assert.Equal("ALICE@EXAMPLE.COM", round.NormalizedEmail);
            Assert.True(round.EmailConfirmed);
            Assert.Equal("hash", round.PasswordHash);
            Assert.Equal("stamp", round.SecurityStamp);
            Assert.Equal("concurrency", round.ConcurrencyStamp);
            Assert.Equal("+380000000", round.PhoneNumber);
            Assert.True(round.PhoneNumberConfirmed);
            Assert.True(round.TwoFactorEnabled);
            Assert.Equal(lockoutEnd, round.LockoutEnd);
            Assert.True(round.LockoutEnabled);
            Assert.Equal(3, round.AccessFailedCount);
        }

        [Fact]
        public void UserModel_SetsCompositeId()
        {
            // Act
            var model = IdentityUserModel.FromIdentity(new IdentityUser { Id = "u1", UserName = "alice" });

            // Assert
            Assert.Equal("User|u1", model.Id);
            Assert.Equal("u1", model.UserId);
        }

        [Fact]
        public void LoginModel_RoundTripsProviderDisplayName()
        {
            // Arrange — ProviderDisplayName is present on existing external-login rows and must
            // not be dropped
            var login = new IdentityUserLogin<string>
            {
                UserId = "u1",
                LoginProvider = "Google",
                ProviderKey = "12345",
                ProviderDisplayName = "Google"
            };

            // Act
            var round = IdentityUserLoginModel.FromIdentity(login).ToIdentity();

            // Assert
            Assert.Equal("u1", round.UserId);
            Assert.Equal("Google", round.LoginProvider);
            Assert.Equal("12345", round.ProviderKey);
            Assert.Equal("Google", round.ProviderDisplayName);
        }
    }
}
