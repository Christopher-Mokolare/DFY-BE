using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AdminUserProfileCorrectionsControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Email_and_phone_corrections_revoke_sessions_and_require_new_verification()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"profile-correction-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var admin = NewUser(1, "admin@test.com", "Admin");
        admin.Roles = "Admin";
        admin.AuthenticatorTwoFactorEnabled = true;
        admin.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var target = NewUser(2, "old@test.com", "User");
        target.EmailVerified = true;
        target.PhoneNumber = "0821234567";
        target.PhoneVerified = true;
        target.PhoneVerificationCode = "123456";
        target.PhoneVerificationExpiry = DateTime.UtcNow.AddMinutes(5);
        target.SecurityStamp = "old-security-stamp";
        context.Users.AddRange(admin, target);
        await context.SaveChangesAsync();

        var email = new RecordingEmailSender();
        var notifications = new RecordingNotificationService();
        var controller = CreateController(context, admin.Id, email, notifications);
        var result = await controller.CorrectProfile(target.Id, new AdminUserProfileCorrectionRequest
        {
            FirstName = "Updated",
            LastName = "Runner",
            Email = "new@test.com",
            PhoneNumber = "+27 82 555 0101",
            Address = "Pretoria",
            Reason = "Corrected from verified account documents"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("new@test.com", target.Email);
        Assert.False(target.EmailVerified);
        Assert.NotNull(target.EmailVerificationToken);
        Assert.NotEqual("old-security-stamp", target.SecurityStamp);
        Assert.Equal("0825550101", target.PhoneNumber);
        Assert.False(target.PhoneVerified);
        Assert.Null(target.PhoneVerificationCode);
        Assert.Null(target.PhoneVerificationExpiry);
        Assert.Equal("Updated", target.FirstName);
        Assert.Equal("Pretoria", target.Address);
        Assert.Contains(context.AuditLogs, log => log.Action == "AdminUserProfileCorrected");
        Assert.Equal(2, email.Messages.Count);
        Assert.Single(notifications.Notifications);
    }

    [Fact]
    public async System.Threading.Tasks.Task Duplicate_email_correction_is_rejected_without_mutating_the_account()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"profile-correction-duplicate-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var admin = NewUser(11, "admin@test.com", "Admin");
        admin.Roles = "Admin";
        var target = NewUser(12, "target@test.com", "User");
        var other = NewUser(13, "taken@test.com", "User");
        context.Users.AddRange(admin, target, other);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id, new RecordingEmailSender(), new RecordingNotificationService());
        var result = await controller.CorrectProfile(target.Id, new AdminUserProfileCorrectionRequest
        {
            FirstName = target.FirstName, LastName = target.LastName, Email = "taken@test.com",
            PhoneNumber = target.PhoneNumber, Address = target.Address,
            Reason = "Corrected details from support ticket"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("target@test.com", target.Email);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task Google_linked_account_email_cannot_be_changed_without_provider_relink()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"profile-correction-google-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var admin = NewUser(31, "admin@test.com", "Admin");
        admin.Roles = "Admin";
        var target = NewUser(32, "google@test.com", "Google");
        target.GoogleSubject = "google-subject-32";
        context.Users.AddRange(admin, target);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id, new RecordingEmailSender(), new RecordingNotificationService());
        var result = await controller.CorrectProfile(target.Id, new AdminUserProfileCorrectionRequest
        {
            FirstName = target.FirstName, LastName = target.LastName, Email = "new-google@test.com",
            PhoneNumber = target.PhoneNumber, Address = target.Address,
            Reason = "Attempted Google-linked email correction"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("google@test.com", target.Email);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task Administrative_accounts_cannot_be_corrected_through_user_profile_workflow()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"profile-correction-admin-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var admin = NewUser(21, "admin@test.com", "Admin");
        admin.Roles = "Admin";
        var target = NewUser(22, "super@test.com", "Super");
        target.Roles = "Admin,SuperAdmin";
        context.Users.AddRange(admin, target);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id, new RecordingEmailSender(), new RecordingNotificationService());
        var result = await controller.CorrectProfile(target.Id, new AdminUserProfileCorrectionRequest
        {
            FirstName = "Super", LastName = "Admin", Email = "super2@test.com",
            PhoneNumber = target.PhoneNumber, Address = target.Address,
            Reason = "Attempted ordinary profile correction"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("super@test.com", target.Email);
        Assert.Empty(context.AuditLogs);
    }

    private static User NewUser(int id, string email, string firstName) => new()
    {
        Id = id, FirstName = firstName, LastName = "Test",
        Email = email, NormalizedEmail = email.ToLowerInvariant(),
        PhoneNumber = "0821234567", UserType = "runner",
        PasswordHash = "test-hash", EmailVerified = true, PhoneVerified = true,
        SecurityStamp = Guid.NewGuid().ToString("N")
    };

    private static AdminUserProfileCorrectionsController CreateController(
        AppDbContext context, int actorId, IEmailSender emailSender, INotificationService notifications)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Frontend:BaseUrl"] = "https://staging.example.test" })
            .Build();
        var controller = new AdminUserProfileCorrectionsController(context, configuration, emailSender, notifications);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
            }
        };
        return controller;
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(string Recipient, string Subject, string Body)> Messages { get; } = [];
        public System.Threading.Tasks.Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Messages.Add((recipient, subject, body));
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(int UserId, string Type, string Title, string Message)> Notifications { get; } = [];
        public System.Threading.Tasks.Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null)
        {
            Notifications.Add((userId, type, title, message));
            return System.Threading.Tasks.Task.CompletedTask;
        }
        public System.Threading.Tasks.Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task CreateNotificationFromOutboxAsync(long outboxId, int userId, string type, string title, string message, CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
    }
}
