using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Roles = "Admin")]
[Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
public sealed class AdminUserProfileCorrectionsController(
    AppDbContext context,
    IConfiguration configuration,
    IEmailSender emailSender,
    INotificationService notifications) : ControllerBase
{
    [HttpGet("{userId:int}/profile-correction")]
    public async Task<ActionResult<ApiResponse<object>>> GetProfileForCorrection(int userId, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .Where(item => item.Id == userId && !item.IsArchived && !item.IsSuspended)
            .Select(item => new
            {
                item.Id, item.FirstName, item.LastName, item.Email, item.EmailVerified,
                item.PhoneNumber, item.PhoneVerified, item.Address,
                isAdmin = item.Roles != null && (item.Roles.Contains("Admin") || item.Roles.Contains("SuperAdmin"))
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Active user not found." });
        if (user.isAdmin)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Administrative accounts require the separately controlled administrator identity-change process." });

        return Ok(new ApiResponse<object> { Success = true, Data = user });
    }

    [HttpPatch("{userId:int}/profile-correction")]
    public async Task<ActionResult<ApiResponse<object>>> CorrectProfile(
        int userId,
        [FromBody] AdminUserProfileCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        var firstName = request.FirstName?.Trim() ?? string.Empty;
        var lastName = request.LastName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var requestedPhone = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : NormalizeSouthAfricanPhone(request.PhoneNumber);
        var address = request.Address?.Trim() ?? string.Empty;
        if (firstName.Length is < 2 or > 80 || lastName.Length is < 2 or > 80)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "First and last names must each contain between 2 and 80 characters." });
        if (string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || email.Length > 254)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter a valid email address." });
        if (address.Length > 300)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Address cannot exceed 300 characters." });

        var target = await context.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (target == null) return NotFound(new ApiResponse<object> { Success = false, Message = "User not found." });
        if (target.IsArchived || target.IsSuspended)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Restore and reinstate the account before correcting profile details." });
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && requestedPhone == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter a valid South African mobile number. Changing it will clear its verified status." });
        var phone = requestedPhone ?? target.PhoneNumber;
        var targetRoles = target.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (targetRoles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase)))
            return Conflict(new ApiResponse<object> { Success = false, Message = "Administrative accounts require the separately controlled administrator identity-change process." });

        var normalizedEmail = email;
        var emailChanged = !string.Equals(target.NormalizedEmail ?? target.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            var emailInUse = await context.Users.AnyAsync(user => user.Id != userId &&
                (user.NormalizedEmail == normalizedEmail || user.Email.ToLower() == normalizedEmail), cancellationToken);
            if (emailInUse)
                return Conflict(new ApiResponse<object> { Success = false, Message = "That email address is already associated with another account." });
            var frontendBaseUrl = Environment.GetEnvironmentVariable("DFY_PUBLIC_URL") ?? configuration["Frontend:BaseUrl"];
            if (string.IsNullOrWhiteSpace(frontendBaseUrl))
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ApiResponse<object> { Success = false, Message = "Email verification is not configured. No profile changes were made." });
        }

        var now = DateTime.UtcNow;
        var oldValues = new
        {
            target.FirstName, target.LastName, target.Email, target.PhoneNumber, target.PhoneVerified, target.Address
        };
        var changed = emailChanged ||
            !string.Equals(target.FirstName, firstName, StringComparison.Ordinal) ||
            !string.Equals(target.LastName, lastName, StringComparison.Ordinal) ||
            !string.Equals(target.PhoneNumber, phone, StringComparison.Ordinal) ||
            !string.Equals(target.Address ?? string.Empty, address, StringComparison.Ordinal);
        if (!changed)
            return Conflict(new ApiResponse<object> { Success = false, Message = "The submitted profile details are unchanged." });

        var oldEmail = target.Email;
        string? verificationToken = null;
        if (emailChanged)
        {
            verificationToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            target.Email = email;
            target.NormalizedEmail = normalizedEmail;
            target.EmailVerified = false;
            target.EmailVerificationToken = HashToken(verificationToken);
            target.EmailVerificationExpiry = now.AddHours(24);
            target.EmailVerificationSentAt = now;
            target.EmailVerificationSendWindowStartAt = now;
            target.EmailVerificationSendCount = 1;
            target.SecurityStamp = CreateSecureToken();
            target.FailedLoginAttempts = 0;
            target.FailedLoginWindowStartAt = null;
            target.LoginLockoutUntil = null;
        }

        var phoneChanged = !string.Equals(target.PhoneNumber, phone, StringComparison.Ordinal);
        target.FirstName = firstName;
        target.LastName = lastName;
        target.PhoneNumber = phone;
        target.Address = string.IsNullOrWhiteSpace(address) ? null : address;
        if (phoneChanged)
        {
            target.PhoneVerified = false;
            target.PhoneVerificationCode = null;
            target.PhoneVerificationExpiry = null;
        }

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        context.AuditLogs.Add(new AuditLog
        {
            UserId = actorId.Value,
            Action = "AdminUserProfileCorrected",
            EntityType = "User",
            EntityId = target.Id,
            OldValues = System.Text.Json.JsonSerializer.Serialize(oldValues),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                target.FirstName, target.LastName, target.Email, target.PhoneNumber,
                target.PhoneVerified, target.Address, target.EmailVerified,
                reason, emailVerificationRequired = emailChanged, phoneReverificationRequired = phoneChanged
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        var message = emailChanged
            ? "Profile corrected. The new email address is unverified; the user must complete the emailed verification link before signing in."
            : phoneChanged
                ? "Profile corrected. The changed phone number is now unverified and must be verified through the supported account process."
                : "Profile corrected. The previous and new values are retained in the restricted audit history.";

        try
        {
            await notifications.CreateNotificationAsync(
                target.Id,
                "admin_profile_corrected",
                "Your DoForYou profile was updated",
                emailChanged
                    ? "An administrator corrected your profile and changed your email address. Verify the new address using the link sent to it. Existing sessions were revoked."
                    : phoneChanged
                        ? "An administrator corrected your profile. Your changed phone number is no longer marked as verified."
                        : "An administrator corrected your profile details. Contact support if you did not expect this change.");
        }
        catch
        {
            // The committed audit record remains the source of truth if notification delivery is unavailable.
        }

        if (emailChanged && verificationToken != null)
        {
            try
            {
                var baseUrl = (Environment.GetEnvironmentVariable("DFY_PUBLIC_URL") ?? configuration["Frontend:BaseUrl"] ?? string.Empty).TrimEnd('/');
                var link = $"{baseUrl}/verify-email#token={Uri.EscapeDataString(verificationToken)}";
                await emailSender.SendAsync(email, "Verify your updated DoForYou email address",
                    $"Your DoForYou account email was updated by an administrator. Verify the new address within 24 hours using this link:\n{link}\n\nIf you did not expect this change, contact DoForYou support.",
                    cancellationToken);
            }
            catch
            {
                // Keep the account unverified and the token persisted; the user can request a resend.
            }

            try
            {
                await emailSender.SendAsync(oldEmail, "A change was made to your DoForYou account email",
                    $"An administrator changed the email address on your DoForYou account. The new address must be verified before sign-in is allowed. If you did not expect this change, contact DoForYou support.",
                    cancellationToken);
            }
            catch
            {
                // Best-effort security notice to the previous address.
            }
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                target.Id, target.FirstName, target.LastName, target.Email, target.EmailVerified,
                target.PhoneNumber, target.PhoneVerified, target.Address,
                emailVerificationRequired = emailChanged,
                phoneReverificationRequired = phoneChanged
            },
            Message = message
        });
    }

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string CreateSecureToken() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string? NormalizeSouthAfricanPhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("27", StringComparison.Ordinal)) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = digits[1..];
        return digits.Length == 9 && digits[0] is >= '6' and <= '8' ? "0" + digits : null;
    }
}

public sealed class AdminUserProfileCorrectionRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string Reason { get; set; } = string.Empty;
}
