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
