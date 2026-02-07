using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/payment")]
public class PaymentController : ControllerBase
{
    private readonly AppDbContext _context;

    public PaymentController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost("initiate")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> InitiatePayment([FromBody] object request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // For now, return a mock PayFast URL
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                paymentUrl = "https://sandbox.payfast.co.za/eng/process",
                paymentId = Guid.NewGuid().ToString()
            },
            Message = "Payment initiated successfully"
        });
    }

    [HttpGet("wallet")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> GetWallet()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                balance = user.WalletBalance,
                recentTransactions = new object[] { }
            }
        });
    }

    [HttpPost("withdraw")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> WithdrawFunds([FromBody] object request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                transactionId = Guid.NewGuid().ToString()
            },
            Message = "Withdrawal request submitted successfully"
        });
    }

    [HttpPost("notify")]
    public async Task<IActionResult> PayFastNotify()
    {
        try
        {
            var form = await Request.ReadFormAsync();
            var paymentId = form["m_payment_id"].ToString();

            if (string.IsNullOrEmpty(paymentId))
                return BadRequest();

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == paymentId);
            if (task == null)
                return NotFound();

            // Update task status after successful payment
            task.PaymentStatus = "Completed";
            task.TaskStatus = "Posted";
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok();
        }
        catch
        {
            return StatusCode(500);
        }
    }

    [HttpGet("return")]
    public IActionResult PayFastReturn()
    {
        return Redirect("http://localhost:4200/tasks/payment-success");
    }

    [HttpGet("cancel")]
    public IActionResult PayFastCancel()
    {
        return Redirect("http://localhost:4200/tasks/payment-cancel");
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}