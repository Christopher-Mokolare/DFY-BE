using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class SuperAdminChangesControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Two_distinct_SuperAdmins_must_approve_promotion()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-two-person-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        context.Users.AddRange(
            CreateUser(1, "root1@example.com", "Admin,SuperAdmin"),
            CreateUser(2, "root2@example.com", "Admin,SuperAdmin"),
            CreateUser(3, "candidate@example.com", "User"));
        await context.SaveChangesAsync();

        var requester = CreateController(context, 1);
        var created = await requester.Create(new CreateSuperAdminChangeRequest
        {
            Action = "grant",
            TargetUserId = 3,
            Reason = "Promote this verified operations lead to platform security owner"
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(created);

        var change = await context.SuperAdminChangeRequests.SingleAsync();
        var sameActorResult = await requester.Decide(change.Id, new DecideSuperAdminChangeRequest
        {
            Decision = "approve",
            Reason = "I approve my own request as the same actor"
        }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(sameActorResult).StatusCode);
        Assert.Equal("User", (await context.Users.FindAsync(3))!.Roles);

        var reviewer = CreateController(context, 2);
        var approved = await reviewer.Decide(change.Id, new DecideSuperAdminChangeRequest
        {
            Decision = "approve",
            Reason = "Independent second person approval after checking business need"
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(approved);

        var target = await context.Users.FindAsync(3);
        Assert.Equal("Admin,SuperAdmin", target!.Roles);
        Assert.NotEqual("stamp-3", target.SecurityStamp);
        Assert.Equal("approved", (await context.SuperAdminChangeRequests.SingleAsync()).Status);
        Assert.Equal(2, await context.AuditLogs.CountAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_cannot_request_own_removal()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"superadmin-last-one-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        context.Users.Add(CreateUser(1, "only-root@example.com", "Admin,SuperAdmin"));
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Create(new CreateSuperAdminChangeRequest
        {
            Action = "revoke",
            TargetUserId = 1,
            Reason = "Remove this redundant super administrator account"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(context.SuperAdminChangeRequests);
    }

    private static User CreateUser(int id, string email, string roles) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email,
        FirstName = "Test",
        LastName = "Admin",
        Roles = roles,
        EmailVerified = true,
        AuthenticatorTwoFactorEnabled = true,
        LastTwoFactorVerifiedAt = DateTime.UtcNow,
        SecurityStamp = $"stamp-{id}"
    };

    private static SuperAdminChangesController CreateController(AppDbContext context, int userId)
    {
        var controller = new SuperAdminChangesController(context);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}
