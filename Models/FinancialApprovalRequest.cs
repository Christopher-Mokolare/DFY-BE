using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Two-person approval record for exceptional escrow release. Approval and execution
/// are separate actions; this record is not itself a payment or payout instruction.
/// </summary>
public sealed class FinancialApprovalRequest
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Action { get; set; } = "force_escrow_release";

    public int TaskId { get; set; }

    public int? DisputeId { get; set; }

    [MaxLength(120)]
    public string PaymentTransactionId { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(24)]
    public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Executing, Executed, Failed, Expired

    [MaxLength(80)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public int RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }

    [MaxLength(500)]
    public string? DecisionReason { get; set; }

    public DateTime? ExecutionStartedAt { get; set; }
    public int ExecutionAttempts { get; set; }
    public DateTime? ExecutedAt { get; set; }

    [MaxLength(1000)]
    public string? LastExecutionError { get; set; }
}
