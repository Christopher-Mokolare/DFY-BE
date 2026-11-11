using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    IEmailSender? emailSender = null) : ControllerBase
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan AdminStepUpWindow = TimeSpan.FromMinutes(15);
    private const int MaxAttemptsPerWindow = 3;
    private const int MaxDocumentBytes = 5 * 1024 * 1024;
    private const string ManualProvider = "DFY_MANUAL";

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

        var configured = encryption.IsConfigured && !string.IsNullOrWhiteSpace(ConsentVersion);
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
        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion))
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
        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion))
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
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new ApiResponse<object> { Success = false, Message = ex.Message });
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
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            await NotifySafelyAsync(service => service.CreateNotificationAsync(
                user.Id, "identity_review_submitted", "DFY Document Review received",
                "Your documents were securely submitted. DFY aims to review submissions within one business day; we will notify you when there is an update."), "runner submission confirmation");
            await NotifySafelyAsync(service => service.NotifyAdminsAsync(
                "identity_review_new", "New DFY Document Review",
                $"Runner {user.Id} submitted document review case {submission.Id}."), "administrator new-case alert");
            await SendEmailSafelyAsync(user.Email, "We received your DFY Document Review submission",
                "Your identity documents were securely submitted. DFY aims to review submissions within one business day. We will notify you when there is an update.", cancellationToken);
            var adminEmails = await context.Users.AsNoTracking()
                .Where(u => !u.IsArchived && u.Roles != null && u.Roles.Contains("Admin"))
                .Select(u => u.Email)
                .ToListAsync(cancellationToken);
            foreach (var adminEmail in adminEmails.Distinct(StringComparer.OrdinalIgnoreCase))
                await SendEmailSafelyAsync(adminEmail, "New DFY Document Review submission",
                    $"A runner submitted document review case {submission.Id}. Sign in to the DFY administrator dashboard to review it.", cancellationToken);
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

        if (!encryption.IsConfigured || string.IsNullOrWhiteSpace(ConsentVersion))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false,
                Error = "DFY_IDENTITY_VERIFICATION_NOT_CONFIGURED",
                Message = "DFY identity-document submission is not available yet."
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
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new ApiResponse<object> { Success = false, Message = ex.Message });
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
    [HttpGet("admin/pending")]
    public async Task<ActionResult<ApiResponse<object>>> GetPending(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = context.IdentityVerificationSubmissions.AsNoTracking()
            .Where(s => s.Status == "pending")
            .OrderBy(s => s.SubmittedAt);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.UserId, s.DocumentType, s.Status, s.SubmittedAt,
                userName = s.User.FirstName + " " + s.User.LastName,
                s.User.Email, s.User.DateOfBirth
            })
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var submissions = rows.Select(s => new
        {
            s.Id, s.UserId, s.DocumentType, s.Status, s.SubmittedAt,
            s.userName, s.Email, s.DateOfBirth,
            reviewDueAt = SouthAfricanBusinessCalendar.AddBusinessHours(s.SubmittedAt, 8, configuration["IdentityVerification:SouthAfricanPublicHolidays"]),
            reviewAgeMinutes = Math.Max(0, (int)(now - s.SubmittedAt).TotalMinutes),
            isOverdue = now > SouthAfricanBusinessCalendar.AddBusinessHours(s.SubmittedAt, 8, configuration["IdentityVerification:SouthAfricanPublicHolidays"])
        }).ToList();
        return Ok(new ApiResponse<object> { Success = true, Data = new { submissions, totalCount = total, page, pageSize } });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("admin/{id:int}/document/{side}")]
    public async Task<IActionResult> GetDocument(int id, string side, CancellationToken cancellationToken)
    {
        var adminId = CurrentUserId();
        if (adminId == null) return Unauthorized();
        if (!await HasRecentAdminTwoFactorAsync(adminId.Value, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Complete administrator authenticator verification before viewing identity documents." });

        var submission = await context.IdentityVerificationSubmissions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (submission == null) return NotFound();

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

        context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_document_accessed", "viewed"));
        await context.SaveChangesAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(plaintext, contentType);
    }

    [Authorize(Roles = "Admin")]
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
                .Where(s => s.Id == id && s.Status == "pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.Status, submissionStatus)
                    .SetProperty(s => s.ReviewedAt, (DateTime?)now)
                    .SetProperty(s => s.ReviewedByUserId, (int?)adminId.Value)
                    .SetProperty(s => s.ReviewReason, reason)
                    .SetProperty(s => s.InternalReviewNote, internalNote)
                    .SetProperty(s => s.DocumentsDeleteAfter, (DateTime?)now.AddDays(90)),
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
            submission.User.IdentityVerificationStatus = nextStatus;
            submission.User.IdentityVerificationProvider = ManualProvider;
            submission.User.IdentityVerificationUpdatedAt = now;
            submission.User.IdentityVerificationLastEventAt = now;
            context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_" + decision, nextStatus));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            var submission = await context.IdentityVerificationSubmissions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (submission == null) return NotFound();
            if (submission.Status != "pending")
                return Conflict(new ApiResponse<object> { Success = false, Message = "This application has already been reviewed or is no longer pending." });

            reviewedUserId = submission.UserId;
            submission.ReviewedAt = now;
            submission.ReviewedByUserId = adminId.Value;
            submission.ReviewReason = reason;
            submission.InternalReviewNote = internalNote;
            submission.Status = submissionStatus;
            submission.DocumentsDeleteAfter = now.AddDays(90);
            submission.User.IdentityVerificationStatus = nextStatus;
            submission.User.IdentityVerificationProvider = ManualProvider;
            submission.User.IdentityVerificationUpdatedAt = now;
            submission.User.IdentityVerificationLastEventAt = now;
            context.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, "admin_" + decision, nextStatus));
            await context.SaveChangesAsync(cancellationToken);
        }

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
        await NotifySafelyAsync(service => service.CreateNotificationAsync(
            reviewedUserId, "identity_review_" + decision, runnerTitle, runnerMessage), "runner review decision notification");
        var runnerEmail = await context.Users.AsNoTracking()
            .Where(u => u.Id == reviewedUserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(runnerEmail))
            await SendEmailSafelyAsync(runnerEmail, runnerTitle, runnerMessage, cancellationToken);

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

    private IdentityVerificationEvent CreateEvent(int userId, string eventType, string resultStatus)
    {
        var eventId = Guid.NewGuid().ToString("N");
        return new IdentityVerificationEvent
        {
            UserId = userId,
            Provider = ManualProvider,
            EventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant(),
            EventType = eventType,
            ResultStatus = resultStatus,
            ReceivedAt = DateTime.UtcNow
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

    [StringLength(40)]
    public string? ReasonCode { get; set; }

    [StringLength(850)]
    public string? RunnerNote { get; set; }

    [StringLength(1000)]
    public string? InternalNote { get; set; }
}
