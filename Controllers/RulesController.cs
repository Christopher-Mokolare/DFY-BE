using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/rules")]
[Authorize(Roles = "Admin")]
public class RulesController : ControllerBase
{
    private readonly AppDbContext _context;

    public RulesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<BusinessRule>>>> GetRules(
        [FromQuery] string? entity = null,
        [FromQuery] string? ruleType = null)
    {
        var query = _context.BusinessRules.AsQueryable();

        if (!string.IsNullOrEmpty(entity))
            query = query.Where(r => r.Entity == entity);

        if (!string.IsNullOrEmpty(ruleType))
            query = query.Where(r => r.RuleType == ruleType);

        var rules = await query.OrderBy(r => r.Entity).ThenBy(r => r.Priority).ToListAsync();

        return Ok(new ApiResponse<List<BusinessRule>>
        {
            Success = true,
            Data = rules
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> GetRule(int id)
    {
        var rule = await _context.BusinessRules.FindAsync(id);
        if (rule == null)
            return NotFound(new ApiResponse<BusinessRule> { Success = false, Message = "Rule not found" });

        return Ok(new ApiResponse<BusinessRule>
        {
            Success = true,
            Data = rule
        });
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> CreateRule([FromBody] BusinessRule rule)
    {
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;

        _context.BusinessRules.Add(rule);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<BusinessRule>
        {
            Success = true,
            Data = rule,
            Message = "Rule created successfully"
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<BusinessRule>>> UpdateRule(int id, [FromBody] BusinessRule rule)
    {
        var existingRule = await _context.BusinessRules.FindAsync(id);
        if (existingRule == null)
            return NotFound(new ApiResponse<BusinessRule> { Success = false, Message = "Rule not found" });

        existingRule.RuleName = rule.RuleName;
        existingRule.RuleType = rule.RuleType;
        existingRule.Entity = rule.Entity;
        existingRule.Condition = rule.Condition;
        existingRule.Action = rule.Action;
        existingRule.ErrorMessage = rule.ErrorMessage;
        existingRule.IsActive = rule.IsActive;
        existingRule.Priority = rule.Priority;
        existingRule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<BusinessRule>
        {
            Success = true,
            Data = existingRule,
            Message = "Rule updated successfully"
        });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteRule(int id)
    {
        var rule = await _context.BusinessRules.FindAsync(id);
        if (rule == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Rule not found" });

        _context.BusinessRules.Remove(rule);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Rule deleted successfully"
        });
    }

    [HttpPost("{id}/toggle")]
    public async Task<ActionResult<ApiResponse<bool>>> ToggleRule(int id)
    {
        var rule = await _context.BusinessRules.FindAsync(id);
        if (rule == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Rule not found" });

        rule.IsActive = !rule.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = $"Rule {(rule.IsActive ? "activated" : "deactivated")} successfully"
        });
    }
}