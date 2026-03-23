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

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}