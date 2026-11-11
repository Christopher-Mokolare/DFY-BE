using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoForYou.API.Controllers;

/// <summary>
/// Retired compatibility routes for the former Ozow payout integration.
/// Paystack is the only active payment and payout provider.
/// </summary>
[ApiController]
[Route("api/ozow/payout")]
public sealed class OzowPayoutController : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("verify")]
    public IActionResult Verify() =>
        StatusCode(StatusCodes.Status410Gone, new
        {
            success = false,
            error = "LEGACY_PAYMENT_PROVIDER_RETIRED",
            message = "Ozow payout verification is retired. Do not use this endpoint."
        });

    [AllowAnonymous]
    [HttpPost("notification")]
    public IActionResult Notification() =>
        StatusCode(StatusCodes.Status410Gone, new
        {
            success = false,
            error = "LEGACY_PAYMENT_PROVIDER_RETIRED",
            message = "Ozow payout notifications are retired. Paystack is the active payout provider."
        });
}
