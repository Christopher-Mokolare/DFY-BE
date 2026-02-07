using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

public class BusinessRule
{
    public int Id { get; set; }
    
    [Required]
    public string RuleName { get; set; } = string.Empty;
    
    [Required]
    public string RuleType { get; set; } = string.Empty; // validation, permission, calculation, workflow
    
    [Required]
    public string Entity { get; set; } = string.Empty; // User, Task, Payment, etc.
    
    [Required]
    public string Condition { get; set; } = string.Empty; // JSON condition
    
    [Required]
    public string Action { get; set; } = string.Empty; // allow, deny, validate, require
    
    public string? ErrorMessage { get; set; }
    
    public bool IsActive { get; set; } = true;
    
    public int Priority { get; set; } = 0;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RuleValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RuleName { get; set; }
}

public class RuleContext
{
    public User? CurrentUser { get; set; }
    public object? Entity { get; set; }
    public string? Action { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
}