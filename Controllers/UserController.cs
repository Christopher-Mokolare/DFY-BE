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
                profileCompleted = user.ProfileCompleted,
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
        user.IdNumber = request.IdNumber;
        user.Username = request.Username;
        if (!string.IsNullOrEmpty(request.UserType)) user.UserType = request.UserType;
        user.ProfileCompleted = !string.IsNullOrEmpty(request.PhoneNumber) && !string.IsNullOrEmpty(request.Address);

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

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
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
}