using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/user")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly AppDbContext _context;
    public UserController(AppDbContext context) => _context = context;

    [HttpGet("profile")]
    public async Task<ActionResult<ApiResponse<object>>> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        return Ok(new ApiResponse<object> {
            Success = true,
            Data = new {
                id = user.Id, firstName = user.FirstName, lastName = user.LastName, email = user.Email,
                phoneNumber = user.PhoneNumber, address = user.Address, userType = user.UserType,
                idNumber = user.IdNumber, dateOfBirth = user.DateOfBirth, username = user.Username,
                profileCompleted = IsProfileComplete(user), profileCompletion = CalculateProfileCompletion(user),
                rating = user.Rating, completedTasks = user.CompletedTasks, isVerified = user.IsVerified,
                emailVerified = user.EmailVerified, phoneVerified = user.PhoneVerified, isAvailable = user.IsAvailable,
                bio = user.Bio, serviceCategories = user.ServiceCategories, coverageArea = user.CoverageArea,
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

        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "First name and last name are required." });

        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && !IsValidPhone(request.PhoneNumber))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Enter a valid South African phone number." });

        if (!string.IsNullOrWhiteSpace(request.Address) && IsPlaceholderAddress(request.Address))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Please enter a real area or suburb, not a placeholder address." });

        if (!string.IsNullOrWhiteSpace(request.IdNumber))
        {
            var id = request.IdNumber.Trim();
            if (!IsValidSouthAfricanId(id))
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Enter a valid 13-digit South African ID number." });
            user.IdNumber = id;
        }

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber)) user.PhoneNumber = request.PhoneNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.Address)) user.Address = request.Address.Trim();
        if (request.DateOfBirth.HasValue) user.DateOfBirth = DateTime.SpecifyKind(request.DateOfBirth.Value.Date, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(request.Username)) user.Username = request.Username.Trim();

        if (!string.IsNullOrWhiteSpace(request.UserType))
        {
            var type = request.UserType.Trim().ToLowerInvariant();
            if (type is not ("creator" or "runner" or "both"))
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Invalid user type." });
            user.UserType = type;
        }

        user.ProfileCompleted = IsProfileComplete(user);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Profile updated successfully" });
    }

    [HttpGet("dashboard/stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId);
        var activeTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        var completedTasks = await _context.Tasks.CountAsync(t => (t.CreatedByUserId == userId || t.AcceptedByUserId == userId) && t.TaskStatus == "Completed");
        var totalEarnings = await _context.Tasks.Where(t => t.AcceptedByUserId == userId && t.TaskStatus == "Completed").SumAsync(t => t.Budget);
        return Ok(new ApiResponse<object> { Success = true, Data = new { postedTasks, activeTasks, completedTasks, totalEarnings } });
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
        return Ok(new ApiResponse<bool> { Success = true, Data = request.IsAvailable, Message = "Availability updated." });
    }

    [HttpPost("validate-id")]
    public ActionResult<ApiResponse<object>> ValidateId([FromBody] ValidateIdRequest request)
    {
        var valid = IsValidSouthAfricanId(request.IdNumber);
        return Ok(new ApiResponse<object> { Success = valid, Data = new { valid }, Message = valid ? "Valid ID number" : "Invalid ID number" });
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserPreferences()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        var type = user?.UserType?.ToLowerInvariant() ?? "";
        return Ok(new ApiResponse<object> {
            Success = true,
            Data = new {
                canCreateTasks = type is "creator" or "both",
                canAcceptTasks = type is "runner" or "both",
                taskCreatorNotifications = true, taskRunnerNotifications = true, paymentNotifications = true,
                emailNotifications = true, smsNotifications = false, minTaskAmount = 50, maxTaskAmount = 100000,
                preferredCategories = Array.Empty<string>(), preferredLocations = Array.Empty<string>()
            }
        });
    }

    [HttpPut("preferences")]
    [HttpPost("preferences")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserPreferences([FromBody] UserPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        var type = request.UserType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type))
            type = request.CanCreateTasks && request.CanAcceptTasks ? "both" :
                   request.CanCreateTasks ? "creator" :
                   request.CanAcceptTasks ? "runner" : null;

        if (type is not ("creator" or "runner" or "both"))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Select Creator, Runner, or Both." });

        user.UserType = type;
        user.ProfileCompleted = IsProfileComplete(user);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Preferences updated successfully" });
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    private static bool IsProfileComplete(User user) =>
        !string.IsNullOrWhiteSpace(user.FirstName) &&
        !string.IsNullOrWhiteSpace(user.LastName) &&
        new EmailAddressAttribute().IsValid(user.Email) &&
        !string.IsNullOrWhiteSpace(user.PhoneNumber) && IsValidPhone(user.PhoneNumber) &&
        !string.IsNullOrWhiteSpace(user.Address) && !IsPlaceholderAddress(user.Address) &&
        IsValidSouthAfricanId(user.IdNumber) &&
        user.DateOfBirth.HasValue &&
        user.UserType is "creator" or "runner" or "both";

    private static int CalculateProfileCompletion(User user)
    {
        var fields = new[] {
            !string.IsNullOrWhiteSpace(user.FirstName),
            !string.IsNullOrWhiteSpace(user.LastName),
            new EmailAddressAttribute().IsValid(user.Email),
            !string.IsNullOrWhiteSpace(user.PhoneNumber) && IsValidPhone(user.PhoneNumber),
            !string.IsNullOrWhiteSpace(user.Address) && !IsPlaceholderAddress(user.Address),
            IsValidSouthAfricanId(user.IdNumber),
            user.DateOfBirth.HasValue,
            user.UserType is "creator" or "runner" or "both"
        };
        return (int)Math.Round(fields.Count(x => x) * 100.0 / fields.Length);
    }

    private static bool IsValidPhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return (digits.Length == 10 && digits.StartsWith("0")) || (digits.Length == 11 && digits.StartsWith("27"));
    }

    private static bool IsPlaceholderAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "just around" or "near me" or "around" or "n/a" or "na" or "tbc" or "unknown" or "somewhere";
    }

    private static bool IsValidSouthAfricanId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 13 || !value.All(char.IsDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 13; i++) {
            var d = value[i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
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
