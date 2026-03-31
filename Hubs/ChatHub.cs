using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace DoForYou.API.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(ILogger<ChatHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _logger.LogInformation("User {UserId} connected", userId);
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(int taskId, string message)
    {
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
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{taskId}");
    }

    public async Task LeaveTaskChat(int taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{taskId}");
    }
}
