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
            task.TaskStatus is "Posted" or "PendingPayment" or "Cancelled" or "RunnerPaid")
            return Conflict(new ApiResponse<object> { Success = false, Message = "An incident can only be reported on a task with an assigned runner that has not been closed." });

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
                i.ReviewedByAdminId, i.CreatedAt, i.ReviewedAt
            }).ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { incidents = rows, total, page, pageSize } });
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
            OldValues = JsonSerializer.Serialize(new { status = "Open" }),
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
