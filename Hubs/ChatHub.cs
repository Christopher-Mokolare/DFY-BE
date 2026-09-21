using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly ILogger<ChatHub> _logger;
    private readonly AppDbContext _context;

    public ChatHub(ILogger<ChatHub> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _logger.LogInformation("User {UserId} connected", userId);
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(int taskId, string message)
    {
        var task = await EnsureTaskMember(taskId);
        if (task.TaskStatus == "RunnerPaid" || task.TaskStatus == "Cancelled")
            throw new HubException("This conversation is closed.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1000)
            throw new HubException("Invalid message.");
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        await Clients.Group($"task-{taskId}").SendAsync("ReceiveMessage", new
        {
            taskId,
            senderId = userId,
            message,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task JoinTaskChat(int taskId)
    {
        await EnsureTaskMember(taskId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{taskId}");
    }

    public async Task LeaveTaskChat(int taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{taskId}");
    }

    private async Task<DoForYou.API.Models.Task> EnsureTaskMember(int taskId)
    {
        if (!int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new HubException("Unauthorized.");
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            throw new HubException("You are not a member of this task.");
        return task;
    }
}
