using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

/// <summary>
/// Provides a short-lived handoff to the configured identity-verification provider.
/// Raw IDs, documents, selfies and provider access tokens are never saved to user
/// profile storage or written to logs.
/// </summary>
[ApiController]
[Route("api/v1/identity-verification")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IdentityVerificationController(
    AppDbContext context,
    IIdentityVerificationService identityVerification,
    ILogger<IdentityVerificationController> logger) : ControllerBase
{
    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);
    private const int MaxAttemptsPerWindow = 3;

    [Authorize]
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<object>>> GetStatus(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new
            {
                u.UserType,
                u.EmailVerified,
                u.IdentityVerificationStatus,
                u.IdentityVerificationProvider,
                u.IdentityVerificationStartedAt,
                u.IdentityVerificationUpdatedAt,
                u.IdentityVerificationAttemptCount,
                u.IdentityVerificationAttemptWindowStartAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null) return NotFound();
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                status = user.IdentityVerificationStatus,
                provider = user.IdentityVerificationProvider,
                startedAt = user.IdentityVerificationStartedAt,
                updatedAt = user.IdentityVerificationUpdatedAt,
                configured = identityVerification.IsConfigured,
                attemptsRemaining = AttemptsRemaining(user.IdentityVerificationAttemptCount, user.IdentityVerificationAttemptWindowStartAt),
                canStart = user.EmailVerified &&
                    IsRunnerPreference(user.UserType) &&
                    identityVerification.IsConfigured &&
                    CanRestart(user.IdentityVerificationStatus)
            }
        });
    }

    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("session")]
    public async Task<ActionResult<ApiResponse<object>>> CreateSession(
        [FromBody] StartIdentityVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (!identityVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_PROVIDER_NOT_CONFIGURED",
                    Message = "Runner identity verification is not available yet. Try again after the verification provider and approved privacy notice are configured."
                });
        }

        if (!request.ConsentAccepted ||
            !string.Equals(request.ConsentVersion, identityVerification.ConsentVersion, StringComparison.Ordinal))
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_CONSENT_REQUIRED",
                Message = "Review the current runner identity-verification notice and accept it before continuing."
            });
        }

        var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user == null || user.IsArchived) return Unauthorized();
        if (!user.EmailVerified)
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<object> { Success = false, Message = "Verify your email before starting runner verification." });
        if (!IsRunnerPreference(user.UserType))
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<object> { Success = false, Message = "Choose Runner or Both in your preferences before starting runner verification." });
        if (string.Equals(user.IdentityVerificationStatus, "passed", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your runner identity check has already passed." });
        if (string.Equals(user.IdentityVerificationStatus, "manual_review", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your verification is waiting for review. Contact support for the next step." });
        if (!CanRestart(user.IdentityVerificationStatus))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your verification is already in progress." });

        var now = DateTime.UtcNow;
        var windowExpired = !user.IdentityVerificationAttemptWindowStartAt.HasValue ||
                            user.IdentityVerificationAttemptWindowStartAt.Value <= now.Subtract(AttemptWindow);
        if (!windowExpired && user.IdentityVerificationAttemptCount >= MaxAttemptsPerWindow)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_RETRY_LIMIT",
                    Message = "You have reached the runner-verification retry limit. Contact support if you need help."
                });
        }

        if (user.IdentityVerificationHandoffTokenHash != null &&
            user.IdentityVerificationHandoffExpiry.HasValue &&
            user.IdentityVerificationHandoffExpiry.Value > now)
        {
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_SESSION_ALREADY_STARTED",
                Message = "A verification session was already prepared. Open the verification page or wait for its link to expire."
            });
        }

        var handoffCode = CreateSecureCode();
        var handoffHash = HashCode(handoffCode);
        var expiry = now.Add(HandoffLifetime);
        var source = "runner-onboarding";
        var consent = new UserConsentEvent
        {
            UserId = user.Id,
            ConsentType = "identity-verification",
            Accepted = true,
            Version = request.ConsentVersion,
            RecordedAt = now,
            Source = source
        };

        if (context.Database.IsRelational())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var lowerWindowBound = now.Subtract(AttemptWindow);
            var affected = await context.Users
                .Where(u => u.Id == user.Id &&
                    !u.IsArchived &&
                    u.EmailVerified &&
                    (u.UserType.ToLower() == "runner" || u.UserType.ToLower() == "both") &&
                    (u.IdentityVerificationStatus == "not_started" ||
                     u.IdentityVerificationStatus == "failed" ||
                     u.IdentityVerificationStatus == "expired") &&
                    (u.IdentityVerificationHandoffTokenHash == null ||
                     u.IdentityVerificationHandoffExpiry == null ||
                     u.IdentityVerificationHandoffExpiry <= now) &&
                    (u.IdentityVerificationAttemptWindowStartAt == null ||
                     u.IdentityVerificationAttemptWindowStartAt <= lowerWindowBound ||
                     u.IdentityVerificationAttemptCount < MaxAttemptsPerWindow))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.IdentityVerificationHandoffTokenHash, handoffHash)
                    .SetProperty(u => u.IdentityVerificationHandoffExpiry, expiry)
                    .SetProperty(u => u.IdentityVerificationUpdatedAt, now)
                    .SetProperty(u => u.IdentityVerificationAttemptWindowStartAt,
                        u => u.IdentityVerificationAttemptWindowStartAt == null ||
                             u.IdentityVerificationAttemptWindowStartAt <= lowerWindowBound
                            ? now
                            : u.IdentityVerificationAttemptWindowStartAt)
                    .SetProperty(u => u.IdentityVerificationAttemptCount,
                        u => u.IdentityVerificationAttemptWindowStartAt == null ||
                             u.IdentityVerificationAttemptWindowStartAt <= lowerWindowBound
                            ? 1
                            : u.IdentityVerificationAttemptCount + 1),
                    cancellationToken);

            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_SESSION_NOT_AVAILABLE",
                    Message = "A verification session could not be started. Refresh your profile and try again if the option remains available."
                });
            }

            context.UserConsentEvents.Add(consent);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            if (!windowExpired && user.IdentityVerificationAttemptCount >= MaxAttemptsPerWindow)
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new ApiResponse<object> { Success = false, Message = "You have reached the runner-verification retry limit." });

            user.IdentityVerificationHandoffTokenHash = handoffHash;
            user.IdentityVerificationHandoffExpiry = expiry;
            user.IdentityVerificationUpdatedAt = now;
            user.IdentityVerificationAttemptWindowStartAt = windowExpired ? now : user.IdentityVerificationAttemptWindowStartAt;
            user.IdentityVerificationAttemptCount = windowExpired ? 1 : user.IdentityVerificationAttemptCount + 1;
            context.UserConsentEvents.Add(consent);
            await context.SaveChangesAsync(cancellationToken);
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                handoffCode,
                expiresAt = expiry,
                consentVersion = request.ConsentVersion,
                instructions = "Continue in the secure verification window. Do not send identity documents to DoForYou support."
            },
            Message = "Verification session prepared."
        });
    }

    /// <summary>
    /// The handoff code is single-use, short-lived and only exchanged for a provider SDK
    /// token. It can be delivered in a URL fragment because fragments are not sent to
    /// the web server; the client must remove it from the address bar before exchange.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("exchange")]
    public async Task<ActionResult<ApiResponse<object>>> ExchangeHandoff(
        [FromBody] ExchangeIdentityVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (!identityVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiResponse<object> { Success = false, Error = "IDENTITY_PROVIDER_NOT_CONFIGURED", Message = "Identity verification is not available yet." });
        }

        var now = DateTime.UtcNow;
        var handoffHash = HashCode(request.HandoffCode);
        var user = await context.Users.AsNoTracking().FirstOrDefaultAsync(u =>
            u.IdentityVerificationHandoffTokenHash == handoffHash &&
            u.IdentityVerificationHandoffExpiry != null &&
            u.IdentityVerificationHandoffExpiry > now &&
            !u.IsArchived &&
            u.EmailVerified,
            cancellationToken);

        if (user == null || !IsRunnerPreference(user.UserType) || !CanRestart(user.IdentityVerificationStatus))
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_HANDOFF_INVALID",
                Message = "This verification link is invalid, expired or already used. Return to your account and start a new session."
            });
        }

        var externalUserId = $"dfy-user-{user.Id}";
        string? accessToken = null;
        string? diditSessionId = null;
        string? diditUrl = null;
        try
        {
            var providerSession = await identityVerification.CreateProviderSessionAsync(externalUserId, user.Email, cancellationToken);
            if (string.Equals(identityVerification.ProviderName, "Didit", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(providerSession.SessionId) ||
                    string.IsNullOrWhiteSpace(providerSession.VerificationUrl))
                    throw new InvalidOperationException("The identity-verification provider returned an invalid hosted session.");
                diditSessionId = providerSession.SessionId;
                diditUrl = providerSession.VerificationUrl;
            }
            else
            {
                accessToken = providerSession.AccessToken;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Identity-provider session creation failed for user {UserId}: {FailureType}", user.Id, ex.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway,
                new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_PROVIDER_UNAVAILABLE",
                    Message = "The identity provider could not start a verification session. Return to your account and retry."
                });
        }

        var startedAt = DateTime.UtcNow;
        var affected = 0;
        if (context.Database.IsRelational())
        {
            affected = await context.Users
                .Where(u => u.Id == user.Id &&
                    u.IdentityVerificationHandoffTokenHash == handoffHash &&
                    u.IdentityVerificationHandoffExpiry != null &&
                    u.IdentityVerificationHandoffExpiry > startedAt &&
                    !u.IsArchived &&
                    u.EmailVerified &&
                    (u.UserType.ToLower() == "runner" || u.UserType.ToLower() == "both") &&
                    (u.IdentityVerificationStatus == "not_started" ||
                     u.IdentityVerificationStatus == "failed" ||
                     u.IdentityVerificationStatus == "expired"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.IdentityVerificationHandoffTokenHash, (string?)null)
                    .SetProperty(u => u.IdentityVerificationHandoffExpiry, (DateTime?)null)
                    .SetProperty(u => u.IdentityVerificationProvider, identityVerification.ProviderName)
                    .SetProperty(u => u.IdentityVerificationExternalUserId, externalUserId)
                    .SetProperty(u => u.IdentityVerificationApplicantId, diditSessionId)
                    .SetProperty(u => u.IdentityVerificationStatus, "pending")
                    .SetProperty(u => u.IdentityVerificationStartedAt, startedAt)
                    .SetProperty(u => u.IdentityVerificationUpdatedAt, startedAt)
                    .SetProperty(u => u.IdentityVerificationLastEventAt, (DateTime?)null),
                    cancellationToken);
        }
        else
        {
            var trackedUser = await context.Users.FirstOrDefaultAsync(u =>
                u.Id == user.Id &&
                u.IdentityVerificationHandoffTokenHash == handoffHash &&
                u.IdentityVerificationHandoffExpiry != null &&
                u.IdentityVerificationHandoffExpiry > startedAt &&
                !u.IsArchived &&
                u.EmailVerified &&
                IsRunnerPreference(u.UserType) &&
                CanRestart(u.IdentityVerificationStatus),
                cancellationToken);
            if (trackedUser != null)
            {
                trackedUser.IdentityVerificationHandoffTokenHash = null;
                trackedUser.IdentityVerificationHandoffExpiry = null;
                trackedUser.IdentityVerificationProvider = identityVerification.ProviderName;
                trackedUser.IdentityVerificationExternalUserId = externalUserId;
                trackedUser.IdentityVerificationApplicantId = diditSessionId;
                trackedUser.IdentityVerificationStatus = "pending";
                trackedUser.IdentityVerificationStartedAt = startedAt;
                trackedUser.IdentityVerificationUpdatedAt = startedAt;
                trackedUser.IdentityVerificationLastEventAt = null;
                affected = await context.SaveChangesAsync(cancellationToken);
                // The update's affected row count is the user row; the tracked provider
                // returns a total SaveChanges count, which includes only this row here.
            }
        }

        if (affected < 1)
        {
            // A concurrent exchange consumed the code. Do not return an unused token.
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_HANDOFF_ALREADY_USED",
                Message = "This verification session has already been opened. Return to your account to check its status."
            });
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = string.Equals(identityVerification.ProviderName, "Didit", StringComparison.Ordinal)
                ? new { verificationUrl = diditUrl, sessionId = diditSessionId, provider = "Didit" } as object
                : new { accessToken, expiresInSeconds = 600, provider = "Sumsub" },
            Message = "Verification session opened."
        });
    }

    /// <summary>
    /// Creates a fresh provider session for the already-authenticated runner's
    /// pending verification. It never accepts a caller-supplied user ID.
    /// </summary>
    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<object>>> RefreshProviderSession(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (!identityVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_PROVIDER_NOT_CONFIGURED",
                    Message = "Identity verification is not available yet."
                });
        }

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.UserType,
                u.EmailVerified,
                u.IsArchived,
                u.IdentityVerificationProvider,
                u.IdentityVerificationStatus,
                u.IdentityVerificationExternalUserId,
                u.IdentityVerificationApplicantId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null || user.IsArchived) return Unauthorized();
        if (!user.EmailVerified ||
            !IsRunnerPreference(user.UserType) ||
            !string.Equals(user.IdentityVerificationProvider, identityVerification.ProviderName, StringComparison.Ordinal) ||
            !string.Equals(user.IdentityVerificationStatus, "pending", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(user.IdentityVerificationExternalUserId, $"dfy-user-{user.Id}", StringComparison.Ordinal))
        {
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_REFRESH_NOT_ALLOWED",
                Message = "A fresh verification session is available only while your runner verification is pending."
            });
        }

        try
        {
            var providerSession = await identityVerification.CreateProviderSessionAsync(
                user.IdentityVerificationExternalUserId!,
                user.Email,
                cancellationToken);

            if (string.Equals(identityVerification.ProviderName, "Didit", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(providerSession.SessionId) ||
                    string.IsNullOrWhiteSpace(providerSession.VerificationUrl))
                    throw new InvalidOperationException("The identity-verification provider returned an invalid hosted session.");

                var trackedUser = await context.Users.FirstOrDefaultAsync(u =>
                    u.Id == user.Id &&
                    u.IdentityVerificationProvider == "Didit" &&
                    u.IdentityVerificationStatus == "pending" &&
                    u.IdentityVerificationExternalUserId == user.IdentityVerificationExternalUserId,
                    cancellationToken);
                if (trackedUser == null) return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_REFRESH_NOT_ALLOWED",
                    Message = "Your verification session changed. Return to your profile and check its status."
                });

                trackedUser.IdentityVerificationApplicantId = providerSession.SessionId;
                trackedUser.IdentityVerificationUpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                return Ok(new ApiResponse<object>
                {
                    Success = true,
                    Data = new { verificationUrl = providerSession.VerificationUrl, sessionId = providerSession.SessionId, provider = "Didit" },
                    Message = "Verification session refreshed."
                });
            }

            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = new { accessToken = providerSession.AccessToken, expiresInSeconds = 600, provider = "Sumsub" },
                Message = "Verification session refreshed."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Identity-provider session refresh failed for user {UserId}: {FailureType}", user.Id, ex.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway,
                new ApiResponse<object>
                {
                    Success = false,
                    Error = "IDENTITY_PROVIDER_UNAVAILABLE",
                    Message = "The verification provider could not refresh this session. Return to your profile and retry."
                });
        }
    }

    /// <summary>
    /// Validates Didit's V2 HMAC webhook and correlates the session to the exact
    /// runner/session created by DFY. Only a fully approved configured workflow
    /// can grant identity eligibility.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("didit/webhook")]
    public async Task<IActionResult> DiditWebhook(CancellationToken cancellationToken)
    {
        if (!identityVerification.IsConfigured ||
            !string.Equals(identityVerification.ProviderName, "Didit", StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        const int maxWebhookBytes = 1024 * 1024;
        if (Request.ContentLength > maxWebhookBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        await using var bodyBuffer = new MemoryStream();
        var readBuffer = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken);
            if (read == 0) break;
            if (bodyBuffer.Length + read > maxWebhookBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            await bodyBuffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
        }
        var rawBody = bodyBuffer.ToArray();
        var signature = Request.Headers["X-Signature-V2"].ToString();
        var previousSignature = Request.Headers["X-Signature-V2-Previous"].ToString();
        var timestampHeader = Request.Headers["X-Timestamp"].ToString();
        if (!identityVerification.VerifyWebhookSignature(rawBody, signature, timestampHeader) &&
            !identityVerification.VerifyWebhookSignature(rawBody, previousSignature, timestampHeader))
        {
            logger.LogWarning("Rejected Didit identity webhook with invalid signature or timestamp.");
            return Unauthorized();
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(rawBody); }
        catch (JsonException) { return BadRequest(); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest();
            var sessionId = ReadString(root, "session_id");
            var vendorData = ReadString(root, "vendor_data");
            var workflowId = ReadString(root, "workflow_id");
            var webhookType = ReadString(root, "webhook_type");
            var status = ReadString(root, "status");
            var eventId = ReadString(root, "event_id");
            var createdAt = ReadString(root, "created_at");
            if (webhookType is not ("status.updated" or "data.updated"))
                return Accepted(new { accepted = true });
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(vendorData) ||
                string.IsNullOrWhiteSpace(workflowId) ||
                string.IsNullOrWhiteSpace(status) ||
                string.IsNullOrWhiteSpace(eventId) ||
                !string.Equals(workflowId, identityVerification.ExpectedLevelName, StringComparison.Ordinal))
                return Accepted(new { accepted = true });

            // Didit refreshes the dispatch timestamp on retries. Use created_at for
            // event ordering and the stable event_id for idempotency.
            if (!long.TryParse(createdAt, NumberStyles.None, CultureInfo.InvariantCulture, out var createdAtSeconds))
                return BadRequest();
            DateTime providerEventAt;
            try { providerEventAt = DateTimeOffset.FromUnixTimeSeconds(createdAtSeconds).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return BadRequest(); }

            var eventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant();
            if (await context.IdentityVerificationEvents.AsNoTracking()
                    .AnyAsync(e => e.Provider == "Didit" && e.EventHash == eventHash, cancellationToken))
                return Ok(new { accepted = true, duplicate = true });

            var user = await context.Users.FirstOrDefaultAsync(u =>
                u.IdentityVerificationApplicantId == sessionId &&
                u.IdentityVerificationProvider == "Didit" &&
                u.IdentityVerificationExternalUserId == vendorData,
                cancellationToken);
            if (user == null)
                return Accepted(new { accepted = true });

            var resultStatus = ResolveDiditStatus(root, status);
            var eventRecord = new IdentityVerificationEvent
            {
                UserId = user.Id,
                Provider = "Didit",
                EventHash = eventHash,
                ProviderApplicantId = Clip(sessionId, 120),
                EventType = Clip(webhookType ?? "status.updated", 60) ?? "status.updated",
                ResultStatus = resultStatus,
                ReceivedAt = DateTime.UtcNow
            };

            if (providerEventAt >= (user.IdentityVerificationStartedAt ?? DateTime.MinValue) &&
                providerEventAt > (user.IdentityVerificationLastEventAt ?? DateTime.MinValue) &&
                resultStatus != "ignored")
            {
                user.IdentityVerificationStatus = resultStatus;
                user.IdentityVerificationUpdatedAt = DateTime.UtcNow;
                user.IdentityVerificationLastEventAt = providerEventAt;
            }

            context.IdentityVerificationEvents.Add(eventRecord);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                context.ChangeTracker.Clear();
                if (await context.IdentityVerificationEvents.AsNoTracking()
                        .AnyAsync(e => e.Provider == "Didit" && e.EventHash == eventHash, cancellationToken))
                    return Ok(new { accepted = true, duplicate = true });
                logger.LogError("Could not persist a signed Didit identity event.");
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok(new { accepted = true });
        }
    }

    private static string ResolveDiditStatus(JsonElement root, string providerStatus)
    {
        if (string.Equals(providerStatus, "Approved", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.Object)
                return "manual_review";

            static bool AllApproved(JsonElement parent, string name)
            {
                if (!parent.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array)
                    return false;
                var values = items.EnumerateArray().ToArray();
                return values.Length > 0 && values.All(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("status", out var state) &&
                    state.ValueKind == JsonValueKind.String &&
                    string.Equals(state.GetString(), "Approved", StringComparison.Ordinal));
            }

            var decisionStatus = ReadString(decision, "status");
            return string.Equals(decisionStatus, "Approved", StringComparison.Ordinal) &&
                   AllApproved(decision, "id_verifications") &&
                   AllApproved(decision, "liveness_checks") &&
                   AllApproved(decision, "face_matches")
                ? "passed"
                : "manual_review";
        }

        return providerStatus switch
        {
            "Declined" => "failed",
            "In Review" => "manual_review",
            "Expired" or "Kyc Expired" or "Abandoned" => "expired",
            "Resubmitted" or "In Progress" or "Not Started" or "Awaiting User" => "pending",
            _ => "ignored"
        };
    }

    [AllowAnonymous]
    [HttpPost("sumsub/webhook")]
    public async Task<IActionResult> SumsubWebhook(CancellationToken cancellationToken)
    {
        if (!identityVerification.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        await using var bodyBuffer = new MemoryStream();
        await Request.Body.CopyToAsync(bodyBuffer, cancellationToken);
        var rawBody = bodyBuffer.ToArray();

        if (!identityVerification.VerifyWebhookSignature(
                rawBody,
                Request.Headers["X-Payload-Digest"].ToString(),
                Request.Headers["X-Payload-Digest-Alg"].ToString()))
        {
            logger.LogWarning("Rejected identity-provider webhook with invalid signature.");
            return Unauthorized();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest();

            if (root.TryGetProperty("testMode", out var testMode) &&
                testMode.ValueKind == JsonValueKind.True)
                return Ok(new { accepted = true, testMode = true });

            var externalUserId = ReadString(root, "externalUserId");
            var eventType = ReadString(root, "type");
            var levelName = ReadString(root, "levelName");
            if (string.IsNullOrWhiteSpace(externalUserId) ||
                string.IsNullOrWhiteSpace(eventType) ||
                string.IsNullOrWhiteSpace(levelName))
                return BadRequest();

            // The configured verification level defines which document and liveness
            // steps can qualify a runner. A signed event from any other level must
            // not grant capabilities.
            if (!string.Equals(levelName, identityVerification.ExpectedLevelName, StringComparison.Ordinal))
            {
                logger.LogWarning("Ignored signed identity-provider event from an unexpected verification level.");
                return Accepted(new { accepted = true });
            }

            var eventHash = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
            if (await context.IdentityVerificationEvents.AsNoTracking()
                    .AnyAsync(e => e.Provider == "Sumsub" && e.EventHash == eventHash, cancellationToken))
                return Ok(new { accepted = true, duplicate = true });

            var user = await context.Users.FirstOrDefaultAsync(u =>
                u.IdentityVerificationExternalUserId == externalUserId &&
                u.IdentityVerificationProvider == "Sumsub",
                cancellationToken);
            if (user == null)
            {
                // Don't disclose whether an external applicant reference exists.
                logger.LogWarning("Ignored identity-provider callback for an unknown external user reference.");
                return Accepted(new { accepted = true });
            }

            var applicantId = ReadString(root, "applicantId");
            var providerEventAt = ParseProviderEventTime(ReadString(root, "createdAtMs")) ?? DateTime.UtcNow;
            var resultStatus = ResolveProviderStatus(root, eventType, user.IdentityVerificationStatus);
            var eventRecord = new IdentityVerificationEvent
            {
                UserId = user.Id,
                Provider = "Sumsub",
                EventHash = eventHash,
                ProviderApplicantId = Clip(applicantId, 120),
                EventType = Clip(eventType, 60) ?? "unknown",
                ResultStatus = resultStatus,
                ReceivedAt = DateTime.UtcNow
            };

            if (providerEventAt >= (user.IdentityVerificationStartedAt ?? DateTime.MinValue) &&
                providerEventAt > (user.IdentityVerificationLastEventAt ?? DateTime.MinValue) &&
                !string.Equals(resultStatus, "ignored", StringComparison.Ordinal))
            {
                // Only final, signed applicantReviewed results can grant the passed state.
                // A delayed non-final event must never downgrade an already-passed result.
                var finalReview = string.Equals(eventType, "applicantReviewed", StringComparison.OrdinalIgnoreCase);
                var newState = resultStatus;

                // A newer pending/on-hold event (for example, an ongoing recheck) must
                // revoke runner eligibility until the provider reports a new final result.
                // Conversely, pending noise cannot clear manual review or a recorded failure.
                if (user.IdentityVerificationStatus is "manual_review" or "failed" &&
                    !finalReview &&
                    newState == "pending")
                {
                    newState = user.IdentityVerificationStatus;
                }

                user.IdentityVerificationStatus = newState;
                user.IdentityVerificationUpdatedAt = DateTime.UtcNow;
                user.IdentityVerificationLastEventAt = providerEventAt;
                if (!string.IsNullOrWhiteSpace(applicantId))
                    user.IdentityVerificationApplicantId = Clip(applicantId, 120);
            }

            context.IdentityVerificationEvents.Add(eventRecord);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent delivery of the same signed payload may win the unique
                // (provider,event hash) race. Treat that exact replay as acknowledged.
                context.ChangeTracker.Clear();
                if (await context.IdentityVerificationEvents.AsNoTracking()
                        .AnyAsync(e => e.Provider == "Sumsub" && e.EventHash == eventHash, cancellationToken))
                    return Ok(new { accepted = true, duplicate = true });

                logger.LogError("Could not persist a signed identity-provider event.");
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok(new { accepted = true });
        }
    }

    private static string ResolveProviderStatus(JsonElement root, string eventType, string currentStatus)
    {
        if (eventType.Equals("applicantOnHold", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("applicantActionOnHold", StringComparison.OrdinalIgnoreCase))
            return "manual_review";

        if (!eventType.Equals("applicantReviewed", StringComparison.OrdinalIgnoreCase))
            return eventType.Equals("applicantPending", StringComparison.OrdinalIgnoreCase) ||
                   eventType.Equals("applicantCreated", StringComparison.OrdinalIgnoreCase) ||
                   eventType.Equals("applicantSubmitted", StringComparison.OrdinalIgnoreCase)
                ? "pending"
                : "ignored";

        var reviewStatus = ReadNestedString(root, "reviewStatus");
        var answer = ReadNestedString(root, "reviewResult", "reviewAnswer");
        var rejectType = ReadNestedString(root, "reviewResult", "reviewRejectType");

        if (!string.Equals(reviewStatus, "completed", StringComparison.OrdinalIgnoreCase))
            return "manual_review";
        if (string.Equals(answer, "GREEN", StringComparison.OrdinalIgnoreCase))
            return "passed";
        if (string.Equals(answer, "RED", StringComparison.OrdinalIgnoreCase))
        {
            // A retryable result may be resubmitted within the per-account limit.
            // A final rejection has no self-service route around it: require review.
            if (string.Equals(rejectType, "RETRY", StringComparison.OrdinalIgnoreCase))
                return "failed";
            if (string.Equals(rejectType, "FINAL", StringComparison.OrdinalIgnoreCase))
                return "manual_review";
        }
        // Unknown or incomplete results are never interpreted as approval.
        return "manual_review";
    }

    private static string? ReadNestedString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(part, out var next))
                return null;
            current = next;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : current.ValueKind == JsonValueKind.Number
                ? current.ToString()
                : null;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static DateTime? ParseProviderEventTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Sumsub's createdAtMs is a UTC timestamp string such as
        // "2026-04-24 13:23:19.002", not an epoch-millisecond number.
        if (DateTime.TryParseExact(
                value,
                ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var offsetParsed))
            return offsetParsed.UtcDateTime;

        return null;
    }

    private static string? Clip(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private static bool IsRunnerPreference(string? userType) =>
        string.Equals(userType?.Trim(), "runner", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(userType?.Trim(), "both", StringComparison.OrdinalIgnoreCase);

    private static bool CanRestart(string? status) =>
        string.Equals(status, "not_started", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    private static int AttemptsRemaining(int count, DateTime? windowStart)
    {
        if (!windowStart.HasValue || windowStart.Value <= DateTime.UtcNow.Subtract(AttemptWindow))
            return MaxAttemptsPerWindow;
        return Math.Max(0, MaxAttemptsPerWindow - count);
    }

    private static string CreateSecureCode() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }
}

public sealed class StartIdentityVerificationRequest
{
    [Required]
    public bool ConsentAccepted { get; set; }

    [Required, StringLength(40)]
    public string ConsentVersion { get; set; } = string.Empty;
}

public sealed class ExchangeIdentityVerificationRequest
{
    [Required, StringLength(200, MinimumLength = 20)]
    public string HandoffCode { get; set; } = string.Empty;
}
