using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/disputes")]
[Authorize]
public class DisputesController : ControllerBase
{
    private readonly AppDbContext _context;

    public DisputesController(AppDbContext context) => _context = context;

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

        var dispute = new Models.Dispute
        {
            TaskId = task.Id,
            ReportedByUserId = userId.Value,
            Issue = request.Issue,
            Category = request.Category,
            Status = "Open"
        };

        _context.Disputes.Add(dispute);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { id = dispute.Id, status = dispute.Status },
            Message = "Dispute raised successfully"
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
        var query = _context.Disputes
            .Include(d => d.Task)
            .Include(d => d.ReportedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(d => d.Status == status);

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

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { disputes, total, page, pageSize }
        });
    }

    [HttpPatch("{id}/resolve")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> ResolveDispute(int id, [FromBody] ResolveDisputeRequest request)
    {
        var dispute = await _context.Disputes.FindAsync(id);
        if (dispute == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Dispute not found" });

        dispute.Status = "Resolved";
        dispute.Resolution = request.Resolution;
        dispute.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Dispute resolved" });
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
