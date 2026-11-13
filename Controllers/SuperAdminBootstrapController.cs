using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

/// <summary>
/// One-time staging-only bootstrap for the first DFY Super Admin.
/// The account must already exist, own the configured mailbox, and have authenticator MFA enrolled.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting("auth")]
[Route("api/v1/admin/bootstrap")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SuperAdminBootstrapController(
    AppDbContext context,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IAuthenticatorService authenticator) : ControllerBase
{
    private const string BootstrapEmail = "admindfy@gmail.com";
    private const string BootstrapAuditAction = "BootstrapSuperAdmin";

    [HttpPost("super-admin")]
    public async Task<IActionResult> BootstrapFirstSuperAdmin(
        [FromBody] SuperAdminBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        // This endpoint is deliberately absent from production behavior even if a secret
        // is accidentally configured there.
        if (!environment.IsStaging())
            return NotFound();

        var bootstrapEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DFY_SUPERADMIN_BOOTSTRAP_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var configuredSecret = Environment.GetEnvironmentVariable("DFY_SUPERADMIN_BOOTSTRAP_SECRET");
        if (!bootstrapEnabled || string.IsNullOrWhiteSpace(configuredSecret))
            return NotFound();

        var suppliedSecret = request.BootstrapSecret ?? string.Empty;
        var expectedBytes = Encoding.UTF8.GetBytes(configuredSecret);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedSecret);
        var secretMatches = CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        CryptographicOperations.ZeroMemory(expectedBytes);
        CryptographicOperations.ZeroMemory(suppliedBytes);
        if (!secretMatches)
            return Forbid();

        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Unauthorized();

        // Serializable isolation prevents two concurrent first-bootstrap requests from
        // both observing an empty bootstrap audit marker and granting different accounts.
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var completed = await context.AuditLogs.AsNoTracking().AnyAsync(
            audit => audit.Action == BootstrapAuditAction && audit.EntityType == "SuperAdminBootstrap",
            cancellationToken);
        if (completed)
            return Conflict(new { message = "The first Super Admin bootstrap has already been completed." });

        var existingSuperAdmins = await context.Users.AsNoTracking()
            .Where(user => !user.IsArchived && user.Roles != null)
            .Select(user => new { user.Id, user.Roles })
            .ToListAsync(cancellationToken);
        if (existingSuperAdmins.Any(user => user.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase))))
            return Conflict(new { message = "A Super Admin already exists. Use the audited role-management workflow." });

        var account = await context.Users.FirstOrDefaultAsync(
            user => user.Id == actorId && !user.IsArchived,
            cancellationToken);
        if (account == null ||
            !string.Equals(account.NormalizedEmail, BootstrapEmail, StringComparison.OrdinalIgnoreCase) ||
            !account.EmailVerified)
            return Forbid();

        if (!account.AuthenticatorTwoFactorEnabled ||
            string.IsNullOrWhiteSpace(account.AuthenticatorSecretEncrypted) ||
            !authenticator.IsConfigured)
            return Conflict(new { message = "Enroll and verify authenticator MFA before running the bootstrap." });

        string authenticatorSecret;
        try
        {
            authenticatorSecret = authenticator.DecryptSecret(account.AuthenticatorSecretEncrypted);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return Conflict(new { message = "The authenticator enrollment must be repaired before bootstrap." });
        }

        var now = DateTimeOffset.UtcNow;
        var validCode = authenticator.TryVerifyCode(
            authenticatorSecret, request.AuthenticatorCode, out var matchedStep, now);
        if (!validCode || (account.LastAuthenticatorTimeStep.HasValue &&
            matchedStep <= account.LastAuthenticatorTimeStep.Value))
            return Forbid();

        var previousRoles = account.Roles;
        account.Roles = "Admin,SuperAdmin";
        account.LastAuthenticatorTimeStep = matchedStep;
        account.LastTwoFactorVerifiedAt = now.UtcDateTime;
        account.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = account.Id,
            Action = BootstrapAuditAction,
            EntityType = "SuperAdminBootstrap",
            EntityId = account.Id,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { roles = previousRoles }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                roles = account.Roles,
                email = BootstrapEmail,
                bootstrappedAtUtc = now,
                stagingOnly = true
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            message = "The first Super Admin was provisioned. Sign in again; the current session has been revoked."
        });
    }
}

public sealed class SuperAdminBootstrapRequest
{
    [Required, StringLength(512, MinimumLength = 32)]
    public string BootstrapSecret { get; set; } = string.Empty;

    [Required, RegularExpression("^[0-9]{6}$")]
    public string AuthenticatorCode { get; set; } = string.Empty;
}
