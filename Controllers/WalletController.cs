using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
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

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (request.Amount <= 0)
            return Ok(new ApiResponse<object> { Success = false, Message = "Invalid withdrawal amount" });

        if (request.Amount > user.WalletBalance)
            return Ok(new ApiResponse<object> { Success = false, Message = "Insufficient balance" });

        // Find or create bank account
        var bankAccount = await _context.BankAccounts
            .FirstOrDefaultAsync(b => b.UserId == userId && b.IsActive);

        if (bankAccount == null)
        {
            bankAccount = new DoForYou.API.Models.BankAccount
            {
                UserId = userId.Value,
                BankName = request.BankName ?? string.Empty,
                AccountNumber = request.BankAccount ?? string.Empty,
                AccountHolderName = request.AccountHolder ?? string.Empty,
                BranchCode = request.BranchCode ?? string.Empty,
                AccountType = request.AccountType ?? "Cheque",
                IsActive = true
            };
            _context.BankAccounts.Add(bankAccount);
            await _context.SaveChangesAsync();
        }

        var reference = $"WD-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Random.Shared.Next(1000, 9999)}";

        var withdrawal = new DoForYou.API.Models.WithdrawalRequest
        {
            UserId = userId.Value,
            BankAccountId = bankAccount.Id,
            Amount = request.Amount,
            Fee = 0,
            Status = "Pending",
            Reference = reference
        };
        _context.WithdrawalRequests.Add(withdrawal);

        // Debit wallet
        user.WalletBalance -= request.Amount;
        _context.WalletTransactions.Add(new DoForYou.API.Models.WalletTransaction
        {
            UserId = userId.Value,
            Amount = request.Amount,
            TransactionType = "debit",
            Status = "processing",
            Description = "Withdrawal request",
            Reference = reference
        });

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { reference, amount = request.Amount, status = "processing" },
            Message = "Withdrawal request submitted successfully"
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}