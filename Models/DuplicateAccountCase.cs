using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Tracks a possible duplicate-account investigation without merging, deleting,
/// suspending, or moving financial/KYC records between accounts.
/// </summary>
public sealed class DuplicateAccountCase
{
    public int Id { get; set; }
    public int PrimaryUserId { get; set; }
    public int CandidateUserId { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? EvidenceReference { get; set; }

    [MaxLength(40)]
    public string Status { get; set; } = "Open";

    [MaxLength(500)]
    public string? ResolutionNote { get; set; }

    public int CreatedByUserId { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}
