using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Single-use, time-limited invitation for a verified DFY account to receive Admin access.
/// Invitations never contain a password or grant SuperAdmin access.
/// </summary>
public sealed class AdminInvitation
{
    public int Id { get; set; }

    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(320)]
    public string NormalizedEmail { get; set; } = string.Empty;

    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public int? AcceptedByUserId { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
