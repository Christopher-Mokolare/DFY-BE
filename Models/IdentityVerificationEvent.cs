using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Minimal audit metadata for DFY's first-party manual identity-review workflow.
/// Raw identity-document bytes and decrypted payloads are never persisted in this audit table.
/// </summary>
public sealed class IdentityVerificationEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [Required, StringLength(24)]
    public string Provider { get; set; } = "DFY_MANUAL";

    [Required, StringLength(64)]
    public string EventHash { get; set; } = string.Empty;

    [StringLength(120)]
    public string? ProviderApplicantId { get; set; }

    [Required, StringLength(60)]
    public string EventType { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string ResultStatus { get; set; } = string.Empty;

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    // Actor and document references make sensitive access auditable without storing document bytes.
    public int? ActorUserId { get; set; }
    public int? SubmissionId { get; set; }

    [StringLength(8)]
    public string? DocumentSide { get; set; }

    public User User { get; set; } = null!;
}
