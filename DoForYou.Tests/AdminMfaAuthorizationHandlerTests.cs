using System.Security.Claims;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AdminMfaAuthorizationHandlerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Active_verified_Admin_with_MFA_passes_policy()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-mfa-pass-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 1, Email = "admin@example.com", NormalizedEmail = "admin@example.com",
            Roles = "Admin", EmailVerified = true, AuthenticatorTwoFactorEnabled = true
        });
        await db.SaveChangesAsync();

        var context = CreateAuthorizationContext(1);
        await new AdminMfaAuthorizationHandler(db).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Admin_without_authenticator_MFA_is_denied()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-mfa-deny-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 2, Email = "admin@example.com", NormalizedEmail = "admin@example.com",
            Roles = "Admin", EmailVerified = true, AuthenticatorTwoFactorEnabled = false
        });
        await db.SaveChangesAsync();

        var context = CreateAuthorizationContext(2);
        await new AdminMfaAuthorizationHandler(db).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Archived_or_unverified_admin_is_denied()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-mfa-state-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 3, Email = "admin@example.com", NormalizedEmail = "admin@example.com",
            Roles = "Admin", EmailVerified = false, AuthenticatorTwoFactorEnabled = true
        });
        await db.SaveChangesAsync();

        var context = CreateAuthorizationContext(3);
        await new AdminMfaAuthorizationHandler(db).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext CreateAuthorizationContext(int userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "test"));
        return new AuthorizationHandlerContext(new[] { new AdminMfaRequirement() }, principal, null);
    }
}
