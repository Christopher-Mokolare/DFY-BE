using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _context;

    public NotificationsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetNotifications()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notifications = await _context.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                id = n.Id,
                type = n.Type,
                title = n.Title,
                message = n.Message,
                isRead = n.IsRead,
                createdAt = n.CreatedAt,
                relatedTaskId = n.RelatedTaskId
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = notifications,
            Message = "Notifications retrieved successfully"
        });
    }

    [HttpPost("{id}/mark-read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAsRead(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Notification not found" });

        notification.IsRead = true;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Notification marked as read"
        });
    }

    [HttpPost("mark-all-read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAllAsRead()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.IsRead, true));

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "All notifications marked as read"
        });
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<ApiResponse<int>>> GetUnreadCount()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var count = await _context.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);

        return Ok(new ApiResponse<int>
        {
            Success = true,
            Data = count
        });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteNotification(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Notification not found" });

        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Notification deleted successfully"
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}