using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Two-person approval record for granting or revoking SuperAdmin privileges.
/// </summary>
public sealed class SuperAdminChangeRequest
{
    public int Id { get; set; }

    [MaxLength(16)]
    public string Action { get; set; } = string.Empty; // grant or revoke

    public int TargetUserId { get; set; }
    public int RequestedByUserId { get; set; }
    public int? DecidedByUserId { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(24)]
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
