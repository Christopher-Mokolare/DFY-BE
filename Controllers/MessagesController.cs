using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/tasks")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _context;

    public MessagesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost("{taskId}/messages")]
    public async Task<ActionResult<ApiResponse<bool>>> SendMessage(string taskId, [FromBody] SendMessageRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Access denied" });

        var message = new TaskMessage
        {
            TaskId = task.Id,
            SenderId = userId.Value,
            Content = request.Content,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.TaskMessages.Add(message);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Message sent successfully!"
        });
    }

    [HttpGet("{taskId}/messages")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetMessages(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            return Ok(new ApiResponse<List<object>> { Success = false, Message = "Access denied" });

        var messages = await _context.TaskMessages
            .Include(m => m.Sender)
            .Where(m => m.TaskId == task.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                id = m.Id,
                taskId = taskId,
                senderId = m.SenderId,
                senderName = $"{m.Sender.FirstName} {m.Sender.LastName}",
                content = m.Content,
                timestamp = m.CreatedAt,
                isRead = m.IsRead,
                isCurrentUser = m.SenderId == userId
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = messages,
            Message = "Messages retrieved successfully"
        });
    }

    [HttpPost("{taskId}/progress")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProgress(string taskId, [FromBody] UpdateProgressRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.AcceptedByUserId != userId)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Access denied" });

        var progressUpdate = new TaskProgressUpdate
        {
            TaskId = task.Id,
            UserId = userId.Value,
            Message = request.ProgressNote,
            CreatedAt = DateTime.UtcNow
        };

        _context.TaskProgressUpdates.Add(progressUpdate);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Progress updated successfully!"
        });
    }

    [HttpGet("{taskId}/progress")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetProgress(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            return Ok(new ApiResponse<List<object>> { Success = false, Message = "Access denied" });

        var progressUpdates = await _context.TaskProgressUpdates
            .Include(p => p.User)
            .Where(p => p.TaskId == task.Id)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                id = p.Id,
                message = p.Message,
                timestamp = p.CreatedAt,
                userId = p.UserId,
                userName = $"{p.User.FirstName} {p.User.LastName}"
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = progressUpdates,
            Message = "Progress updates retrieved successfully"
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public class SendMessageRequest
{
    public string Content { get; set; } = string.Empty;
}

public class UpdateProgressRequest
{
    public string ProgressNote { get; set; } = string.Empty;
}