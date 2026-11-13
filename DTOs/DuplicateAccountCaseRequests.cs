using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.DTOs;

public sealed class CreateDuplicateAccountCaseRequest
{
    [Range(1, int.MaxValue)]
    public int PrimaryUserId { get; set; }

    [Range(1, int.MaxValue)]
    public int CandidateUserId { get; set; }

    [Required, StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? EvidenceReference { get; set; }
}

public sealed class ResolveDuplicateAccountCaseRequest
{
    [Required, StringLength(40)]
    public string Outcome { get; set; } = string.Empty;

    [Required, StringLength(500, MinimumLength = 10)]
    public string ResolutionNote { get; set; } = string.Empty;
}
