using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/duplicate-account-cases")]
[Authorize(Roles = "Admin")]
[Authorize(Policy = "AdminMfa")]
[Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
public sealed class DuplicateAccountCasesController(AppDbContext context) : ControllerBase
{
    private static readonly string[] AllowedOutcomes =
        ["ConfirmedDuplicate", "FalsePositive", "InsufficientEvidence"];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = context.DuplicateAccountCases.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status is not ("Open" or "ConfirmedDuplicate" or "FalsePositive" or "InsufficientEvidence"))
                return BadRequest(new { message = "Unsupported duplicate investigation status." });
            query = query.Where(item => item.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var cases = await query.OrderByDescending(item => item.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new
            {
                item.Id, item.PrimaryUserId, item.CandidateUserId, item.Reason,
                item.EvidenceReference, item.Status, item.ResolutionNote,
                item.CreatedByUserId, item.DecidedByUserId, item.CreatedAt,
                item.UpdatedAt, item.ResolvedAt
            }).ToListAsync(cancellationToken);

        return Ok(new { success = true, data = new { cases, total, page, pageSize } });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateDuplicateAccountCaseRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        if (request.PrimaryUserId == request.CandidateUserId)
            return BadRequest(new { message = "A duplicate investigation must reference two different accounts." });

        var evidenceReference = request.EvidenceReference?.Trim();
        if (!string.IsNullOrEmpty(evidenceReference) &&
            Regex.IsMatch(evidenceReference, @"(?<!\d)\d{13}(?!\d)"))
            return BadRequest(new { message = "Do not submit raw identity numbers or document contents; use a protected evidence reference." });

        var accounts = await context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(user => user.Id == request.PrimaryUserId || user.Id == request.CandidateUserId)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        if (accounts.Count != 2)
            return NotFound(new { message = "Both account records must exist before an investigation can be opened." });

        var existing = await context.DuplicateAccountCases.AnyAsync(item =>
            item.Status == "Open" &&
            ((item.PrimaryUserId == request.PrimaryUserId && item.CandidateUserId == request.CandidateUserId) ||
             (item.PrimaryUserId == request.CandidateUserId && item.CandidateUserId == request.PrimaryUserId)),
            cancellationToken);
        if (existing)
            return Conflict(new { message = "An open investigation already exists for these two accounts." });

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;

        var now = DateTime.UtcNow;
        var investigation = new DuplicateAccountCase
        {
            PrimaryUserId = request.PrimaryUserId,
            CandidateUserId = request.CandidateUserId,
            Reason = request.Reason.Trim(),
            EvidenceReference = evidenceReference,
            Status = "Open",
            CreatedByUserId = actorId.Value,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.DuplicateAccountCases.Add(investigation);
        await context.SaveChangesAsync(cancellationToken);
        context.AuditLogs.Add(CreateAudit(actorId.Value, "OpenDuplicateAccountInvestigation",
            investigation.Id, null,
            JsonSerializer.Serialize(new
            {
                investigation.PrimaryUserId, investigation.CandidateUserId,
                investigation.Status, investigation.Reason, investigation.EvidenceReference
            })));
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            data = new { investigation.Id, investigation.PrimaryUserId, investigation.CandidateUserId, investigation.Status },
            message = "Investigation opened. No accounts were merged, suspended, deleted, or modified."
        });
    }

    [HttpPost("{id:int}/resolve")]
    public async Task<IActionResult> Resolve(
        int id,
        [FromBody] ResolveDuplicateAccountCaseRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        if (!AllowedOutcomes.Contains(request.Outcome, StringComparer.Ordinal))
            return BadRequest(new { message = "Choose ConfirmedDuplicate, FalsePositive or InsufficientEvidence." });

        var investigation = await context.DuplicateAccountCases
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (investigation == null) return NotFound();
        if (investigation.Status != "Open")
            return Conflict(new { message = "This investigation has already been resolved." });
        if (investigation.CreatedByUserId == actorId.Value)
            return Conflict(new { message = "The investigation creator cannot make the final decision; an independent administrator is required." });

        var oldValues = JsonSerializer.Serialize(new { investigation.Status, investigation.ResolutionNote });
        var now = DateTime.UtcNow;
        investigation.Status = request.Outcome;
        investigation.ResolutionNote = request.ResolutionNote.Trim();
        investigation.DecidedByUserId = actorId.Value;
        investigation.ResolvedAt = now;
        investigation.UpdatedAt = now;
        context.AuditLogs.Add(CreateAudit(actorId.Value, "ResolveDuplicateAccountInvestigation",
            investigation.Id, oldValues,
            JsonSerializer.Serialize(new { investigation.Status, investigation.ResolutionNote })));
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            data = new { investigation.Id, investigation.Status, investigation.DecidedByUserId, investigation.ResolvedAt },
            message = "Investigation resolved. Account records and financial/KYC references were not merged or changed."
        });
    }

    private AuditLog CreateAudit(int actorId, string action, int caseId, string? oldValues, string newValues) => new()
    {
        UserId = actorId,
        Action = action,
        EntityType = "DuplicateAccountCase",
        EntityId = caseId,
        OldValues = oldValues,
        NewValues = newValues,
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        UserAgent = Request.Headers.UserAgent.ToString()
    };

    private int? CurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var id) ? id : null;
    }
}
