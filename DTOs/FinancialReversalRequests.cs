using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.DTOs;

public sealed class FinancialReversalCreateRequest
{
    [Range(1, long.MaxValue)]
    public long OriginalJournalId { get; set; }

    [Required, MinLength(10), MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class FinancialReversalDecisionRequest
{
    [Required, RegularExpression("^(approve|reject)$"), MaxLength(7)]
    public string Decision { get; set; } = string.Empty;

    [Required, MinLength(10), MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}
