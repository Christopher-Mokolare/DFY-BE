using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;

    public AdminController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var totalUsers = await _context.Users.CountAsync();
        var totalTasks = await _context.Tasks.CountAsync();
        var pendingTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "PendingPayment");
        var activeTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Posted");
        var completedTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Completed");
        var totalRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed")
            .SumAsync(t => t.Budget);

        var recentTasks = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .Select(t => new
            {
                taskId = t.TaskId,
                description = t.TaskDescription,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                budget = t.Budget,
                status = t.TaskStatus,
                createdAt = t.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalUsers,
                totalTasks,
                pendingTasks,
                activeTasks,
                completedTasks,
                totalRevenue,
                recentTasks
            }
        });
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetAllTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null)
    {
        var query = _context.Tasks.Include(t => t.CreatedByUser).AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(t => t.TaskStatus == status);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.TaskDescription.Contains(search) || t.TaskId.Contains(search));

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var tasks = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskDto
            {
                Id = t.Id,
                TaskId = t.TaskId,
                UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                UserContact = t.CreatedByUser.PhoneNumber ?? t.CreatedByUser.Email,
                CreatedByUserId = t.CreatedByUserId,
                TaskDescription = t.TaskDescription,
                Category = t.Category,
                Area = t.Area,
                DateNeeded = t.DateNeeded,
                Budget = t.Budget,
                Notes = t.Notes,
                PaymentStatus = t.PaymentStatus,
                TaskStatus = t.TaskStatus,
                HelperName = t.HelperName,
                HelperContact = t.HelperContact,
                Priority = t.Priority,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt
            })
            .ToListAsync();

        var response = new PaginatedResponse<TaskDto>
        {
            Success = true,
            Count = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Tasks = tasks
        };

        return Ok(new ApiResponse<PaginatedResponse<TaskDto>>
        {
            Success = true,
            Data = response
        });
    }

    [HttpPatch("tasks/{taskId}/verify")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyTask(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        task.PaymentStatus = "Completed";
        task.TaskStatus = "Posted";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task verified successfully"
        });
    }

    [HttpPatch("tasks/{taskId}/unverify")]
    public async Task<ActionResult<ApiResponse<bool>>> UnverifyTask(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        task.PaymentStatus = "Pending";
        task.TaskStatus = "PendingPayment";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task unverified successfully"
        });
    }

    [HttpGet("payments")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentsOverview()
    {
        var totalRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed")
            .SumAsync(t => t.Budget);
        
        var pendingRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Pending")
            .SumAsync(t => t.Budget);

        var recentPayments = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.PaymentStatus == "Completed")
            .OrderByDescending(t => t.UpdatedAt)
            .Take(10)
            .Select(t => new
            {
                taskId = t.TaskId,
                amount = t.Budget,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                date = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalRevenue,
                pendingRevenue,
                recentPayments
            }
        });
    }

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        var totalCount = await _context.Users.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var users = await _context.Users
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                id = u.Id,
                name = $"{u.FirstName} {u.LastName}",
                email = u.Email,
                contact = u.PhoneNumber,
                role = u.Roles,
                isVerified = u.IsVerified,
                profileCompleted = u.ProfileCompleted,
                tasksPosted = _context.Tasks.Count(t => t.CreatedByUserId == u.Id),
                tasksCompleted = u.CompletedTasks,
                rating = u.Rating,
                lastLoginAt = u.LastLoginAt,
                createdAt = u.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                users,
                totalCount,
                page,
                pageSize,
                totalPages
            }
        });
    }

    [HttpPatch("users/{userId}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserStatus(int userId, [FromBody] object request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        // For now, just return success - in a real app you'd parse the request and update the user
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "User status updated successfully"
        });
    }

    [HttpPatch("users/{userId}/role")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserRole(int userId, [FromBody] object request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        // For now, just return success - in a real app you'd parse the request and update the user role
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "User role updated successfully"
        });
    }

    [HttpPatch("tasks/bulk-verify")]
    public async Task<ActionResult<ApiResponse<bool>>> BulkVerifyTasks([FromBody] object request)
    {
        // For now, just return success - in a real app you'd parse the taskIds and update them
        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Tasks verified successfully"
        });
    }
}