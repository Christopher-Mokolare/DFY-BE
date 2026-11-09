using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class TaskIncidentsController : ControllerBase
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Scope change", "Task information", "Access or location", "Creator unresponsive",
        "Safety concern", "Threats or harassment", "Cancellation or lost costs", "Other"
    };

    private readonly AppDbContext _context;
    private readonly INotificationService _notifications;

    public TaskIncidentsController(AppDbContext context, INotificationService notifications)
    {
        _context = context;
        _notifications = notifications;
    }

    [HttpPost("tasks/{taskId}/incidents")]
    public async Task<ActionResult<ApiResponse<object>>> Report(string taskId, [FromBody] CreateTaskIncidentRequest request)
    {
        var userId = CurrentUserId();
        if (userId == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Category) || !AllowedCategories.Contains(request.Category))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Select a valid incident category." });
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 20 || request.Description.Trim().Length > 2000)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Describe the incident in 20 to 2,000 characters." });

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Task not found." });
        if (task.AcceptedByUserId != userId && task.CreatedByUserId != userId)
            return Forbid();
        if (task.AcceptedByUserId == null ||
            task.TaskStatus is not ("Claimed" or "InProgress" or "AwaitingConfirmation"))
            return Conflict(new ApiResponse<object> { Success = false, Message = "An incident can only be reported on a claimed, in-progress or awaiting-confirmation task." });

        var evidenceIds = request.EvidenceMessageIds ?? new List<int>();
        if (evidenceIds.Count > 10 || evidenceIds.Any(id => id <= 0))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "You can attach up to 10 valid chat message references." });
        if (evidenceIds.Count > 0)
        {
            var validEvidenceCount = await _context.TaskMessages.CountAsync(m =>
                m.TaskId == task.Id && evidenceIds.Contains(m.Id));
            if (validEvidenceCount != evidenceIds.Distinct().Count())
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Evidence must reference messages from this task's Chat Hub." });
        }

        var incident = new TaskIncident
        {
            TaskId = task.Id,
            TaskReference = task.TaskId,
            ReporterUserId = userId.Value,
            CreatorUserId = task.CreatedByUserId,
            RunnerUserId = task.AcceptedByUserId.Value,
            Category = request.Category.Trim(),
            Description = request.Description.Trim(),
            EvidenceMessageIdsJson = evidenceIds.Count == 0 ? null : JsonSerializer.Serialize(evidenceIds.Distinct().ToArray())
        };
        _context.TaskIncidents.Add(incident);
        await _context.SaveChangesAsync();

        await _notifications.NotifyAdminsAsync(
            "task_incident_reported",
            "New task incident reported",
            $"An incident was reported for task {task.TaskId} ({incident.Category}). Review the report and Chat Hub evidence.",
            task.Id);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { incident.Id, incident.Status, incident.CreatedAt },
            Message = "Incident sent to DoForYou support. Your report does not automatically change escrow or payout status."
        });
    }

    [HttpGet("incidents/my")]
    public async Task<ActionResult<ApiResponse<object>>> MyIncidents()
    {
        var userId = CurrentUserId();
        if (userId == null) return Unauthorized();
        var incidents = await _context.TaskIncidents.AsNoTracking()
            .Where(i => i.ReporterUserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id, i.TaskReference, i.Category, i.Description, i.Status, i.AdminNotes, i.CreatedAt, i.ReviewedAt })
            .ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = incidents });
    }

    [HttpGet("admin/incidents")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> AdminList([FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.TaskIncidents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(i => i.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new
            {
                i.Id, i.TaskReference, i.TaskId, i.ReporterUserId, i.CreatorUserId, i.RunnerUserId,
                i.Category, i.Description, i.Status, i.EvidenceMessageIdsJson, i.AdminNotes,
                i.ReviewedByAdminId, i.CreatedAt, i.ReviewedAt,
                i.EscrowHoldPlacedAt, i.EscrowHoldReleasedAt, i.EscrowHoldPlacedByAdminId,
                i.EscrowHoldDeadlineBefore, i.EscrowHoldReason, i.EscrowHoldReleaseReason
            }).ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { incidents = rows, total, page, pageSize } });
    }

    [HttpPost("admin/incidents/{id:int}/hold-escrow")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> PlaceEscrowHold(int id, [FromBody] IncidentEscrowHoldRequest request)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;

        var incident = await _context.TaskIncidents
            .Include(i => i.Task)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (incident == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Incident not found." });
        if (incident.EscrowHoldPlacedAt.HasValue)
            return Conflict(new ApiResponse<object> { Success = false, Message = "This incident has already used its escrow-hold action." });

        var task = incident.Task;
        if (task.TaskStatus != "AwaitingConfirmation" ||
            task.EscrowStatus != "held" ||
            task.PaymentStatus != "EscrowHeld")
            return Conflict(new ApiResponse<object> { Success = false, Message = "An incident hold can only be placed while a completed task is awaiting confirmation and escrow remains held." });

        var now = DateTime.UtcNow;
        int claimed;
        if (_context.Database.IsRelational())
        {
            // This conditional database update races safely with EscrowService's
            // conditional AwaitingConfirmation -> PayoutPending claim. Only one wins.
            claimed = await _context.Tasks
                .Where(t => t.Id == task.Id &&
                            t.TaskStatus == "AwaitingConfirmation" &&
                            t.EscrowStatus == "held" &&
                            t.PaymentStatus == "EscrowHeld")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.EscrowStatus, "incident_hold")
                    .SetProperty(t => t.UpdatedAt, now));
        }
        else
        {
            task.EscrowStatus = "incident_hold";
            task.UpdatedAt = now;
            claimed = 1;
        }

        if (claimed != 1)
            return Conflict(new ApiResponse<object> { Success = false, Message = "The task is no longer eligible for an incident escrow hold. Refresh and review its current state." });

        incident.Status = "UnderReview";
        incident.EscrowHoldPlacedAt = now;
        incident.EscrowHoldPlacedByAdminId = adminId.Value;
        incident.EscrowHoldDeadlineBefore = task.EscrowHoldUntil ?? task.CompletedAt?.AddHours(24);
        incident.EscrowHoldReason = reason;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "TaskIncidentEscrowHeld",
            EntityType = "TaskIncident",
            EntityId = incident.Id,
            OldValues = JsonSerializer.Serialize(new { escrowStatus = "held", paymentStatus = task.PaymentStatus }),
            NewValues = JsonSerializer.Serialize(new { escrowStatus = "incident_hold", reason, taskReference = task.TaskId }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { incident.Id, incident.Status, incident.EscrowHoldPlacedAt, incident.EscrowHoldDeadlineBefore },
            Message = "Escrow is on an audited incident hold. The payment status and runner payout have not been released."
        });
    }

    [HttpPost("admin/incidents/{id:int}/release-escrow-hold")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> ReleaseEscrowHold(int id, [FromBody] IncidentEscrowHoldRequest request)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;

        var incident = await _context.TaskIncidents
            .Include(i => i.Task)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (incident == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Incident not found." });
        if (!incident.EscrowHoldPlacedAt.HasValue || incident.EscrowHoldReleasedAt.HasValue)
            return Conflict(new ApiResponse<object> { Success = false, Message = "This incident does not have an active escrow hold." });

        var task = incident.Task;
        if (task.TaskStatus != "AwaitingConfirmation" ||
            task.EscrowStatus != "incident_hold" ||
            task.PaymentStatus != "EscrowHeld")
            return Conflict(new ApiResponse<object> { Success = false, Message = "The task's escrow state has changed. Do not release this hold automatically; review the task and payment state first." });

        var now = DateTime.UtcNow;
        var heldFor = now - incident.EscrowHoldPlacedAt.Value;
        if (heldFor < TimeSpan.Zero) heldFor = TimeSpan.Zero;
        var originalDeadline = incident.EscrowHoldDeadlineBefore ?? task.EscrowHoldUntil ?? task.CompletedAt?.AddHours(24) ?? now;
        var extendedDeadline = originalDeadline.Add(heldFor);

        int released;
        if (_context.Database.IsRelational())
        {
            released = await _context.Tasks
                .Where(t => t.Id == task.Id &&
                            t.TaskStatus == "AwaitingConfirmation" &&
                            t.EscrowStatus == "incident_hold" &&
                            t.PaymentStatus == "EscrowHeld")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.EscrowStatus, "held")
                    .SetProperty(t => t.EscrowHoldUntil, extendedDeadline)
                    .SetProperty(t => t.UpdatedAt, now));
        }
        else
        {
            task.EscrowStatus = "held";
            task.EscrowHoldUntil = extendedDeadline;
            task.UpdatedAt = now;
            released = 1;
        }

        if (released != 1)
            return Conflict(new ApiResponse<object> { Success = false, Message = "The escrow hold could not be released because the task state changed." });

        incident.EscrowHoldReleasedAt = now;
        incident.EscrowHoldReleaseReason = reason;
        incident.AdminNotes = string.IsNullOrWhiteSpace(incident.AdminNotes)
            ? $"Escrow hold released: {reason}"
            : $"{incident.AdminNotes}\nEscrow hold released: {reason}";

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "TaskIncidentEscrowHoldReleased",
            EntityType = "TaskIncident",
            EntityId = incident.Id,
            OldValues = JsonSerializer.Serialize(new { escrowStatus = "incident_hold", escrowHoldUntil = originalDeadline }),
            NewValues = JsonSerializer.Serialize(new { escrowStatus = "held", escrowHoldUntil = extendedDeadline, heldForSeconds = (long)heldFor.TotalSeconds, reason }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { incident.Id, incident.EscrowHoldReleasedAt, escrowHoldUntil = extendedDeadline },
            Message = "Escrow hold released. The confirmation deadline was extended by the hold duration; normal backend release rules now apply."
        });
    }

    [HttpPatch("admin/incidents/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> Review(int id, [FromBody] ReviewTaskIncidentRequest request)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        var status = request.Status?.Trim();
        var notes = request.AdminNotes?.Trim();
        if (status is not ("UnderReview" or "Resolved" or "Dismissed"))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Status must be UnderReview, Resolved or Dismissed." });
        if (string.IsNullOrWhiteSpace(notes) || notes.Length < 5 || notes.Length > 1000)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Admin notes are required (5 to 1,000 characters)." });

        var incident = await _context.TaskIncidents.FirstOrDefaultAsync(i => i.Id == id);
        if (incident == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Incident not found." });
        if (incident.Status is "Resolved" or "Dismissed")
            return Conflict(new ApiResponse<object> { Success = false, Message = "This incident has already been closed." });

        var previousStatus = incident.Status;
        incident.Status = status!;
        incident.AdminNotes = notes;
        incident.ReviewedByAdminId = adminId.Value;
        incident.ReviewedAt = DateTime.UtcNow;
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "TaskIncidentReviewed",
            EntityType = "TaskIncident",
            EntityId = incident.Id,
            OldValues = JsonSerializer.Serialize(new { status = previousStatus }),
            NewValues = JsonSerializer.Serialize(new { status, adminNotes = notes }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { incident.Id, incident.Status, incident.ReviewedAt } });
    }

    private int? CurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }
}

public class CreateTaskIncidentRequest
{
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<int>? EvidenceMessageIds { get; set; }
}

public class ReviewTaskIncidentRequest
{
    public string Status { get; set; } = string.Empty;
    public string AdminNotes { get; set; } = string.Empty;
}

public class IncidentEscrowHoldRequest
{
    public string Reason { get; set; } = string.Empty;
}

