using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public sealed class SuperAdminInvitationsControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task SuperAdmin_can_create_single_use_invitation_with_hashed_token()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-invite-create-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        context.Users.Add(new User
        {
            Id = 10, Email = "root@example.com", NormalizedEmail = "root@example.com",
            Roles = "Admin,SuperAdmin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow, SecurityStamp = "root-stamp"
        });
        await context.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var controller = CreateController(context, sender, 10);
        var result = await controller.Create(new CreateAdminInvitationRequest
        {
            Email = "newadmin@example.com",
            Reason = "Operations administrator onboarding"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var invitation = Assert.Single(context.AdminInvitations);
        Assert.Equal("newadmin@example.com", invitation.Email);
        Assert.Equal("pending", invitation.Status);
        Assert.Equal(DateTime.UtcNow.Date, invitation.CreatedAt.Date);
        Assert.Contains("newadmin@example.com", sender.Body);
        var rawToken = sender.Body.Split("#token=", StringSplitOptions.None)[1].Split('\\r', '\\n')[0].Trim();
        Assert.Equal(HashToken(rawToken), invitation.TokenHash);
        Assert.Single(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task Ordinary_Admin_cannot_create_invitation()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-invite-deny-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        context.Users.Add(new User
        {
            Id = 11, Email = "admin@example.com", NormalizedEmail = "admin@example.com",
            Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var controller = CreateController(context, sender, 11);
        var result = await controller.Create(new CreateAdminInvitationRequest
        {
            Email = "newadmin@example.com",
            Reason = "Operations administrator onboarding"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(context.AdminInvitations);
        Assert.Empty(sender.Body);
    }

    [Fact]
    public async System.Threading.Tasks.Task Invitation_acceptance_requires_matching_verified_email_and_recent_MFA()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-invite-accept-deny-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        var token = "one-time-secret-token";
        context.Users.Add(new User
        {
            Id = 12, Email = "different@example.com", NormalizedEmail = "different@example.com",
            EmailVerified = true, Roles = "User", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow, SecurityStamp = "user-stamp"
        });
        context.AdminInvitations.Add(new AdminInvitation
        {
            Email = "invited@example.com", NormalizedEmail = "invited@example.com",
            TokenHash = HashToken(token), Reason = "Operations administrator onboarding",
            Status = "pending", CreatedByUserId = 10, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(48)
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, new FakeEmailSender(), 12);
        var result = await controller.Accept(new AcceptAdminInvitationRequest { Token = token }, CancellationToken.None);

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        Assert.Equal("User", (await context.Users.FindAsync(12))!.Roles);
        Assert.Equal("pending", (await context.AdminInvitations.SingleAsync()).Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task Verified_invitee_with_recent_MFA_can_accept_once()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-invite-accept-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        var token = "another-one-time-secret";
        context.Users.Add(new User
        {
            Id = 13, Email = "invited@example.com", NormalizedEmail = "invited@example.com",
            EmailVerified = true, Roles = "User", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow, SecurityStamp = "old-stamp"
        });
        context.AdminInvitations.Add(new AdminInvitation
        {
            Email = "invited@example.com", NormalizedEmail = "invited@example.com",
            TokenHash = HashToken(token), Reason = "Operations administrator onboarding",
            Status = "pending", CreatedByUserId = 10, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(48)
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, new FakeEmailSender(), 13);
        var result = await controller.Accept(new AcceptAdminInvitationRequest { Token = token }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var user = await context.Users.FindAsync(13);
        Assert.Equal("Admin", user!.Roles);
        Assert.NotEqual("old-stamp", user.SecurityStamp);
        Assert.Equal("accepted", (await context.AdminInvitations.SingleAsync()).Status);
        Assert.Single(context.AuditLogs);

        var replay = await controller.Accept(new AcceptAdminInvitationRequest { Token = token }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(replay);
    }

    private static SuperAdminInvitationsController CreateController(AppDbContext context, FakeEmailSender sender, int userId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Frontend:BaseUrl"] = "https://dfy-fe-staging.onrender.com" })
            .Build();
        var controller = new SuperAdminInvitationsController(context, sender, config, NullLogger<SuperAdminInvitationsController>.Instance);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class FakeEmailSender : IEmailSender
    {
        public string Body { get; private set; } = string.Empty;
        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Body = body;
            return Task.CompletedTask;
        }
    }
}
