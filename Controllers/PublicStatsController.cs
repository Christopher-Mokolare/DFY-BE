using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/public/stats")]
[AllowAnonymous]
public class PublicStatsController : ControllerBase
{
    private readonly AppDbContext _context;

    public PublicStatsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get()
    {
        // A task is counted as completed only after the runner has been paid.
        // The Tasks query filter also excludes deleted tasks.
        var tasksCompleted = await _context.Tasks
            .CountAsync(t => t.TaskStatus == "RunnerPaid");

        // "Active runners" means verified, completed profiles that are currently
        // available and have a runner-capable user type.
        var activeRunners = await _context.Users
            .CountAsync(u =>
                u.IsVerified &&
                u.ProfileCompleted &&
                u.IsAvailable &&
                (EF.Functions.ILike(u.UserType, "%runner%") ||
                 EF.Functions.ILike(u.UserType, "%both%")));

        // User.Rating is the backend's maintained aggregate rating for each user.
        // Exclude users with no rating so new accounts do not dilute the average.
        var ratedUsers = await _context.Users
            .Where(u => u.Rating > 0)
            .Select(u => u.Rating)
            .ToListAsync();

        var averageRating = ratedUsers.Count == 0
            ? 0m
            : Math.Round(ratedUsers.Average(), 2, MidpointRounding.AwayFromZero);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                tasksCompleted,
                activeRunners,
                averageRating
            }
        });
    }
}
