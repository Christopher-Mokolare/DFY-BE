using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.DTOs;

public class CreateTaskRequest
{
    [Required]
    public string TaskDescription { get; set; } = string.Empty;
    
    [Required]
    public string Category { get; set; } = string.Empty;
    
    [Required]
    public string Area { get; set; } = string.Empty;
    
    [Required]
    public DateTime DateNeeded { get; set; }
    
    [Required, Range(50, 100000, ErrorMessage = "validation failed: minimum budget is R50")]
    public decimal Budget { get; set; }
    
    public string? Notes { get; set; }
    public string Priority { get; set; } = "Standard";
    public bool TermsAccepted { get; set; }
}

public class UpdateTaskRequest
{
    public string? TaskDescription { get; set; }
    public string? Category { get; set; }
    public string? Area { get; set; }
    public DateTime? DateNeeded { get; set; }
    public decimal? Budget { get; set; }
    public string? Notes { get; set; }
    public string? Priority { get; set; }
}

public class TaskDto
{
    public int Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserContact { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public string TaskDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public DateTime DateNeeded { get; set; }
    public decimal Budget { get; set; }
    public string? Notes { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public string TaskStatus { get; set; } = string.Empty;
    public string? HelperName { get; set; }
    public string? HelperContact { get; set; }
    public string Priority { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool CanAccept { get; set; } = true;
    
    // Additional fields for frontend compatibility
    public string Timestamp => CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public string Name => UserName; // Legacy compatibility
    public string Contact => UserContact; // Legacy compatibility
    public string Status => TaskStatus; // Legacy compatibility
}

public class ClaimTaskRequest
{
    [Required]
    public string HelperName { get; set; } = string.Empty;
    
    [Required]
    public string HelperContact { get; set; } = string.Empty;
}

public class UpdatePaymentStatusRequest
{
    [Required]
    public string PaymentStatus { get; set; } = string.Empty;
}

public class ApiResponse<T>
{
    public bool Success { get; set; } = true;
    public T? Data { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class PaginatedResponse<T>
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public List<T> Tasks { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    
    [Required]
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [Required]
    public string FirstName { get; set; } = string.Empty;
    
    [Required]
    public string LastName { get; set; } = string.Empty;
    
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    
    [Required]
    public string Password { get; set; } = string.Empty;
    
    [Required]
    public string? PhoneNumber { get; set; }
    
    [Required]
    public string UserType { get; set; } = string.Empty;
    
    public string? IdNumber { get; set; }
    
    [Required]
    public string Address { get; set; } = string.Empty;
    
    public DateTime? DateOfBirth { get; set; }
    
    public string? Username { get; set; }
}

public class AuthResponse
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public UserDto? User { get; set; }
    public string? Message { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Name => $"{FirstName} {LastName}"; // Computed field for frontend
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string Contact => PhoneNumber ?? Email; // Computed field for frontend
    public bool ProfileCompleted { get; set; }
    public int ProfileCompletion { get; set; }
    public decimal Rating { get; set; }
    public int CompletedTasks { get; set; }
    public string? Roles { get; set; }
    public string[] RolesArray => Roles?.Split(',') ?? new string[0]; // For frontend compatibility
    public bool IsAdmin { get; set; }
    public string? UserType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsVerified { get; set; }
}


public class WithdrawalRequestDto
{
    public decimal Amount { get; set; }
    public string? BankAccount { get; set; }
    public string? BankName { get; set; }
    public string? AccountHolder { get; set; }
    public string? BranchCode { get; set; }
    public string? AccountType { get; set; }
}
