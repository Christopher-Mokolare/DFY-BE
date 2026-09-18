using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using BCrypt.Net;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly INotificationService _notificationService;

    public AuthController(AppDbContext context, IConfiguration configuration, ILogger<AuthController> logger, INotificationService notificationService)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _notificationService = notificationService;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        if (await _context.Users.AnyAsync(u => u.Email == request.Email))
        {
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Email already exists"
            });
        }

        var user = new User
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email ?? string.Empty,
            PhoneNumber = request.PhoneNumber,
            UserType = request.UserType,
            IdNumber = request.IdNumber,
            Address = request.Address,
            DateOfBirth = ConvertToUtc(request.DateOfBirth),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsVerified = false,
            ProfileCompleted = false,
            EmailVerified = false,
            PhoneVerified = false,
            Roles = "User",
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _ = _notificationService.NotifyAdminsAsync(
            "new_user",
            "New User Registered",
            $"{user.FirstName} {user.LastName} ({user.Email}) joined as {user.UserType ?? "user"}"
        );

        var token = GenerateJwtToken(user);

        return Ok(new AuthResponse
        {
            Success = true,
            Token = token,
            User = new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                ProfileCompleted = user.ProfileCompleted,
                ProfileCompletion = CalculateProfileCompletion(user),
                Rating = user.Rating,
                CompletedTasks = user.CompletedTasks,
                Roles = user.Roles,
                IsAdmin = user.Roles?.Contains("Admin") == true,
                UserType = user.UserType,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsVerified = user.IsVerified
            },
            Message = "Registration successful"
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<bool>>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId)) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            return Ok(new ApiResponse<bool> { Success = false, Message = "New password must be different from the current password" });

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Current password is incorrect" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Password changed successfully" });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        if (request == null)
            return BadRequest("Invalid request");
        
        // Clean email - remove mailto: prefix if present
        var cleanEmail = request.Email?.Replace("mailto:", "").Trim();
        
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == cleanEmail);
        
        if (user == null)
        {
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
            });
        }

        var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
        
        if (!passwordValid)
        {
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
            });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var token = GenerateJwtToken(user);

        _logger.LogInformation("Successful login for user id {UserId}", user.Id);
        return Ok(new AuthResponse
        {
            Success = true,
            Token = token,
            User = new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                ProfileCompleted = user.ProfileCompleted,
                ProfileCompletion = CalculateProfileCompletion(user),
                Rating = user.Rating,
                CompletedTasks = user.CompletedTasks,
                Roles = user.Roles,
                IsAdmin = user.Roles?.Contains("Admin") == true,
                UserType = user.UserType,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsVerified = user.IsVerified
            },
            Message = "Login successful"
        });
    }

    private static int CalculateProfileCompletion(User user)
    {
        var fields = new[]
        {
            !string.IsNullOrWhiteSpace(user.FirstName),
            !string.IsNullOrWhiteSpace(user.LastName),
            new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(user.Email),
            !string.IsNullOrWhiteSpace(user.PhoneNumber) && IsValidPhone(user.PhoneNumber),
            !string.IsNullOrWhiteSpace(user.Address) && !IsPlaceholderAddress(user.Address),
            IsValidSouthAfricanId(user.IdNumber),
            user.DateOfBirth.HasValue,
            user.UserType is "creator" or "runner" or "both"
        };
        return (int)Math.Round(fields.Count(f => f) * 100.0 / fields.Length);
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
        for (var i = 0; i < 13; i++)
        {
            var d = value[i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    private string GenerateJwtToken(User user)
    {
        var configuredKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY");
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException("JWT signing key is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email)
        };

        // Add each role as a separate claim
        if (!string.IsNullOrEmpty(user.Roles))
        {
            var roles = user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "DoForYou",
            audience: _configuration["Jwt:Audience"] ?? "DoForYou",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static DateTime? ConvertToUtc(DateTime? dateTime)
    {
        if (!dateTime.HasValue) return null;
        
        return dateTime.Value.Kind switch
        {
            DateTimeKind.Utc => dateTime.Value,
            DateTimeKind.Local => dateTime.Value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime.Value, DateTimeKind.Utc),
            _ => dateTime.Value
        };
    }
}