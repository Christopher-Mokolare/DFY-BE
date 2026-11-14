using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// General-ledger account. This is an accounting control account, not a user wallet.
/// </summary>
public sealed class FinancialAccount
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(16)]
    public string AccountType { get; set; } = string.Empty; // Asset, Liability, Equity, Revenue, Expense

    [MaxLength(3)]
    public string Currency { get; set; } = "ZAR";

    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FinancialJournalLine> JournalLines { get; set; } = new List<FinancialJournalLine>();
}

/// <summary>
/// Immutable double-entry journal header. Historical payment, wallet and task totals
/// are deliberately not imported or treated as journal entries.
/// </summary>
public sealed class FinancialJournal
{
    public long Id { get; set; }

    [MaxLength(80)]
    public string JournalNumber { get; set; } = string.Empty;

    [MaxLength(40)]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(40)]
    public string SourceType { get; set; } = string.Empty;

    [MaxLength(120)]
    public string SourceId { get; set; } = string.Empty;

    [MaxLength(120)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(3)]
    public string Currency { get; set; } = "ZAR";

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? Provider { get; set; }

    [MaxLength(120)]
    public string? ProviderReference { get; set; }

    public long? ReversalOfJournalId { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FinancialJournalLine> Lines { get; set; } = new List<FinancialJournalLine>();
    public FinancialJournal? ReversalOfJournal { get; set; }
}

/// <summary>
/// One immutable debit or credit line. Exactly one side must be positive.
/// </summary>
public sealed class FinancialJournalLine
{
    public long Id { get; set; }
    public long JournalId { get; set; }
    public int LineNumber { get; set; }
    public int AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "ZAR";

    [MaxLength(240)]
    public string? Memo { get; set; }

    public FinancialJournal Journal { get; set; } = null!;
    public FinancialAccount Account { get; set; } = null!;
}
