using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Stores encrypted runner identity-document evidence for DFY's internal review.
/// Plaintext document bytes are never persisted.
/// </summary>
public sealed class IdentityVerificationSubmission
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [Required, StringLength(32)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string Status { get; set; } = "pending";

    [Required]
    public byte[] FrontCiphertext { get; set; } = [];

    [Required]
    public byte[] FrontNonce { get; set; } = [];

    [Required]
    public byte[] FrontTag { get; set; } = [];

    [Required, StringLength(80)]
    public string FrontContentType { get; set; } = "image/jpeg";

    public byte[]? BackCiphertext { get; set; }
    public byte[]? BackNonce { get; set; }
    public byte[]? BackTag { get; set; }

    [StringLength(80)]
    public string? BackContentType { get; set; }

    [Required, StringLength(40)]
    public string ConsentVersion { get; set; } = string.Empty;

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedByUserId { get; set; }

    [StringLength(1000)]
    public string? ReviewReason { get; set; }

    public DateTime? DocumentsDeleteAfter { get; set; }

    public User User { get; set; } = null!;
    public User? ReviewedByUser { get; set; }
}
