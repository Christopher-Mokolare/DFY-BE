using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Historical, individually assignable administrator permission grant.
/// Revocation is represented by a timestamp; grants are never hard-deleted.
/// </summary>
public sealed class AdminPermissionGrant
{
    public int Id { get; set; }

    public int UserId { get; set; }

    [MaxLength(80)]
    public string PermissionKey { get; set; } = string.Empty;

    public int GrantedByUserId { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public int? RevokedByUserId { get; set; }
}
