using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

public class Task
{
    public int Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string TaskDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public DateTime DateNeeded { get; set; }
    [Required]
    [Range(50, 10000)]
    public decimal Budget { get; set; }
    public string? Notes { get; set; }
    public string PaymentStatus { get; set; } = "Pending";
    public string TaskStatus { get; set; } = "PendingPayment";
    public string Priority { get; set; } = "Standard";
    public int CreatedByUserId { get; set; }
    public int? AcceptedByUserId { get; set; }
    public string? HelperName { get; set; }
    public string? HelperContact { get; set; }
    public string? HelperEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidToRunnerAt { get; set; }

    // Navigation properties
    public User CreatedByUser { get; set; } = null!;
    public User? AcceptedByUser { get; set; }
}