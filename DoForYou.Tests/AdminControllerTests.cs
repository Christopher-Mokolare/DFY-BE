using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public class AdminControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task LegacyAdmin_cannot_change_another_users_role()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-role-denied-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(42, "Admin");
        var target = CreateUser(43, "User");
        target.EmailVerified = true;
        target.SecurityStamp = "target-session-stamp";
        context.Users.AddRange(actor, target);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        using var request = JsonDocument.Parse("""{"role":"Admin","reason":"Promote support user"}""");

        var result = await controller.UpdateUserRole(target.Id, request.RootElement);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Equal("User", target.Roles);
        Assert.Equal("target-session-stamp", target.SecurityStamp);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_with_fresh_MFA_can_change_another_users_role_and_audit_it()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-role-allowed-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(42, "Admin,SuperAdmin");
        actor.AuthenticatorTwoFactorEnabled = true;
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var target = CreateUser(43, "User");
        target.EmailVerified = true;
        target.SecurityStamp = "target-session-stamp";
        context.Users.AddRange(actor, target);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        using var request = JsonDocument.Parse("""{"role":"Admin","reason":"Approved support administrator"}""");

        var result = await controller.UpdateUserRole(target.Id, request.RootElement);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Admin", target.Roles);
        Assert.NotEqual("target-session-stamp", target.SecurityStamp);
        Assert.Equal("UpdateUserRole", Assert.Single(context.AuditLogs).Action);
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_cannot_grant_Admin_access_to_unverified_email()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-role-unverified-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(44, "Admin,SuperAdmin");
        actor.AuthenticatorTwoFactorEnabled = true;
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var target = CreateUser(45, "User");
        target.EmailVerified = false;
        context.Users.AddRange(actor, target);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        using var request = JsonDocument.Parse("""{"role":"Admin","reason":"Grant verified admin access"}""");

        var result = await controller.UpdateUserRole(target.Id, request.RootElement);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("User", target.Roles);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task Admin_can_suspend_and_reinstate_a_regular_account_with_audit_and_session_revocation()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-user-suspension-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(46, "Admin");
        var target = CreateUser(47, "User");
        target.SecurityStamp = "old-session";
        context.Users.AddRange(actor, target);
        await context.SaveChangesAsync();
        var controller = CreateController(context, actor.Id);

        var suspend = await controller.UpdateUserSuspension(target.Id,
            new UpdateUserSuspensionRequest { IsSuspended = true, Reason = "Confirmed platform abuse report" });

        Assert.IsType<OkObjectResult>(suspend.Result);
        Assert.True(target.IsSuspended);
        Assert.NotEqual("old-session", target.SecurityStamp);
        Assert.Equal("SuspendUser", Assert.Single(context.AuditLogs).Action);

        var reinstate = await controller.UpdateUserSuspension(target.Id,
            new UpdateUserSuspensionRequest { IsSuspended = false, Reason = "Appeal reviewed and accepted" });

        Assert.IsType<OkObjectResult>(reinstate.Result);
        Assert.False(target.IsSuspended);
        Assert.Null(target.SuspendedAt);
        Assert.Equal(2, context.AuditLogs.Count());
        Assert.Contains(context.AuditLogs, log => log.Action == "ReinstateUser");
    }

    [Fact]
    public async System.Threading.Tasks.Task Admin_cannot_suspend_another_administrator()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-suspend-admin-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(48, "Admin");
        var target = CreateUser(49, "Admin,SuperAdmin");
        context.Users.AddRange(actor, target);
        await context.SaveChangesAsync();
        var controller = CreateController(context, actor.Id);

        var result = await controller.UpdateUserSuspension(target.Id,
            new UpdateUserSuspensionRequest { IsSuspended = true, Reason = "Security concern requires suspension" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False(target.IsSuspended);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetUsers_returns_marketplace_role_separately_from_system_roles()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-marketplace-roles-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var creator = CreateUser(51, "User");
        creator.UserType = "creator";
        var runner = CreateUser(52, "User");
        runner.UserType = "runner";
        var both = CreateUser(53, "Admin,SuperAdmin");
        both.UserType = "both";
        var unset = CreateUser(54, "User");
        unset.UserType = "";
        context.Users.AddRange(creator, runner, both, unset);
        await context.SaveChangesAsync();

        var controller = new AdminController(
            context, null!, new DoForYou.API.Services.UserPolicyService(), null!, null!, null!);

        var result = await controller.GetUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = ok.Value!.GetType().GetProperty("Data")!.GetValue(ok.Value)!;
        var users = (System.Collections.IEnumerable)data.GetType().GetProperty("users")!.GetValue(data)!;
        var byId = users.Cast<object>().ToDictionary(
            user => (int)user.GetType().GetProperty("id")!.GetValue(user)!);

        Assert.Equal("Creator", byId[creator.Id].GetType().GetProperty("marketplaceRole")!.GetValue(byId[creator.Id]));
        Assert.Equal("Runner", byId[runner.Id].GetType().GetProperty("marketplaceRole")!.GetValue(byId[runner.Id]));
        Assert.Equal("Creator & Runner", byId[both.Id].GetType().GetProperty("marketplaceRole")!.GetValue(byId[both.Id]));
        Assert.Equal("Not selected", byId[unset.Id].GetType().GetProperty("marketplaceRole")!.GetValue(byId[unset.Id]));
        Assert.Equal("Admin,SuperAdmin", byId[both.Id].GetType().GetProperty("role")!.GetValue(byId[both.Id]));
        Assert.Equal("both", byId[both.Id].GetType().GetProperty("userType")!.GetValue(byId[both.Id]));
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_account_directory_requires_persisted_SuperAdmin_role()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-directory-denied-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(61, "Admin");
        context.Users.Add(actor);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        var result = await controller.GetSuperAdminAccounts();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_account_directory_returns_admin_and_candidate_accounts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-directory-allowed-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(62, "Admin,SuperAdmin");
        var admin = CreateUser(63, "Admin");
        admin.Email = "admin@example.test";
        var candidate = CreateUser(64, "User");
        candidate.Email = "candidate@example.test";
        var archived = CreateUser(65, "Admin");
        archived.IsArchived = true;
        context.Users.AddRange(actor, admin, candidate, archived);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        var result = await controller.GetSuperAdminAccounts(search: "example.test");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = ok.Value!;
        var data = response.GetType().GetProperty("Data")!.GetValue(response)!;
        var accounts = ((System.Collections.IEnumerable)data.GetType().GetProperty("accounts")!.GetValue(data)!)
            .Cast<object>().ToArray();

        Assert.Equal(3, accounts.Length);
        Assert.Contains(accounts, account =>
            (int)account.GetType().GetProperty("id")!.GetValue(account)! == admin.Id &&
            (bool)account.GetType().GetProperty("isAdministrator")!.GetValue(account)!);
        Assert.Contains(accounts, account =>
            (int)account.GetType().GetProperty("id")!.GetValue(account)! == candidate.Id &&
            !(bool)account.GetType().GetProperty("isAdministrator")!.GetValue(account)!);
    }

    private static User CreateUser(int id, string roles) => new()
    {
        Id = id,
        FirstName = "Test",
        LastName = "User",
        Email = $"role-{id}@example.test",
        PhoneNumber = "0000000000",
        PasswordHash = "not-a-real-password-hash",
        Roles = roles,
        SecurityStamp = $"session-stamp-{id}"
    };

    private static AdminController CreateController(AppDbContext context, int actorId)
    {
        var controller = new AdminController(context, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) },
                        "test"))
                }
            }
        };
        return controller;
    }
}
