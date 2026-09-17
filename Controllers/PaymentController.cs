using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/payment")]
public class PaymentController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IOzowPaymentService _ozowPaymentService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(
        AppDbContext context,
        IOzowPaymentService ozowPaymentService,
        IConfiguration configuration,
        ILogger<PaymentController> logger)
    {
        _context = context;
        _ozowPaymentService = ozowPaymentService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("initiate")]
    [Authorize]
    public IActionResult InitiatePayment()
    {
        return StatusCode(StatusCodes.Status410Gone,
            new ApiResponse<object>
            {
                Success = false,
                Message = "Use the task payment workflow."
            });
    }

    [HttpGet("wallet")]
    public ActionResult<ApiResponse<object>> GetWallet()
    {
        return StatusCode(StatusCodes.Status410Gone,
            new ApiResponse<object>
            {
                Success = false,
                Message = "Runner wallets have been retired. Runner earnings are paid directly to the verified bank account."
            });
    }

    [HttpPost("withdraw")]
    public ActionResult<ApiResponse<object>> WithdrawFunds()
    {
        return StatusCode(StatusCodes.Status410Gone,
            new ApiResponse<object>
            {
                Success = false,
                Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
            });
    }

    [HttpPost("notify")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> OzowNotify()
    {
        try
        {
            var fields = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);

            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();

                foreach (var item in form)
                    fields[item.Key] = item.Value.ToString();
            }
            else
            {
                var body = await Request.ReadFromJsonAsync<Dictionary<string, object?>>();

                if (body != null)
                {
                    foreach (var item in body)
                    {
                        fields[item.Key] =
                            item.Value?.ToString();
                    }
                }
            }

            var siteCode = GetField(fields, "SiteCode");
            var transactionReference = GetField(fields, "TransactionReference");
            var status = GetField(fields, "Status");
            var amountText = GetField(fields, "Amount");

            var configuredSiteCode = _configuration["Ozow:SiteCode"];

            if (string.IsNullOrWhiteSpace(siteCode) ||
                string.IsNullOrWhiteSpace(transactionReference) ||
                string.IsNullOrWhiteSpace(status) ||
                string.IsNullOrWhiteSpace(amountText))
            {
                return BadRequest();
            }

            if (!string.Equals(
                    siteCode,
                    configuredSiteCode,
                    StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Rejected Ozow notification because SiteCode did not match.");
                return BadRequest();
            }

            if (!_ozowPaymentService.VerifyNotificationHash(fields))
            {
                _logger.LogWarning(
                    "Rejected Ozow notification because hash validation failed for {Reference}.",
                    transactionReference);

                return BadRequest();
            }

            if (!decimal.TryParse(
                    amountText,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var notificationAmount))
            {
                return BadRequest();
            }

            var task = await _context.Tasks
                .FirstOrDefaultAsync(
                    t => t.PaymentReference == transactionReference);

            if (task == null)
            {
                _logger.LogWarning(
                    "Ozow notification received for unknown payment reference {Reference}.",
                    transactionReference);

                return NotFound();
            }

            if (notificationAmount != task.Budget)
            {
                _logger.LogWarning(
                    "Ozow amount mismatch for {Reference}. Expected {Expected}, received {Received}.",
                    transactionReference,
                    task.Budget,
                    notificationAmount);

                return BadRequest();
            }

            if (string.Equals(
                    status,
                    "Complete",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Never trust the webhook alone.
                // Verify the transaction directly with Ozow as well.
                var verified =
                    await _ozowPaymentService.GetTransactionByReferenceAsync(
                        transactionReference,
                        HttpContext.RequestAborted);

                if (!verified.Found ||
                    !string.Equals(
                        verified.Status,
                        "Complete",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Ozow notification for {Reference} could not be independently verified.",
                        transactionReference);

                    return BadRequest();
                }

                if (verified.Amount.HasValue &&
                    verified.Amount.Value != task.Budget)
                {
                    _logger.LogWarning(
                        "Ozow verified amount mismatch for {Reference}. Expected {Expected}, received {Received}.",
                        transactionReference,
                        task.Budget,
                        verified.Amount.Value);

                    return BadRequest();
                }

                // Idempotent transition.
                if (task.PaymentStatus != "EscrowHeld" ||
                    task.TaskStatus == "PendingPayment")
                {
                    task.PaymentStatus = "EscrowHeld";
                    task.TaskStatus = "Posted";
                    task.EscrowStatus = "held";
                    task.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                }

                return Ok();
            }

            if (string.Equals(
                    status,
                    "Cancelled",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    status,
                    "Error",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Do not activate the task.
                // It remains PendingPayment and can be retried.
                _logger.LogInformation(
                    "Ozow payment {Reference} ended with status {Status}.",
                    transactionReference,
                    status);

                return Ok();
            }

            // Unknown/non-final statuses are acknowledged but do not activate the task.
            _logger.LogInformation(
                "Ozow payment {Reference} notification status: {Status}.",
                transactionReference,
                status);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Ozow payment notification processing failed.");

            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("return")]
    [AllowAnonymous]
    public IActionResult PaymentReturn([FromQuery] string? taskId = null)
    {
        var frontendUrl =
            Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";

        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payment/success"
            : $"{frontendUrl}/payment/success?taskId={Uri.EscapeDataString(taskId)}";

        return Redirect(redirect);
    }

    [HttpGet("cancel")]
    [AllowAnonymous]
    public IActionResult PaymentCancel([FromQuery] string? taskId = null)
    {
        var frontendUrl =
            Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";

        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payment/cancelled"
            : $"{frontendUrl}/payment/cancelled?taskId={Uri.EscapeDataString(taskId)}";

        return Redirect(redirect);
    }

    private static string GetField(
        IReadOnlyDictionary<string, string?> fields,
        string name)
    {
        return fields.TryGetValue(name, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
    }
}
