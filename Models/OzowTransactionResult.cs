namespace DoForYou.API.Models;

public sealed record OzowTransactionResult(
    bool Success,
    string? TransactionId,
    string? Status,
    decimal? Amount,
    string? TransactionReference,
    string? Error)
{
    public bool Found =>
        Success &&
        (!string.IsNullOrWhiteSpace(TransactionId) ||
         !string.IsNullOrWhiteSpace(TransactionReference));
}
