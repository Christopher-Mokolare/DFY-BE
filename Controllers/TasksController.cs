using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IRulesEngine _rulesEngine;

    public TasksController(AppDbContext context, IRulesEngine rulesEngine)
    {
        _context = context;
        _rulesEngine = rulesEngine;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> CreateTask([FromBody] CreateTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        // Check if user can create tasks using rules engine
        var ruleContext = new RuleContext
        {
            CurrentUser = user,
            Action = "CreateTask"
        };

        var canCreate = await _rulesEngine.CanPerformActionAsync("User", "CreateTask", ruleContext);
        if (!canCreate)
        {
            var validationResults = await _rulesEngine.ValidateAsync("User", "permission", ruleContext);
            var errorMessage = validationResults.FirstOrDefault(r => !r.IsValid)?.ErrorMessage ?? "You cannot create tasks";
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = errorMessage
            });
        }

        var taskId = GenerateTaskId();
        var task = new Models.Task
        {
            TaskId = taskId,
            TaskDescription = request.TaskDescription,
            Category = request.Category,
            Area = request.Area,
            DateNeeded = request.DateNeeded,
            Budget = request.Budget,
            Notes = request.Notes,
            Priority = request.Priority,
            CreatedByUserId = userId.Value,
            PaymentStatus = "Pending",
            TaskStatus = "PendingPayment"
        };

        // Validate task using rules engine
        ruleContext.Entity = task;
        var taskValidation = await _rulesEngine.ValidateEntityAsync(task, ruleContext);
        if (!taskValidation.IsValid)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = taskValidation.ErrorMessage ?? "Task validation failed"
            });
        }

        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        var taskDto = new TaskDto
        {
            Id = task.Id,
            TaskId = task.TaskId,
            UserName = $"{user.FirstName} {user.LastName}",
            UserContact = user.PhoneNumber ?? user.Email,
            CreatedByUserId = task.CreatedByUserId,
            TaskDescription = task.TaskDescription,
            Category = task.Category,
            Area = task.Area,
            DateNeeded = task.DateNeeded,
            Budget = task.Budget,
            Notes = task.Notes,
            PaymentStatus = task.PaymentStatus,
            TaskStatus = task.TaskStatus,
            Priority = task.Priority,
            CreatedAt = task.CreatedAt
        };

        var paymentUrl = GeneratePayFastUrl(task, user);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { task = taskDto, paymentUrl },
            Message = "Task created successfully. Complete payment to activate."
        });
    }

    [HttpGet("available")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<TaskDto>>> GetAvailableTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null)
    {
        var query = _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.PaymentStatus == "Completed" && t.TaskStatus == "Posted");

        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.TaskDescription.Contains(search) || t.Area.Contains(search));

        if (!string.IsNullOrEmpty(category))
            query = query.Where(t => t.Category == category);

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

        return Ok(new PaginatedResponse<TaskDto>
        {
            Success = true,
            Count = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Tasks = tasks
        });
    }

    [HttpGet("my-posted")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetMyPostedTasks()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var tasks = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.CreatedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
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
            Count = tasks.Count,
            Page = 1,
            PageSize = tasks.Count,
            TotalPages = 1,
            Tasks = tasks
        };

        return Ok(new ApiResponse<PaginatedResponse<TaskDto>>
        {
            Success = true,
            Data = response
        });
    }

    [HttpPost("{taskId}/claim")]
    public async Task<ActionResult<ApiResponse<bool>>> ClaimTask(string taskId, [FromBody] ClaimTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.CreatedByUserId == userId || task.TaskStatus != "Posted")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Task not available" });

        task.AcceptedByUserId = userId;
        task.HelperName = request.HelperName;
        task.HelperContact = request.HelperContact;
        task.TaskStatus = "Claimed";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task claimed successfully!"
        });
    }

    [HttpPut("{taskId}/payment-status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdatePaymentStatus(string taskId, [FromBody] UpdatePaymentStatusRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.CreatedByUserId != userId)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        if (task.PaymentStatus != "Pending" || request.PaymentStatus.ToLower() != "completed")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid payment status update" });

        task.PaymentStatus = "Completed";
        task.TaskStatus = "Posted";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Payment status updated successfully!"
        });
    }

    [HttpGet("filters")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> GetFilterOptions()
    {
        var categories = await _context.Categories.Select(c => c.Name).ToListAsync();
        var statuses = new[] { "Posted", "Claimed", "Completed" };
        
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { categories, statuses }
        });
    }

    [HttpGet("{taskId}")]
    public async Task<ActionResult<ApiResponse<object>>> GetTask(string taskId)
    {
        var task = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Include(t => t.AcceptedByUser)
            .FirstOrDefaultAsync(t => t.TaskId == taskId);

        if (task == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Task not found" });

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
            .ToListAsync();

        var taskDto = new
        {
            id = task.Id,
            taskId = task.TaskId,
            title = task.TaskDescription,
            description = task.TaskDescription,
            category = task.Category,
            location = task.Area,
            budget = task.Budget,
            status = task.TaskStatus.ToLower(),
            priority = task.Priority.ToLower(),
            createdAt = task.CreatedAt,
            dueDate = task.DateNeeded,
            creatorName = $"{task.CreatedByUser.FirstName} {task.CreatedByUser.LastName}",
            creatorContact = task.CreatedByUser.PhoneNumber ?? task.CreatedByUser.Email,
            runnerName = task.AcceptedByUser != null ? $"{task.AcceptedByUser.FirstName} {task.AcceptedByUser.LastName}" : null,
            runnerContact = task.AcceptedByUser?.PhoneNumber ?? task.AcceptedByUser?.Email,
            runnerId = task.AcceptedByUserId,
            createdByUserId = task.CreatedByUserId,
            completedAt = task.CompletedAt,
            notes = task.Notes,
            progressUpdates = progressUpdates,
            canEdit = task.TaskStatus == "Posted",
            canComplete = task.TaskStatus == "Claimed",
            canCancel = task.TaskStatus != "Completed"
        };

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = taskDto,
            Message = "Task retrieved successfully"
        });
    }

    [HttpPost("{taskId}/complete")]
    public async Task<ActionResult<ApiResponse<bool>>> CompleteTask(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.AcceptedByUserId != userId || task.TaskStatus != "Claimed")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Cannot complete task" });

        task.TaskStatus = "Completed";
        task.CompletedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task marked as completed successfully!"
        });
    }

    [HttpGet("my-active")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetMyActiveTasks()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var tasks = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed")
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new
            {
                id = t.Id,
                taskId = t.TaskId,
                title = t.TaskDescription,
                description = t.TaskDescription,
                category = t.Category,
                budget = t.Budget,
                status = t.TaskStatus.ToLower(),
                createdAt = t.CreatedAt,
                dueDate = t.DateNeeded,
                creatorName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                creatorContact = t.CreatedByUser.PhoneNumber ?? t.CreatedByUser.Email
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = tasks,
            Message = "Active tasks retrieved successfully"
        });
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<TaskDto>>> GetUserTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var query = _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.CreatedByUserId == userId);

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

        return Ok(new PaginatedResponse<TaskDto>
        {
            Success = true,
            Count = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Tasks = tasks
        });
    }

    [HttpPost("payment-success")]
    public async Task<ActionResult<ApiResponse<bool>>> HandlePaymentSuccess()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Find the most recent pending payment task for this user
        var task = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.PaymentStatus == "Pending")
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (task != null)
        {
            task.PaymentStatus = "Completed";
            task.TaskStatus = "Posted";
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Payment success processed"
        });
    }

    [HttpGet("dashboard/stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetTaskDashboardStats()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Get user info for wallet balance
        var user = await _context.Users.FindAsync(userId);
        
        // Tasks posted by this user
        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId);
        
        // Tasks currently being worked on by this user
        var activeTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        
        // Tasks completed by this user (as runner) - count RunnerPaid as completed
        var completedTasks = await _context.Tasks.CountAsync(t => 
            t.AcceptedByUserId == userId && 
            (t.TaskStatus == "Completed" || t.TaskStatus == "RunnerPaid"));
        
        // Total earnings from wallet balance
        var totalEarnings = user?.WalletBalance ?? 0;

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                postedTasks,
                activeTasks,
                completedTasks,
                totalEarnings
            }
        });
    }

    [HttpGet("dashboard/activity")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetDashboardActivity([FromQuery] int limit = 5)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var activities = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.CreatedByUserId == userId || t.AcceptedByUserId == userId)
            .OrderByDescending(t => t.UpdatedAt)
            .Take(limit)
            .Select(t => new
            {
                id = t.Id,
                taskId = t.TaskId,
                description = t.TaskDescription,
                status = t.TaskStatus,
                updatedAt = t.UpdatedAt,
                type = t.CreatedByUserId == userId ? "created" : "accepted"
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = activities
        });
    }

    [HttpGet("payment-history")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetPaymentHistory()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var payments = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.PaymentStatus == "Completed")
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new
            {
                taskId = t.TaskId,
                amount = t.Budget,
                status = t.PaymentStatus,
                date = t.UpdatedAt,
                description = t.TaskDescription
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = payments,
            Message = "Payment history retrieved successfully"
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private string GenerateTaskId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var random = Random.Shared.Next(1000, 9999);
        return $"DFY-{timestamp}-{random}";
    }

    private string GeneratePayFastUrl(Models.Task task, User user)
    {
        var baseUrl = "https://sandbox.payfast.co.za/eng/process";
        var merchantId = "10000100";
        var merchantKey = "46f0cd694581a";
        var returnUrl = "http://localhost:4200/tasks/payment-success";
        var cancelUrl = "http://localhost:4200/tasks/payment-cancel";
        var notifyUrl = "http://localhost:5001/api/v1/payment/notify";

        var parameters = new Dictionary<string, string>
        {
            ["merchant_id"] = merchantId,
            ["merchant_key"] = merchantKey,
            ["return_url"] = returnUrl,
            ["cancel_url"] = cancelUrl,
            ["notify_url"] = notifyUrl,
            ["name_first"] = user.FirstName,
            ["name_last"] = user.LastName,
            ["email_address"] = user.Email,
            ["m_payment_id"] = task.TaskId,
            ["amount"] = task.Budget.ToString("F2"),
            ["item_name"] = $"Task Payment - {task.TaskDescription}"
        };

        var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{baseUrl}?{queryString}";
    }
}