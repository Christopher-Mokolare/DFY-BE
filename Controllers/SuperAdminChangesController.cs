using System.Security.Claims;
using System.Security.Cryptography;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/super-admin/changes")]
[Authorize(Roles = "Admin")]
public sealed class SuperAdminChangesController(AppDbContext context) : ControllerBase
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(24);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSuperAdminChangeRequest request, CancellationToken cancellationToken)
    {
        var actorId = GetCurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var action = request.Action?.Trim().ToLowerInvariant();
        var reason = request.Reason?.Trim();
        if (action is not ("grant" or "revoke"))
            return BadRequest(new { message = "Action must be grant or revoke." });
        if (request.TargetUserId <= 0 || request.TargetUserId == actorId.Value)
            return BadRequest(new { message = "Choose another account. SuperAdmins cannot request changes to their own access." });
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new { message = "A reason between 10 and 500 characters is required." });

        var target = await context.Users.AsNoTracking()
            .Where(user => user.Id == request.TargetUserId && !user.IsArchived)
            .Select(user => new { user.Id, user.Email, user.Roles, user.EmailVerified, user.AuthenticatorTwoFactorEnabled })
            .FirstOrDefaultAsync(cancellationToken);
        if (target == null) return NotFound(new { message = "The target account was not found or is archived." });

        var targetIsSuperAdmin = HasRole(target.Roles, "SuperAdmin");
        if (action == "grant")
        {
            if (targetIsSuperAdmin)
                return Conflict(new { message = "The target is already a SuperAdmin." });
            if (!target.EmailVerified || !target.AuthenticatorTwoFactorEnabled)
                return Conflict(new { message = "The target must have a verified email address and authenticator MFA enabled before SuperAdmin promotion can be requested." });
        }
        else
        {
            if (!targetIsSuperAdmin)
                return Conflict(new { message = "The target is not a SuperAdmin." });
            var activeCount = await CountActiveSuperAdminsAsync(cancellationToken);
            if (activeCount <= 1)
                return Conflict(new { message = "The last active SuperAdmin cannot be removed." });
        }

        var hasPending = await context.SuperAdminChangeRequests.AnyAsync(item =>
            item.TargetUserId == target.Id && item.Status == "pending" && item.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (hasPending)
            return Conflict(new { message = "A pending SuperAdmin change already exists for this account." });

        var now = DateTime.UtcNow;
        var change = new SuperAdminChangeRequest
        {
            Action = action,
            TargetUserId = target.Id,
            RequestedByUserId = actorId.Value,
            Reason = reason,
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.Add(RequestLifetime)
        };
        context.SuperAdminChangeRequests.Add(change);
        await context.SaveChangesAsync(cancellationToken);
        context.AuditLogs.Add(CreateAudit(actorId.Value, "RequestSuperAdminChange", change.Id,
            null, $"action={action}; targetUserId={target.Id}; reason={reason}"));
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            data = new { change.Id, change.Action, change.TargetUserId, change.Status, change.CreatedAt, change.ExpiresAt },
            message = "A second, different SuperAdmin must approve this request within 24 hours."
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var actorId = GetCurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(actorId.Value, cancellationToken)) return Forbid();

        var requests = await context.SuperAdminChangeRequests.AsNoTracking()
            .Where(item => item.Status == "pending" && item.ExpiresAt > DateTime.UtcNow)
            .OrderBy(item => item.CreatedAt)
            .Take(100)
            .Join(context.Users.AsNoTracking(), item => item.TargetUserId, user => user.Id,
                (item, user) => new
                {
                    item.Id, item.Action, item.TargetUserId, targetEmail = user.Email,
                    targetName = user.FirstName + " " + user.LastName,
                    item.RequestedByUserId, item.Reason, item.CreatedAt, item.ExpiresAt
                })
            .ToListAsync(cancellationToken);
        return Ok(new { success = true, data = requests });
    }

    [HttpPost("{id:int}/decide")]
    public async Task<IActionResult> Decide(int id, [FromBody] DecideSuperAdminChangeRequest request, CancellationToken cancellationToken)
    {
        var reviewerId = GetCurrentUserId();
        if (reviewerId == null) return Unauthorized();
        if (!await IsFreshSuperAdminAsync(reviewerId.Value, cancellationToken)) return Forbid();

        var decision = request.Decision?.Trim().ToLowerInvariant();
        var decisionReason = request.Reason?.Trim();
        if (decision is not ("approve" or "reject"))
            return BadRequest(new { message = "Decision must be approve or reject." });
        if (string.IsNullOrWhiteSpace(decisionReason) || decisionReason.Length < 10 || decisionReason.Length > 500)
            return BadRequest(new { message = "A reason between 10 and 500 characters is required for the second-person decision." });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;

        // Re-check the approver inside the serializable transaction so a concurrent
        // demotion/revocation cannot race this privileged decision.
        if (!await IsFreshSuperAdminAsync(reviewerId.Value, cancellationToken)) return Forbid();

        var change = await context.SuperAdminChangeRequests.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (change == null) return NotFound();
        if (change.Status != "pending" || change.ExpiresAt <= DateTime.UtcNow)
            return Conflict(new { message = "This approval request has expired or has already been decided." });
        if (change.RequestedByUserId == reviewerId.Value || change.TargetUserId == reviewerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "The requester and target account cannot approve or reject their own access change. A separate SuperAdmin must decide." });

        var target = await context.Users.FirstOrDefaultAsync(user => user.Id == change.TargetUserId, cancellationToken);
        if (target == null || target.IsArchived)
            return Conflict(new { message = "The target account is no longer active. The request cannot be applied." });

        var now = DateTime.UtcNow;
        if (decision == "approve")
        {
            if (change.Action == "grant")
            {
                if (HasRole(target.Roles, "SuperAdmin") || !target.EmailVerified || !target.AuthenticatorTwoFactorEnabled)
                    return Conflict(new { message = "The target account no longer meets the SuperAdmin promotion requirements." });
                target.Roles = "Admin,SuperAdmin";
            }
            else
            {
                if (!HasRole(target.Roles, "SuperAdmin"))
                    return Conflict(new { message = "The target is no longer a SuperAdmin." });
                var activeCount = await CountActiveSuperAdminsAsync(cancellationToken);
                if (activeCount <= 1)
                    return Conflict(new { message = "The last active SuperAdmin cannot be removed." });
                target.Roles = "Admin";
            }
            target.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            change.Status = "approved";
        }
        else
        {
            change.Status = "rejected";
        }

        change.DecidedByUserId = reviewerId.Value;
        change.DecidedAt = now;
        context.AuditLogs.Add(CreateAudit(reviewerId.Value,
            decision == "approve" ? "ApproveSuperAdminChange" : "RejectSuperAdminChange",
            change.Id,
            $"pending; targetUserId={target.Id}; action={change.Action}",
            $"{change.Status}; reason={decisionReason}"));
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            data = new { change.Id, change.Status, change.DecidedAt },
            message = decision == "approve" ? "The SuperAdmin change has been applied." : "The SuperAdmin change request has been rejected."
        });
    }

    private async Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken)
    {
        var roles = await context.Users.AsNoTracking()
            .Where(user => !user.IsArchived && user.Roles != null && user.Roles.Contains("SuperAdmin"))
            .Select(user => user.Roles)
            .ToListAsync(cancellationToken);
        return roles.Count(value => HasRole(value, "SuperAdmin"));
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

    private AuditLog CreateAudit(int actorId, string action, int requestId, string? oldValues, string? newValues) => new()
    {
        UserId = actorId,
        Action = action,
        EntityType = "SuperAdminChangeRequest",
        EntityId = requestId,
        OldValues = oldValues,
        NewValues = newValues,
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        UserAgent = Request.Headers.UserAgent.ToString()
    };
}

public sealed class CreateSuperAdminChangeRequest
{
    public string Action { get; set; } = string.Empty;
    public int TargetUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class DecideSuperAdminChangeRequest
{
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
