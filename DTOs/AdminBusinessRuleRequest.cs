using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.DTOs;

public sealed class AdminBusinessRuleRequest
{
    [Required, StringLength(200)]
    public string RuleName { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string RuleType { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Entity { get; set; } = string.Empty;

    [Required, StringLength(4000)]
    public string Condition { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Action { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? ErrorMessage { get; set; }

    public bool IsActive { get; set; } = true;

    public int Priority { get; set; }

    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class AdminRuleReasonRequest
{
    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;
}
