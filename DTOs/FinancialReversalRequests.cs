namespace DoForYou.API.DTOs;

public sealed class FinancialReversalCreateRequest
{
    public long OriginalJournalId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class FinancialReversalDecisionRequest
{
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
