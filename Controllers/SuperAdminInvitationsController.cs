using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/super-admin/invitations")]
[Authorize]
public sealed class SuperAdminInvitationsController(
    AppDbContext context,
    IEmailSender emailSender,
    IConfiguration configuration,
    ILogger<SuperAdminInvitationsController> logger) : ControllerBase
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(48);

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAdminInvitationRequest request, CancellationToken cancellationToken)
    {
        var actorId = GetCurrentUserId();
        if (actorId == null) return Unauthorized();

        var actor = await context.Users.AsNoTracking()
            .Where(user => user.Id == actorId.Value && !user.IsArchived)
            .Select(user => new { user.Roles, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (!HasRole(actor?.Roles, "SuperAdmin")) return Forbid();
        if (actor?.AuthenticatorTwoFactorEnabled != true ||
            !actor.LastTwoFactorVerifiedAt.HasValue ||
            actor.LastTwoFactorVerifiedAt.Value < DateTime.UtcNow.Subtract(StepUpWindow))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Fresh authenticator verification is required to invite an administrator." });

        var email = request.Email?.Trim();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 ||
            !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) ||
            !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Enter a valid email address." });
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new { message = "A reason between 5 and 500 characters is required." });

        var normalizedEmail = email.ToLowerInvariant();
        var frontendBaseUrl = Environment.GetEnvironmentVariable("DFY_FRONTEND_URL")
            ?? configuration["Frontend:BaseUrl"]
            ?? configuration["AllowedOrigin"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var frontendUri) ||
            frontendUri.Scheme != Uri.UriSchemeHttps)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Secure frontend invitation URL is not configured. Set DFY_FRONTEND_URL to the HTTPS staging frontend." });

        var existingUser = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);
        if (existingUser != null && (existingUser.IsArchived || HasRole(existingUser.Roles, "Admin") || HasRole(existingUser.Roles, "SuperAdmin")))
            return Conflict(new { message = "This email already belongs to an archived or administrative account. Resolve that account explicitly instead of issuing a new invitation." });

        var now = DateTime.UtcNow;
        var pending = await context.AdminInvitations
            .Where(invitation => invitation.NormalizedEmail == normalizedEmail && invitation.Status == "pending")
            .ToListAsync(cancellationToken);
        foreach (var old in pending)
        {
            old.Status = "revoked";
            old.RevokedAt = now;
        }

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var invitation = new AdminInvitation
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            TokenHash = HashToken(rawToken),
            Reason = reason,
            Status = "pending",
            CreatedByUserId = actorId.Value,
            CreatedAt = now,
            ExpiresAt = now.Add(InvitationLifetime)
        };
        context.AdminInvitations.Add(invitation);
        await context.SaveChangesAsync(cancellationToken);

        context.AuditLogs.Add(new AuditLog
        {
            UserId = actorId.Value,
            Action = "CreateAdminInvitation",
            EntityType = "AdminInvitation",
            EntityId = invitation.Id,
            OldValues = null,
            NewValues = $"email={normalizedEmail}; expiresAt={invitation.ExpiresAt:O}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);

        var acceptUrl = new Uri(frontendUri, $"/admin/invitation/accept#token={Uri.EscapeDataString(rawToken)}");
        var body = $"""
            You have been invited to administer the DoForYou marketplace.

            To accept:
            1. Open this secure invitation link: {acceptUrl}
            2. Register or sign in using {email}.
            3. Verify your email address if you have not already.
            4. Enable authenticator MFA and complete the acceptance step.

            This invitation expires in 48 hours and can only be used once. It grants Admin access, not SuperAdmin access.
            If you were not expecting this invitation, you can ignore this email.
            """;
        try
        {
            await emailSender.SendAsync(email, "DoForYou administrator invitation", body, cancellationToken);
        }
        catch (Exception exception)
        {
            invitation.Status = "delivery_failed";
            await context.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Unable to deliver administrator invitation {InvitationId}.", invitation.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The invitation could not be delivered. No usable invitation was issued; check email delivery and retry." });
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                invitationId = invitation.Id,
                invitation.Email,
                invitation.Status,
                invitation.CreatedAt,
                invitation.ExpiresAt
            },
            message = "Administrator invitation sent. The recipient must verify their email and enable authenticator MFA before accepting."
        });
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var actorId = GetCurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var items = await context.AdminInvitations.AsNoTracking()
            .OrderByDescending(invitation => invitation.CreatedAt)
            .Take(100)
            .Select(invitation => new
            {
                invitation.Id,
                invitation.Email,
                invitation.Reason,
                invitation.Status,
                invitation.CreatedAt,
                invitation.ExpiresAt,
                invitation.AcceptedAt,
                invitation.RevokedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(new { success = true, data = items });
    }

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
    [HttpPost("{id:int}/revoke")]
    public async Task<IActionResult> Revoke(int id, [FromBody] RevokeAdminInvitationRequest request, CancellationToken cancellationToken)
    {
        var actorId = GetCurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new { message = "A reason between 5 and 500 characters is required." });

        var invitation = await context.AdminInvitations.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (invitation == null) return NotFound();
        if (invitation.Status != "pending" || invitation.ExpiresAt <= DateTime.UtcNow)
            return Conflict(new { message = "Only a pending, unexpired invitation can be revoked." });

        invitation.Status = "revoked";
        invitation.RevokedAt = DateTime.UtcNow;
        context.AuditLogs.Add(new AuditLog
        {
            UserId = actorId.Value,
            Action = "RevokeAdminInvitation",
            EntityType = "AdminInvitation",
            EntityId = invitation.Id,
            OldValues = "pending",
            NewValues = $"revoked; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, message = "Invitation revoked." });
    }

    [Authorize]
    [HttpPost("accept")]
    public async Task<IActionResult> Accept([FromBody] AcceptAdminInvitationRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var rawToken = request.Token?.Trim();
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 200)
            return BadRequest(new { message = "A valid invitation token is required." });

        var now = DateTime.UtcNow;
        var tokenHash = HashToken(rawToken);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;

        var invitation = await context.AdminInvitations
            .FirstOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (invitation == null || invitation.Status != "pending" || invitation.ExpiresAt <= now)
            return BadRequest(new { message = "This invitation is invalid, expired or already used." });

        var user = await context.Users.FirstOrDefaultAsync(item => item.Id == userId.Value && !item.IsArchived, cancellationToken);
        if (user == null) return Unauthorized();
        if (!user.EmailVerified || !string.Equals(user.NormalizedEmail, invitation.NormalizedEmail, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Sign in with the invited email address and verify that email before accepting." });
        if (!user.AuthenticatorTwoFactorEnabled || !user.LastTwoFactorVerifiedAt.HasValue ||
            user.LastTwoFactorVerifiedAt.Value < now.Subtract(StepUpWindow))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Enable authenticator MFA and complete a fresh verification before accepting this administrator invitation." });
        if (HasRole(user.Roles, "Admin") || HasRole(user.Roles, "SuperAdmin"))
            return Conflict(new { message = "This account already has administrative access." });

        invitation.Status = "accepted";
        invitation.AcceptedAt = now;
        invitation.AcceptedByUserId = user.Id;
        var oldRoles = user.Roles;
        user.Roles = "Admin";
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        context.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id,
            Action = "AcceptAdminInvitation",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = oldRoles,
            NewValues = $"Admin; invitationId={invitation.Id}; invitedBy={invitation.CreatedByUserId}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return Ok(new { success = true, message = "Administrator access has been activated. Sign in again to refresh your access token." });
    }

    private async Task<bool> IsFreshSuperAdminAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .Where(item => item.Id == userId && !item.IsArchived)
            .Select(item => new { item.Roles, item.AuthenticatorTwoFactorEnabled, item.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return HasRole(user?.Roles, "SuperAdmin") &&
            user?.AuthenticatorTwoFactorEnabled == true &&
            user.LastTwoFactorVerifiedAt.HasValue &&
            user.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.Subtract(StepUpWindow);
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var id) ? id : null;
    }

    private static bool HasRole(string? roles, string role) =>
        roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(value.Trim(), role, StringComparison.OrdinalIgnoreCase)) == true;

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class CreateAdminInvitationRequest
{
    public string Email { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class RevokeAdminInvitationRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class AcceptAdminInvitationRequest
{
    public string Token { get; set; } = string.Empty;
}
