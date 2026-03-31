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
    private readonly IEscrowService _escrowService;
    private readonly INotificationService _notificationService;

    public TasksController(AppDbContext context, IRulesEngine rulesEngine, IEscrowService escrowService, INotificationService notificationService)
    {
        _context = context;
        _rulesEngine = rulesEngine;
        _escrowService = escrowService;
        _notificationService = notificationService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> CreateTask([FromBody] CreateTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();
        
        if (!user.ProfileCompleted)
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "Please complete your profile before creating tasks"
            });

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
        
        // Calculate commission
        var commission = _escrowService.CalculateCommission(request.Budget);
        var payout = request.Budget - commission;
        
        var task = new Models.Task
        {
            TaskId = taskId,
            TaskDescription = request.TaskDescription,
            Category = request.Category,
            Area = request.Area,
            DateNeeded = DateTime.SpecifyKind(request.DateNeeded, DateTimeKind.Utc),
            Budget = request.Budget,
            Notes = request.Notes,
            Priority = request.Priority,
            CreatedByUserId = userId.Value,
            PaymentStatus = "Pending",
            TaskStatus = "PendingPayment",
            CommissionAmount = commission,
            PayoutAmount = payout,
            EscrowStatus = "pending"
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
            .Where(t => (t.PaymentStatus == "EscrowHeld" || t.PaymentStatus == "Completed") && t.TaskStatus == "Posted");

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

    [HttpGet("pending-payment")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetPendingPaymentTasks()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var tasks = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.TaskStatus == "PendingPayment")
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                taskId = t.TaskId,
                description = t.TaskDescription,
                budget = t.Budget,
                createdAt = t.CreatedAt,
                category = t.Category,
                area = t.Area
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = tasks,
            Message = "Pending payment tasks retrieved successfully"
        });
    }

    [HttpPost("{taskId}/claim")]
    public async Task<ActionResult<ApiResponse<bool>>> ClaimTask(string taskId, [FromBody] ClaimTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null || !user.ProfileCompleted)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Please complete your profile before claiming tasks" });

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.CreatedByUserId == userId || task.TaskStatus != "Posted")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Task not available" });

        task.AcceptedByUserId = userId;
        task.HelperName = request.HelperName;
        task.HelperContact = request.HelperContact;
        task.TaskStatus = "Claimed";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Send notification to task creator
        await _notificationService.NotifyTaskClaimedAsync(task.CreatedByUserId, task.TaskDescription, request.HelperName);

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

    [HttpGet("{taskId}/payment-url")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentUrl(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.TaskId == taskId && t.CreatedByUserId == userId);

        if (task == null || task.TaskStatus != "PendingPayment")
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found or payment not pending" });

        var paymentUrl = GeneratePayFastUrl(task, task.CreatedByUser);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { paymentUrl },
            Message = "Payment URL generated successfully"
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
        task.EscrowHoldUntil = DateTime.UtcNow.AddHours(48); // 48-hour escrow hold
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Send notification to task creator
        var runner = await _context.Users.FindAsync(userId.Value);
        var runnerName = $"{runner?.FirstName} {runner?.LastName}";
        await _notificationService.NotifyTaskCompletedAsync(task.CreatedByUserId, task.TaskDescription, runnerName);

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task completed! Payment will be released after confirmation."
        });
    }

    [HttpPost("{taskId}/confirm")]
    public async Task<ActionResult<ApiResponse<bool>>> ConfirmTask(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.CreatedByUserId != userId || task.TaskStatus != "Completed")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Cannot confirm task" });

        // Release payment to runner
        task.TaskStatus = "RunnerPaid";
        task.EscrowStatus = "none";
        task.PaidToRunnerAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;

        // Add wallet transaction for runner
        var walletTransaction = new WalletTransaction
        {
            UserId = task.AcceptedByUserId.Value,
            Amount = task.PayoutAmount,
            TransactionType = "credit",
            Status = "completed",
            Description = $"Payment for task: {task.TaskDescription}",
            Reference = task.TaskId,
            CreatedAt = DateTime.UtcNow
        };

        // Update runner's wallet balance
        var runner = await _context.Users.FindAsync(task.AcceptedByUserId);
        if (runner != null)
        {
            runner.WalletBalance += task.PayoutAmount;
        }

        _context.WalletTransactions.Add(walletTransaction);
        await _context.SaveChangesAsync();

        // Send notification to runner about payment
        await _notificationService.NotifyPaymentReleasedAsync(task.AcceptedByUserId.Value, task.TaskDescription, task.PayoutAmount);

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task confirmed and payment released!"
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
                location = t.Area,
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


    [HttpPut("{taskId}")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateTask(string taskId, [FromBody] UpdateTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId && t.CreatedByUserId == userId);
        if (task == null)
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });

        if (task.TaskStatus != "PendingPayment")
            return Ok(new ApiResponse<object> { Success = false, Message = "Only pending payment tasks can be edited" });

        // Update task fields
        task.TaskDescription = request.TaskDescription;
        task.Category = request.Category;
        task.Area = request.Area;
        task.Priority = request.Priority;
        task.DateNeeded = DateTime.SpecifyKind(request.DateNeeded, DateTimeKind.Utc);
        task.Budget = request.Budget;
        task.Notes = request.Notes;
        task.UpdatedAt = DateTime.UtcNow;

        // Recalculate commission and payout with new budget
        var commission = _escrowService.CalculateCommission(request.Budget);
        task.CommissionAmount = commission;
        task.PayoutAmount = request.Budget - commission;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { taskId = task.TaskId },
            Message = "Task updated successfully"
        });
    }

    [HttpPost("payment-success")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> HandlePaymentSuccess()
    {
        var userId = GetCurrentUserId();
        
        // If no user ID from token, try to find the most recent pending payment task
        if (userId == null)
        {
            // For anonymous access, we can't identify the specific user
            // This should be handled by the PayFast notify webhook instead
            return Ok(new ApiResponse<bool>
            {
                Success = true,
                Data = true,
                Message = "Payment confirmation received"
            });
        }

        // Find the most recent pending payment task for this user
        var task = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.PaymentStatus == "Pending")
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (task != null)
        {
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "Posted";
            task.EscrowStatus = "held";
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

        var user = await _context.Users.FindAsync(userId);
        var currentMonth = DateTime.UtcNow.Month;
        var currentYear = DateTime.UtcNow.Year;
        
        // Task Creator Stats
        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId);
        var pendingPayment = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "PendingPayment");
        var creatorActiveTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "Claimed");
        var awaitingConfirmation = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "Completed");
        var creatorCompletedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid");
        
        var totalSpent = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid")
            .SumAsync(t => t.Budget);
            
        var thisMonthSpending = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid" && 
                       t.UpdatedAt.Month == currentMonth && t.UpdatedAt.Year == currentYear)
            .SumAsync(t => t.Budget);
            
        var averageTaskCost = creatorCompletedTasks > 0 ? totalSpent / creatorCompletedTasks : 0;
        
        // Task Runner Stats
        var availableTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Posted" && t.PaymentStatus == "Completed");
        var runnerActiveTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        var runnerCompletedTasks = await _context.Tasks.CountAsync(t => 
            t.AcceptedByUserId == userId && 
            (t.TaskStatus == "Completed" || t.TaskStatus == "RunnerPaid"));
            
        var totalEarnings = await _context.WalletTransactions
            .Where(wt => wt.UserId == userId && 
                        wt.TransactionType == "credit" && 
                        wt.Status == "completed")
            .SumAsync(wt => wt.Amount);
            
        var pendingPayouts = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId && 
                       t.TaskStatus == "Completed" && 
                       t.EscrowStatus == "held")
            .SumAsync(t => t.PayoutAmount);
            
        var thisMonthEarnings = await _context.WalletTransactions
            .Where(wt => wt.UserId == userId && 
                        wt.TransactionType == "credit" && 
                        wt.Status == "completed" &&
                        wt.CreatedAt.Month == currentMonth && wt.CreatedAt.Year == currentYear)
            .SumAsync(wt => wt.Amount);
            
        var totalAcceptedTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId);
        var completionRate = totalAcceptedTasks > 0 ? (runnerCompletedTasks * 100) / totalAcceptedTasks : 0;
        var averageEarning = runnerCompletedTasks > 0 ? totalEarnings / runnerCompletedTasks : 0;

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                // Task Creator Stats
                postedTasks,
                pendingPayment,
                activeTasks = creatorActiveTasks,
                awaitingConfirmation,
                completedTasks = creatorCompletedTasks,
                totalSpent,
                thisMonthSpending,
                averageTaskCost,
                
                // Task Runner Stats
                availableTasks,
                myActiveTasks = runnerActiveTasks,
                runnerCompletedTasks,
                totalEarnings,
                availableBalance = user?.WalletBalance ?? 0,
                pendingPayouts,
                thisMonthEarnings,
                completionRate,
                averageEarning,
                
                // Shared
                myRating = user?.Rating ?? 0
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
        var backendUrl = Environment.GetEnvironmentVariable("BACKEND_URL") ?? "http://localhost:5000";
        var returnUrl = $"{backendUrl}/api/v1/payment/return";
        var cancelUrl = $"{backendUrl}/api/v1/payment/cancel";
        var notifyUrl = $"{backendUrl}/api/v1/payment/notify";

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