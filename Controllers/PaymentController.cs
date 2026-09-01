using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/payment")]
public class PaymentController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(AppDbContext context, IConfiguration configuration, ILogger<PaymentController> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("initiate")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> InitiatePayment([FromBody] object request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        return StatusCode(StatusCodes.Status410Gone,
            new ApiResponse<object> { Success = false, Message = "Use the task payment workflow." });
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

        return StatusCode(StatusCodes.Status410Gone,
            new ApiResponse<object> { Success = false, Message = "Use the banking withdrawal workflow." });
    }

    [HttpPost("notify")]
    [IgnoreAntiforgeryToken]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> PayFastNotify()
    {
        try
        {
            var form = await Request.ReadFormAsync();
            var paymentId = form["m_payment_id"].ToString();
            var paymentStatus = form["payment_status"].ToString();

            if (string.IsNullOrEmpty(paymentId) || string.IsNullOrWhiteSpace(form["pf_payment_id"]))
                return BadRequest();

            // Verify PayFast signature
            if (!VerifyPayFastSignature(form))
            {
                return BadRequest();
            }

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == paymentId);
            if (task == null)
            {
                return NotFound();
            }

            if (!decimal.TryParse(form["amount_gross"], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                amount != task.Budget ||
                string.IsNullOrWhiteSpace(_configuration["PayFast:MerchantId"]) ||
                !string.Equals(form["merchant_id"], _configuration["PayFast:MerchantId"], StringComparison.Ordinal) ||
                !string.Equals(paymentStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                return BadRequest();

            // ITNs are retried; only the pending state may transition to held.
            if (task.PaymentStatus == "Pending" && task.TaskStatus == "PendingPayment")
            {
                task.PaymentStatus = "EscrowHeld";
                task.TaskStatus = "Posted";
                task.EscrowStatus = "held";
                task.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayFast ITN processing failed");
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
            if (!form.TryGetValue("signature", out var signature) || string.IsNullOrWhiteSpace(signature))
                return false;

            var passphrase = Environment.GetEnvironmentVariable("PAYFAST_PASSPHRASE")
                ?? _configuration["PayFast:Passphrase"];
            if (string.IsNullOrWhiteSpace(passphrase))
                return false;

            var fields = form
                .Where(f => f.Key != "signature")
                .OrderBy(f => f.Key)
                .Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value.ToString())}");

            var paramString = string.Join("&", fields);
            paramString += $"&passphrase={Uri.EscapeDataString(passphrase)}";

            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = string.Concat(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(paramString))
                .Select(b => b.ToString("x2")));

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hash),
                Encoding.UTF8.GetBytes(signature.ToString().ToLowerInvariant()));
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