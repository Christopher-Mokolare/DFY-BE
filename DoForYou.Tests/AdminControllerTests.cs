using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.DTOs;
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

    [Fact]
    public async System.Threading.Tasks.Task Single_SuperAdmin_cannot_force_release_escrow_without_dual_approval()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-force-release-dual-approval-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(71, "Admin,SuperAdmin");
        actor.AuthenticatorTwoFactorEnabled = true;
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(72, "User");
        var runner = CreateUser(73, "User");
        var task = new DoForYou.API.Models.Task
        {
            Id = 74,
            TaskId = "DFY-DUAL-APPROVAL-TEST",
            TaskDescription = "Test task",
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id,
            TaskStatus = "AwaitingConfirmation",
            PaymentStatus = "EscrowHeld",
            EscrowStatus = "held",
            Budget = 200m,
            PayoutAmount = 170m
        };
        context.Users.AddRange(actor, creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        var result = await controller.ForceReleaseEscrow(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Exceptional release requested after operational review" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("AwaitingConfirmation", task.TaskStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
        Assert.Equal("held", task.EscrowStatus);
        Assert.Empty(context.Payouts);
        Assert.Contains(context.AuditLogs, log =>
            log.Action == "ForceReleaseEscrowBlockedPendingDualApproval" &&
            log.EntityId == task.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task Financial_approval_requires_a_distinct_fresh_MFA_SuperAdmin_and_executes_only_after_approval()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-approval-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(81, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var approver = CreateUser(82, "Admin,SuperAdmin");
        approver.AuthenticatorTwoFactorEnabled = true;
        approver.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(83, "User");
        var runner = CreateUser(84, "User");
        var task = new DoForYou.API.Models.Task
        {
            Id = 85,
            TaskId = "DFY-FINANCIAL-APPROVAL-TEST",
            TaskDescription = "Two person release test",
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id,
            TaskStatus = "AwaitingConfirmation",
            PaymentStatus = "EscrowHeld",
            EscrowStatus = "held",
            Budget = 200m,
            PayoutAmount = 170m
        };
        context.Users.AddRange(requester, approver, creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var requesterController = CreateController(context, requester.Id);
        var requested = await requesterController.RequestFinancialApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Exceptional escrow release after completed dispute review" });
        var requestOk = Assert.IsType<OkObjectResult>(requested.Result);
        var requestId = (int)requestOk.Value!.GetType().GetProperty("Data")!.GetValue(requestOk.Value)!;
        var duplicateRequest = await requesterController.RequestFinancialApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Duplicate exceptional release request" });
        Assert.IsType<ConflictObjectResult>(duplicateRequest.Result);
        Assert.Single(context.FinancialApprovalRequests);

        var selfApproval = await requesterController.ApproveFinancialRequest(
            requestId,
            new AdminActionReasonRequest { Reason = "I approve my own requested release" });
        Assert.IsType<ConflictObjectResult>(selfApproval.Result);
        Assert.Empty(context.Payouts);

        var approved = await CreateController(context, approver.Id).ApproveFinancialRequest(
            requestId,
            new AdminActionReasonRequest { Reason = "Independently reviewed task evidence and escrow eligibility" });
        Assert.IsType<OkObjectResult>(approved.Result);
        Assert.Equal("Approved", (await context.FinancialApprovalRequests.SingleAsync()).Status);
        Assert.Empty(context.Payouts);

        approver.LastTwoFactorVerifiedAt = DateTime.UtcNow.AddMinutes(-16);
        await context.SaveChangesAsync();
        var staleApproverExecution = await CreateController(context, requester.Id).ExecuteFinancialApproval(requestId);
        Assert.IsType<ForbidResult>(staleApproverExecution.Result);
        Assert.Empty(context.Payouts);

        approver.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        var escrow = new DoForYou.API.Services.EscrowService(
            context,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DoForYou.API.Services.EscrowService>.Instance);
        var executed = await CreateController(context, requester.Id, escrow).ExecuteFinancialApproval(requestId);

        Assert.IsType<OkObjectResult>(executed.Result);
        var approval = await context.FinancialApprovalRequests.SingleAsync();
        Assert.Equal("Executed", approval.Status);
        Assert.Equal(requester.Id, approval.RequestedByUserId);
        Assert.Equal(approver.Id, approval.DecidedByUserId);
        Assert.NotNull(approval.ExecutedAt);
        var persistedTask = await context.Tasks.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("PayoutPending", persistedTask.TaskStatus);
        Assert.Equal("EscrowReleased", persistedTask.PaymentStatus);
        Assert.Equal("released", persistedTask.EscrowStatus);
        var payout = await context.Payouts.SingleAsync();
        Assert.Equal(170m, payout.Amount);
        Assert.Equal("AwaitingBankDetails", payout.Status);
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialApprovalRequested");
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialApprovalGranted");
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialApprovalExecuted");

        var repeatedExecution = await CreateController(context, requester.Id, escrow).ExecuteFinancialApproval(requestId);
        Assert.IsType<OkObjectResult>(repeatedExecution.Result);
        Assert.Single(context.Payouts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Independent_SuperAdmin_can_reject_financial_request_without_creating_a_payout()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-approval-reject-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(101, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var reviewer = CreateUser(102, "Admin,SuperAdmin");
        reviewer.AuthenticatorTwoFactorEnabled = true;
        reviewer.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(103, "User");
        var runner = CreateUser(104, "User");
        context.Users.AddRange(requester, reviewer, creator, runner);
        context.Tasks.Add(new DoForYou.API.Models.Task
        {
            Id = 105, TaskId = "DFY-FINANCIAL-REJECT-TEST", TaskDescription = "Reject test",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "AwaitingConfirmation", PaymentStatus = "EscrowHeld",
            EscrowStatus = "held", Budget = 200m, PayoutAmount = 170m
        });
        await context.SaveChangesAsync();

        var requested = await CreateController(context, requester.Id).RequestFinancialApproval(
            "DFY-FINANCIAL-REJECT-TEST",
            new AdminActionReasonRequest { Reason = "Exceptional release requires independent review" });
        var requestOk = Assert.IsType<OkObjectResult>(requested.Result);
        var requestId = (int)requestOk.Value!.GetType().GetProperty("Data")!.GetValue(requestOk.Value)!;

        var rejected = await CreateController(context, reviewer.Id).RejectFinancialRequest(
            requestId,
            new AdminActionReasonRequest { Reason = "Evidence does not support overriding the confirmation hold" });

        Assert.IsType<OkObjectResult>(rejected.Result);
        var approval = await context.FinancialApprovalRequests.SingleAsync();
        Assert.Equal("Rejected", approval.Status);
        Assert.Equal(requester.Id, approval.RequestedByUserId);
        Assert.Equal(reviewer.Id, approval.DecidedByUserId);
        Assert.Equal("Evidence does not support overriding the confirmation hold", approval.DecisionReason);
        Assert.Empty(context.Payouts);
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialApprovalRejected");
    }

    [Fact]
    public async System.Threading.Tasks.Task User_history_includes_profile_correction_before_and_after_audit()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"user-profile-history-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var admin = CreateUser(151, "Admin");
        var user = CreateUser(152, "User");
        context.Users.AddRange(admin, user);
        context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = admin.Id,
            Action = "AdminUserProfileCorrected",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = "{\"email\":\"old@example.test\"}",
            NewValues = "{\"email\":\"new@example.test\",\"reason\":\"Verified support correction\"}",
            IpAddress = "192.0.2.10",
            UserAgent = "test-agent",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var result = await CreateController(context, admin.Id).GetUserTaskHistory(user.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = ok.Value!.GetType().GetProperty("Data")!.GetValue(ok.Value)!;
        var changes = (System.Collections.IEnumerable)data.GetType().GetProperty("profileChanges")!.GetValue(data)!;

        Assert.Single(changes.Cast<object>());
        var change = changes.Cast<object>().Single();
        Assert.Equal(admin.Id, change.GetType().GetProperty("UserId")!.GetValue(change));
        Assert.Equal("AdminUserProfileCorrected", context.AuditLogs.Single().Action);
    }

    [Fact]
    public async System.Threading.Tasks.Task SuperAdmins_can_request_two_person_refund_approval_only_for_open_unpaid_disputes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-refund-request-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(111, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(112, "User");
        var runner = CreateUser(113, "User");
        context.Users.AddRange(requester, creator, runner);
        var task = new DoForYou.API.Models.Task
        {
            Id = 114, TaskId = "DFY-REFUND-APPROVAL", TaskDescription = "Refund approval test",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "Completed", PaymentStatus = "DisputePending",
            EscrowStatus = "disputed", Budget = 200m, PayoutAmount = 170m
        };
        context.Tasks.Add(task);
        context.Disputes.Add(new Dispute
        {
            TaskId = task.Id, ReportedByUserId = creator.Id,
            Issue = "Task incomplete", Category = "quality", Status = "Open"
        });
        context.PaymentAttempts.Add(new PaymentAttempt
        {
            TaskId = task.Id, Provider = "Paystack", Reference = "pay_ref_114",
            TransactionId = "987654", Amount = 200m, Status = "Completed"
        });
        await context.SaveChangesAsync();

        var requested = await CreateController(context, requester.Id).RequestFinancialRefundApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Refund after evidence review confirms incomplete work" });

        var response = Assert.IsType<OkObjectResult>(requested.Result);
        var approvalId = (int)response.Value!.GetType().GetProperty("Data")!.GetValue(response.Value)!;
        var approval = await context.FinancialApprovalRequests.SingleAsync();
        Assert.Equal(approvalId, approval.Id);
        Assert.Equal("refund_creator", approval.Action);
        Assert.Equal("Pending", approval.Status);
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialRefundApprovalRequested");

        var competingRelease = await CreateController(context, requester.Id).RequestDisputeReleaseApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Competing release request must be rejected" });
        Assert.IsType<ConflictObjectResult>(competingRelease.Result);
        Assert.Single(context.FinancialApprovalRequests);
        Assert.Empty(context.Refunds);
        Assert.Empty(context.Payouts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Disputed_escrow_release_requires_independent_two_person_approval()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-dispute-release-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(141, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var approver = CreateUser(142, "Admin,SuperAdmin");
        approver.AuthenticatorTwoFactorEnabled = true;
        approver.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(143, "User");
        var runner = CreateUser(144, "User");
        context.Users.AddRange(requester, approver, creator, runner);
        var task = new DoForYou.API.Models.Task
        {
            Id = 145, TaskId = "DFY-DISPUTE-RELEASE", TaskDescription = "Dispute release test",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "Completed", PaymentStatus = "DisputePending",
            EscrowStatus = "disputed", Budget = 200m, PayoutAmount = 170m
        };
        context.Tasks.Add(task);
        context.Disputes.Add(new Dispute
        {
            TaskId = task.Id, ReportedByUserId = creator.Id,
            Issue = "Work completed", Category = "quality", Status = "Open"
        });
        context.PaymentAttempts.Add(new PaymentAttempt
        {
            TaskId = task.Id, Provider = "Paystack", Reference = "pay_ref_release",
            TransactionId = "987656", Amount = 200m, Status = "Completed"
        });
        await context.SaveChangesAsync();

        var requested = await CreateController(context, requester.Id).RequestDisputeReleaseApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Release after independent review of task evidence" });
        var requestOk = Assert.IsType<OkObjectResult>(requested.Result);
        var requestId = (int)requestOk.Value!.GetType().GetProperty("Data")!.GetValue(requestOk.Value)!;

        var approved = await CreateController(context, approver.Id).ApproveFinancialRequest(
            requestId,
            new AdminActionReasonRequest { Reason = "Evidence supports payment to the assigned runner" });

        Assert.IsType<OkObjectResult>(approved.Result);
        var approval = await context.FinancialApprovalRequests.SingleAsync();
        Assert.Equal("release_to_runner", approval.Action);
        Assert.Equal("Approved", approval.Status);
        Assert.Equal(requester.Id, approval.RequestedByUserId);
        Assert.Equal(approver.Id, approval.DecidedByUserId);
        Assert.Empty(context.Payouts);
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialDisputeReleaseApprovalRequested");
        Assert.Contains(context.AuditLogs, log => log.Action == "FinancialApprovalGranted");
    }

    [Fact]
    public async System.Threading.Tasks.Task Refund_approval_rejects_charge_amount_mismatch()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-refund-amount-mismatch-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(131, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(132, "User");
        var runner = CreateUser(133, "User");
        context.Users.AddRange(requester, creator, runner);
        var task = new DoForYou.API.Models.Task
        {
            Id = 134, TaskId = "DFY-REFUND-AMOUNT-MISMATCH", TaskDescription = "Amount mismatch",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "Completed", PaymentStatus = "DisputePending",
            EscrowStatus = "disputed", Budget = 200m, PayoutAmount = 170m
        };
        context.Tasks.Add(task);
        context.Disputes.Add(new Dispute
        {
            TaskId = task.Id, ReportedByUserId = creator.Id,
            Issue = "Task incomplete", Category = "quality", Status = "Open"
        });
        context.PaymentAttempts.Add(new PaymentAttempt
        {
            TaskId = task.Id, Provider = "Paystack", Reference = "pay_ref_134",
            TransactionId = "987655", Amount = 150m, Status = "Completed"
        });
        await context.SaveChangesAsync();

        var result = await CreateController(context, requester.Id).RequestFinancialRefundApproval(
            task.TaskId,
            new AdminActionReasonRequest { Reason = "Refund requested after review of evidence" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(context.FinancialApprovalRequests);
        Assert.Empty(context.Refunds);
        Assert.Empty(context.Payouts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Refund_approval_is_denied_when_no_open_dispute_or_verified_charge_exists()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-refund-ineligible-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var requester = CreateUser(121, "Admin,SuperAdmin");
        requester.AuthenticatorTwoFactorEnabled = true;
        requester.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var creator = CreateUser(122, "User");
        var runner = CreateUser(123, "User");
        context.Users.AddRange(requester, creator, runner);
        context.Tasks.Add(new DoForYou.API.Models.Task
        {
            Id = 124, TaskId = "DFY-REFUND-INELIGIBLE", TaskDescription = "No dispute",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "Completed", PaymentStatus = "DisputePending",
            EscrowStatus = "disputed", Budget = 200m, PayoutAmount = 170m
        });
        await context.SaveChangesAsync();

        var result = await CreateController(context, requester.Id).RequestFinancialRefundApproval(
            "DFY-REFUND-INELIGIBLE",
            new AdminActionReasonRequest { Reason = "Refund requested after operational review" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(context.FinancialApprovalRequests);
        Assert.Empty(context.Refunds);
        Assert.Empty(context.Payouts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Financial_approval_request_rejects_stale_MFA_and_non_SuperAdmins()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"financial-approval-denied-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var actor = CreateUser(91, "Admin,SuperAdmin");
        actor.AuthenticatorTwoFactorEnabled = true;
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow.AddMinutes(-16);
        var creator = CreateUser(92, "User");
        var runner = CreateUser(93, "User");
        context.Users.AddRange(actor, creator, runner);
        context.Tasks.Add(new DoForYou.API.Models.Task
        {
            Id = 94, TaskId = "DFY-STALE-MFA", TaskDescription = "Stale MFA test",
            CreatedByUserId = creator.Id, AcceptedByUserId = runner.Id,
            TaskStatus = "AwaitingConfirmation", PaymentStatus = "EscrowHeld",
            EscrowStatus = "held", Budget = 100m, PayoutAmount = 85m
        });
        await context.SaveChangesAsync();

        var denied = await CreateController(context, actor.Id).RequestFinancialApproval(
            "DFY-STALE-MFA",
            new AdminActionReasonRequest { Reason = "Exceptional escrow release after operational review" });

        Assert.IsType<ForbidResult>(denied.Result);
        Assert.Empty(context.FinancialApprovalRequests);
        Assert.Empty(context.Payouts);
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

    private static AdminController CreateController(AppDbContext context, int actorId, DoForYou.API.Services.IEscrowService? escrowService = null)
    {
        var controller = new AdminController(context, escrowService!, null!, null!, null!, null!)
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
