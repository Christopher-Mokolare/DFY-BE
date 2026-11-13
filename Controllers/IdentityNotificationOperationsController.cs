using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DoForYou.API.Data;
using DoForYou.API.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

/// <summary>
/// Operational visibility and controlled recovery for identity notification dead letters.
/// Payload content and recipient addresses are deliberately never returned.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
[Authorize(Policy = AdminPermissionPolicies.ConfigurationManagePolicy)]
[Route("api/v1/admin/identity-notifications")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IdentityNotificationOperationsController(AppDbContext context) : ControllerBase
{
    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 10000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = context.IdentityNotificationOutbox.AsNoTracking()
            .Where(item => item.Status == "dead_letter");

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.Id,
                item.Type,
                item.Channel,
                item.Status,
                item.AttemptCount,
                item.AvailableAt,
                item.LastError,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items
        });
    }

    [HttpPost("{id:long}/replay")]
    public async Task<IActionResult> ReplayDeadLetter(
        long id,
        [FromBody] ReplayDeadLetterRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var actorValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(actorValue, out var actorId))
            return Unauthorized();

        var operatorAccount = await context.Users.AsNoTracking()
            .Where(user => user.Id == actorId && !user.IsArchived)
            .Select(user => new { user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (operatorAccount?.AuthenticatorTwoFactorEnabled != true ||
            !operatorAccount.LastTwoFactorVerifiedAt.HasValue ||
            operatorAccount.LastTwoFactorVerifiedAt.Value < DateTime.UtcNow.AddMinutes(-15))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Fresh authenticator verification is required for dead-letter replay." });

        var reason = request.Reason.Trim();
        if (reason.Length is < 10 or > 500)
            return BadRequest(new { message = "Provide an operational reason between 10 and 500 characters." });

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var updated = await context.IdentityNotificationOutbox
            .Where(item => item.Id == id && item.Status == "dead_letter")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "pending")
                .SetProperty(item => item.AvailableAt, now)
                .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.LastError, (string?)null),
                cancellationToken);

        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound(new { message = "Dead-letter notification not found or already replayed." });
        }

        context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId,
            Action = "identity_notification_dead_letter_replayed",
            EntityType = "IdentityNotificationOutbox",
            EntityId = checked((int)Math.Min(id, int.MaxValue)),
            OldValues = "{\"status\":\"dead_letter\"}",
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "pending",
                reason,
                correlationId = HttpContext.TraceIdentifier
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Accepted(new { id, status = "pending", message = "Notification queued for retry." });
    }
}

public sealed class ReplayDeadLetterRequest
{
    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;
}
