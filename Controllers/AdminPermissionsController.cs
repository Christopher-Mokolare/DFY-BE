using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/super-admin/permissions")]
[Authorize(Policy = "SuperAdminMfa")]
public sealed class AdminPermissionsController(AppDbContext context) : ControllerBase
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromDays(90);

    [HttpGet("catalog")]
    public IActionResult Catalog()
    {
        return Ok(new
        {
            success = true,
            data = AdminPermissionPolicies.All.Select(key => new
            {
                key,
                description = key switch
                {
                    AdminPermissionPolicies.UsersManage => "Manage user accounts and access state",
                    AdminPermissionPolicies.KycReview => "Review and decide runner identity submissions",
                    AdminPermissionPolicies.KycViewDocuments => "View decrypted identity-document images",
                    AdminPermissionPolicies.KycOverride => "Perform separately approved KYC overrides",
                    AdminPermissionPolicies.DisputesManage => "Manage dispute workflow and evidence",
                    AdminPermissionPolicies.TasksManage => "Intervene in task lifecycle operations",
                    AdminPermissionPolicies.FinanceManage => "Access finance operations and controls",
                    AdminPermissionPolicies.ContentManage => "Manage public content and publishing",
                    AdminPermissionPolicies.ConfigurationManage => "Manage audited platform configuration",
                    AdminPermissionPolicies.AuditRead => "Read administrative audit history",
                    _ => string.Empty
                }
            })
        });
    }

    [HttpGet("grants")]
    public async Task<IActionResult> List([FromQuery] int? userId, CancellationToken cancellationToken)
    {
        var query = context.AdminPermissionGrants.AsNoTracking();
        if (userId.HasValue) query = query.Where(grant => grant.UserId == userId.Value);

        var grants = await query
            .OrderByDescending(grant => grant.GrantedAt)
            .Take(500)
            .Select(grant => new
            {
                grant.Id, grant.UserId, grant.PermissionKey, grant.GrantedByUserId,
                grant.Reason, grant.GrantedAt, grant.ExpiresAt, grant.RevokedAt, grant.RevokedByUserId
            })
            .ToListAsync(cancellationToken);

        return Ok(new { success = true, data = grants });
    }

    [HttpPost("grants")]
    public async Task<IActionResult> Grant(
        [FromBody] GrantAdminPermissionRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var permissionKey = request.PermissionKey?.Trim() ?? string.Empty;
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (!AdminPermissionPolicies.IsKnown(permissionKey))
            return BadRequest(new { message = "Choose a permission from the supported permission catalogue." });
        if (string.Equals(permissionKey, AdminPermissionPolicies.KycOverride, StringComparison.Ordinal))
            return Conflict(new { message = "KYC override grants remain disabled until the separately controlled override workflow and approval tests are delivered." });
        if (request.UserId <= 0 || request.UserId == actorId.Value)
            return BadRequest(new { message = "Choose a different administrator account." });
        if (reason.Length < 10 || reason.Length > 500)
            return BadRequest(new { message = "A reason between 10 and 500 characters is required." });

        var now = DateTime.UtcNow;
        if (request.ExpiresAt <= now || request.ExpiresAt > now.Add(MaximumGrantLifetime))
            return BadRequest(new { message = "Permission grants must expire in the future and within 90 days." });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;

        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var target = await context.Users.AsNoTracking()
            .Where(user => user.Id == request.UserId && !user.IsArchived && !user.IsSuspended)
            .Select(user => new { user.Id, user.Roles, user.EmailVerified, user.AuthenticatorTwoFactorEnabled })
            .FirstOrDefaultAsync(cancellationToken);
        var targetRoles = target?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var isAdmin = targetRoles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase));
        var isSuperAdmin = targetRoles.Any(role => string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase));
        if (target == null || !isAdmin || isSuperAdmin || !target.EmailVerified || !target.AuthenticatorTwoFactorEnabled)
            return Conflict(new { message = "Permissions can only be granted to active, verified Admin accounts with authenticator MFA enabled." });

        var expiredGrants = await context.AdminPermissionGrants
            .Where(grant => grant.UserId == request.UserId && grant.PermissionKey == permissionKey &&
                grant.RevokedAt == null && grant.ExpiresAt != null && grant.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var expiredGrant in expiredGrants)
            expiredGrant.RevokedAt = expiredGrant.ExpiresAt;
        if (expiredGrants.Count > 0)
            await context.SaveChangesAsync(cancellationToken);

        var existing = await context.AdminPermissionGrants.AnyAsync(grant =>
            grant.UserId == request.UserId && grant.PermissionKey == permissionKey &&
            grant.RevokedAt == null && (grant.ExpiresAt == null || grant.ExpiresAt > now),
            cancellationToken);
        if (existing)
            return Conflict(new { message = "This administrator already has an active grant for that permission." });

        var grant = new AdminPermissionGrant
        {
            UserId = request.UserId,
            PermissionKey = permissionKey,
            GrantedByUserId = actorId.Value,
            Reason = reason,
            GrantedAt = now,
            ExpiresAt = request.ExpiresAt
        };
        context.AdminPermissionGrants.Add(grant);
        await context.SaveChangesAsync(cancellationToken);
        context.AuditLogs.Add(new AuditLog
        {
            UserId = actorId.Value,
            Action = "GrantAdminPermission",
            EntityType = "AdminPermissionGrant",
            EntityId = grant.Id,
            OldValues = null,
            NewValues = $"userId={grant.UserId};permission={grant.PermissionKey};expiresAt={grant.ExpiresAt:O};reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            data = new { grant.Id, grant.UserId, grant.PermissionKey, grant.GrantedAt, grant.ExpiresAt },
            message = "Administrator permission granted with a 90-day maximum lifetime."
        });
    }

    [HttpDelete("grants/{grantId:int}")]
    public async Task<IActionResult> Revoke(int grantId, [FromBody] RevokeAdminPermissionRequest request, CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 10 || reason.Length > 500)
            return BadRequest(new { message = "A reason between 10 and 500 characters is required." });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;

        var grant = await context.AdminPermissionGrants
            .FirstOrDefaultAsync(item => item.Id == grantId, cancellationToken);
        if (grant == null) return NotFound();
        if (grant.RevokedAt.HasValue)
            return Conflict(new { message = "This permission grant has already been revoked." });

        var now = DateTime.UtcNow;
        grant.RevokedAt = now;
        grant.RevokedByUserId = actorId.Value;
        context.AuditLogs.Add(new AuditLog
        {
            UserId = actorId.Value,
            Action = "RevokeAdminPermission",
            EntityType = "AdminPermissionGrant",
            EntityId = grant.Id,
            OldValues = $"userId={grant.UserId};permission={grant.PermissionKey};expiresAt={grant.ExpiresAt:O}",
            NewValues = $"revokedAt={now:O};reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        return Ok(new { success = true, data = new { grant.Id, grant.RevokedAt }, message = "Administrator permission revoked." });
    }

    private async Task<bool> IsFreshSuperAdminAsync(int userId, CancellationToken cancellationToken)
    {
        var account = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsArchived && !user.IsSuspended)
            .Select(user => new { user.Roles, user.EmailVerified, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var hasRole = account?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(role => string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true;
        return hasRole && account?.EmailVerified == true && account.AuthenticatorTwoFactorEnabled &&
            account.LastTwoFactorVerifiedAt.HasValue &&
            account.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.Subtract(StepUpWindow);
    }

    private int? CurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : null;
    }
}

public sealed class GrantAdminPermissionRequest
{
    [Range(1, int.MaxValue)]
    public int UserId { get; set; }

    [Required, StringLength(80)]
    public string PermissionKey { get; set; } = string.Empty;

    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    public DateTime ExpiresAt { get; set; }
}

public sealed class RevokeAdminPermissionRequest
{
    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;
}
