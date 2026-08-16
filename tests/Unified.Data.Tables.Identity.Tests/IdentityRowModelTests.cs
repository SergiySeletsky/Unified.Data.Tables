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
            // Arrange — LockoutEnd is the property the old reflection serializer could never read back
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
            // Arrange — ProviderDisplayName is present on all 37 live rows and must not be dropped
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
