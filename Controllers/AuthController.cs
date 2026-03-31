using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using BCrypt.Net;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
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
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            UserType = request.UserType,
            IdNumber = request.IdNumber,
            Address = request.Address,
            DateOfBirth = ConvertToUtc(request.DateOfBirth),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsVerified = true,
            ProfileCompleted = true,
            EmailVerified = true,
            PhoneVerified = true,
            Roles = "User",
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

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

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        Console.WriteLine($"=== LOGIN REQUEST RECEIVED ===");
        Console.WriteLine($"Email: {request?.Email}");
        Console.WriteLine($"Password length: {request?.Password?.Length}");
        Console.WriteLine($"Request headers: {string.Join(", ", Request.Headers.Select(h => $"{h.Key}: {h.Value}"))}");
        Console.WriteLine($"Request origin: {Request.Headers["Origin"]}");
        
        if (request == null)
        {
            Console.WriteLine("Request is null");
            return BadRequest("Invalid request");
        }
        
        // Clean email - remove mailto: prefix if present
        var cleanEmail = request.Email?.Replace("mailto:", "").Trim();
        
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == cleanEmail);
        
        if (user == null)
        {
            Console.WriteLine($"User not found: {cleanEmail}");
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
            });
        }

        Console.WriteLine($"User found: {user.Email}, checking password...");
        var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
        Console.WriteLine($"Password valid: {passwordValid}");
        
        if (!passwordValid)
        {
            Console.WriteLine("Password verification failed");
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
            });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var token = GenerateJwtToken(user);

        Console.WriteLine($"Login successful for: {user.Email}");
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

    private string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? "your-secret-key-here"));
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