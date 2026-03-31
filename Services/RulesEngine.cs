using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Reflection;

namespace DoForYou.API.Services;

public interface IRulesEngine
{
    Task<List<RuleValidationResult>> ValidateAsync(string entity, string ruleType, RuleContext context);
    Task<bool> CanPerformActionAsync(string entity, string action, RuleContext context);
    Task<RuleValidationResult> ValidateEntityAsync(object entity, RuleContext context);
}

public class RulesEngine : IRulesEngine
{
    private readonly AppDbContext _context;

    public RulesEngine(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<RuleValidationResult>> ValidateAsync(string entity, string ruleType, RuleContext context)
    {
        var rules = await _context.BusinessRules
            .Where(r => r.Entity == entity && r.RuleType == ruleType && r.IsActive)
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        var results = new List<RuleValidationResult>();

        foreach (var rule in rules)
        {
            var result = await EvaluateRuleAsync(rule, context);
            results.Add(result);
        }

        return results;
    }

    public async Task<bool> CanPerformActionAsync(string entity, string action, RuleContext context)
    {
        context.Action = action;
        var results = await ValidateAsync(entity, "permission", context);
        
        // If any rule explicitly denies, return false
        if (results.Any(r => !r.IsValid && r.RuleName?.Contains("Cannot") == true))
            return false;
            
        // If any rule explicitly allows, return true
        if (results.Any(r => r.IsValid))
            return true;
            
        // Default to true — allow unless explicitly denied
        return true;
    }

    public async Task<RuleValidationResult> ValidateEntityAsync(object entity, RuleContext context)
    {
        var entityType = entity.GetType().Name;
        var results = await ValidateAsync(entityType, "validation", context);
        
        var failedRule = results.FirstOrDefault(r => !r.IsValid);
        if (failedRule != null)
        {
            return failedRule;
        }

        return new RuleValidationResult { IsValid = true };
    }

    private async Task<RuleValidationResult> EvaluateRuleAsync(BusinessRule rule, RuleContext context)
    {
        try
        {
            var condition = JsonSerializer.Deserialize<Dictionary<string, object>>(rule.Condition);
            if (condition == null)
            {
                return new RuleValidationResult 
                { 
                    IsValid = false, 
                    ErrorMessage = "Invalid rule condition",
                    RuleName = rule.RuleName 
                };
            }

            bool isValid = await EvaluateConditionAsync(condition, context);

            // For deny actions, invert the result
            if (rule.Action == "deny")
                isValid = !isValid;

            return new RuleValidationResult
            {
                IsValid = isValid,
                ErrorMessage = isValid ? null : rule.ErrorMessage,
                RuleName = rule.RuleName
            };
        }
        catch (Exception ex)
        {
            return new RuleValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Rule evaluation error: {ex.Message}",
                RuleName = rule.RuleName
            };
        }
    }

    private async Task<bool> EvaluateConditionAsync(Dictionary<string, object> condition, RuleContext context)
    {
        foreach (var kvp in condition)
        {
            var key = kvp.Key;
            var expectedValue = kvp.Value;

            switch (key)
            {
                case "userType":
                    if (!EvaluateUserType(expectedValue, context.CurrentUser))
                        return false;
                    break;

                case "profileCompleted":
                    if (!EvaluateProfileCompleted(expectedValue, context.CurrentUser))
                        return false;
                    break;

                case "isVerified":
                    if (!EvaluateIsVerified(expectedValue, context.CurrentUser))
                        return false;
                    break;

                case "roles":
                    if (!EvaluateRoles(expectedValue, context.CurrentUser))
                        return false;
                    break;

                case "budget":
                    if (!EvaluateBudget(expectedValue, context))
                        return false;
                    break;

                case "required":
                    if (!EvaluateRequiredFields(expectedValue, context))
                        return false;
                    break;

                case "createdByUserId":
                    if (!EvaluateUserIdComparison(expectedValue, context))
                        return false;
                    break;

                default:
                    // Handle custom conditions
                    if (!EvaluateCustomCondition(key, expectedValue, context))
                        return false;
                    break;
            }
        }

        return true;
    }

    private bool EvaluateUserType(object expectedValue, User? user)
    {
        if (user?.UserType == null) return false;

        if (expectedValue is JsonElement jsonArray && jsonArray.ValueKind == JsonValueKind.Array)
        {
            var allowedTypes = jsonArray.EnumerateArray().Select(x => x.GetString()).ToList();
            return allowedTypes.Contains(user.UserType);
        }

        return expectedValue.ToString() == user.UserType;
    }

    private bool EvaluateProfileCompleted(object expectedValue, User? user)
    {
        if (user == null) return false;
        return user.ProfileCompleted == Convert.ToBoolean(expectedValue);
    }

    private bool EvaluateIsVerified(object expectedValue, User? user)
    {
        if (user == null) return false;
        return user.IsVerified == Convert.ToBoolean(expectedValue);
    }

    private bool EvaluateRoles(object expectedValue, User? user)
    {
        if (user?.Roles == null) return false;

        if (expectedValue is JsonElement jsonArray && jsonArray.ValueKind == JsonValueKind.Array)
        {
            var requiredRoles = jsonArray.EnumerateArray().Select(x => x.GetString()).ToList();
            return requiredRoles.Any(role => user.Roles.Contains(role ?? ""));
        }

        return user.Roles.Contains(expectedValue.ToString() ?? "");
    }

    private bool EvaluateBudget(object expectedValue, RuleContext context)
    {
        if (context.Entity == null) return false;

        var budgetProperty = context.Entity.GetType().GetProperty("Budget");
        if (budgetProperty == null) return false;

        var budget = (decimal?)budgetProperty.GetValue(context.Entity);
        if (budget == null) return false;

        if (expectedValue is JsonElement jsonObj && jsonObj.ValueKind == JsonValueKind.Object)
        {
            if (jsonObj.TryGetProperty("min", out var minElement))
            {
                var minValue = minElement.GetDecimal();
                if (budget < minValue) return false;
            }

            if (jsonObj.TryGetProperty("max", out var maxElement))
            {
                var maxValue = maxElement.GetDecimal();
                if (budget > maxValue) return false;
            }
        }

        return true;
    }

    private bool EvaluateRequiredFields(object expectedValue, RuleContext context)
    {
        if (context.Entity == null) return false;

        if (expectedValue is JsonElement jsonArray && jsonArray.ValueKind == JsonValueKind.Array)
        {
            var requiredFields = jsonArray.EnumerateArray().Select(x => x.GetString()).ToList();
            var entityType = context.Entity.GetType();

            foreach (var fieldName in requiredFields)
            {
                var property = entityType.GetProperty(fieldName ?? "", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return false;

                var value = property.GetValue(context.Entity);
                if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
                    return false;
            }
        }

        return true;
    }

    private bool EvaluateUserIdComparison(object expectedValue, RuleContext context)
    {
        if (context.CurrentUser == null || context.Entity == null) return false;

        var createdByProperty = context.Entity.GetType().GetProperty("CreatedByUserId");
        if (createdByProperty == null) return false;

        var createdByUserId = (int?)createdByProperty.GetValue(context.Entity);
        var currentUserId = context.CurrentUser.Id;

        var comparison = expectedValue.ToString();
        return comparison switch
        {
            "!=currentUserId" => createdByUserId != currentUserId,
            "==currentUserId" => createdByUserId == currentUserId,
            _ => false
        };
    }

    private bool EvaluateCustomCondition(string key, object expectedValue, RuleContext context)
    {
        // Handle custom business logic here
        // This can be extended for specific business rules
        return true;
    }
}