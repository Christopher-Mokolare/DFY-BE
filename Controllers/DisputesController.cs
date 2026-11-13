using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Authorization;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;
using System.Text.Json;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/disputes")]
[Authorize]
public class DisputesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEscrowService _escrowService;
    private readonly INotificationService _notificationService;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IFinancialLedgerService _financialLedgerService;

    public DisputesController(
        AppDbContext context,
        IEscrowService escrowService,
        INotificationService notificationService,
        IPaymentProvider paymentProvider,
        IFinancialLedgerService? financialLedgerService = null)
    {
        _context = context;
        _escrowService = escrowService;
        _notificationService = notificationService;
        _paymentProvider = paymentProvider;
        _financialLedgerService = financialLedgerService ?? new FinancialLedgerService(context);
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> RaiseDispute([FromBody] RaiseDisputeRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == request.TaskId);
        if (task == null)
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });

        // Completion disputes are a creator protection, not a general task-issue
        // channel. Runners must use the separate incident-report workflow.
        if (task.CreatedByUserId != userId)
            return Forbid();

        var now = DateTime.UtcNow;
        var activeIncidentHold = await _context.TaskIncidents
            .AsNoTracking()
            .Where(i => i.TaskId == task.Id &&
                        i.EscrowHoldPlacedAt != null &&
                        i.EscrowHoldReleasedAt == null)
            .OrderByDescending(i => i.EscrowHoldPlacedAt)
            .Select(i => new { i.Id, i.EscrowHoldPlacedAt, i.EscrowHoldDeadlineBefore })
            .FirstOrDefaultAsync();
        var disputeDeadline = activeIncidentHold?.EscrowHoldDeadlineBefore.HasValue == true
            ? activeIncidentHold.EscrowHoldDeadlineBefore.Value.Add(now - activeIncidentHold.EscrowHoldPlacedAt!.Value)
            : task.EscrowHoldUntil ?? task.CompletedAt?.AddHours(24);

        if (task.TaskStatus != "AwaitingConfirmation" ||
            task.EscrowStatus is not ("held" or "incident_hold") ||
            task.PaymentStatus != "EscrowHeld" ||
            !disputeDeadline.HasValue ||
            now > disputeDeadline.Value)
            return Conflict(new ApiResponse<object> { Success = false, Message = "A completion dispute can only be raised by the task creator within the active 24-hour confirmation window while escrow is held or on an incident hold." });

        var existingOpenDispute = await _context.Disputes
            .AnyAsync(d => d.TaskId == task.Id && d.Status == "Open");
        if (existingOpenDispute)
            return Ok(new ApiResponse<object> { Success = false, Message = "An open dispute already exists for this task" });

        var dispute = new Models.Dispute
        {
            TaskId = task.Id,
            ReportedByUserId = userId.Value,
            Issue = request.Issue,
            Category = request.Category,
            Status = "Open"
        };

        // Atomically claim the task for the formal dispute. This competes with the
        // escrow service's AwaitingConfirmation -> PayoutPending claim, so a payout
        // and a dispute cannot both win the same task transition.
        var eligibleTask = _context.Tasks.Where(t =>
            t.Id == task.Id &&
            t.TaskStatus == "AwaitingConfirmation" &&
            (t.EscrowStatus == "held" || t.EscrowStatus == "incident_hold") &&
            t.PaymentStatus == "EscrowHeld");
        if (activeIncidentHold == null)
        {
            eligibleTask = eligibleTask.Where(t =>
                (t.EscrowHoldUntil.HasValue && t.EscrowHoldUntil.Value >= now) ||
                (!t.EscrowHoldUntil.HasValue && t.CompletedAt.HasValue && t.CompletedAt.Value.AddHours(24) >= now));
        }

        int claimed;
        if (_context.Database.IsRelational())
        {
            claimed = await eligibleTask.ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.EscrowStatus, "disputed")
                .SetProperty(t => t.PaymentStatus, "DisputePending")
                .SetProperty(t => t.UpdatedAt, now));
        }
        else
        {
            var eligible = await eligibleTask.FirstOrDefaultAsync();
            if (eligible == null)
                claimed = 0;
            else
            {
                eligible.EscrowStatus = "disputed";
                eligible.PaymentStatus = "DisputePending";
                eligible.UpdatedAt = now;
                claimed = 1;
            }
        }

        if (claimed != 1)
            return Conflict(new ApiResponse<object> { Success = false, Message = "The task is no longer eligible for a completion dispute. Refresh and review its current state." });

        // If an incident hold was active (including one placed concurrently after
        // the initial read), mark that operational hold as superseded. The escrow
        // remains frozen by the formal dispute, not released back to the payout path.
        var incidentHoldToEnd = await _context.TaskIncidents
            .Where(i => i.TaskId == task.Id &&
                        i.EscrowHoldPlacedAt != null &&
                        i.EscrowHoldReleasedAt == null)
            .OrderByDescending(i => i.EscrowHoldPlacedAt)
            .FirstOrDefaultAsync();
        if (incidentHoldToEnd != null)
        {
            await _context.Entry(incidentHoldToEnd).ReloadAsync();
            if (!incidentHoldToEnd.EscrowHoldReleasedAt.HasValue)
            {
                incidentHoldToEnd.EscrowHoldReleasedAt = now;
                incidentHoldToEnd.EscrowHoldReleaseReason = "Superseded by a formal creator completion dispute; escrow remains frozen under the dispute state.";
                incidentHoldToEnd.AdminNotes = string.IsNullOrWhiteSpace(incidentHoldToEnd.AdminNotes)
                    ? "Incident hold ended because the creator opened a formal completion dispute."
                    : $"{incidentHoldToEnd.AdminNotes}\nIncident hold ended because the creator opened a formal completion dispute.";
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId.Value,
                    Action = "TaskIncidentHoldSupersededByDispute",
                    EntityType = "TaskIncident",
                    EntityId = incidentHoldToEnd.Id,
                    OldValues = JsonSerializer.Serialize(new { escrowStatus = "incident_hold" }),
                    NewValues = JsonSerializer.Serialize(new { escrowStatus = "disputed", disputeStatus = "Open" }),
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
            }
        }

        _context.Disputes.Add(dispute);
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId.Value,
            Action = "CompletionDisputeRaised",
            EntityType = "Task",
            EntityId = task.Id,
            OldValues = JsonSerializer.Serialize(new { taskStatus = "AwaitingConfirmation", escrowStatus = task.EscrowStatus, paymentStatus = "EscrowHeld" }),
            NewValues = JsonSerializer.Serialize(new { escrowStatus = "disputed", paymentStatus = "DisputePending", category = request.Category }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();

        var reporter = await _context.Users.FindAsync(userId.Value);
        var reporterName = reporter != null ? $"{reporter.FirstName} {reporter.LastName}" : "A user";
        await _notificationService.NotifyAdminsAsync(
            "dispute_raised",
            "New Dispute Raised",
            $"{reporterName} raised a dispute on task {task.TaskId}: {request.Issue.Substring(0, Math.Min(request.Issue.Length, 80))}",
            task.Id
        );

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { id = dispute.Id, status = dispute.Status },
            Message = "Dispute raised successfully. Escrow is frozen pending admin resolution."
        });
    }

    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyDisputes()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var disputes = await _context.Disputes
            .Include(d => d.Task)
            .Where(d => d.ReportedByUserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                id = d.Id,
                taskId = d.Task.TaskId,
                taskDescription = d.Task.TaskDescription,
                issue = d.Issue,
                category = d.Category,
                status = d.Status,
                resolution = d.Resolution,
                createdAt = d.CreatedAt,
                resolvedAt = d.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = disputes });
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [Authorize(Policy = AdminPermissionPolicies.DisputesManagePolicy)]
    public async Task<ActionResult<ApiResponse<object>>> GetAllDisputes(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Disputes
            .Include(d => d.Task)
            .Include(d => d.ReportedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status)) query = query.Where(d => d.Status == status);

        var total = await query.CountAsync();
        var disputes = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                id = d.Id,
                taskId = d.Task.TaskId,
                taskDescription = d.Task.TaskDescription,
                reportedBy = $"{d.ReportedByUser.FirstName} {d.ReportedByUser.LastName}",
                issue = d.Issue,
                category = d.Category,
                status = d.Status,
                resolution = d.Resolution,
                createdAt = d.CreatedAt,
                resolvedAt = d.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = new { disputes, total, page, pageSize } });
    }

    [HttpPatch("{id}/resolve")]
    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [Authorize(Policy = "SuperAdminMfa")]
    [Authorize(Policy = AdminPermissionPolicies.DisputesManagePolicy)]
    public async Task<ActionResult<ApiResponse<bool>>> ResolveDispute(int id, [FromBody] JsonElement request)
    {
        var actorId = GetCurrentUserId();
        var actor = actorId.HasValue
            ? await _context.Users.AsNoTracking().Where(user => user.Id == actorId.Value && !user.IsArchived && !user.IsSuspended)
                .Select(user => new { user.Roles, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
                .FirstOrDefaultAsync()
            : null;
        var isFreshSuperAdmin = actor?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true &&
            actor.AuthenticatorTwoFactorEnabled &&
            actor.LastTwoFactorVerifiedAt.HasValue &&
            actor.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.AddMinutes(-15);
        if (!isFreshSuperAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<bool> { Success = false, Message = "A SuperAdmin with fresh authenticator verification is required to resolve financial disputes." });

        var action = request.TryGetProperty("action", out var actionElement) ? actionElement.GetString()?.Trim() : null;
        var resolution = request.TryGetProperty("resolution", out var resolutionElement) ? resolutionElement.GetString()?.Trim() : null;
        var reason = request.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString()?.Trim() : null;

        if (string.IsNullOrWhiteSpace(action) ||
            string.IsNullOrWhiteSpace(resolution) ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Length < 5 ||
            reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Action, resolution and a reason between 5 and 500 characters are required." });

        var dispute = await _context.Disputes
            .Include(d => d.Task)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (dispute == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Dispute not found" });
        if (dispute.Status == "Resolved")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Dispute is already resolved" });

        var task = dispute.Task;
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Dispute task not found" });

        Models.FinancialApprovalRequest? financialApproval = null;
        if (action is "refund_creator" or "release_to_runner")
        {
            financialApproval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item =>
                item.TaskId == task.Id && item.DisputeId == dispute.Id && item.Action == action &&
                item.Status == "Approved" && item.ExpiresAt > DateTime.UtcNow);
            if (financialApproval == null || !financialApproval.DecidedByUserId.HasValue ||
                financialApproval.RequestedByUserId == financialApproval.DecidedByUserId.Value)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "A current, independently approved two-person refund request is required. No refund was submitted." });

            var approver = await _context.Users.AsNoTracking()
                .Where(user => user.Id == financialApproval.DecidedByUserId.Value && !user.IsArchived && !user.IsSuspended)
                .Select(user => new { user.Roles, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
                .FirstOrDefaultAsync();
            var approverIsFreshSuperAdmin = approver?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true &&
                approver.AuthenticatorTwoFactorEnabled &&
                approver.LastTwoFactorVerifiedAt.HasValue &&
                approver.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.AddMinutes(-15);
            if (!approverIsFreshSuperAdmin)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "The approving SuperAdmin is no longer active or their authenticator verification is stale. Obtain a new independent approval." });

            await using var financialApprovalTransaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;
            var claimTime = DateTime.UtcNow;
            var claimedApproval = 0;
            if (_context.Database.IsRelational())
            {
                claimedApproval = await _context.FinancialApprovalRequests
                    .Where(item => item.Id == financialApproval.Id && item.Status == "Approved" &&
                        item.DisputeId == dispute.Id && item.ExpiresAt > claimTime &&
                        item.RequestedByUserId != item.DecidedByUserId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, "Executing")
                        .SetProperty(item => item.ExecutionStartedAt, claimTime)
                        .SetProperty(item => item.ExecutionAttempts, item => item.ExecutionAttempts + 1));
            }
            else
            {
                financialApproval.Status = "Executing";
                financialApproval.ExecutionStartedAt = claimTime;
                financialApproval.ExecutionAttempts++;
                claimedApproval = 1;
            }
            if (claimedApproval != 1)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "The refund approval was already consumed or expired. Refresh before retrying." });

            financialApproval.Status = "Executing";
            _context.AuditLogs.Add(new Models.AuditLog
            {
                UserId = actorId,
                Action = action == "refund_creator" ? "FinancialRefundExecutionStarted" : "FinancialDisputeReleaseExecutionStarted",
                EntityType = "FinancialApprovalRequest",
                EntityId = financialApproval.Id,
                OldValues = "Approved",
                NewValues = $"Executing; taskId={task.Id}; disputeId={dispute.Id}; requester={financialApproval.RequestedByUserId}; approver={financialApproval.DecidedByUserId}; reason={reason}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await _context.SaveChangesAsync();
            if (financialApprovalTransaction != null)
                await financialApprovalTransaction.CommitAsync();
        }

        if (action == "release_to_runner")
        {
            // Re-open the escrow hold for the release service, then create the normal
            // idempotent Paystack payout. The payout processor will only mark the runner paid
            // after Paystack confirms a successful transfer.
            task.EscrowStatus = "held";
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "AwaitingConfirmation";
            task.EscrowHoldUntil = DateTime.UtcNow.AddSeconds(-1);
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var released = await _escrowService.ReleaseEscrowAsync(
                task.Id,
                force: true,
                expectedPaymentTransactionId: financialApproval!.PaymentTransactionId);
            if (!released)
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Approved dispute release did not produce a queued payout.");
                return Ok(new ApiResponse<bool> { Success = false, Message = "Unable to release escrow into the Paystack payout flow" });
            }
            await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", "Two-person-approved dispute release queued the normal payout flow; no provider transfer was submitted by this action.");
        }
        else if (action == "refund_creator")
        {
            if (task.TaskStatus == "RunnerPaid" ||
                task.EscrowStatus == "released" ||
                task.EscrowStatus == "refunded")
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Task is no longer eligible for refund.");
                return Conflict(new ApiResponse<bool> { Success = false, Message = "This task can no longer be refunded." });
            }

            // A refund must be tied to a current, persisted successful Paystack charge.
            // Audit text and task-level payment references are not payment evidence.
            var successfulAttempts = await _context.PaymentAttempts
                .Where(p => p.TaskId == task.Id &&
                    p.Provider == "Paystack" &&
                    p.Status == "Completed" &&
                    p.TransactionId == financialApproval!.PaymentTransactionId &&
                    p.Amount == financialApproval.Amount)
                .OrderByDescending(p => p.CompletedAt ?? p.CreatedAt)
                .ToListAsync();

            if (successfulAttempts.Count != 1 ||
                string.IsNullOrWhiteSpace(successfulAttempts[0].TransactionId))
            {
                await CompleteRefundApprovalAsync(
                    financialApproval,
                    actorId,
                    "Failed",
                    "Exactly one persisted successful Paystack charge matching the task budget is required.");
                return Conflict(new ApiResponse<bool>
                {
                    Success = false,
                    Message = "A refund cannot be submitted without exactly one persisted successful Paystack charge matching the task budget. Legacy audit-log references are not accepted."
                });
            }

            var transactionId = successfulAttempts[0].TransactionId;
            var chargeJournalExists = await _context.FinancialJournals.AnyAsync(journal =>
                journal.SourceType == "paystack_transaction" &&
                journal.SourceId == transactionId &&
                journal.EventType == "payment_collected");
            if (!chargeJournalExists)
            {
                await CompleteRefundApprovalAsync(
                    financialApproval,
                    actorId,
                    "Failed",
                    "The verified charge is not represented in the new double-entry journal; historical records are not automatically imported.");
                return Conflict(new ApiResponse<bool>
                {
                    Success = false,
                    Message = "This charge predates or is absent from the new financial journal. No refund was submitted; reconcile the historical charge through a separately approved accounting process."
                });
            }

            var existingRefund = await _context.Refunds
                .FirstOrDefaultAsync(r => r.TaskId == task.Id);

            if (existingRefund != null &&
                (existingRefund.Provider != "Paystack" ||
                 existingRefund.TransactionId != transactionId ||
                 existingRefund.Amount != task.Budget))
            {
                await CompleteRefundApprovalAsync(
                    financialApproval,
                    actorId,
                    "Failed",
                    "Existing refund record does not match the approved Paystack transaction and full task amount.");
                return Conflict(new ApiResponse<bool>
                {
                    Success = false,
                    Message = "An existing refund record is bound to a different charge or amount. No provider action was taken; manual reconciliation is required."
                });
            }

            if (existingRefund != null && existingRefund.Status == "Complete")
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", "Existing refund record is already complete.");
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "The refund has already completed." });
            }

            if (existingRefund != null &&
                existingRefund.Status == "PendingInvestigation")
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Existing refund requires customer bank details and manual provider operations.");
                return Conflict(new ApiResponse<bool> { Success = false, Message = "Paystack requires customer bank details to complete this refund. Keep the task on refund hold and resolve the request through the approved Paystack operations process." });
            }

            if (existingRefund != null && !string.IsNullOrWhiteSpace(existingRefund.RefundId) &&
                existingRefund.Status is "Pending" or "Submitted")
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", $"Existing refund {existingRefund.RefundId} is already submitted.");
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already submitted and awaiting Paystack confirmation." });
            }

            // Atomically reserve the disputed task for refund before calling Paystack.
            // The payout worker only submits rows whose task remains EscrowReleased /
            // PayoutPending, so this state transition and payout claim cannot both win.
            var priorTaskStatus = task.TaskStatus;
            var reservationQuery = _context.Tasks.Where(t =>
                t.Id == task.Id &&
                t.PaymentStatus == "DisputePending" &&
                t.EscrowStatus == "disputed" &&
                (t.TaskStatus == "AwaitingConfirmation" || t.TaskStatus == "Completed"));

            var reservedPayout = 0;
            if (_context.Database.IsRelational())
            {
                reservationQuery = reservationQuery.Where(t => !_context.Payouts.Any(p =>
                    p.TaskId == t.Id &&
                    (p.Status == "Pending" ||
                     p.Status == "Processing" ||
                     p.Status == "AwaitingFunds" ||
                     p.Status == "AwaitingEligibility" ||
                     p.Status == "AwaitingBankDetails" ||
                     p.Status == "Queued" ||
                     p.Status == "Initiated" ||
                     p.Status == "Completed")));

                reservedPayout = await reservationQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.PaymentStatus, "RefundPending")
                    .SetProperty(t => t.TaskStatus, "RefundPending")
                    .SetProperty(t => t.EscrowStatus, "refund_pending")
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow),
                    HttpContext.RequestAborted);
            }
            else
            {
                var activePayout = await _context.Payouts.AnyAsync(p =>
                    p.TaskId == task.Id &&
                    (p.Status == "Pending" ||
                     p.Status == "Processing" ||
                     p.Status == "AwaitingFunds" ||
                     p.Status == "AwaitingEligibility" ||
                     p.Status == "AwaitingBankDetails" ||
                     p.Status == "Queued" ||
                     p.Status == "Initiated" ||
                     p.Status == "Completed"));

                var reservableTask = activePayout ? null : await reservationQuery.FirstOrDefaultAsync();
                if (reservableTask != null)
                {
                    reservableTask.PaymentStatus = "RefundPending";
                    reservableTask.TaskStatus = "RefundPending";
                    reservableTask.EscrowStatus = "refund_pending";
                    reservableTask.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    reservedPayout = 1;
                }
            }

            if (reservedPayout != 1)
            {
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Task reservation failed or a payout is active; no refund submission was attempted.");
                return Conflict(new ApiResponse<bool> { Success = false, Message = "The task is no longer held for dispute resolution or a runner payout is already queued/processing." });
            }

            task.PaymentStatus = "RefundPending";
            task.TaskStatus = "RefundPending";
            task.EscrowStatus = "refund_pending";
            task.UpdatedAt = DateTime.UtcNow;

            // Create the unique refund record before calling Paystack. TaskId is unique,
            // so concurrent admin requests cannot create two DFY refund records.
            var refundRecord = existingRefund ?? new Models.Refund
            {
                TaskId = task.Id,
                RefundId = string.Empty,
                TransactionId = transactionId,
                Amount = task.Budget,
                Reason = resolution,
                Status = "Pending",
                Provider = "Paystack"
            };

            if (existingRefund == null)
            {
                _context.Refunds.Add(refundRecord);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    refundRecord = await _context.Refunds.FirstAsync(r => r.TaskId == task.Id);
                    if (!string.IsNullOrWhiteSpace(refundRecord.RefundId))
                        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already submitted and awaiting Paystack confirmation." });
                }
            }

            // If a previous attempt has a provider record, reconcile it before resubmitting.
            IReadOnlyList<RefundStatusResult> providerRefunds;
            try
            {
                providerRefunds = await _paymentProvider.GetRefundsByTransactionIdAsync(
                    refundRecord.TransactionId,
                    HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                refundRecord.Status = "ReconciliationRequired";
                refundRecord.FailureReason = "Paystack refund state could not be verified. No new refund was submitted; manual provider reconciliation is required.";
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                task.PaymentStatus = "RefundPending";
                task.TaskStatus = "RefundPending";
                task.EscrowStatus = "refund_pending";
                dispute.Status = "RefundPending";
                dispute.Resolution = resolution;
                dispute.ResolvedAt = null;
                await _context.SaveChangesAsync();
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed",
                    $"Provider reconciliation unavailable; submission was not attempted. {ex.GetType().Name}");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ApiResponse<bool> { Success = false, Message = "Paystack refund state could not be verified. No new refund was submitted; the case is held for manual reconciliation." });
            }
            var matchingProviderRefunds = providerRefunds
                .Where(item => item.Amount == refundRecord.Amount &&
                    string.Equals(item.TransactionId, refundRecord.TransactionId, StringComparison.Ordinal))
                .ToList();
            if (providerRefunds.Count > 0 && matchingProviderRefunds.Count != 1)
            {
                refundRecord.Status = "ReconciliationRequired";
                refundRecord.FailureReason = "Paystack returned partial or ambiguous refund records for this charge. Manual reconciliation is required; no additional refund was submitted.";
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                task.PaymentStatus = "RefundPending";
                task.TaskStatus = "RefundPending";
                task.EscrowStatus = "refund_pending";
                dispute.Status = "RefundPending";
                dispute.Resolution = resolution;
                dispute.ResolvedAt = null;
                await _context.SaveChangesAsync();
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Provider returned ambiguous existing refund records; no new refund submitted.");
                return Conflict(new ApiResponse<bool> { Success = false, Message = "Paystack has existing refund records that do not match one exact full refund. The task remains frozen for manual reconciliation." });
            }
            var matchingProviderRefund = matchingProviderRefunds.SingleOrDefault();

            if (matchingProviderRefund != null)
            {
                refundRecord.RefundId = matchingProviderRefund.RefundId;
                refundRecord.Status = matchingProviderRefund.Status.ToLowerInvariant() switch
                {
                    "processed" => "Complete",
                    "failed" => "Failed",
                    "needs-attention" => "PendingInvestigation",
                    "pending" or "processing" => "Submitted",
                    _ => "Submitted"
                };
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                refundRecord.FailureReason = refundRecord.Status switch
                {
                    "Failed" => "Paystack reports this refund failed.",
                    "PendingInvestigation" => "Paystack requires customer bank details to complete this refund.",
                    _ => null
                };
                if (refundRecord.Status == "Complete")
                    refundRecord.CompletedAt = DateTime.UtcNow;
                if (refundRecord.Status == "Complete")
                {
                    await JournalConfirmedRefundAsync(refundRecord, actorId, HttpContext.RequestAborted);
                    task.PaymentStatus = "Refunded";
                    task.TaskStatus = "Cancelled";
                    task.EscrowStatus = "refunded";
                    task.PayoutStatus = "NotStarted";
                    task.PayoutReference = null;
                    task.PayoutInitiatedAt = null;
                    task.PayoutCompletedAt = null;
                    task.PaidToRunnerAt = null;
                    dispute.Status = "Resolved";
                    dispute.Resolution = resolution;
                    dispute.ResolvedAt = DateTime.UtcNow;
                }
                else if (refundRecord.Status == "Failed")
                {
                    task.PaymentStatus = "DisputePending";
                    task.TaskStatus = priorTaskStatus;
                    task.EscrowStatus = "disputed";
                    dispute.Status = "Open";
                    dispute.ResolvedAt = null;
                }
                else
                {
                    // A needs-attention refund is still outstanding, not failed.
                    // Keep the task frozen until Paystack confirms the outcome.
                    dispute.Status = "RefundPending";
                    dispute.Resolution = resolution;
                    dispute.ResolvedAt = null;
                }

                await _context.SaveChangesAsync();

                if (refundRecord.Status == "Complete")
                {
                    await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", $"Existing provider refund {refundRecord.RefundId} reconciled as complete.");
                    return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "The refund was confirmed with Paystack." });
                }

                if (refundRecord.Status == "Failed")
                {
                    await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", $"Existing provider refund {refundRecord.RefundId} is failed.");
                    return Conflict(new ApiResponse<bool> { Success = false, Message = "Paystack reports the existing refund as failed; the dispute is reopened for review." });
                }

                if (refundRecord.Status == "PendingInvestigation")
                {
                    await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed", "Paystack requires additional customer bank details; the refund remains on hold.");
                    return Conflict(new ApiResponse<bool> { Success = false, Message = "Paystack requires customer bank details to complete this refund. Keep the task on refund hold and resolve the request through the approved Paystack operations process." });
                }

                await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", $"Existing provider refund {refundRecord.RefundId} reconciled as pending.");
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already with Paystack and awaiting completion." });
            }

            var refund = await _paymentProvider.CreateRefundAsync(
                refundRecord.TransactionId,
                refundRecord.Amount,
                refundRecord.Reason,
                HttpContext.RequestAborted);

            if (!refund.Success || string.IsNullOrWhiteSpace(refund.RefundId))
            {
                refundRecord.Status = refund.OutcomeUnknown ? "ReconciliationRequired" : "Failed";
                refundRecord.FailureReason = refund.Error;
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                if (refund.OutcomeUnknown)
                {
                    task.PaymentStatus = "RefundPending";
                    task.TaskStatus = "RefundPending";
                    task.EscrowStatus = "refund_pending";
                    dispute.Status = "RefundPending";
                    dispute.Resolution = resolution;
                    dispute.ResolvedAt = null;
                }
                else
                {
                    task.PaymentStatus = "DisputePending";
                    task.TaskStatus = priorTaskStatus;
                    task.EscrowStatus = "disputed";
                    dispute.Status = "Open";
                    dispute.ResolvedAt = null;
                }
                await _context.SaveChangesAsync();
                await CompleteRefundApprovalAsync(financialApproval, actorId, "Failed",
                    refund.OutcomeUnknown
                        ? "Paystack submission outcome is unknown; task remains frozen and no automatic retry is allowed."
                        : refund.Error ?? "Paystack refund submission failed.");
                return StatusCode(refund.OutcomeUnknown ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway,
                    new ApiResponse<bool> { Success = false, Message = refund.Error ?? "Unable to submit Paystack refund." });
            }

            refundRecord.RefundId = refund.RefundId;
            refundRecord.Status = "Submitted";
            refundRecord.SubmittedAt = DateTime.UtcNow;
            refundRecord.FailureReason = null;
            await _context.SaveChangesAsync();

            task.PaymentStatus = "RefundPending";
            task.TaskStatus = "RefundPending";
            task.EscrowStatus = "refund_pending";
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _context.AuditLogs.AddAsync(new Models.AuditLog
            {
                UserId = GetCurrentUserId(),
                Action = "PaystackRefundSubmitted",
                EntityType = "Task",
                EntityId = task.Id,
                NewValues = $"refundId={refund.RefundId}; transactionId={transactionId}; amount={task.Budget:F2}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await _context.SaveChangesAsync();

            dispute.Status = "RefundPending";
            dispute.Resolution = resolution;
            dispute.ResolvedAt = null;
            await _context.SaveChangesAsync();

            await _notificationService.CreateNotificationAsync(
                task.CreatedByUserId,
                "refund_pending",
                "Refund Submitted",
                $"Your refund for task {task.TaskId} has been submitted to Paystack and is awaiting confirmation.",
                task.Id);
            await CompleteRefundApprovalAsync(financialApproval, actorId, "Executed", $"Paystack refund {refund.RefundId} submitted; awaiting provider confirmation.");

            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Refund submitted to Paystack and is awaiting provider confirmation." });
        }
        else
        {
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Unsupported dispute resolution action" });
        }

        dispute.Status = "Resolved";
        dispute.Resolution = resolution;
        dispute.ResolvedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Notify the participants about the administrative resolution.
        var message = action == "release_to_runner"
            ? $"Your dispute for task {task.TaskId} was resolved and the runner payout is being processed through Paystack."
            : $"Your dispute for task {task.TaskId} was resolved.";

        await _notificationService.CreateNotificationAsync(
            task.CreatedByUserId,
            "dispute_resolved",
            "Dispute Resolved",
            message,
            task.Id);

        await _context.AuditLogs.AddAsync(new Models.AuditLog
        {
            UserId = GetCurrentUserId(),
            Action = "ResolveDispute",
            EntityType = "Dispute",
            EntityId = dispute.Id,
            NewValues = $"action={action}; resolution={resolution}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();

        if (task.AcceptedByUserId.HasValue)
        {
            await _notificationService.CreateNotificationAsync(
                task.AcceptedByUserId.Value,
                "dispute_resolved",
                "Dispute Resolved",
                message,
                task.Id);
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Dispute resolved" });
    }

    private async Task JournalConfirmedRefundAsync(
        Models.Refund refund,
        int? actorId,
        CancellationToken cancellationToken)
    {
        var originalChargeJournal = await _context.FinancialJournals
            .Include(journal => journal.Lines)
                .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(journal =>
                journal.SourceType == "paystack_transaction" &&
                journal.SourceId == refund.TransactionId &&
                journal.EventType == "payment_collected",
                cancellationToken);

        if (originalChargeJournal == null)
        {
            _context.AuditLogs.Add(new Models.AuditLog
            {
                UserId = actorId,
                Action = "PaystackRefundCompletedWithoutLedgerSource",
                EntityType = "Refund",
                EntityId = refund.Id,
                NewValues = "Paystack confirmed the refund, but the original charge predates or is absent from the new financial journal. No historical journal entry was fabricated.",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            return;
        }

        await _financialLedgerService.ReverseAsync(
            originalChargeJournal.Id,
            sourceType: "refund",
            sourceId: refund.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            idempotencyKey: $"paystack:refund:{refund.Id}:completed",
            description: "Reverse the original verified Paystack collection after provider reconciliation confirmed the full refund.",
            provider: "Paystack",
            providerReference: refund.RefundId,
            createdByUserId: actorId,
            occurredAt: DateTime.UtcNow,
            cancellationToken: cancellationToken);
    }

    private static string? ExtractAuditValue(string? values, string key)
    {
        if (string.IsNullOrWhiteSpace(values)) return null;
        var prefix = key + "=";
        var start = values.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = values.IndexOf(';', start);
        return (end >= 0 ? values[start..end] : values[start..]).Trim();
    }

    private async System.Threading.Tasks.Task CompleteRefundApprovalAsync(
        Models.FinancialApprovalRequest? approval,
        int? actorId,
        string status,
        string details)
    {
        if (approval == null)
            return;

        approval.Status = status;
        if (status == "Executed")
            approval.ExecutedAt = DateTime.UtcNow;
        approval.LastExecutionError = status == "Failed" ? details : null;
        var auditAction = approval.Action == "refund_creator"
            ? status == "Executed" ? "FinancialRefundExecuted" : "FinancialRefundExecutionFailed"
            : status == "Executed" ? "FinancialDisputeReleaseExecuted" : "FinancialDisputeReleaseExecutionFailed";
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId,
            Action = auditAction,
            EntityType = "FinancialApprovalRequest",
            EntityId = approval.Id,
            OldValues = "Executing",
            NewValues = $"{status}; taskId={approval.TaskId}; {details}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
