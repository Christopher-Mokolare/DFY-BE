using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Two-person, reasoned request to reverse one immutable journal.
/// It cannot edit balances or create arbitrary debit/credit entries.
/// </summary>
public sealed class FinancialReversalRequest
{
    public int Id { get; set; }
    public long OriginalJournalId { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(24)]
    public string Status { get; set; } = "Pending"; // Pending, Approved, Executing, Executed, Rejected, Expired, Failed

    [MaxLength(120)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public int RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }

    [MaxLength(500)]
    public string? DecisionReason { get; set; }

    public int? ExecutedByUserId { get; set; }
    public DateTime? ExecutedAt { get; set; }

    [MaxLength(500)]
    public string? FailureReason { get; set; }

    public FinancialJournal OriginalJournal { get; set; } = null!;
}
