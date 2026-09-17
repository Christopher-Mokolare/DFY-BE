using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/disputes")]
[Authorize]
public class DisputesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEscrowService _escrowService;
    private readonly INotificationService _notificationService;

    public DisputesController(AppDbContext context, IEscrowService escrowService, INotificationService notificationService)
    {
        _context = context;
        _escrowService = escrowService;
        _notificationService = notificationService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> RaiseDispute([FromBody] RaiseDisputeRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == request.TaskId);
        if (task == null)
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });

        if (task.CreatedByUserId != userId && task.AcceptedByUserId != userId)
            return Ok(new ApiResponse<object> { Success = false, Message = "Not authorized to dispute this task" });

        if (task.TaskStatus == "RunnerPaid" || task.EscrowStatus == "released" || task.EscrowStatus == "refunded")
            return Ok(new ApiResponse<object> { Success = false, Message = "This task is no longer eligible for a dispute" });

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

        // A dispute must stop the normal 48-hour auto-release path. The task remains
        // completed/held so an admin can later release it into the Ozow payout flow.
        if (task.EscrowStatus == "held" || task.PaymentStatus == "EscrowHeld")
        {
            task.EscrowStatus = "disputed";
            task.PaymentStatus = "DisputePending";
            task.UpdatedAt = DateTime.UtcNow;
        }

        _context.Disputes.Add(dispute);
        await _context.SaveChangesAsync();

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
    public async Task<ActionResult<ApiResponse<bool>>> ResolveDispute(int id, [FromBody] ResolveDisputeRequest request)
    {
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

        if (request.Action == "release_to_runner")
        {
            // Re-open the escrow hold for the release service, then create the normal
            // idempotent Ozow payout. The payout processor will only mark the runner paid
            // after Ozow confirms status 5.
            task.EscrowStatus = "held";
            task.PaymentStatus = "EscrowHeld";
            task.EscrowHoldUntil = DateTime.UtcNow.AddSeconds(-1);
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var released = await _escrowService.ReleaseEscrowAsync(task.Id, force: true);
            if (!released)
                return Ok(new ApiResponse<bool> { Success = false, Message = "Unable to release escrow into the Ozow payout flow" });
        }
        else if (request.Action == "refund_creator")
        {
            // Refund provider integration is not yet implemented. Do not mark a task as
            // financially refunded when no provider-side refund has been confirmed.
            return StatusCode(StatusCodes.Status501NotImplemented,
                new ApiResponse<bool>
                {
                    Success = false,
                    Message = "Creator refunds require the Ozow refund integration and cannot be marked refunded yet."
                });
        }
        else
        {
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Unsupported dispute resolution action" });
        }

        dispute.Status = "Resolved";
        dispute.Resolution = request.Resolution;
        dispute.ResolvedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Notify the participants about the administrative resolution.
        var message = request.Action == "release_to_runner"
            ? $"Your dispute for task {task.TaskId} was resolved and the runner payout is being processed through Ozow."
            : $"Your dispute for task {task.TaskId} was resolved.";

        await _notificationService.CreateNotificationAsync(
            task.CreatedByUserId,
            "dispute_resolved",
            "Dispute Resolved",
            message,
            task.Id);

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

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
