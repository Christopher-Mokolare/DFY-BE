using System.Security.Claims;
using DoForYou.API.Authorization;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AdminPermissionsControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Fresh_SuperAdmin_can_grant_a_time_limited_permission_and_audit_it()
    {
        await using var db = CreateContext();
        db.Users.AddRange(SuperAdmin(1), Admin(2));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Grant(new GrantAdminPermissionRequest
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.KycReview,
            Reason = "Assigned to the identity review team.",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var grant = await db.AdminPermissionGrants.SingleAsync();
        Assert.Equal(2, grant.UserId);
        Assert.Equal(AdminPermissionPolicies.KycReview, grant.PermissionKey);
        Assert.Equal(1, grant.GrantedByUserId);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("GrantAdminPermission", audit.Action);
        Assert.Equal(grant.Id, audit.EntityId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Self_assignment_is_rejected()
    {
        await using var db = CreateContext();
        db.Users.Add(SuperAdmin(1));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Grant(new GrantAdminPermissionRequest
        {
            UserId = 1, PermissionKey = AdminPermissionPolicies.UsersManage,
            Reason = "Self assignment is not allowed.",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.AdminPermissionGrants.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Unknown_permission_is_rejected()
    {
        await using var db = CreateContext();
        db.Users.AddRange(SuperAdmin(1), Admin(2));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Grant(new GrantAdminPermissionRequest
        {
            UserId = 2, PermissionKey = "not-a-supported-permission",
            Reason = "This permission is not in the catalogue.",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.AdminPermissionGrants.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Duplicate_active_permission_is_rejected()
    {
        await using var db = CreateContext();
        db.Users.AddRange(SuperAdmin(1), Admin(2));
        db.AdminPermissionGrants.Add(new AdminPermissionGrant
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.KycReview,
            GrantedByUserId = 1, Reason = "Existing review assignment.",
            GrantedAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(5)
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Grant(new GrantAdminPermissionRequest
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.KycReview,
            Reason = "A duplicate review assignment request.",
            ExpiresAt = DateTime.UtcNow.AddDays(10)
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(await db.AdminPermissionGrants.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Fresh_SuperAdmin_can_revoke_a_grant_and_audit_it()
    {
        await using var db = CreateContext();
        db.Users.Add(SuperAdmin(1));
        var grant = new AdminPermissionGrant
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.KycReview,
            GrantedByUserId = 1, Reason = "Assigned to the identity review team.",
            GrantedAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        db.AdminPermissionGrants.Add(grant);
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Revoke(grant.Id,
            new RevokeAdminPermissionRequest { Reason = "Reviewer access is no longer required." },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(grant.RevokedAt);
        Assert.Equal(1, grant.RevokedByUserId);
        Assert.Equal("RevokeAdminPermission", (await db.AuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async System.Threading.Tasks.Task Stale_SuperAdmin_MFA_cannot_grant_permissions()
    {
        await using var db = CreateContext();
        var actor = SuperAdmin(1);
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow.AddHours(-1);
        db.Users.AddRange(actor, Admin(2));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 1).Grant(new GrantAdminPermissionRequest
        {
            UserId = 2, PermissionKey = AdminPermissionPolicies.KycReview,
            Reason = "Assigned to the identity review team.",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.AdminPermissionGrants.ToListAsync());
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-permission-controller-{Guid.NewGuid():N}").Options;
        return new AppDbContext(options);
    }

    private static AdminPermissionsController CreateController(AppDbContext db, int actorId)
    {
        var controller = new AdminPermissionsController(db);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
            new Claim(ClaimTypes.Role, "SuperAdmin")
        }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static User SuperAdmin(int id) => new()
    {
        Id = id, Email = $"superadmin{id}@example.test", NormalizedEmail = $"superadmin{id}@example.test",
        PasswordHash = "test-hash", Roles = "SuperAdmin", EmailVerified = true,
        AuthenticatorTwoFactorEnabled = true, LastTwoFactorVerifiedAt = DateTime.UtcNow
    };

    private static User Admin(int id) => new()
    {
        Id = id, Email = $"admin{id}@example.test", NormalizedEmail = $"admin{id}@example.test",
        PasswordHash = "test-hash", Roles = "Admin", EmailVerified = true,
        AuthenticatorTwoFactorEnabled = true, LastTwoFactorVerifiedAt = DateTime.UtcNow
    };
}
