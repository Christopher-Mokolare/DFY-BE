using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Authorization;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/rules")]
[Authorize(Roles = "Admin")]
[Authorize(Policy = "AdminMfa")]
[Authorize(Policy = AdminPermissionPolicies.ConfigurationManagePolicy)]
public class RulesController(AppDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<BusinessRule>>>> GetRules(
        [FromQuery] string? entity = null,
        [FromQuery] string? ruleType = null)
    {
        var query = context.BusinessRules.AsQueryable();
        if (!string.IsNullOrEmpty(entity)) query = query.Where(rule => rule.Entity == entity);
        if (!string.IsNullOrEmpty(ruleType)) query = query.Where(rule => rule.RuleType == ruleType);
        var rules = await query.OrderBy(rule => rule.Entity).ThenBy(rule => rule.Priority).ToListAsync();
        return Ok(new ApiResponse<List<BusinessRule>> { Success = true, Data = rules });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> GetRule(int id)
    {
        var rule = await context.BusinessRules.FindAsync(id);
        if (rule == null)
            return NotFound(new ApiResponse<BusinessRule> { Success = false, Message = "Rule not found" });
        return Ok(new ApiResponse<BusinessRule> { Success = true, Data = rule });
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> CreateRule(
        [FromBody] AdminBusinessRuleRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var now = DateTime.UtcNow;
        var rule = new BusinessRule
        {
            RuleName = request.RuleName.Trim(),
            RuleType = request.RuleType.Trim(),
            Entity = request.Entity.Trim(),
            Condition = request.Condition.Trim(),
            Action = request.Action.Trim(),
            ErrorMessage = request.ErrorMessage?.Trim(),
            IsActive = request.IsActive,
            Priority = request.Priority,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.BusinessRules.Add(rule);
        await context.SaveChangesAsync(cancellationToken);
        context.AuditLogs.Add(CreateAudit(actorId.Value, "CreateBusinessRule", rule.Id, null,
            JsonSerializer.Serialize(new { rule.RuleName, rule.RuleType, rule.Entity, rule.Condition, rule.Action, rule.ErrorMessage, rule.IsActive, rule.Priority, reason = request.Reason.Trim() })));
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);

        return Ok(new ApiResponse<BusinessRule> { Success = true, Data = rule, Message = "Rule created and audited successfully." });
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> UpdateRule(
        int id,
        [FromBody] AdminBusinessRuleRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        var rule = await context.BusinessRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return NotFound(new ApiResponse<BusinessRule> { Success = false, Message = "Rule not found" });

        var oldValues = JsonSerializer.Serialize(new
        {
            rule.RuleName, rule.RuleType, rule.Entity, rule.Condition, rule.Action,
            rule.ErrorMessage, rule.IsActive, rule.Priority
        });
        rule.RuleName = request.RuleName.Trim();
        rule.RuleType = request.RuleType.Trim();
        rule.Entity = request.Entity.Trim();
        rule.Condition = request.Condition.Trim();
        rule.Action = request.Action.Trim();
        rule.ErrorMessage = request.ErrorMessage?.Trim();
        rule.IsActive = request.IsActive;
        rule.Priority = request.Priority;
        rule.UpdatedAt = DateTime.UtcNow;

        context.AuditLogs.Add(CreateAudit(actorId.Value, "UpdateBusinessRule", rule.Id, oldValues,
            JsonSerializer.Serialize(new
            {
                rule.RuleName, rule.RuleType, rule.Entity, rule.Condition, rule.Action,
                rule.ErrorMessage, rule.IsActive, rule.Priority, reason = request.Reason.Trim()
            })));
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<BusinessRule> { Success = true, Data = rule, Message = "Rule updated and audited successfully." });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteRule(
        int id,
        [FromBody] AdminRuleReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        var rule = await context.BusinessRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Rule not found" });
        if (!rule.IsActive)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Rule is already inactive." });

        var oldValues = JsonSerializer.Serialize(new { rule.RuleName, rule.RuleType, rule.Entity, rule.Condition, rule.Action, rule.IsActive, rule.Priority });
        rule.IsActive = false;
        rule.UpdatedAt = DateTime.UtcNow;
        context.AuditLogs.Add(CreateAudit(actorId.Value, "DeactivateBusinessRule", rule.Id, oldValues,
            JsonSerializer.Serialize(new { rule.IsActive, reason = request.Reason.Trim() })));
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Rule deactivated; its history has been retained." });
    }

    [HttpPost("{id:int}/toggle")]
    public async Task<ActionResult<ApiResponse<bool>>> ToggleRule(
        int id,
        [FromBody] AdminRuleReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (actorId == null) return Unauthorized();
        var rule = await context.BusinessRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Rule not found" });

        var oldValue = rule.IsActive;
        rule.IsActive = !rule.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;
        context.AuditLogs.Add(CreateAudit(actorId.Value, "ToggleBusinessRule", rule.Id,
            JsonSerializer.Serialize(new { isActive = oldValue }),
            JsonSerializer.Serialize(new { isActive = rule.IsActive, reason = request.Reason.Trim() })));
        await context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<bool>
        {
            Success = true, Data = true,
            Message = $"Rule {(rule.IsActive ? "activated" : "deactivated")} and audited successfully."
        });
    }

    private AuditLog CreateAudit(int actorId, string action, int ruleId, string? oldValues, string newValues) => new()
    {
        UserId = actorId,
        Action = action,
        EntityType = "BusinessRule",
        EntityId = ruleId,
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
