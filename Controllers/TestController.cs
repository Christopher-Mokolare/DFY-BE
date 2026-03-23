using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/test")]
public class TestController : ControllerBase
{
    private readonly AppDbContext _context;

    public TestController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new { message = "API is working", timestamp = DateTime.UtcNow });
    }

    [HttpPost("test-login")]
    public IActionResult TestLogin([FromBody] object request)
    {
        Console.WriteLine($"Test login request received: {request}");
        return Ok(new { message = "Request received", data = request });
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        try
        {
            var users = await _context.Users
                .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, u.Roles })
                .ToListAsync();
            
            return Ok(users);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("db")]
    public async Task<IActionResult> TestDatabase()
    {
        try
        {
            var canConnect = await _context.Database.CanConnectAsync();
            var categoryCount = await _context.Categories.CountAsync();
            var userCount = await _context.Users.CountAsync();
            
            return Ok(new
            {
                connected = canConnect,
                categories = categoryCount,
                users = userCount,
                connectionString = _context.Database.GetConnectionString()
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("simulate-payfast/{taskId}")]
    public async Task<IActionResult> SimulatePayFastWebhook(string taskId)
    {
        try
        {
            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null)
                return NotFound(new { error = "Task not found" });

            // Simulate successful PayFast webhook
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "Posted";
            task.EscrowStatus = "held";
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { 
                message = "PayFast webhook simulated successfully", 
                taskId = task.TaskId,
                paymentStatus = task.PaymentStatus,
                taskStatus = task.TaskStatus
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}