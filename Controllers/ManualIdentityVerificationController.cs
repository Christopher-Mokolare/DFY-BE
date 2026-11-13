using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.Authorization;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;

namespace DoForYou.API.Controllers;

/// <summary>
/// First-party DFY runner identity-document submission and manual review.
/// This controller does not call an external identity-verification provider.
/// </summary>
[ApiController]
[Route("api/v1/manual-identity-verification")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManualIdentityVerificationController(
    AppDbContext context,
    IIdentityDocumentEncryptionService encryption,
    IConfiguration configuration,
    ILogger<ManualIdentityVerificationController> logger,
    INotificationService? notifications = null,
    IEmailSender? emailSender = null,
    IIdentityDocumentImageService? imageService = null,
    IIdentityDocumentMalwareScanner? malwareScanner = null,
    IIdentityDocumentIdService? documentIdService = null) : ControllerBase
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan AdminStepUpWindow = TimeSpan.FromMinutes(15);
    private const int MaxAttemptsPerWindow = 3;
    private const int MaxDocumentBytes = 5 * 1024 * 1024;
    private const string ManualProvider = "manual_document_review";

    private string ConsentVersion =>
        Environment.GetEnvironmentVariable("IDENTITY_VERIFICATION_CONSENT_VERSION")?.Trim()
        ?? configuration["IdentityVerification:ConsentVersion"]?.Trim()
        ?? string.Empty;

    [Authorize]
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<object>>> GetStatus(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new
            {
                u.UserType, u.EmailVerified, u.IdentityVerificationStatus,
                u.IdentityVerificationProvider, u.IdentityVerificationStartedAt,
                u.IdentityVerificationUpdatedAt, u.IdentityVerificationAttemptCount,
                u.IdentityVerificationAttemptWindowStartAt, u.IdentityVerificationHandoffTokenHash,
                u.IdentityVerificationHandoffExpiry
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (user == null) return NotFound();

        var latest = await context.IdentityVerificationSubmissions.AsNoTracking()
            .Where(s => s.UserId == userId.Value)
            .OrderByDescending(s => s.SubmittedAt)
            .Select(s => new { s.Id, s.DocumentType, s.Status, s.SubmittedAt, s.ReviewReason })
            .FirstOrDefaultAsync(cancellationToken);

        var configured = encryption.IsConfigured && !string.IsNullOrWhiteSpace(ConsentVersion) && malwareScanner?.IsConfigured == true;
        var legacyProviderStateCanBeReplaced =
            (string.Equals(user.IdentityVerificationStatus, "pending", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(user.IdentityVerificationStatus, "manual_review", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(user.IdentityVerificationProvider, ManualProvider, StringComparison.Ordinal);
        var attemptsRemaining = AttemptsRemaining(user.IdentityVerificationAttemptCount, user.IdentityVerificationAttemptWindowStartAt);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                status = user.IdentityVerificationStatus,
                provider = ManualProvider,
                startedAt = user.IdentityVerificationStartedAt,
                updatedAt = user.IdentityVerificationUpdatedAt,
                configured,
                consentVersion = ConsentVersion,
                attemptsRemaining,
                handoffPending = user.IdentityVerificationHandoffTokenHash != null && user.IdentityVerificationHandoffExpiry > DateTime.UtcNow,
                latestSubmission = latest,
                canStart = configured && user.EmailVerified && IsRunner(user.UserType) &&
                    (CanRestart(user.IdentityVerificationStatus) || legacyProviderStateCanBeReplaced) &&
                    attemptsRemaining > 0 &&
                    !(user.IdentityVerificationHandoffTokenHash != null && user.IdentityVerificationHandoffExpiry > DateTime.UtcNow)
            }
        });
    }

    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("handoff")]
    public async Task<ActionResult<ApiResponse<object>>> CreateHandoff(
        [FromBody] StartManualIdentityVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId == null) return Unauthorized();
        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion) || malwareScanner?.IsConfigured != true)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false, Error = "DFY_IDENTITY_VERIFICATION_NOT_CONFIGURED",
                Message = "DFY identity-document submission is not available yet."
            });
        if (!request.ConsentAccepted || !string.Equals(request.ConsentVersion, ConsentVersion, StringComparison.Ordinal))
            return BadRequest(new ApiResponse<object> { Success = false, Error = "IDENTITY_CONSENT_REQUIRED", Message = "Review and accept the current DFY identity-document notice before continuing." });

        var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user == null || user.IsArchived) return Unauthorized();
        if (!user.EmailVerified || !IsRunner(user.UserType))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object> { Success = false, Message = "A verified-email Runner or Both account is required." });
        if (string.Equals(user.IdentityVerificationStatus, "passed", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your runner identity review has already been approved." });
        if (string.Equals(user.IdentityVerificationStatus, "pending", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(user.IdentityVerificationProvider, ManualProvider, StringComparison.Ordinal))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your documents are already awaiting DFY review." });
        if (string.Equals(user.IdentityVerificationStatus, "manual_review", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(user.IdentityVerificationProvider, ManualProvider, StringComparison.Ordinal))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your application is already awaiting DFY review." });

        var now = DateTime.UtcNow;
        if (user.IdentityVerificationHandoffTokenHash != null &&
            user.IdentityVerificationHandoffExpiry.HasValue &&
            user.IdentityVerificationHandoffExpiry.Value > now)
            return Conflict(new ApiResponse<object> { Success = false, Message = "A document-submission link is already active. Open it or wait for it to expire." });

        var windowExpired = !user.IdentityVerificationAttemptWindowStartAt.HasValue ||
            user.IdentityVerificationAttemptWindowStartAt.Value <= now.Subtract(AttemptWindow);
        if (!windowExpired && user.IdentityVerificationAttemptCount >= MaxAttemptsPerWindow)
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiResponse<object>
            {
                Success = false, Error = "IDENTITY_RETRY_LIMIT",
                Message = "You have reached the runner identity-document submission limit. Contact support for assistance."
            });

        var handoffCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        user.IdentityVerificationHandoffTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handoffCode))).ToLowerInvariant();
        user.IdentityVerificationHandoffExpiry = now.AddMinutes(5);
        user.IdentityVerificationAttemptWindowStartAt = windowExpired ? now : user.IdentityVerificationAttemptWindowStartAt;
        user.IdentityVerificationAttemptCount = windowExpired ? 1 : user.IdentityVerificationAttemptCount + 1;
        user.IdentityVerificationUpdatedAt = now;
        context.UserConsentEvents.Add(new UserConsentEvent
        {
            UserId = user.Id, ConsentType = "identity-verification", Accepted = true,
            Version = request.ConsentVersion, RecordedAt = now, Source = "runner-manual-identity-handoff"
        });
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { handoffCode, expiresAt = user.IdentityVerificationHandoffExpiry, consentVersion = request.ConsentVersion },
            Message = "Secure document-submission link prepared. It expires in five minutes and can only be used once."
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("submission/handoff")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<object>>> SubmitHandoff(
        [FromForm] SubmitManualIdentityVerificationHandoffRequest request,
        CancellationToken cancellationToken)
    {
        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion) || malwareScanner?.IsConfigured != true)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object> { Success = false, Message = "DFY document submission is not available yet." });
        var now = DateTime.UtcNow;
        var handoffHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.HandoffCode))).ToLowerInvariant();
        var user = await context.Users.FirstOrDefaultAsync(u =>
            u.IdentityVerificationHandoffTokenHash == handoffHash &&
            u.IdentityVerificationHandoffExpiry != null &&
            u.IdentityVerificationHandoffExpiry > now &&
            !u.IsArchived && u.EmailVerified, cancellationToken);
        if (user == null || !IsRunner(user.UserType) ||
            (user.IdentityVerificationStatus == "passed") ||
            (user.IdentityVerificationStatus == "pending" && user.IdentityVerificationProvider == ManualProvider))
            return BadRequest(new ApiResponse<object> { Success = false, Error = "IDENTITY_HANDOFF_INVALID", Message = "This document-submission link is invalid, expired or already used. Return to your DoForYou profile and start again." });

        var consent = await context.UserConsentEvents.AsNoTracking()
            .Where(e => e.UserId == user.Id && e.ConsentType == "identity-verification" &&
                e.Accepted && e.Version == ConsentVersion && e.RecordedAt >= now.AddMinutes(-6))
            .OrderByDescending(e => e.RecordedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (consent == null)
            return BadRequest(new ApiResponse<object> { Success = false, Error = "IDENTITY_CONSENT_REQUIRED", Message = "The identity-document consent has expired. Return to your profile and start again." });

        var documentType = request.DocumentType?.Trim().ToLowerInvariant();
        if (documentType is not ("south_african_id_card" or "south_african_id_book" or "passport"))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose a South African Smart ID card, green barcoded ID book or passport." });
        if (request.FrontDocument == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Upload the front or photo-information page of your identity document." });
        if (documentType == "south_african_id_card" && request.BackDocument == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Upload both sides of your South African Smart ID card." });
        if (documentType != "south_african_id_card" && request.BackDocument != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A green ID book or passport submission requires only its photo-information page." });

        byte[] frontBytes;
        string frontType;
        byte[]? backBytes = null;
        string? backType = null;
        try
        {
            (frontBytes, frontType) = await ReadImageAsync(request.FrontDocument, cancellationToken);
            if (request.BackDocument != null) (backBytes, backType) = await ReadImageAsync(request.BackDocument, cancellationToken);
            await malwareScanner!.ScanAsync(frontBytes, cancellationToken);
            if (backBytes != null) await malwareScanner.ScanAsync(backBytes, cancellationToken);
            if (imageService != null)
            {
                var normalizedFront = imageService.NormalizeAndStripMetadata(frontBytes, frontType);
                CryptographicOperations.ZeroMemory(frontBytes);
                frontBytes = normalizedFront.Bytes;
                frontType = normalizedFront.ContentType;
                if (backBytes != null && backType != null)
                {
                    var normalizedBack = imageService.NormalizeAndStripMetadata(backBytes, backType);
                    CryptographicOperations.ZeroMemory(backBytes);
                    backBytes = normalizedBack.Bytes;
                    backType = normalizedBack.ContentType;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new ApiResponse<object> { Success = false, Message = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            logger.LogError(ex, "DFY malware scanning or image normalization failed for a document submission.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_DOCUMENT_SCANNER_UNAVAILABLE",
                Message = "Secure document checks are temporarily unavailable. Please try again later."
            });
        }

        var encryptedFront = encryption.Encrypt(frontBytes);
        var encryptedBack = backBytes == null ? null : encryption.Encrypt(backBytes);
        CryptographicOperations.ZeroMemory(frontBytes);
        if (backBytes != null) CryptographicOperations.ZeroMemory(backBytes);

        // Consume the short-lived handoff with a conditional database update so
        // concurrent submissions cannot create two applications from one link.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        if (context.Database.IsRelational())
            transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (context.Database.IsRelational())
            {
                var affected = await context.Users.Where(u => u.Id == user.Id &&
                        u.IdentityVerificationHandoffTokenHash == handoffHash &&
                        u.IdentityVerificationHandoffExpiry != null &&
                        u.IdentityVerificationHandoffExpiry > now &&
                        !u.IsArchived && u.EmailVerified)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.IdentityVerificationHandoffTokenHash, (string?)null)
                        .SetProperty(u => u.IdentityVerificationHandoffExpiry, (DateTime?)null)
                        .SetProperty(u => u.IdentityVerificationStatus, "pending")
                        .SetProperty(u => u.IdentityVerificationProvider, ManualProvider)
                        .SetProperty(u => u.IdentityVerificationExternalUserId, (string?)null)
                        .SetProperty(u => u.IdentityVerificationApplicantId, (string?)null)
                        .SetProperty(u => u.IdentityVerificationStartedAt, now)
                        .SetProperty(u => u.IdentityVerificationUpdatedAt, now)
                        .SetProperty(u => u.IdentityVerificationLastEventAt, (DateTime?)null), cancellationToken);
                if (affected != 1)
                    return BadRequest(new ApiResponse<object> { Success = false, Error = "IDENTITY_HANDOFF_ALREADY_USED", Message = "This document-submission link has already been used." });
            }
            else
            {
                if (user.IdentityVerificationHandoffTokenHash != handoffHash ||
                    !user.IdentityVerificationHandoffExpiry.HasValue ||
                    user.IdentityVerificationHandoffExpiry.Value <= now)
                    return BadRequest(new ApiResponse<object> { Success = false, Error = "IDENTITY_HANDOFF_ALREADY_USED", Message = "This document-submission link has already been used." });
                user.IdentityVerificationHandoffTokenHash = null;
                user.IdentityVerificationHandoffExpiry = null;
                user.IdentityVerificationStatus = "pending";
                user.IdentityVerificationProvider = ManualProvider;
                user.IdentityVerificationExternalUserId = null;
                user.IdentityVerificationApplicantId = null;
                user.IdentityVerificationStartedAt = now;
                user.IdentityVerificationUpdatedAt = now;
                user.IdentityVerificationLastEventAt = null;
            }

            var submission = new IdentityVerificationSubmission
        {
            UserId = user.Id, DocumentType = documentType, Status = "pending",
            FrontCiphertext = encryptedFront.Ciphertext, FrontNonce = encryptedFront.Nonce, FrontTag = encryptedFront.Tag,
            FrontContentType = frontType, BackCiphertext = encryptedBack?.Ciphertext,
            BackNonce = encryptedBack?.Nonce, BackTag = encryptedBack?.Tag, BackContentType = backType,
            ConsentVersion = ConsentVersion, SubmittedAt = now,
                DocumentsDeleteAfter = now.AddDays(90)
        };
            context.IdentityVerificationSubmissions.Add(submission);
            context.IdentityVerificationEvents.Add(CreateEvent(user.Id, "submission_received", "pending"));
            await context.SaveChangesAsync(cancellationToken);

            const string runnerSubmissionTitle = "DFY Document Review received";
            const string runnerSubmissionMessage = "Your documents were securely submitted. DFY aims to review submissions within one business day; we will notify you when there is an update.";
            EnqueueIdentityNotification(context, $"identity-submission:{submission.Id}:runner:in-app",
                "in_app", user.Id, user.Email, "identity_review_submitted", runnerSubmissionTitle, runnerSubmissionMessage);
            EnqueueIdentityNotification(context, $"identity-submission:{submission.Id}:runner:email",
                "email", user.Id, user.Email, "identity_review_submitted", runnerSubmissionTitle, runnerSubmissionMessage);

            var adminRecipients = await context.Users.AsNoTracking()
                .Where(u => !u.IsArchived && u.Roles != null && u.Roles.Contains("Admin"))
                .Select(u => new { u.Id, u.Email })
                .ToListAsync(cancellationToken);
            foreach (var admin in adminRecipients)
            {
                var adminMessage = $"Runner {user.Id} submitted document review case {submission.Id}.";
                EnqueueIdentityNotification(context, $"identity-submission:{submission.Id}:admin:{admin.Id}:in-app",
                    "in_app", admin.Id, admin.Email, "identity_review_new", "New DFY Document Review", adminMessage);
                EnqueueIdentityNotification(context, $"identity-submission:{submission.Id}:admin:{admin.Id}:email",
                    "email", admin.Id, admin.Email, "identity_review_new", "New DFY Document Review",
                    $"A runner submitted document review case {submission.Id}. Sign in to the DFY administrator dashboard to review it.");
            }

            await context.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("DFY identity documents submitted using a one-time handoff. Submission {SubmissionId}, user {UserId}.", submission.Id, user.Id);

            return Accepted(new ApiResponse<object>
            {
                Success = true,
                Data = new { submissionId = submission.Id, status = submission.Status, submittedAt = submission.SubmittedAt },
                Message = "Your identity documents were securely submitted. Runner eligibility remains locked until DFY completes its review."
            });
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction != null) await transaction.DisposeAsync();
        }
    }

    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("submission")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<object>>> Submit(
        [FromForm] SubmitManualIdentityVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId == null) return Unauthorized();

        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion) ||
            malwareScanner?.IsConfigured != true || imageService == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false,
                Error = "DFY_IDENTITY_VERIFICATION_NOT_CONFIGURED",
                Message = "DFY secure document submission, malware scanning and image sanitisation are not fully configured."
            });

        if (!request.ConsentAccepted ||
            !string.Equals(request.ConsentVersion, ConsentVersion, StringComparison.Ordinal))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "IDENTITY_CONSENT_REQUIRED",
                Message = "Review and accept the current DFY identity-document notice before submitting."
            });

        var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user == null || user.IsArchived) return Unauthorized();
        if (!user.EmailVerified)
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
            {
                Success = false, Message = "Verify your email before submitting runner identity documents."
            });
        if (!IsRunner(user.UserType))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
            {
                Success = false, Message = "Choose Runner or Both in your preferences before submitting identity documents."
            });
        if (string.Equals(user.IdentityVerificationStatus, "passed", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your runner identity review has already been approved." });
        if (string.Equals(user.IdentityVerificationStatus, "pending", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(user.IdentityVerificationProvider, ManualProvider, StringComparison.Ordinal))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your documents are already awaiting DFY review." });
        if (string.Equals(user.IdentityVerificationStatus, "manual_review", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(user.IdentityVerificationProvider, ManualProvider, StringComparison.Ordinal))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your application is already awaiting DFY review." });

        var now = DateTime.UtcNow;
        var windowExpired = !user.IdentityVerificationAttemptWindowStartAt.HasValue ||
            user.IdentityVerificationAttemptWindowStartAt.Value <= now.Subtract(AttemptWindow);
        if (!windowExpired && user.IdentityVerificationAttemptCount >= MaxAttemptsPerWindow)
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiResponse<object>
            {
                Success = false, Error = "IDENTITY_RETRY_LIMIT",
                Message = "You have reached the runner identity-document submission limit. Contact support for assistance."
            });

        var documentType = request.DocumentType?.Trim().ToLowerInvariant();
        if (documentType is not ("south_african_id_card" or "south_african_id_book" or "passport"))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose a South African Smart ID card, green barcoded ID book or passport." });
        if (request.FrontDocument == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Upload the front or photo-information page of your identity document." });
        if (documentType == "south_african_id_card" && request.BackDocument == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Upload both sides of your South African Smart ID card." });
        if (documentType != "south_african_id_card" && request.BackDocument != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A green ID book or passport submission requires only its photo-information page." });

        byte[] frontBytes;
        string frontType;
        byte[]? backBytes = null;
        string? backType = null;
        try
        {
            (frontBytes, frontType) = await ReadImageAsync(request.FrontDocument, cancellationToken);
            if (request.BackDocument != null)
                (backBytes, backType) = await ReadImageAsync(request.BackDocument, cancellationToken);
            await malwareScanner!.ScanAsync(frontBytes, cancellationToken);
            if (backBytes != null) await malwareScanner.ScanAsync(backBytes, cancellationToken);
            if (imageService != null)
            {
                var normalizedFront = imageService.NormalizeAndStripMetadata(frontBytes, frontType);
                CryptographicOperations.ZeroMemory(frontBytes);
                frontBytes = normalizedFront.Bytes;
                frontType = normalizedFront.ContentType;
                if (backBytes != null && backType != null)
                {
                    var normalizedBack = imageService.NormalizeAndStripMetadata(backBytes, backType);
                    CryptographicOperations.ZeroMemory(backBytes);
                    backBytes = normalizedBack.Bytes;
                    backType = normalizedBack.ContentType;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new ApiResponse<object> { Success = false, Message = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            logger.LogError(ex, "DFY malware scanning or image normalization failed for a document submission.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false, Error = "IDENTITY_DOCUMENT_SCANNER_UNAVAILABLE",
                Message = "Secure document checks are temporarily unavailable. Please try again later."
            });
        }

        var encryptedFront = encryption.Encrypt(frontBytes);
        var encryptedBack = backBytes == null ? null : encryption.Encrypt(backBytes);
        CryptographicOperations.ZeroMemory(frontBytes);
        if (backBytes != null) CryptographicOperations.ZeroMemory(backBytes);

        var submission = new IdentityVerificationSubmission
        {
            UserId = user.Id,
            DocumentType = documentType,
            Status = "pending",
            FrontCiphertext = encryptedFront.Ciphertext,
            FrontNonce = encryptedFront.Nonce,
            FrontTag = encryptedFront.Tag,
            FrontContentType = frontType,
            BackCiphertext = encryptedBack?.Ciphertext,
            BackNonce = encryptedBack?.Nonce,
            BackTag = encryptedBack?.Tag,
            BackContentType = backType,
            ConsentVersion = request.ConsentVersion,
            SubmittedAt = now,
            DocumentsDeleteAfter = now.AddDays(90)
        };

        user.IdentityVerificationStatus = "pending";
        user.IdentityVerificationProvider = ManualProvider;
        user.IdentityVerificationExternalUserId = null;
        user.IdentityVerificationApplicantId = null;
        user.IdentityVerificationStartedAt = now;
        user.IdentityVerificationUpdatedAt = now;
        user.IdentityVerificationLastEventAt = null;
        user.IdentityVerificationAttemptWindowStartAt = windowExpired ? now : user.IdentityVerificationAttemptWindowStartAt;
        user.IdentityVerificationAttemptCount = windowExpired ? 1 : user.IdentityVerificationAttemptCount + 1;

        context.IdentityVerificationSubmissions.Add(submission);
        context.UserConsentEvents.Add(new UserConsentEvent
        {
            UserId = user.Id,
            ConsentType = "identity-verification",
            Accepted = true,
            Version = request.ConsentVersion,
            RecordedAt = now,
            Source = "runner-manual-identity-submission"
        });
        context.IdentityVerificationEvents.Add(CreateEvent(user.Id, "submission_received", "pending"));

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("DFY identity documents submitted for review. Submission {SubmissionId}, user {UserId}.", submission.Id, user.Id);

        return Accepted(new ApiResponse<object>
        {
            Success = true,
            Data = new { submissionId = submission.Id, status = submission.Status, submittedAt = submission.SubmittedAt },
            Message = "Your identity documents were securely submitted. Runner eligibility remains locked until DFY completes its review."
        });
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [Authorize(Policy = AdminPermissionPolicies.KycReviewPolicy)]
    [HttpGet("admin/pending")]
    public async Task<ActionResult<ApiResponse<object>>> GetPending(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string filter = "all",
        CancellationToken cancellationToken = default)
    {
        var supportedFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "all", "near-deadline", "overdue", "claimed-by-me", "unclaimed", "resubmission"
        };
        if (!supportedFilters.Contains(filter))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose a supported identity-review queue filter." });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var now = DateTime.UtcNow;
        var holidays = configuration["IdentityVerification:SouthAfricanPublicHolidays"];
        var pendingRows = await context.IdentityVerificationSubmissions.AsNoTracking()
            .Where(s => s.Status == "pending")
            .OrderBy(s => s.SubmittedAt)
            .Select(s => new
            {
                s.Id, s.UserId, s.DocumentType, s.Status, s.SubmittedAt, s.DuplicateIdDetected,
                s.ReviewClaimedByUserId, s.ReviewClaimExpiresAt,
                userName = s.User.FirstName + " " + s.User.LastName,
                s.User.Email
            })
            .ToListAsync(cancellationToken);

        var pendingUserIds = pendingRows.Select(s => s.UserId).Distinct().ToArray();
        var priorInformationRequests = new Dictionary<int, List<DateTime>>();
        if (pendingUserIds.Length > 0)
        {
            var priorRequests = await context.IdentityVerificationSubmissions.AsNoTracking()
                .Where(s => pendingUserIds.Contains(s.UserId) && s.Status == "more_info_requested")
                .Select(s => new { s.UserId, s.SubmittedAt })
                .ToListAsync(cancellationToken);
            priorInformationRequests = priorRequests
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.SubmittedAt).ToList());
        }
        var claimOwnerIds = pendingRows
            .Where(s => s.ReviewClaimExpiresAt > now && s.ReviewClaimedByUserId.HasValue)
            .Select(s => s.ReviewClaimedByUserId!.Value)
            .Distinct()
            .ToArray();
        var claimOwners = claimOwnerIds.Length == 0
            ? new Dictionary<int, string>()
            : await context.Users.AsNoTracking()
                .Where(u => claimOwnerIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.FirstName + " " + u.LastName })
                .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);

        var submissions = pendingRows.Select(s =>
        {
            var reviewDueAt = SouthAfricanBusinessCalendar.AddBusinessHours(s.SubmittedAt, 8, holidays);
            var nearDeadlineAt = SouthAfricanBusinessCalendar.AddBusinessHours(s.SubmittedAt, 6, holidays);
            var isOverdue = now >= reviewDueAt;
            var isClaimed = s.ReviewClaimedByUserId.HasValue && s.ReviewClaimExpiresAt > now;
            return new
            {
                s.Id,
                s.UserId,
                s.DocumentType,
                s.Status,
                s.SubmittedAt,
                s.DuplicateIdDetected,
                s.userName,
                s.Email,
                reviewDueAt,
                reviewAgeMinutes = Math.Max(0, (int)(now - s.SubmittedAt).TotalMinutes),
                isOverdue,
                isNearDeadline = now >= nearDeadlineAt && !isOverdue,
                isResubmission = priorInformationRequests.TryGetValue(s.UserId, out var previousRequests) &&
                    previousRequests.Any(previousSubmittedAt => previousSubmittedAt < s.SubmittedAt),
                isClaimed,
                isClaimedByMe = isClaimed && s.ReviewClaimedByUserId == CurrentUserId(),
                claimedByName = isClaimed && s.ReviewClaimedByUserId.HasValue &&
                    claimOwners.TryGetValue(s.ReviewClaimedByUserId.Value, out var ownerName) ? ownerName : null,
                claimExpiresAt = isClaimed ? s.ReviewClaimExpiresAt : null
            };
        }).ToList();

        var queueCounts = new
        {
            total = submissions.Count,
            nearDeadline = submissions.Count(s => s.isNearDeadline),
            overdue = submissions.Count(s => s.isOverdue),
            claimedByMe = submissions.Count(s => s.isClaimedByMe),
            unclaimed = submissions.Count(s => !s.isClaimed),
            resubmission = submissions.Count(s => s.isResubmission)
        };
        var filtered = filter.ToLowerInvariant() switch
        {
            "near-deadline" => submissions.Where(s => s.isNearDeadline).ToList(),
            "overdue" => submissions.Where(s => s.isOverdue).ToList(),
            "claimed-by-me" => submissions.Where(s => s.isClaimedByMe).ToList(),
            "unclaimed" => submissions.Where(s => !s.isClaimed).ToList(),
            "resubmission" => submissions.Where(s => s.isResubmission).ToList(),
            _ => submissions
        };
        var pageRows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { submissions = pageRows, totalCount = filtered.Count, page, pageSize, filter, queueCounts }
        });
    }


    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
        [Authorize(Policy = AdminPermissionPolicies.KycReviewPolicy)]
    [HttpPost("admin/{id:int}/claim")]
    public async Task<ActionResult<ApiResponse<object>>> ClaimReview(int id, CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object> { Success = false, Message = "Complete administrator authenticator verification before claiming a review." });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);
        int affected;
        if (context.Database.IsRelational())
        {
            affected = await context.IdentityVerificationSubmissions
                .Where(s => s.Id == id && s.Status == "pending" &&
                    (s.ReviewClaimExpiresAt == null || s.ReviewClaimExpiresAt <= now || s.ReviewClaimedByUserId == adminId.Value))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.ReviewClaimedByUserId, (int?)adminId.Value)
                    .SetProperty(s => s.ReviewClaimHeartbeatAt, (DateTime?)now)
                    .SetProperty(s => s.ReviewClaimExpiresAt, (DateTime?)expires), cancellationToken);
        }
        else
        {
            var submission = await context.IdentityVerificationSubmissions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (submission == null) return NotFound();
            if (submission.Status != "pending" ||
                (submission.ReviewClaimExpiresAt > now && submission.ReviewClaimedByUserId != adminId.Value))
                return Conflict(new ApiResponse<object> { Success = false, Message = "Another reviewer currently owns this case." });
            submission.ReviewClaimedByUserId = adminId.Value;
            submission.ReviewClaimHeartbeatAt = now;
            submission.ReviewClaimExpiresAt = expires;
            affected = await context.SaveChangesAsync(cancellationToken);
        }
        if (affected == 0)
            return Conflict(new ApiResponse<object> { Success = false, Message = "This case is no longer pending or is currently claimed by another reviewer." });

        var runnerUserId = await context.IdentityVerificationSubmissions.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (runnerUserId == 0) return NotFound();
        context.IdentityVerificationEvents.Add(CreateEvent(runnerUserId, "review_claimed", "claimed", adminId.Value, id));
        context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "AdminIdentityReviewClaimed",
            EntityType = "IdentityVerificationSubmission",
            EntityId = id,
            OldValues = "unclaimed_or_expired",
            NewValues = $"claimed;expiresAt={expires:O}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return Ok(new ApiResponse<object> { Success = true, Data = new { claimedByUserId = adminId.Value, expiresAt = expires }, Message = "Review case claimed for ten minutes." });
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
        [Authorize(Policy = AdminPermissionPolicies.KycReviewPolicy)]
    [HttpPost("admin/{id:int}/heartbeat")]
    public async Task<ActionResult<ApiResponse<object>>> HeartbeatReview(int id, CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object> { Success = false, Message = "Complete administrator authenticator verification to keep this review claim active." });
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);
        int affected;
        if (context.Database.IsRelational())
        {
            affected = await context.IdentityVerificationSubmissions
                .Where(s => s.Id == id && s.Status == "pending" &&
                    s.ReviewClaimedByUserId == adminId.Value && s.ReviewClaimExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.ReviewClaimHeartbeatAt, (DateTime?)now)
                    .SetProperty(s => s.ReviewClaimExpiresAt, (DateTime?)expires), cancellationToken);
        }
        else
        {
            var submission = await context.IdentityVerificationSubmissions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (submission == null || submission.Status != "pending" || submission.ReviewClaimedByUserId != adminId.Value || submission.ReviewClaimExpiresAt <= now)
                affected = 0;
            else
            {
                submission.ReviewClaimHeartbeatAt = now;
                submission.ReviewClaimExpiresAt = expires;
                affected = await context.SaveChangesAsync(cancellationToken);
            }
        }
        if (affected != 1)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Your review claim has expired or belongs to another reviewer. Claim the case again." });

        context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "AdminIdentityReviewClaimHeartbeat",
            EntityType = "IdentityVerificationSubmission",
            EntityId = id,
            OldValues = "claimed",
            NewValues = $"claim_extended;expiresAt={expires:O}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return Ok(new ApiResponse<object> { Success = true, Data = new { expiresAt = expires } });
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
        [Authorize(Policy = AdminPermissionPolicies.KycReviewPolicy)]
    [HttpDelete("admin/{id:int}/claim")]
    public async Task<ActionResult<ApiResponse<object>>> ReleaseReview(int id, CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
            {
                Success = false,
                Message = "Complete a fresh administrator authenticator verification before releasing a review claim."
            });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTime.UtcNow;
        int affected;
        if (context.Database.IsRelational())
        {
            affected = await context.IdentityVerificationSubmissions
                .Where(s => s.Id == id && s.Status == "pending" && s.ReviewClaimedByUserId == adminId.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.ReviewClaimedByUserId, (int?)null)
                    .SetProperty(s => s.ReviewClaimHeartbeatAt, (DateTime?)null)
                    .SetProperty(s => s.ReviewClaimExpiresAt, (DateTime?)null), cancellationToken);
        }
        else
        {
            var submission = await context.IdentityVerificationSubmissions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (submission != null && submission.Status == "pending" && submission.ReviewClaimedByUserId == adminId.Value)
            {
                submission.ReviewClaimedByUserId = null;
                submission.ReviewClaimHeartbeatAt = null;
                submission.ReviewClaimExpiresAt = null;
                affected = await context.SaveChangesAsync(cancellationToken);
            }
            else affected = 0;
        }

        if (affected > 0)
        {
            var runnerUserId = await context.IdentityVerificationSubmissions.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => s.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (runnerUserId != 0)
            {
                context.IdentityVerificationEvents.Add(CreateEvent(
                    runnerUserId, "review_claim_released", "released", adminId.Value, id));
                context.AuditLogs.Add(new AuditLog
                {
                    UserId = adminId.Value,
                    Action = "AdminIdentityReviewClaimReleased",
                    EntityType = "IdentityVerificationSubmission",
                    EntityId = id,
                    OldValues = "claimed",
                    NewValues = "released",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return Ok(new ApiResponse<object> { Success = true, Data = new { released = affected > 0, releasedAt = now } });
    }

    // Document viewing is a separate permission from ordinary KYC review and requires fresh MFA and a reason.
    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [Authorize(Policy = AdminPermissionPolicies.KycViewDocumentsPolicy)]
    [HttpGet("admin/{id:int}/document/{side}")]
    public async Task<IActionResult> GetDocument(
        int id,
        string side,
        [FromQuery] string reason,
        CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Complete a fresh SuperAdmin authenticator verification before viewing identity documents." });
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length < 10 || reason.Length > 500)
            return BadRequest(new { message = "A specific access reason between 10 and 500 characters is required for every identity-document view." });

        var submission = await context.IdentityVerificationSubmissions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (submission == null) return NotFound();
        if (submission.Status != "pending" || submission.ReviewClaimedByUserId != adminId.Value ||
            !submission.ReviewClaimExpiresAt.HasValue || submission.ReviewClaimExpiresAt.Value <= DateTime.UtcNow)
            return Conflict(new { message = "Claim this pending review before viewing its documents." });

        byte[]? cipher;
        byte[]? nonce;
        byte[]? tag;
        string? contentType;
        if (side.Equals("front", StringComparison.OrdinalIgnoreCase))
        {
            cipher = submission.FrontCiphertext; nonce = submission.FrontNonce; tag = submission.FrontTag;
            contentType = submission.FrontContentType;
        }
        else if (side.Equals("back", StringComparison.OrdinalIgnoreCase))
        {
            cipher = submission.BackCiphertext; nonce = submission.BackNonce; tag = submission.BackTag;
            contentType = submission.BackContentType;
        }
        else return BadRequest();

        if (cipher == null || cipher.Length == 0 || nonce == null || nonce.Length != 12 || tag == null || tag.Length != 16 || contentType == null) return NotFound();
        byte[] plaintext;
        try { plaintext = encryption.Decrypt(cipher, nonce, tag); }
        catch (CryptographicException)
        {
            logger.LogError("Unable to decrypt DFY identity document for submission {SubmissionId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        byte[] watermarked;
        try
        {
            watermarked = imageService?.ApplyWatermark(
                plaintext, contentType,
                $"DFY CONFIDENTIAL · REVIEW ONLY · CASE {submission.Id} · ADMIN {adminId.Value} · {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                ?? plaintext;
        }
        catch (InvalidDataException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            logger.LogError(ex, "Unable to watermark DFY identity document for submission {SubmissionId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
        if (!ReferenceEquals(watermarked, plaintext)) CryptographicOperations.ZeroMemory(plaintext);

        context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_document_accessed", "viewed", adminId.Value, submission.Id, side.ToLowerInvariant()));
        context.AuditLogs.Add(new AuditLog
        {
            UserId = adminId.Value,
            Action = "AdminIdentityDocumentViewed",
            EntityType = "IdentityVerificationSubmission",
            EntityId = submission.Id,
            OldValues = null,
            NewValues = $"side={side.ToLowerInvariant()}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(watermarked, contentType);
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
        [Authorize(Policy = AdminPermissionPolicies.KycReviewPolicy)]
    [HttpPost("admin/{id:int}/decision")]
    public async Task<ActionResult<ApiResponse<object>>> Decide(
        int id,
        [FromBody] ManualIdentityVerificationDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
            {
                Success = false, Message = "Complete administrator authenticator verification before reviewing identity applications."
            });

        var decision = request.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject" or "request_information"))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose approve, reject or request_information." });
        var reasonCode = request.ReasonCode?.Trim().ToLowerInvariant() ?? string.Empty;
        var runnerNote = request.RunnerNote?.Trim() ?? string.Empty;
        var internalNote = request.InternalNote?.Trim();
        if (internalNote?.Length > 1000)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Internal administrator notes cannot exceed 1000 characters." });
        if (decision == "approve")
        {
            if (!request.VerifiedDateOfBirth.HasValue)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Record the date of birth shown on the document before approving runner activation." });
            var dob = request.VerifiedDateOfBirth.Value.Date;
            var today = DateTime.UtcNow.Date;
            var age = today.Year - dob.Year;
            if (dob > today.AddYears(-age)) age--;
            if (dob > today || age < 18 || age > 110)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Runner activation requires a document-verified date of birth showing an age of at least 18." });
        }


        string? approvedIdHmac = null;
        IdentityVerificationSubmission? preflightSubmission = null;
        if (decision == "approve")
        {
            preflightSubmission = await context.IdentityVerificationSubmissions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (preflightSubmission == null) return NotFound();
            if (preflightSubmission.Status != "pending" ||
                preflightSubmission.ReviewClaimedByUserId != adminId.Value ||
                !preflightSubmission.ReviewClaimExpiresAt.HasValue ||
                preflightSubmission.ReviewClaimExpiresAt.Value <= DateTime.UtcNow)
                return Conflict(new ApiResponse<object> { Success = false, Message = "Claim this pending case before approving it." });

            if (preflightSubmission.DocumentType is "south_african_id_card" or "south_african_id_book")
            {
                if (documentIdService?.IsConfigured != true)
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
                    {
                        Success = false, Error = "IDENTITY_DUPLICATE_CHECK_NOT_CONFIGURED",
                        Message = "South African ID duplicate checks are not configured. Approval is paused until the secure fingerprint key is configured."
                    });

                var idNumber = request.SouthAfricanIdNumber?.Trim() ?? string.Empty;
                var policy = new UserPolicyService();
                if (!policy.IsValidSouthAfricanId(idNumber) ||
                    !policy.TryGetAdultDateOfBirthFromSouthAfricanId(idNumber, out var idDateOfBirth))
                    return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter the valid 13-digit South African ID number shown on the document. The checksum and adult date of birth must be valid." });
                if (idDateOfBirth.Date != request.VerifiedDateOfBirth!.Value.Date)
                    return BadRequest(new ApiResponse<object> { Success = false, Message = "The date of birth entered does not match the date encoded in the South African ID number." });

                approvedIdHmac = documentIdService.ComputeHmac(idNumber);
                var duplicateSubmission = await context.IdentityVerificationSubmissions.AsNoTracking()
                    .AnyAsync(s => s.UserId != preflightSubmission.UserId && s.DocumentIdHmac == approvedIdHmac, cancellationToken);
                var duplicateApprovedUser = await context.Users.AsNoTracking()
                    .AnyAsync(u => u.Id != preflightSubmission.UserId && u.IdentityDocumentIdHmac == approvedIdHmac, cancellationToken);
                var legacyIdNumbers = await context.Users.AsNoTracking()
                    .Where(u => u.Id != preflightSubmission.UserId && u.IdNumber != null)
                    .Select(u => u.IdNumber!)
                    .ToListAsync(cancellationToken);
                var duplicateLegacyUser = legacyIdNumbers.Any(existing =>
                    policy.IsValidSouthAfricanId(existing) &&
                    string.Equals(documentIdService.ComputeHmac(existing), approvedIdHmac, StringComparison.Ordinal));

                if (duplicateSubmission || duplicateApprovedUser || duplicateLegacyUser)
                {
                    preflightSubmission.DocumentIdHmac = approvedIdHmac;
                    preflightSubmission.DuplicateIdDetected = true;
                    context.IdentityVerificationEvents.Add(CreateEvent(
                        preflightSubmission.UserId, "duplicate_id_flagged", "investigation_required", adminId.Value, id));
                    await context.SaveChangesAsync(cancellationToken);
                    return Conflict(new ApiResponse<object>
                    {
                        Success = false, Error = "IDENTITY_DUPLICATE_REVIEW_REQUIRED",
                        Message = "A possible duplicate South African ID number was found. This is a review flag, not an automatic rejection. Do not approve this case; escalate it for a separate identity investigation."
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.SouthAfricanIdNumber))
            {
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Only South African ID card and green barcoded ID book reviews accept a South African ID number." });
            }
        }

        var reasonLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document_unclear"] = "Document image is unclear",
            ["back_side_missing"] = "Required back side is missing",
            ["document_expired"] = "Document is expired",
            ["name_mismatch"] = "Name does not match the profile",
            ["id_number_unreadable"] = "ID number is unreadable",
            ["suspected_tampering"] = "Document requires additional authenticity review",
            ["other"] = "Other review reason"
        };
        string reason;
        if (decision == "approve")
        {
            reason = string.IsNullOrWhiteSpace(runnerNote)
                ? "DFY manual document review completed and approved."
                : runnerNote;
        }
        else
        {
            if (!reasonLabels.TryGetValue(reasonCode, out var reasonLabel))
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose a valid standard review reason." });
            if (runnerNote.Length < 10 || runnerNote.Length > 850)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Provide runner-facing instructions between 10 and 850 characters." });
            reason = reasonCode + ": " + reasonLabel + "\n" + runnerNote;
        }
        if (reason.Length > 1000)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "The runner-facing review reason cannot exceed 1000 characters." });

        var now = DateTime.UtcNow;
        var nextStatus = decision switch
        {
            "approve" => "passed",
            "reject" => "failed",
            _ => "more_info_requested"
        };
        var submissionStatus = decision switch
        {
            "approve" => "approved",
            "reject" => "rejected",
            _ => "more_info_requested"
        };

        var reviewedUserId = 0;
        if (context.Database.IsRelational())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await context.IdentityVerificationSubmissions
                .Where(s => s.Id == id && s.Status == "pending" && s.ReviewClaimedByUserId == adminId.Value && s.ReviewClaimExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.Status, submissionStatus)
                    .SetProperty(s => s.ReviewedAt, (DateTime?)now)
                    .SetProperty(s => s.ReviewedByUserId, (int?)adminId.Value)
                    .SetProperty(s => s.ReviewReason, reason)
                    .SetProperty(s => s.InternalReviewNote, internalNote)
                    .SetProperty(s => s.DocumentsDeleteAfter, (DateTime?)now.AddDays(90))
                    .SetProperty(s => s.ReviewClaimedByUserId, (int?)null)
                    .SetProperty(s => s.ReviewClaimHeartbeatAt, (DateTime?)null)
                    .SetProperty(s => s.ReviewClaimExpiresAt, (DateTime?)null),
                    cancellationToken);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                var exists = await context.IdentityVerificationSubmissions.AsNoTracking().AnyAsync(s => s.Id == id, cancellationToken);
                return exists
                    ? Conflict(new ApiResponse<object> { Success = false, Message = "This application has already been reviewed or is no longer pending." })
                    : NotFound();
            }

            var submission = await context.IdentityVerificationSubmissions
                .Include(s => s.User)
                .FirstAsync(s => s.Id == id, cancellationToken);
            reviewedUserId = submission.UserId;
            if (decision == "approve")
            {
                submission.User.DateOfBirth = request.VerifiedDateOfBirth!.Value.Date;
                submission.DocumentIdHmac = approvedIdHmac;
                submission.User.IdentityDocumentIdHmac = approvedIdHmac ?? submission.User.IdentityDocumentIdHmac;
            }
            submission.User.IdentityVerificationStatus = nextStatus;
            submission.User.IdentityVerificationProvider = ManualProvider;
            submission.User.IdentityVerificationUpdatedAt = now;
            submission.User.IdentityVerificationLastEventAt = now;
            context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_" + decision, nextStatus, adminId.Value, submission.Id));
            context.AuditLogs.Add(new AuditLog
            {
                UserId = adminId.Value,
                Action = "AdminIdentityReviewDecision",
                EntityType = "IdentityVerificationSubmission",
                EntityId = submission.Id,
                OldValues = "pending",
                NewValues = $"decision={decision};status={submissionStatus};reason={reason}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            var runnerTitle = decision switch
            {
                "approve" => "DFY Document Review approved",
                "reject" => "DFY Document Review unsuccessful",
                _ => "More information required for DFY Document Review"
            };
            var runnerMessage = decision switch
            {
                "approve" => "DFY has completed its manual document review. Your runner eligibility has been updated.",
                "reject" => "DFY could not approve this document submission. Review reason: " + reason,
                _ => "DFY needs you to update your submission. Please follow these instructions: " + reason
            };
            EnqueueIdentityNotification(context, $"identity-review:{submission.Id}:{decision}:runner:in-app",
                "in_app", submission.UserId, submission.User.Email, "identity_review_" + decision, runnerTitle, runnerMessage);
            if (!string.IsNullOrWhiteSpace(submission.User.Email))
                EnqueueIdentityNotification(context, $"identity-review:{submission.Id}:{decision}:runner:email",
                    "email", submission.UserId, submission.User.Email, "identity_review_" + decision, runnerTitle, runnerMessage);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            var submission = await context.IdentityVerificationSubmissions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (submission == null) return NotFound();
            if (submission.Status != "pending" || submission.ReviewClaimedByUserId != adminId.Value ||
                !submission.ReviewClaimExpiresAt.HasValue || submission.ReviewClaimExpiresAt.Value <= now)
                return Conflict(new ApiResponse<object> { Success = false, Message = "This application has already been reviewed or is no longer pending." });

            reviewedUserId = submission.UserId;
            submission.ReviewedAt = now;
            submission.ReviewedByUserId = adminId.Value;
            submission.ReviewReason = reason;
            submission.InternalReviewNote = internalNote;
            submission.Status = submissionStatus;
            submission.DocumentsDeleteAfter = now.AddDays(90);
            submission.ReviewClaimedByUserId = null;
            submission.ReviewClaimHeartbeatAt = null;
            submission.ReviewClaimExpiresAt = null;
            if (decision == "approve")
            {
                submission.User.DateOfBirth = request.VerifiedDateOfBirth!.Value.Date;
                submission.DocumentIdHmac = approvedIdHmac;
                submission.User.IdentityDocumentIdHmac = approvedIdHmac ?? submission.User.IdentityDocumentIdHmac;
            }
            submission.User.IdentityVerificationStatus = nextStatus;
            submission.User.IdentityVerificationProvider = ManualProvider;
            submission.User.IdentityVerificationUpdatedAt = now;
            submission.User.IdentityVerificationLastEventAt = now;
            context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_" + decision, nextStatus, adminId.Value, submission.Id));
            context.AuditLogs.Add(new AuditLog
            {
                UserId = adminId.Value,
                Action = "AdminIdentityReviewDecision",
                EntityType = "IdentityVerificationSubmission",
                EntityId = submission.Id,
                OldValues = "pending",
                NewValues = $"decision={decision};status={submissionStatus};reason={reason}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { submissionId = id, status = nextStatus, reviewedAt = now },
            Message = decision == "approve"
                ? "DFY recorded the manual document review as approved."
                : decision == "reject"
                    ? "DFY recorded the manual document review as rejected."
                    : "Additional information has been requested. The runner may submit a corrected application."
        });
    }

    private static void EnqueueIdentityNotification(
        AppDbContext db,
        string idempotencyKey,
        string channel,
        int recipientUserId,
        string? recipientEmail,
        string type,
        string title,
        string message)
    {
        if (channel == "email" && string.IsNullOrWhiteSpace(recipientEmail)) return;
        db.IdentityNotificationOutbox.Add(new IdentityNotificationOutbox
        {
            IdempotencyKey = idempotencyKey,
            Channel = channel,
            RecipientUserId = recipientUserId,
            RecipientEmail = string.IsNullOrWhiteSpace(recipientEmail) ? null : recipientEmail.Trim(),
            Type = type,
            Title = title,
            Message = message,
            Status = "pending",
            AttemptCount = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
    }

    private async System.Threading.Tasks.Task SendEmailSafelyAsync(string? recipient, string subject, string body, CancellationToken cancellationToken)
    {
        if (emailSender == null || string.IsNullOrWhiteSpace(recipient)) return;
        try
        {
            await emailSender.SendAsync(recipient, subject, body, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DFY Document Review email notification failed for recipient {Recipient}; the workflow remains saved.", recipient);
        }
    }

    private async System.Threading.Tasks.Task NotifySafelyAsync(Func<INotificationService, System.Threading.Tasks.Task> notification, string purpose)
    {
        if (notifications == null) return;
        try
        {
            await notification(notifications);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DFY Document Review notification failed for {Purpose}; the workflow remains saved.", purpose);
        }
    }

    private async Task<bool> HasRecentAdminTwoFactorAsync(int adminId, CancellationToken cancellationToken)
    {
        var account = await context.Users.AsNoTracking()
            .Where(u => u.Id == adminId && !u.IsArchived)
            .Select(u => new { u.AuthenticatorTwoFactorEnabled, u.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return account?.AuthenticatorTwoFactorEnabled == true &&
            account.LastTwoFactorVerifiedAt.HasValue &&
            account.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.Subtract(AdminStepUpWindow);
    }

    private static async Task<(byte[] Bytes, string ContentType)> ReadImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxDocumentBytes)
            throw new InvalidDataException("Each identity-document image must be between 1 byte and 5 MB.");

        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        var bytes = stream.ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return (bytes, "image/jpeg");
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (bytes, "image/png");

        CryptographicOperations.ZeroMemory(bytes);
        throw new InvalidDataException("Upload a JPG or PNG image. PDF files and other formats are not accepted yet.");
    }

    private IdentityVerificationEvent CreateEvent(int userId, string eventType, string resultStatus, int? actorUserId = null, int? submissionId = null, string? documentSide = null)
    {
        var eventId = Guid.NewGuid().ToString("N");
        return new IdentityVerificationEvent
        {
            UserId = userId,
            Provider = ManualProvider,
            EventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant(),
            EventType = eventType,
            ResultStatus = resultStatus,
            ReceivedAt = DateTime.UtcNow,
            ActorUserId = actorUserId,
            SubmissionId = submissionId,
            DocumentSide = documentSide
        };
    }

    private static bool IsRunner(string? userType) =>
        string.Equals(userType?.Trim(), "runner", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(userType?.Trim(), "both", StringComparison.OrdinalIgnoreCase);

    private static bool CanRestart(string? status) =>
        string.Equals(status, "not_started", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "more_info_requested", StringComparison.OrdinalIgnoreCase);

    private static int AttemptsRemaining(int count, DateTime? windowStart) =>
        !windowStart.HasValue || windowStart.Value <= DateTime.UtcNow.Subtract(AttemptWindow)
            ? MaxAttemptsPerWindow
            : Math.Max(0, MaxAttemptsPerWindow - count);

    private int? CurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }
}

public sealed class StartManualIdentityVerificationRequest
{
    [Required]
    public bool ConsentAccepted { get; set; }

    [Required, StringLength(40)]
    public string ConsentVersion { get; set; } = string.Empty;
}

public sealed class SubmitManualIdentityVerificationHandoffRequest
{
    [Required, StringLength(200, MinimumLength = 20)]
    public string HandoffCode { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string DocumentType { get; set; } = string.Empty;

    [Required]
    public IFormFile? FrontDocument { get; set; }

    public IFormFile? BackDocument { get; set; }
}

public sealed class SubmitManualIdentityVerificationRequest
{
    [Required]
    public bool ConsentAccepted { get; set; }

    [Required, StringLength(40)]
    public string ConsentVersion { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string DocumentType { get; set; } = string.Empty;

    [Required]
    public IFormFile? FrontDocument { get; set; }

    public IFormFile? BackDocument { get; set; }
}

public sealed class ManualIdentityVerificationDecisionRequest
{
    [Required, StringLength(32)]
    public string Decision { get; set; } = string.Empty;

    public DateTime? VerifiedDateOfBirth { get; set; }

    [StringLength(40)]
    public string? ReasonCode { get; set; }

    [StringLength(850)]
    public string? RunnerNote { get; set; }

    [StringLength(13, MinimumLength = 13)]
    public string? SouthAfricanIdNumber { get; set; }

    [StringLength(1000)]
    public string? InternalNote { get; set; }
}
