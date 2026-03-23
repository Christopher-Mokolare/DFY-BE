using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Hubs;
using DoForYou.API.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/tasks")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly INotificationService _notificationService;

    public MessagesController(AppDbContext context, IHubContext<ChatHub> hubContext, INotificationService notificationService)
    {
        _context = context;
        _hubContext = hubContext;
        _notificationService = notificationService;
    }

    [HttpPost("{taskId}/messages")]
    public async Task<ActionResult<ApiResponse<bool>>> SendMessage(string taskId, [FromBody] SendMessageRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Try to find task by string TaskId first, then by numeric Id
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null && int.TryParse(taskId, out var numericId))
        {
            task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == numericId);
        }
        
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

        // Get sender info for notification
        var sender = await _context.Users.FindAsync(userId.Value);
        var senderName = $"{sender?.FirstName} {sender?.LastName}";
        
        // Send notification to the other party
        var recipientId = task.CreatedByUserId == userId ? task.AcceptedByUserId : task.CreatedByUserId;
        if (recipientId.HasValue)
        {
            await _notificationService.NotifyNewMessageAsync(recipientId.Value, task.TaskDescription, senderName, task.Id);
        }

        // Broadcast message to all users in the task group
        var messageDto = new
        {
            id = message.Id,
            taskId = taskId,
            senderId = message.SenderId,
            senderName = senderName,
            content = message.Content,
            timestamp = message.CreatedAt,
            isRead = message.IsRead
        };
        
        await _hubContext.Clients.Group($"task-{taskId}").SendAsync("ReceiveMessage", messageDto);

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

        // Try to find task by string TaskId first, then by numeric Id
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null && int.TryParse(taskId, out var numericId))
        {
            task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == numericId);
        }
        
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            return Ok(new ApiResponse<List<object>> { Success = false, Message = "Access denied" });

        var messages = await _context.TaskMessages
            .Include(m => m.Sender)
            .Where(m => m.TaskId == task.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                id = m.Id,
                taskId = task.TaskId,
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

    [HttpPut("{taskId}/messages/read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkMessagesAsRead(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Access denied" });

        await _context.TaskMessages
            .Where(m => m.TaskId == task.Id && m.SenderId != userId && !m.IsRead)
            .ExecuteUpdateAsync(m => m.SetProperty(p => p.IsRead, true));

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Messages marked as read"
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