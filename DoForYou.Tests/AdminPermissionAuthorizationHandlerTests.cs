using System.Security.Claims;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AdminPermissionAuthorizationHandlerTests
{
    [Fact]
    public async Task Active_admin_with_specific_unexpired_grant_and_fresh_mfa_is_allowed()
    {
        await using var db = CreateContext();
        db.Users.Add(Admin(1));
        db.AdminPermissionGrants.Add(new AdminPermissionGrant
        {
            UserId = 1, PermissionKey = AdminPermissionPolicies.KycReview,
            GrantedByUserId = 99, Reason = "Assigned to the identity review team.",
            GrantedAt = DateTime.UtcNow.AddMinutes(-2), ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();

        var result = CreateAuthorizationContext(1, AdminPermissionPolicies.KycReview);
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(result);

        Assert.True(result.HasSucceeded);
    }

    [Fact]
    public async Task Admin_without_matching_grant_is_denied()
    {
        await using var db = CreateContext();
        db.Users.Add(Admin(2));
        db.AdminPermissionGrants.Add(new AdminPermissionGrant
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.TasksManage,
            GrantedByUserId = 99, Reason = "Assigned for task operations.",
            GrantedAt = DateTime.UtcNow.AddMinutes(-2), ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();

        var result = CreateAuthorizationContext(2, AdminPermissionPolicies.KycReview);
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(result);

        Assert.False(result.HasSucceeded);
    }

    [Fact]
    public async Task Expired_or_revoked_permission_is_denied()
    {
        await using var db = CreateContext();
        db.Users.Add(Admin(3));
        db.AdminPermissionGrants.AddRange(
            new AdminPermissionGrant
            {
                UserId = 3, PermissionKey = AdminPermissionPolicies.KycReview,
                GrantedByUserId = 99, Reason = "Expired reviewer assignment.",
                GrantedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(-1)
            },
            new AdminPermissionGrant
            {
                UserId = 3, PermissionKey = AdminPermissionPolicies.KycViewDocuments,
                GrantedByUserId = 99, Reason = "Revoked document viewer assignment.",
                GrantedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(2),
                RevokedAt = DateTime.UtcNow.AddHours(-1), RevokedByUserId = 99
            });
        await db.SaveChangesAsync();

        var expired = CreateAuthorizationContext(3, AdminPermissionPolicies.KycReview);
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(expired);
        var revoked = CreateAuthorizationContext(3, AdminPermissionPolicies.KycViewDocuments);
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(revoked);

        Assert.False(expired.HasSucceeded);
        Assert.False(revoked.HasSucceeded);
    }

    [Fact]
    public async Task SuperAdmin_with_fresh_mfa_can_pass_permission_policy_without_grant()
    {
        await using var db = CreateContext();
        db.Users.Add(Admin(4, "SuperAdmin"));
        await db.SaveChangesAsync();

        var result = CreateAuthorizationContext(4, AdminPermissionPolicies.FinanceManage, "SuperAdmin");
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(result);

        Assert.True(result.HasSucceeded);
    }

    [Fact]
    public async Task Stale_mfa_is_denied_even_when_permission_grant_exists()
    {
        await using var db = CreateContext();
        var admin = Admin(5);
        admin.LastTwoFactorVerifiedAt = DateTime.UtcNow.AddHours(-1);
        db.Users.Add(admin);
        db.AdminPermissionGrants.Add(new AdminPermissionGrant
        {
            UserId = 5, PermissionKey = AdminPermissionPolicies.KycReview,
            GrantedByUserId = 99, Reason = "Assigned to the identity review team.",
            GrantedAt = DateTime.UtcNow.AddMinutes(-2), ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();

        var result = CreateAuthorizationContext(5, AdminPermissionPolicies.KycReview);
        await new AdminPermissionAuthorizationHandler(db).HandleAsync(result);

        Assert.False(result.HasSucceeded);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-permission-{Guid.NewGuid():N}").Options;
        return new AppDbContext(options);
    }

    private static User Admin(int id, string role = "Admin") => new()
    {
        Id = id,
        Email = $"admin{id}@example.test",
        NormalizedEmail = $"admin{id}@example.test",
        PasswordHash = "test-hash",
        Roles = role,
        EmailVerified = true,
        AuthenticatorTwoFactorEnabled = true,
        LastTwoFactorVerifiedAt = DateTime.UtcNow
    };

    private static AuthorizationHandlerContext CreateAuthorizationContext(
        int userId, string permissionKey, string role = "Admin")
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "test"));
        return new AuthorizationHandlerContext(
            new[] { new AdminPermissionRequirement(permissionKey) }, principal, null);
    }
}
