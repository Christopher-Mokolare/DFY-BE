using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/wallet")]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly AppDbContext _context;

    public WalletController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("balance")]
    public async Task<ActionResult<ApiResponse<object>>> GetWalletBalance()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        // Calculate pending payouts from completed tasks
        var pendingPayouts = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId && 
                       t.TaskStatus == "Completed" && 
                       t.EscrowStatus == "held")
            .SumAsync(t => t.PayoutAmount);

        // Calculate total earned from wallet transactions
        var totalEarned = await _context.WalletTransactions
            .Where(wt => wt.UserId == userId && 
                        wt.TransactionType == "credit" && 
                        wt.Status == "completed")
            .SumAsync(wt => wt.Amount);

        // Calculate total withdrawn
        var totalWithdrawn = await _context.WalletTransactions
            .Where(wt => wt.UserId == userId && 
                        wt.TransactionType == "debit" && 
                        wt.Status == "completed")
            .SumAsync(wt => wt.Amount);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                available = user.WalletBalance,
                availableBalance = user.WalletBalance,
                pendingPayouts = pendingPayouts,
                totalEarned = totalEarned,
                totalWithdrawn = Math.Abs(totalWithdrawn)
            }
        });
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<ApiResponse<object>>> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var query = _context.WalletTransactions
            .Where(wt => wt.UserId == userId)
            .OrderByDescending(wt => wt.CreatedAt);

        var totalCount = await query.CountAsync();
        
        var transactions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(wt => new
            {
                id = wt.Id,
                amount = wt.Amount,
                transactionType = wt.TransactionType,
                status = wt.Status,
                description = wt.Description,
                reference = wt.Reference,
                createdAt = wt.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                items = transactions,
                totalCount = totalCount,
                page = page,
                pageSize = pageSize
            }
        });
    }

    [HttpPost("withdraw")]
    public async Task<ActionResult<ApiResponse<object>>> RequestWithdrawal([FromBody] WithdrawalRequestDto request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        return Ok(new ApiResponse<object> { Success = false, Message = "Please add a bank account and use the withdrawal flow." });
    }

    [HttpGet("withdrawals/pending")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetPendingWithdrawals()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var pending = await _context.WithdrawalRequests
            .Include(w => w.BankAccount)
            .Where(w => w.UserId == userId && (w.Status == "Pending" || w.Status == "Verified" || w.Status == "Processing"))
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                id = w.Id,
                reference = w.Reference,
                amount = w.Amount,
                fee = w.Fee,
                status = w.Status,
                bankName = w.BankAccount.BankName,
                createdAt = w.CreatedAt
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>> { Success = true, Data = pending });
    }

    [HttpPost("verify-otp")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var withdrawal = await _context.WithdrawalRequests
            .FirstOrDefaultAsync(w => w.Reference == request.Reference && w.UserId == userId);

        if (withdrawal == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid reference" });

        var bankingService = HttpContext.RequestServices.GetRequiredService<IBankingService>();
        var verified = await bankingService.VerifyOtpAsync(withdrawal.Id, request.OtpCode);
        if (!verified)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid or expired OTP" });

        await bankingService.ProcessWithdrawalAsync(withdrawal.Id);
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Withdrawal submitted for processing" });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}