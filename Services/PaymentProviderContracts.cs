using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record PaymentCreationResult(
    bool Success,
    string? PaymentUrl,
    string? TransactionReference,
    string? Error,
    string? Currency = null);

public record PaymentVerificationResult(
    bool Success,
    bool Found,
    string? TransactionId,
    string? Status,
    decimal? Amount,
    string? TransactionReference,
    string? Error,
    string? Currency = null);

public record RefundResult(
    bool Success,
    string? RefundId,
    string? TransactionId,
    decimal? Amount,
    string? Status,
    string? Error);

public record RefundStatusResult(
    string RefundId,
    string TransactionId,
    decimal Amount,
    string Status);

public record PayoutSubmissionResult(
    bool Success,
    string? ProviderReference,
    bool Retryable,
    string? Error);

public record PayoutStatusResult(
    bool Success,
    bool Found,
    string? ProviderReference,
    string? Status,
    string? Error);

public record SupportedBank(
    string Code,
    string Name,
    string? BranchCode = null);

public record BankVerificationResultV2(
    bool Success,
    string Message,
    string? ProviderReference = null);

public sealed record PaystackSettlementRecord(
    string Id,
    string Status,
    string Currency,
    decimal TotalAmount,
    decimal EffectiveAmount,
    decimal TotalFees,
    decimal TotalProcessed,
    DateTimeOffset? SettlementDate,
    string Domain);

public sealed record PaystackSettlementTransaction(
    string SettlementId,
    string Reference,
    string Status,
    string Currency,
    decimal Amount,
    decimal? Fees,
    string? TransactionId,
    DateTimeOffset? PaidAt);

public sealed record PaystackSettlementReadResult(
    bool Success,
    bool IsComplete,
    IReadOnlyList<PaystackSettlementRecord> Settlements,
    IReadOnlyList<PaystackSettlementTransaction> Transactions,
    string? Error);

public sealed record PaystackTransferRecord(
    string Id,
    string TransferCode,
    string Reference,
    string Status,
    string Currency,
    decimal Amount,
    decimal? Fees,
    DateTimeOffset? TransferredAt,
    string Domain);

public sealed record PaystackTransferReadResult(
    bool Success,
    bool IsComplete,
    IReadOnlyList<PaystackTransferRecord> Transfers,
    string? Error);

public sealed record PaystackDisputeRecord(
    string Id,
    string Status,
    string Category,
    string TransactionId,
    string TransactionReference,
    string Currency,
    decimal Amount,
    decimal? RefundAmount,
    DateTimeOffset? DueAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string Domain);

public sealed record PaystackDisputeReadResult(
    bool Success,
    bool IsComplete,
    IReadOnlyList<PaystackDisputeRecord> Disputes,
    string? Error);

public interface IPaystackSettlementProvider
{
    Task<PaystackSettlementReadResult> GetSettlementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int maxSettlements = 25,
        CancellationToken cancellationToken = default);

    Task<PaystackTransferReadResult> GetTransfersAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPages = 20,
        int perPage = 100,
        CancellationToken cancellationToken = default);
    Task<PaystackDisputeReadResult> GetDisputesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPages = 20,
        int perPage = 100,
        CancellationToken cancellationToken = default);

}

public interface IPaymentProvider
{
    Task<PaymentCreationResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default);

    Task<PaymentVerificationResult> VerifyPaymentAsync(
        string transactionReference,
        CancellationToken cancellationToken = default);

    Task<RefundResult> CreateRefundAsync(
        string transactionId,
        decimal amount,
        string reason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefundStatusResult>> GetRefundsByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    bool VerifyWebhookSignature(string rawBody, string? signature, bool isTimestampedSignature = false);
}

public interface IPayoutProvider
{
    Task<PayoutSubmissionResult> CreatePayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);

    Task<PayoutStatusResult> GetPayoutStatusAsync(
        Payout payout,
        CancellationToken cancellationToken = default);
}

public interface IBankProvider
{
    Task<IReadOnlyList<SupportedBank>> GetSupportedBanksAsync(
        CancellationToken cancellationToken = default);

    Task<BankVerificationResultV2> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}
