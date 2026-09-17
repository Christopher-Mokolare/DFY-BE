using Microsoft.AspNetCore.Mvc
using Microsoft.AspNetCore.Authorization
using Microsoft.EntityFrameworkCore
using DoForYou.API.Data
using DoForYou.API.DTOs
using DoForYou.API.Models
using System.Security.Claims
using System.Text.Json
using BCrypt.Net

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/user")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public UserController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    [HttpGet("profile")]
    public async Task<ActionResult<ApiResponse<object>>> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                id = user.Id,
                firstName = user.FirstName,
                lastName = user.LastName,
                email = user.Email,
                phoneNumber = user.PhoneNumber,
                address = user.Address,
                userType = user.UserType,
                idNumber = user.IdNumber,
                dateOfBirth = user.DateOfBirth,
                username = user.Username,
                profileCompleted = user.ProfileCompleted,
                profileCompletion = CalculateProfileCompletion(user),
                rating = user.Rating,
                completedTasks = user.CompletedTasks,
                isVerified = user.IsVerified,
                emailVerified = user.EmailVerified,
                phoneVerified = user.PhoneVerified,
                isAvailable = user.IsAvailable,
                bio = user.Bio,
                serviceCategories = user.ServiceCategories,
                coverageArea = user.CoverageArea,
                profilePhotoUrl = user.ProfilePhotoUrl
            }
        });
    }

    [HttpPost("profile")]
    [HttpPut("profile")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        if (!string.IsNullOrEmpty(request.PhoneNumber)) user.PhoneNumber = request.PhoneNumber;
        if (!string.IsNullOrEmpty(request.Address)) user.Address = request.Address;
        user.DateOfBirth = request.DateOfBirth.HasValue
            ? DateTime.SpecifyKind(request.DateOfBirth.Value, DateTimeKind.Utc)
            : null;
        if (!string.IsNullOrEmpty(request.IdNumber)) user.IdNumber = request.IdNumber;
        if (!string.IsNullOrEmpty(request.Username)) user.Username = request.Username;
        if (!string.IsNullOrEmpty(request.UserType)) user.UserType = request.UserType;
        user.ProfileCompleted = !string.IsNullOrEmpty(user.PhoneNumber)
            && !string.IsNullOrEmpty(user.Address)
            && !string.IsNullOrEmpty(user.IdNumber)
            && !string.IsNullOrEmpty(user.UserType);

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Profile updated successfully"
        });
    }

    [HttpGet("/api/v1/users/dashboard")]
    [HttpGet("/api/v1/users/dashboard/stats")]
    [HttpGet("dashboard/stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId);
        var activeTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        var completedTasks = await _context.Tasks.CountAsync(t => (t.CreatedByUserId == userId || t.AcceptedByUserId == userId) && t.TaskStatus == "Completed");
        var totalEarnings = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId && t.TaskStatus == "Completed")
            .SumAsync(t => t.Budget);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                postedTasks,
                activeTasks,
                completedTasks,
                totalEarnings
            }
        });
    }

    [HttpPost("availability")]
    public async Task<ActionResult<ApiResponse<bool>>> SetAvailability([FromBody] AvailabilityRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();
        user.IsAvailable = request.IsAvailable;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = request.IsAvailable, Message = $"Availability set to {request.IsAvailable}" });
    }

    [HttpPut("provider-profile")]
    [HttpPost("provider-profile")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProviderProfile([FromBody] ProviderProfileRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();
        if (request.Bio != null) user.Bio = request.Bio;
        if (request.ServiceCategories != null) user.ServiceCategories = request.ServiceCategories;
        if (request.CoverageArea != null) user.CoverageArea = request.CoverageArea;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Provider profile updated" });
    }

    [HttpPost("validate-id")]
    public ActionResult<ApiResponse<object>> ValidateId([FromBody] ValidateIdRequest request)
    {
        var id = request.IdNumber?.Trim() ?? "";
        if (id.Length != 13 || !id.All(char.IsDigit))
            return Ok(new ApiResponse<object> { Success = false, Message = "ID number must be 13 digits" });
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = id[i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        var valid = sum % 10 == 0;
        return Ok(new ApiResponse<object> { Success = valid, Data = new { valid }, Message = valid ? "Valid ID number" : "Invalid ID number" });
    }

    [HttpPost("/api/v1/auth/send-verification")]
    public async Task<ActionResult<ApiResponse<bool>>> SendVerification([FromBody] SendVerificationRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();
        var code = Random.Shared.Next(100000, 999999).ToString();
        user.PhoneVerificationCode = BCrypt.Net.BCrypt.HashPassword(code);
        user.PhoneVerificationExpiry = DateTime.UtcNow.AddMinutes(10);
        await _context.SaveChangesAsync();
        var isDev = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = isDev ? $"OTP: {code} (dev only)" : "Verification code sent"
        });
    }

    [HttpPost("/api/v1/auth/verify-phone")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyPhone([FromBody] VerifyPhoneRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();
        if (user.PhoneVerificationCode == null || user.PhoneVerificationExpiry < DateTime.UtcNow)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Code expired. Request a new one." });
        if (!BCrypt.Net.BCrypt.Verify(request.Code, user.PhoneVerificationCode))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid code" });
        user.PhoneVerified = true;
        user.IsVerified = true;
        user.PhoneVerificationCode = null;
        user.PhoneVerificationExpiry = null;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Phone verified successfully" });
    }

    [HttpGet("/api/v1/UserPreferences")]
    [HttpGet("preferences")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserPreferences()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        var userType = user?.UserType ?? "creator";
        
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                canCreateTasks = userType == "creator" || userType == "both",
                canAcceptTasks = userType == "runner" || userType == "both",
                taskCreatorNotifications = true,
                taskRunnerNotifications = true,
                paymentNotifications = true,
                emailNotifications = true,
                smsNotifications = false,
                minTaskAmount = 50,
                maxTaskAmount = 5000,
                preferredCategories = new string[] { },
                preferredLocations = new string[] { }
            }
        });
    }

    [HttpPut("/api/v1/UserPreferences")]
    [HttpPost("/api/v1/UserPreferences")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserPreferences([FromBody] UserPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        if (!string.IsNullOrEmpty(request.UserType))
        {
            user.UserType = request.UserType;
        }
        else if (request.CanCreateTasks && request.CanAcceptTasks)
            user.UserType = "both";
        else if (request.CanCreateTasks)
            user.UserType = "creator";
        else if (request.CanAcceptTasks)
            user.UserType = "runner";

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Preferences updated successfully"
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static int CalculateProfileCompletion(User user)
    {
        var fields = new[]
        {
            !string.IsNullOrEmpty(user.FirstName),
            !string.IsNullOrEmpty(user.LastName),
            !string.IsNullOrEmpty(user.PhoneNumber),
            !string.IsNullOrEmpty(user.Address),
            !string.IsNullOrEmpty(user.IdNumber),
            !string.IsNullOrEmpty(user.UserType),
            user.DateOfBirth.HasValue
        };
        return (int)Math.Round((double)fields.Count(f => f) / fields.Length * 100);
    }
}

public class UpdateProfileRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? IdNumber { get; set; }
    public string? Username { get; set; }
    public string? UserType { get; set; }
}

public class UserPreferencesRequest
{
    public bool CanCreateTasks { get; set; }
    public bool CanAcceptTasks { get; set; }
    public string? UserType { get; set; }
}
