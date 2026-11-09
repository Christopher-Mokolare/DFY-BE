using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskEntity = DoForYou.API.Models.Task;
using Task = System.Threading.Tasks.Task;

namespace DoForYou.Tests;

public class DisputeLifecycleTests
{
    [Fact]
    public async System.Threading.Tasks.Task EligibleDisputeFreezesHeldEscrowAndCannotBeRepeated()
    {
        using var context = CreateContext(nameof(EligibleDisputeFreezesHeldEscrowAndCannotBeRepeated));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, creator.Id);
        var request = new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The work was not completed as agreed.",
            Category = "Incomplete work"
        };

        var result = await controller.RaiseDispute(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.True(response.Success, response.Message);
        Assert.Equal("disputed", task.EscrowStatus);
        Assert.Equal("DisputePending", task.PaymentStatus);
        Assert.Equal("AwaitingConfirmation", task.TaskStatus);
        var dispute = Assert.Single(context.Disputes);
        Assert.Equal("Open", dispute.Status);
        Assert.Equal(creator.Id, dispute.ReportedByUserId);

        var repeated = await controller.RaiseDispute(request);
        Assert.IsType<ConflictObjectResult>(repeated.Result);
        Assert.Single(context.Disputes);
    }

    [Theory]
    [InlineData("Posted", "EscrowHeld", "held")]
    [InlineData("Claimed", "EscrowHeld", "held")]
    [InlineData("RunnerPaid", "EscrowReleased", "released")]
    [InlineData("Cancelled", "Refunded", "refunded")]
    [InlineData("AwaitingConfirmation", "DisputePending", "disputed")]
    public async System.Threading.Tasks.Task IneligibleTaskStateCannotOpenDisputeOrMutateEscrow(
        string taskStatus,
        string paymentStatus,
        string escrowStatus)
    {
        using var context = CreateContext($"{nameof(IneligibleTaskStateCannotOpenDisputeOrMutateEscrow)}-{taskStatus}");
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, taskStatus, paymentStatus, escrowStatus);
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, creator.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The work or payment status is incorrect.",
            Category = "Other"
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal(taskStatus, task.TaskStatus);
        Assert.Equal(paymentStatus, task.PaymentStatus);
        Assert.Equal(escrowStatus, task.EscrowStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task NonParticipantCannotFreezeAnotherTasksEscrow()
    {
        using var context = CreateContext(nameof(NonParticipantCannotFreezeAnotherTasksEscrow));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var stranger = CreateUser(3, "runner", "stranger@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner, stranger);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, stranger.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "An unrelated user is trying to dispute this task.",
            Category = "Abuse"
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunnerCannotRaiseCreatorCompletionDispute()
    {
        using var context = CreateContext(nameof(RunnerCannotRaiseCreatorCompletionDispute));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, runner.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The creator changed the scope after I accepted.",
            Category = "Scope change"
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreatorCannotDisputeAfterConfirmationWindowExpires()
    {
        using var context = CreateContext(nameof(CreatorCannotDisputeAfterConfirmationWindowExpires));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        task.EscrowHoldUntil = DateTime.UtcNow.AddMinutes(-1);
        task.CompletedAt = DateTime.UtcNow.AddHours(-25);
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, creator.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The work does not match the agreed requirements.",
            Category = "Quality"
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunnerIncidentReportIsStoredWithoutChangingEscrowOrPayment()
    {
        using var context = CreateContext(nameof(RunnerIncidentReportIsStoredWithoutChangingEscrowOrPayment));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, "Claimed", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateIncidentController(context, runner.Id);
        var result = await controller.Report(task.TaskId, new CreateTaskIncidentRequest
        {
            Category = "Scope change",
            Description = "The creator added additional work after the task was accepted."
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.True(response.Success);
        var incident = Assert.Single(context.TaskIncidents);
        Assert.Equal(runner.Id, incident.ReporterUserId);
        Assert.Equal("Open", incident.Status);
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
        Assert.Equal("Claimed", task.TaskStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task AdminIncidentHoldBlocksReleaseAndExtendsConfirmationDeadlineWhenReleased()
    {
        using var context = CreateContext(nameof(AdminIncidentHoldBlocksReleaseAndExtendsConfirmationDeadlineWhenReleased));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var admin = CreateUser(3, "admin", "admin@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner, admin);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var participantController = CreateIncidentController(context, runner.Id);
        var report = await participantController.Report(task.TaskId, new CreateTaskIncidentRequest
        {
            Category = "Scope change",
            Description = "The agreed task scope changed after acceptance and needs support review."
        });
        Assert.IsType<OkObjectResult>(report.Result);
        var incident = Assert.Single(context.TaskIncidents);
        var originalDeadline = task.EscrowHoldUntil!.Value;

        var adminController = CreateIncidentController(context, admin.Id, "Admin");
        var hold = await adminController.PlaceEscrowHold(incident.Id, new IncidentEscrowHoldRequest
        {
            Reason = "Reviewing the runner's documented scope-change evidence."
        });
        Assert.IsType<OkObjectResult>(hold.Result);
        Assert.Equal("incident_hold", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
        Assert.Equal("AwaitingConfirmation", task.TaskStatus);

        var releaseWhileHeld = await new EscrowService(context, NullLogger<EscrowService>.Instance)
            .ReleaseEscrowAsync(task.Id, force: true);
        Assert.False(releaseWhileHeld);

        var closeWhileHeld = await adminController.Review(incident.Id, new ReviewTaskIncidentRequest
        {
            Status = "Resolved",
            AdminNotes = "The review is complete but the financial hold remains active."
        });
        Assert.IsType<ConflictObjectResult>(closeWhileHeld.Result);

        incident.EscrowHoldPlacedAt = DateTime.UtcNow.AddMinutes(-15);
        await context.SaveChangesAsync();
        var release = await adminController.ReleaseEscrowHold(incident.Id, new IncidentEscrowHoldRequest
        {
            Reason = "The incident review is complete and the hold can safely be removed."
        });

        Assert.IsType<OkObjectResult>(release.Result);
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
        Assert.Equal("AwaitingConfirmation", task.TaskStatus);
        Assert.True(task.EscrowHoldUntil >= originalDeadline.AddMinutes(14));
        Assert.NotNull(incident.EscrowHoldReleasedAt);
        Assert.Equal(2, await context.AuditLogs.CountAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task CreatorCanOpenFormalDisputeWhileIncidentHoldIsActive()
    {
        using var context = CreateContext(nameof(CreatorCanOpenFormalDisputeWhileIncidentHoldIsActive));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var admin = CreateUser(3, "admin", "admin@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner, admin);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var runnerController = CreateIncidentController(context, runner.Id);
        var report = await runnerController.Report(task.TaskId, new CreateTaskIncidentRequest
        {
            Category = "Scope change",
            Description = "The task scope changed after acceptance and needs a support review."
        });
        Assert.IsType<OkObjectResult>(report.Result);
        var incident = Assert.Single(context.TaskIncidents);

        var adminController = CreateIncidentController(context, admin.Id, "Admin");
        var hold = await adminController.PlaceEscrowHold(incident.Id, new IncidentEscrowHoldRequest
        {
            Reason = "Reviewing the runner's submitted scope-change evidence."
        });
        Assert.IsType<OkObjectResult>(hold.Result);
        Assert.Equal("incident_hold", task.EscrowStatus);

        // Simulate the original confirmation deadline passing while the hold is active.
        // The effective dispute clock must remain paused for the full hold duration.
        incident.EscrowHoldPlacedAt = DateTime.UtcNow.AddHours(-25);
        incident.EscrowHoldDeadlineBefore = DateTime.UtcNow.AddHours(-1);
        await context.SaveChangesAsync();

        var creatorController = CreateController(context, creator.Id);
        var dispute = await creatorController.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The completed work does not match the agreed requirements.",
            Category = "Quality"
        });

        var ok = Assert.IsType<OkObjectResult>(dispute.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal("disputed", task.EscrowStatus);
        Assert.Equal("DisputePending", task.PaymentStatus);
        Assert.NotNull(incident.EscrowHoldReleasedAt);
        Assert.Contains("formal creator completion dispute", incident.EscrowHoldReleaseReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await context.AuditLogs.CountAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task NonParticipantCannotSubmitTaskIncident()
    {
        using var context = CreateContext(nameof(NonParticipantCannotSubmitTaskIncident));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var stranger = CreateUser(3, "runner", "stranger@test.com");
        var task = CreateTask(creator.Id, runner.Id, "Claimed", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner, stranger);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateIncidentController(context, stranger.Id);
        var result = await controller.Report(task.TaskId, new CreateTaskIncidentRequest
        {
            Category = "Safety concern",
            Description = "An unrelated user is attempting to report this task."
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await context.TaskIncidents.ToListAsync());
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
    }

    private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private static User CreateUser(int id, string type, string email) => new()
    {
        Id = id,
        FirstName = type == "creator" ? "Creator" : "Runner",
        LastName = "Test",
        Email = email,
        NormalizedEmail = email.ToLowerInvariant(),
        PhoneNumber = "0821234567",
        UserType = type,
        EmailVerified = true,
        IdentityVerificationStatus = type == "runner" ? "passed" : "not_started",
        AuthenticatorTwoFactorEnabled = type == "runner",
        PasswordHash = "test-hash",
        Roles = "User"
    };

    private static TaskEntity CreateTask(int creatorId, int runnerId, string taskStatus, string paymentStatus, string escrowStatus) => new()
    {
        TaskId = "DFY-DISPUTE-" + Guid.NewGuid().ToString("N")[..8],
        TaskName = "Test task",
        TaskDescription = "A task used to test dispute escrow transitions.",
        Category = "Other",
        Area = "Pretoria",
        DateNeeded = DateTime.UtcNow.AddDays(1),
        Budget = 200m,
        CommissionAmount = 30m,
        PayoutAmount = 170m,
        CreatedByUserId = creatorId,
        AcceptedByUserId = runnerId,
        TaskStatus = taskStatus,
        PaymentStatus = paymentStatus,
        EscrowStatus = escrowStatus,
        EscrowHoldUntil = DateTime.UtcNow.AddHours(23),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        CompletedAt = taskStatus == "AwaitingConfirmation" ? DateTime.UtcNow : null
    };

    private static TaskIncidentsController CreateIncidentController(AppDbContext context, int userId, string role = "User")
    {
        var controller = new TaskIncidentsController(context, new NoOpNotificationService());
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ],
            "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static DisputesController CreateController(AppDbContext context, int userId)
    {
        var controller = new DisputesController(
            context,
            new EscrowService(context, NullLogger<EscrowService>.Instance),
            new NoOpNotificationService(),
            new FakePaymentProvider());
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed class FakePaymentProvider : IPaymentProvider
    {
        public Task<PaymentCreationResult> CreatePaymentAsync(
            TaskEntity task,
            User customer,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new PaymentCreationResult(true, "https://checkout.test/payment", "DFY_TEST", null));

        public Task<PaymentVerificationResult> VerifyPaymentAsync(
            string transactionReference,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new PaymentVerificationResult(true, true, "TX-TEST", "success", 200m, transactionReference, null));

        public Task<RefundResult> CreateRefundAsync(
            string transactionId,
            decimal amount,
            string reason,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new RefundResult(true, "REF-TEST", transactionId, amount, "processed", null));

        public Task<IReadOnlyList<RefundStatusResult>> GetRefundsByTransactionIdAsync(
            string transactionId,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<RefundStatusResult>>(Array.Empty<RefundStatusResult>());

        public bool VerifyWebhookSignature(string rawBody, string? signature, bool isTimestampedSignature = false) => true;
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => Task.CompletedTask;
        public Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => Task.CompletedTask;
        public Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => Task.CompletedTask;
        public Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => Task.CompletedTask;
    }
}
