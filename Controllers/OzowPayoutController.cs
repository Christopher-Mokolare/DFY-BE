using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/ozow/payout")]
public class OzowPayoutController(
    AppDbContext context,
    OzowPayoutHashService hashService,
    IConfiguration config,
    ILogger<OzowPayoutController> logger)
    : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("verify")]
    public IActionResult Verify(
        [FromBody] OzowPayoutVerifyRequest request)
    {
        var expectedSiteCode =
            config["Ozow:SiteCode"];

        var apiKey =
            config["Ozow:PayoutApiKey"];

        var accessToken =
            config["Ozow:AccessToken"];

        var environment =
            config["ASPNETCORE_ENVIRONMENT"] ??
            "Production";

        if (string.IsNullOrWhiteSpace(
                request.PayoutId) ||
            string.IsNullOrWhiteSpace(
                request.SiteCode) ||
            string.IsNullOrWhiteSpace(
                request.HashCheck))
        {
            return BadRequest();
        }

        if (!string.Equals(
                request.SiteCode,
                expectedSiteCode,
                StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        if (string.Equals(
                environment,
                "Production",
                StringComparison.OrdinalIgnoreCase))
        {
            var suppliedToken =
                Request.Headers["Authorization"]
                    .ToString()
                    .Replace(
                        "Bearer ",
                        "",
                        StringComparison.OrdinalIgnoreCase)
                    .Trim();

            if (string.IsNullOrWhiteSpace(accessToken) ||
                !CryptographicEquals(
                    suppliedToken,
                    accessToken))
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(apiKey) ||
                !hashService.VerifyPayoutHash(
                    request,
                    apiKey))
            {
                return Unauthorized();
            }
        }

        var decryptionKey =
            config["Ozow:AccountNumberDecryptionKey"];

        return Ok(
            new OzowPayoutVerifyResponse
            {
                PayoutId = request.PayoutId,
                IsVerified = true,
                AccountNumberDecryptionKey =
                    decryptionKey
            });
    }

    [AllowAnonymous]
    [HttpPost("notification")]
    public async Task<IActionResult> Notification(
        [FromBody] OzowPayoutNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var expectedSiteCode =
            config["Ozow:SiteCode"];

        var apiKey =
            config["Ozow:PayoutApiKey"];

        if (string.IsNullOrWhiteSpace(
                request.PayoutId) ||
            string.IsNullOrWhiteSpace(
                request.SiteCode) ||
            string.IsNullOrWhiteSpace(
                request.MerchantReference) ||
            string.IsNullOrWhiteSpace(
                request.HashCheck))
        {
            return BadRequest();
        }

        if (!string.Equals(
                request.SiteCode,
                expectedSiteCode,
                StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError(
                "Ozow payout API key is not configured.");

            return StatusCode(500);
        }

        if (!hashService.VerifyNotificationHash(
                request,
                apiKey,
                out var status,
                out var subStatus))
        {
            logger.LogWarning(
                "Invalid Ozow payout notification. Ref={Reference}",
                Sanitize(request.MerchantReference));

            return Unauthorized();
        }

        var payout =
            await context.Payouts
                .Include(p => p.Task)
                .FirstOrDefaultAsync(
                    p =>
                        p.MerchantReference ==
                            request.MerchantReference ||
                        p.ProviderReference ==
                            request.PayoutId,
                    cancellationToken);

        if (payout == null)
        {
            logger.LogWarning(
                "Ozow payout notification received for unknown payout. Ref={Reference}",
                Sanitize(request.MerchantReference));

            return Ok();
        }

        /*
         * Ozow payout lifecycle:
         *
         * 1/2/3/7  = provider is still processing / awaiting a state change.
         * 5        = payout complete.
         * 4        = processing error; subStatus determines recovery.
         * 6        = pending investigation.
         * 90       = payout returned.
         * 99       = payout cancelled.
         *
         * IMPORTANT:
         * Only status 5 is allowed to transition the task to RunnerPaid.
         */

        payout.ProviderReference =
            request.PayoutId;

        payout.UpdatedAt =
            DateTime.UtcNow;

        payout.Task.PayoutReference =
            request.PayoutId;

        payout.Task.UpdatedAt =
            DateTime.UtcNow;

        if (status == 5)
        {
            // PayoutComplete.
            //
            // Idempotent: repeated completion notifications must not
            // create another completion timestamp or another payout.
            if (payout.Status != "Completed")
            {
                var completedAt =
                    DateTime.UtcNow;

                payout.Status =
                    "Completed";

                payout.CompletedAt =
                    completedAt;

                payout.FailureReason =
                    null;

                payout.ProcessingError =
                    null;

                payout.NextAttemptAt =
                    null;

                payout.Task.PayoutStatus =
                    "Completed";

                payout.Task.PayoutCompletedAt =
                    completedAt;

                payout.Task.PaidToRunnerAt =
                    completedAt;

                payout.Task.PaymentStatus =
                    "PayoutCompleted";

                payout.Task.TaskStatus =
                    "RunnerPaid";
            }

            await context.SaveChangesAsync(
                cancellationToken);
        }
        else if (status == 90)
        {
            // PayoutReturned.
            //
            // A payout that was previously completed can later be returned.
            // Do NOT automatically convert historical RunnerPaid into a
            // new payout failure state and do NOT automatically resubmit.
            payout.Status =
                "Returned";

            payout.FailureReason =
                $"Ozow payout returned. SubStatus={subStatus}.";

            payout.ProcessingError =
                null;

            payout.NextAttemptAt =
                null;

            payout.Task.PayoutStatus =
                "Returned";

            // Preserve PaidToRunnerAt because status 90 can happen after
            // a genuine PayoutComplete notification.
            //
            // Do not create a second payout automatically.
            payout.Task.PaymentStatus =
                "PayoutReturned";

            if (payout.Task.TaskStatus != "RunnerPaid")
            {
                payout.Task.TaskStatus =
                    "PayoutReturned";
            }

            await context.SaveChangesAsync(
                cancellationToken);
        }
        else if (status == 4)
        {
            switch (subStatus)
            {
                case 403:
                    // Insufficient Ozow float.
                    // Do NOT retry automatically.
                    payout.Status =
                        "AwaitingFunds";

                    payout.FailureReason =
                        "Ozow payout is awaiting merchant float.";

                    payout.NextAttemptAt =
                        null;

                    payout.Task.PayoutStatus =
                        "AwaitingFunds";

                    payout.Task.TaskStatus =
                        "PayoutPending";

                    break;

                case 405:
                    // Invalid destination account.
                    // Do NOT retry until bank details are corrected.
                    payout.Status =
                        "AwaitingBankDetails";

                    payout.FailureReason =
                        "Ozow rejected the destination bank account.";

                    payout.NextAttemptAt =
                        null;

                    payout.Task.PayoutStatus =
                        "AwaitingBankDetails";

                    payout.Task.TaskStatus =
                        "PayoutPending";

                    break;

                case 401:
                case 402:
                case 404:
                    // Ozow indicates this payout may be resubmitted.
                    //
                    // IMPORTANT:
                    // Do not move directly to Pending. The original Ozow
                    // payout already exists and its final provider state
                    // must be reconciled before another submission is made.
                    //
                    // Also protect against an out-of-order/late webhook
                    // moving an already completed payout backwards.

                    if (payout.Status == "Completed" ||
                        payout.Task.PayoutStatus == "Completed" ||
                        payout.Task.TaskStatus == "RunnerPaid")
                    {
                        logger.LogWarning(
                            "Ignoring late Ozow retryable status {SubStatus} for already completed payout {PayoutId}.",
                            subStatus,
                            payout.Id);

                        payout.UpdatedAt =
                            DateTime.UtcNow;

                        break;
                    }

                    payout.Status =
                        "Processing";

                    payout.ProcessingAt =
                        DateTime.UtcNow;

                    payout.FailureReason =
                        $"Ozow retryable processing error. SubStatus={subStatus}. Reconciliation required before resubmission.";

                    payout.ProcessingError =
                        payout.FailureReason;

                    // Give the processor enough time to enter its
                    // reconciliation window. It will NOT blindly resubmit.
                    payout.NextAttemptAt =
                        DateTime.UtcNow.AddMinutes(10);

                    payout.Task.PayoutStatus =
                        "Processing";

                    payout.Task.TaskStatus =
                        "PayoutPending";

                    payout.Task.UpdatedAt =
                        DateTime.UtcNow;

                    break;

                default:
                    // Unknown processing-error substatus.
                    // Never blindly resubmit an unknown state.
                    payout.Status =
                        "Failed";

                    payout.FailureReason =
                        $"Ozow payout processing error. SubStatus={subStatus}.";

                    payout.NextAttemptAt =
                        null;

                    payout.Task.PayoutStatus =
                        "Failed";

                    payout.Task.TaskStatus =
                        "PayoutFailed";

                    break;
            }

            await context.SaveChangesAsync(
                cancellationToken);
        }
        else if (status == 6)
        {
            // Pending investigation.
            // Keep the payout in a non-paid processing state.
            payout.Status =
                "Processing";

            payout.FailureReason =
                $"Ozow payout pending investigation. SubStatus={subStatus}.";

            payout.NextAttemptAt =
                null;

            payout.Task.PayoutStatus =
                "Processing";

            payout.Task.TaskStatus =
                "PayoutPending";

            await context.SaveChangesAsync(
                cancellationToken);
        }
        else if (status == 99)
        {
            // Cancelled.
            // Cancellation is not a successful payout and should not be
            // automatically resubmitted without an explicit recovery path.
            payout.Status =
                "Cancelled";

            payout.FailureReason =
                $"Ozow payout cancelled. SubStatus={subStatus}.";

            payout.NextAttemptAt =
                null;

            payout.Task.PayoutStatus =
                "Cancelled";

            if (payout.Task.TaskStatus != "RunnerPaid")
            {
                payout.Task.TaskStatus =
                    "PayoutCancelled";
            }

            await context.SaveChangesAsync(
                cancellationToken);
        }
        else
        {
            // Statuses such as 1, 2, 3 and 7 represent non-final provider
            // states. Never mark the runner as paid here.
            payout.Status =
                "Processing";

            payout.ProcessingError =
                null;

            payout.NextAttemptAt =
                null;

            payout.Task.PayoutStatus =
                "Processing";

            payout.Task.TaskStatus =
                "PayoutPending";

            await context.SaveChangesAsync(
                cancellationToken);
        }

        logger.LogInformation(
            "Ozow payout notification processed. Ref={Reference} Status={Status} SubStatus={SubStatus}",
            Sanitize(request.MerchantReference),
            status,
            subStatus);

        return Ok();
    }

    private static bool CryptographicEquals(
        string a,
        string b)
    {
        var left =
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(a));

        var right =
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(b));

        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(left, right);
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace("\r", "")
            .Replace("\n", "");
    }
}
