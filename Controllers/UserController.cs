using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;
using System.Text.Json;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/user")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly AppDbContext _context;

    public UserController(AppDbContext context)
    {
        _context = context;
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
                profileCompleted = CalculateProfileCompleted(user),
                profileCompletion = CalculateProfileCompletion(user),
                rating = user.Rating,
                completedTasks = user.CompletedTasks,
                walletBalance = user.WalletBalance,
                isVerified = user.IsVerified,
                emailVerified = user.EmailVerified,
                phoneVerified = user.PhoneVerified
            }
        });
    }

    [HttpPut("profile")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        user.PhoneNumber = request.PhoneNumber;
        user.Address = request.Address;
        user.DateOfBirth = request.DateOfBirth;
        user.IdNumber = request.IdNumber ?? string.Empty;
        user.Username = request.Username ?? string.Empty;
        if (!string.IsNullOrEmpty(request.UserType)) user.UserType = request.UserType;
        
        // Calculate profile completion percentage
        var checks = new[]
        {
            !string.IsNullOrWhiteSpace(user.FirstName),
            !string.IsNullOrWhiteSpace(user.LastName),
            !string.IsNullOrWhiteSpace(user.PhoneNumber),
            !string.IsNullOrWhiteSpace(user.Address),
            user.DateOfBirth.HasValue,
            !string.IsNullOrWhiteSpace(user.IdNumber),
            user.EmailVerified,
            user.PhoneVerified
        };
        
        user.ProfileCompleted = checks.All(c => c); // Require all fields
        
        // Reset verification if ID number is removed
        if (string.IsNullOrEmpty(request.IdNumber) && user.IsVerified)
        {
            user.IsVerified = false;
        }

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Profile updated successfully"
        });
    }

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

    [HttpGet("/api/v1/UserPreferences")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserPreferences()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Return preferences based on user type
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
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserPreferences([FromBody] UserPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        // Update user type based on preferences
        if (request.CanCreateTasks && request.CanAcceptTasks)
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

    [HttpPost("validate-id")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<object>> ValidateIdNumber([FromBody] ValidateIdRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdNumber) || request.IdNumber.Length != 13)
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "Invalid ID number format"
            });
        }

        try
        {
            var dateOfBirth = ExtractDateOfBirthFromId(request.IdNumber);
            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = new
                {
                    dateOfBirth = dateOfBirth?.ToString("yyyy-MM-dd"),
                    isValid = dateOfBirth.HasValue
                }
            });
        }
        catch
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = "Invalid ID number"
            });
        }
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private int CalculateProfileCompletion(Models.User user)
    {
        var checks = new[]
        {
            !string.IsNullOrWhiteSpace(user.FirstName),
            !string.IsNullOrWhiteSpace(user.LastName),
            !string.IsNullOrWhiteSpace(user.PhoneNumber),
            !string.IsNullOrWhiteSpace(user.Address),
            user.DateOfBirth.HasValue,
            !string.IsNullOrWhiteSpace(user.IdNumber)
        };
        
        int total = checks.Length;
        int filled = checks.Count(c => c);
        return (int)Math.Round((double)filled / total * 100);
    }

    private bool CalculateProfileCompleted(Models.User user)
    {
        return !string.IsNullOrWhiteSpace(user.FirstName) &&
               !string.IsNullOrWhiteSpace(user.LastName) &&
               !string.IsNullOrWhiteSpace(user.PhoneNumber) &&
               !string.IsNullOrWhiteSpace(user.Address) &&
               user.DateOfBirth.HasValue &&
               !string.IsNullOrWhiteSpace(user.IdNumber);
    }

    private DateTime? ExtractDateOfBirthFromId(string idNumber)
    {
        if (string.IsNullOrWhiteSpace(idNumber) || idNumber.Length != 13)
            return null;

        try
        {
            var yearPart = idNumber.Substring(0, 2);
            var monthPart = idNumber.Substring(2, 2);
            var dayPart = idNumber.Substring(4, 2);

            var year = int.Parse(yearPart);
            var month = int.Parse(monthPart);
            var day = int.Parse(dayPart);

            // Determine century (assume 00-30 = 2000s, 31-99 = 1900s)
            var fullYear = year <= 30 ? 2000 + year : 1900 + year;

            return new DateTime(fullYear, month, day);
        }
        catch
        {
            return null;
        }
    }
}

public class UpdateProfileRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
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
}

public class ValidateIdRequest
{
    public string IdNumber { get; set; } = string.Empty;
}