using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskEntity = DoForYou.API.Models.Task;
using TaskResult = System.Threading.Tasks.Task;

namespace DoForYou.Tests;

public class ScenarioTests
{
    [Fact]
    public async System.Threading.Tasks.Task PosterCanCreateTask_WhenProfileIsComplete()
    {
        using var context = CreateContext(nameof(PosterCanCreateTask_WhenProfileIsComplete));
        var poster = new User
        {
            Id = 1,
            FirstName = "Jane",
            LastName = "Poster",
            Email = "poster@example.com",
            PhoneNumber = "0712345678",
            UserType = "creator",
            ProfileCompleted = true,
            Address = "123 Main Road",
            Roles = "User",
        };
        context.Users.Add(poster);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, poster.Id);
        var request = new CreateTaskRequest
        {
            TaskDescription = "Fix the leaking tap",
            Category = "Home",
            Area = "Johannesburg",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 500m,
            Notes = "Urgent fix",
            Priority = "High",
            TermsAccepted = true,
        };

        var result = await controller.CreateTask(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.True(response.Success);
        Assert.Equal("Task created successfully. Complete payment to activate.", response.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunnerCanClaimPostedTask()
    {
        using var context = CreateContext(nameof(RunnerCanClaimPostedTask));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(poster, runner);

        var task = new TaskEntity
        {
            TaskId = "DFY-1",
            TaskDescription = "Garden maintenance",
            Category = "Home",
            Area = "Cape Town",
            DateNeeded = DateTime.UtcNow.AddDays(3),
            Budget = 750m,
            CommissionAmount = 112.50m,
            PayoutAmount = 637.50m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Posted",
            EscrowStatus = "held",
            CreatedByUserId = poster.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, runner.Id);
        var result = await controller.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Name", HelperContact = "0820000000" });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        var savedTask = await context.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.True(response.Success);
        Assert.Equal("Claimed", savedTask.TaskStatus);
        Assert.Equal(runner.Id, savedTask.AcceptedByUserId);
    }

    [Fact]
    public async System.Threading.Tasks.Task DuplicateClaim_IsRejected()
    {
        using var context = CreateContext(nameof(DuplicateClaim_IsRejected));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner1 = CreateUser(2, "runner", true, "runner1@test.com");
        var runner2 = CreateUser(3, "runner", true, "runner2@test.com");
        context.Users.AddRange(poster, runner1, runner2);

        var task = new TaskEntity
        {
            TaskId = "DFY-2",
            TaskDescription = "Plumbing repair",
            Category = "Home",
            Area = "Durban",
            DateNeeded = DateTime.UtcNow.AddDays(4),
            Budget = 600m,
            CommissionAmount = 90m,
            PayoutAmount = 510m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Posted",
            EscrowStatus = "held",
            CreatedByUserId = poster.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller1 = CreateTasksController(context, runner1.Id);
        var first = await controller1.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner One", HelperContact = "0811111111" });
        Assert.True(((ApiResponse<bool>)((OkObjectResult)first.Result!).Value!).Success);

        var controller2 = CreateTasksController(context, runner2.Id);
        var second = await controller2.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Two", HelperContact = "0822222222" });
        var ok = Assert.IsType<OkObjectResult>(second.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        Assert.False(response.Success);
        Assert.Equal("Task not available", response.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task EscrowRelease_CreditsRunnerWalletAndMarksTaskPaid()
    {
        using var context = CreateContext(nameof(EscrowRelease_CreditsRunnerWalletAndMarksTaskPaid));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(poster, runner);

        var task = new TaskEntity
        {
            TaskId = "DFY-3",
            TaskDescription = "Electrical fix",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(3),
            Budget = 800m,
            CommissionAmount = 120m,
            PayoutAmount = 680m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Completed",
            EscrowStatus = "held",
            EscrowHoldUntil = DateTime.UtcNow.AddHours(-1),
            CreatedByUserId = poster.Id,
            AcceptedByUserId = runner.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var escrow = new EscrowService(context, NullLogger<EscrowService>.Instance);
        var released = await escrow.ReleaseEscrowAsync(task.Id);

        Assert.True(released);

        var refreshedRunner = await context.Users.SingleAsync(u => u.Id == runner.Id);
        var transaction = await context.WalletTransactions.SingleAsync(w => w.UserId == runner.Id);

        Assert.Equal(680m, refreshedRunner.WalletBalance);
        Assert.Equal("credit", transaction.TransactionType);
        Assert.Equal("completed", transaction.Status);
        Assert.Equal(task.TaskId, transaction.Reference);
    }

    [Fact]
    public async System.Threading.Tasks.Task BankAccountValidation_AndWithdrawalFee_AreWithinExpectedLimits()
    {
        using var context = CreateContext(nameof(BankAccountValidation_AndWithdrawalFee_AreWithinExpectedLimits));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:OtpSalt"] = "salt-value" })
            .Build();
        var bankingService = new BankingService(context, new NoOpNotificationService(), config);

        var valid = await bankingService.ValidateBankAccountAsync("FNB", "123456789", "250655");
        var minFee = await bankingService.CalculateWithdrawalFeeAsync(100m);
        var highFee = await bankingService.CalculateWithdrawalFeeAsync(1000m);

        Assert.True(valid);
        Assert.Equal(5.00m, minFee);
        Assert.Equal(15.00m, highFee);
    }

    private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

        return new AppDbContext(options);
    }

    private static TasksController CreateTasksController(AppDbContext context, int userId)
    {
        var controller = new TasksController(
            context,
            new RulesEngine(context),
            new EscrowService(context, NullLogger<EscrowService>.Instance),
            new NoOpNotificationService(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-key-for-scenario-tests-32chars!!",
                ["PayFast:MerchantId"] = "10000100",
                ["PayFast:MerchantKey"] = "test-merchant-key",
                ["BackendUrl"] = "https://example.test",
                ["FrontendUrl"] = "https://example.test",
            }).Build());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                }, "TestAuth"))
            }
        };

        return controller;
    }

    private static User CreateUser(int id, string userType, bool profileCompleted, string email)
    {
        return new User
        {
            Id = id,
            FirstName = userType == "creator" ? "Poster" : "Runner",
            LastName = "User",
            Email = email,
            PhoneNumber = "0712345678",
            UserType = userType,
            ProfileCompleted = profileCompleted,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "hash",
            IsVerified = true,
        };
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public TaskResult CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName) => TaskResult.CompletedTask;
        public TaskResult NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount) => TaskResult.CompletedTask;
        public TaskResult NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
    }
}
