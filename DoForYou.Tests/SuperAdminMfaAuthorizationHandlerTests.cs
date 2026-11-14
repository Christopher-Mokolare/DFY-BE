using System.Security.Claims;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class SuperAdminMfaAuthorizationHandlerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Active_verified_SuperAdmin_with_MFA_passes_policy()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-mfa-pass-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 41, Email = "root@example.com", NormalizedEmail = "root@example.com",
            Roles = "Admin,SuperAdmin", EmailVerified = true, AuthenticatorTwoFactorEnabled = true
        });
        await db.SaveChangesAsync();

        var context = CreateContext(41);
        await new SuperAdminMfaAuthorizationHandler(db).HandleAsync(context);
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Ordinary_Admin_is_denied_SuperAdmin_policy()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-mfa-deny-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 42, Email = "admin@example.com", NormalizedEmail = "admin@example.com",
            Roles = "Admin", EmailVerified = true, AuthenticatorTwoFactorEnabled = true
        });
        await db.SaveChangesAsync();

        var context = CreateContext(42);
        await new SuperAdminMfaAuthorizationHandler(db).HandleAsync(context);
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Suspended_SuperAdmin_is_denied_even_with_enrolled_MFA()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-mfa-suspended-{Guid.NewGuid():N}").Options;
        await using var db = new AppDbContext(options);
        db.Users.Add(new User
        {
            Id = 43, Email = "suspended-root@example.com", NormalizedEmail = "suspended-root@example.com",
            Roles = "Admin,SuperAdmin", EmailVerified = true, AuthenticatorTwoFactorEnabled = true,
            IsSuspended = true
        });
        await db.SaveChangesAsync();

        var context = CreateContext(43);
        await new SuperAdminMfaAuthorizationHandler(db).HandleAsync(context);
        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext CreateContext(int id)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "test"));
        return new AuthorizationHandlerContext(new[] { new SuperAdminMfaRequirement() }, principal, null);
    }
}
