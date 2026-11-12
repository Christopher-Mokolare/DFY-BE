using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Durable, at-least-once delivery record for manual identity-review notifications.
/// Each recipient/channel has its own idempotency key and bounded retry lifecycle.
/// </summary>
public sealed class IdentityNotificationOutbox
{
    public long Id { get; set; }

    [Required, StringLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(16)]
    public string Channel { get; set; } = "in_app";

    public int? RecipientUserId { get; set; }

    [StringLength(320)]
    public string? RecipientEmail { get; set; }

    [Required, StringLength(80)]
    public string Type { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(2000)]
    public string Message { get; set; } = string.Empty;

    [StringLength(32)]
    public string Status { get; set; } = "pending";

    public int AttemptCount { get; set; }

    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;

    public DateTime? LeaseExpiresAt { get; set; }

    [StringLength(500)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeliveredAt { get; set; }
}
