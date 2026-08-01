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
    private readonly IConfiguration _configuration;

    public PaymentController(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
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
            var paymentStatus = form["payment_status"].ToString();

            Console.WriteLine($"PayFast Notify: PaymentId={paymentId}, Status={paymentStatus}");

            if (string.IsNullOrEmpty(paymentId))
                return BadRequest();

            // Verify PayFast signature
            if (!VerifyPayFastSignature(form))
            {
                Console.WriteLine("PayFast Notify: Invalid signature");
                return BadRequest();
            }

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == paymentId);
            if (task == null)
            {
                Console.WriteLine($"PayFast Notify: Task not found for ID {paymentId}");
                return NotFound();
            }

            if (paymentStatus == "COMPLETE")
            {
                task.PaymentStatus = "EscrowHeld";
                task.TaskStatus = "Posted";
                task.EscrowStatus = "held";
                task.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                Console.WriteLine($"PayFast Notify: Task {paymentId} activated");
            }

            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PayFast Notify Error: {ex.Message}");
            return StatusCode(500);
        }
    }

    [HttpGet("return")]
    public IActionResult PayFastReturn([FromQuery] string? taskId = null)
    {
        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";
        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payment/success"
            : $"{frontendUrl}/payment/success?taskId={taskId}";
        return Redirect(redirect);
    }

    [HttpGet("cancel")]
    public IActionResult PayFastCancel([FromQuery] string? taskId = null)
    {
        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";
        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payment/cancelled"
            : $"{frontendUrl}/payment/cancelled?taskId={taskId}";
        return Redirect(redirect);
    }

    private bool VerifyPayFastSignature(IFormCollection form)
    {
        try
        {
            var isSandbox = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            // In sandbox mode, skip signature verification
            if (isSandbox) return true;

            var passphrase = Environment.GetEnvironmentVariable("PAYFAST_PASSPHRASE")
                ?? _configuration["PayFast:Passphrase"];

            var fields = form
                .Where(f => f.Key != "signature")
                .OrderBy(f => f.Key)
                .Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value.ToString())}");

            var paramString = string.Join("&", fields);
            if (!string.IsNullOrEmpty(passphrase))
                paramString += $"&passphrase={Uri.EscapeDataString(passphrase)}";

            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = string.Concat(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(paramString))
                .Select(b => b.ToString("x2")));

            return hash == form["signature"].ToString();
        }
        catch
        {
            return false;
        }
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}