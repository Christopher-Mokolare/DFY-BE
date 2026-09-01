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
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.Budget);

        var platformEarnings = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.CommissionAmount);

        var openDisputes = await _context.Disputes.CountAsync(d => d.Status == "Open");

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
                platformEarnings,
                grossVolume = totalRevenue,
                openDisputes,
                recentTasks
            }
        });
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetAllTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? taskStatus = null,
        [FromQuery] string? paymentStatus = null,
        [FromQuery] string? priority = null,
        [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Tasks.Include(t => t.CreatedByUser).AsQueryable();

        var resolvedTaskStatus = taskStatus ?? status;
        if (!string.IsNullOrEmpty(resolvedTaskStatus))
            query = query.Where(t => t.TaskStatus == resolvedTaskStatus);

        if (!string.IsNullOrEmpty(paymentStatus))
            query = query.Where(t => t.PaymentStatus == paymentStatus);

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(t => t.Priority == priority);

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
                UserContact = string.Empty,
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
        var paidStatuses = new[] { "EscrowHeld", "Completed", "RunnerPaid" };

        var totalRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.Budget);

        var platformRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.CommissionAmount);

        var helperPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "RunnerPaid")
            .SumAsync(t => t.PayoutAmount);

        var pendingRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Pending" && t.TaskStatus == "PendingPayment")
            .SumAsync(t => t.Budget);

        var pendingPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "Completed" && t.EscrowStatus == "held")
            .SumAsync(t => t.PayoutAmount);

        var recentPayments = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Include(t => t.AcceptedByUser)
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(10)
            .Select(t => new
            {
                taskId = t.TaskId,
                taskName = t.TaskDescription,
                amount = t.Budget,
                commission = t.CommissionAmount,
                payout = t.PayoutAmount,
                status = t.PaymentStatus,
                taskStatus = t.TaskStatus,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                runnerName = t.AcceptedByUser != null ? $"{t.AcceptedByUser.FirstName} {t.AcceptedByUser.LastName}" : null,
                runnerContact = t.AcceptedByUser != null ? t.AcceptedByUser.PhoneNumber : null,
                date = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalRevenue,
                platformEarnings = platformRevenue,
                grossVolume = totalRevenue,
                platformRevenue,
                helperPayouts,
                pendingRevenue,
                pendingCommission = pendingRevenue,
                pendingPayouts,
                totalRefunded = 0,
                recentPayments
            }
        });
    }

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
                contact = u.PhoneNumber == null ? null : "***",
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
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserStatus(int userId, [FromBody] UpdateUserStatusRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        user.IsVerified = request.IsVerified;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User status updated" });
    }

    [HttpPatch("users/{userId}/role")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserRole(int userId, [FromBody] UpdateUserRoleRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        user.Roles = request.Role;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User role updated" });
    }

    [HttpGet("users/{userId}/tasks")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserTaskHistory(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        var postedTasks = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, budget = t.Budget, createdAt = t.CreatedAt })
            .ToListAsync();

        var acceptedTasks = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, payout = t.PayoutAmount, createdAt = t.CreatedAt })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                user = new { user.Id, user.WalletBalance, user.Rating, user.CompletedTasks },
                postedTasks,
                acceptedTasks
            }
        });
    }

    [HttpPatch("tasks/bulk-verify")]
    public async Task<ActionResult<ApiResponse<bool>>> BulkVerifyTasks([FromBody] BulkVerifyRequest request)
    {
        var tasks = await _context.Tasks
            .Where(t => request.TaskIds.Contains(t.TaskId))
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.PaymentStatus = "Completed";
            task.TaskStatus = "Posted";
            task.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = $"{tasks.Count} tasks verified" });
    }

    [HttpPatch("tasks/{taskId}/force-release-escrow")]
    public async Task<ActionResult<ApiResponse<bool>>> ForceReleaseEscrow(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        task.EscrowStatus = "released";
        task.TaskStatus = "RunnerPaid";
        task.EscrowHoldUntil = DateTime.UtcNow.AddSeconds(-1);
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Escrow released" });
    }

    [HttpDelete("tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTask(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        _context.Tasks.Remove(task);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task deleted" });
    }

    [HttpDelete("users/{userId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteUser(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User deleted" });
    }

    [HttpGet("audit-logs")]
    public async Task<ActionResult<ApiResponse<object>>> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var total = await _context.AuditLogs.CountAsync();
        var logs = await _context.AuditLogs
            .Include(a => a.User)
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                id = a.Id,
                userId = a.UserId,
                userName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}" : "System",
                action = a.Action,
                entityType = a.EntityType,
                entityId = a.EntityId,
                ipAddress = a.IpAddress,
                createdAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { logs, total, page, pageSize }
        });
    }

    [HttpGet("withdrawal-requests")]
    public async Task<ActionResult<ApiResponse<object>>> GetWithdrawalRequests([FromQuery] string? status = null)
    {
        var query = _context.WithdrawalRequests
            .Include(w => w.User)
            .Include(w => w.BankAccount)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(w => w.Status == status);

        var requests = await query
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                id = w.Id,
                userId = w.UserId,
                userName = $"{w.User.FirstName} {w.User.LastName}",
                amount = w.Amount,
                fee = w.Fee,
                status = w.Status,
                reference = w.Reference,
                bankName = w.BankAccount.BankName,
                accountNumber = "****",
                createdAt = w.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = requests });
    }

    [HttpGet("bank-accounts")]
    public async Task<ActionResult<ApiResponse<object>>> GetBankAccounts([FromQuery] bool? unverifiedOnly = null)
    {
        var query = _context.BankAccounts.Include(b => b.User).AsQueryable();

        if (unverifiedOnly == true)
            query = query.Where(b => !b.IsVerified);

        var accounts = await query
            .Select(b => new
            {
                id = b.Id,
                userId = b.UserId,
                userName = $"{b.User.FirstName} {b.User.LastName}",
                bankName = b.BankName,
                accountNumber = b.AccountNumber,
                accountHolderName = b.AccountHolderName,
                isVerified = b.IsVerified,
                createdAt = b.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = accounts });
    }

    [HttpPatch("bank-accounts/{id}/verify")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyBankAccount(int id)
    {
        var account = await _context.BankAccounts.FindAsync(id);
        if (account == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Bank account not found" });

        account.IsVerified = true;
        account.VerifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Bank account verified" });
    }
}