using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/ratings")]
[Authorize]
public class RatingsController : ControllerBase
{
    private readonly AppDbContext _context;

    public RatingsController(AppDbContext context) => _context = context;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> SubmitRating([FromBody] SubmitRatingRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == request.TaskId);
        if (task == null)
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });

        if (task.TaskStatus != "RunnerPaid" && task.TaskStatus != "Completed")
            return Ok(new ApiResponse<object> { Success = false, Message = "Task must be completed before rating" });

        // Creator rates runner, runner rates creator
        int ratedUserId;
        if (task.CreatedByUserId == userId && task.AcceptedByUserId.HasValue)
            ratedUserId = task.AcceptedByUserId.Value;
        else if (task.AcceptedByUserId == userId)
            ratedUserId = task.CreatedByUserId;
        else
            return Ok(new ApiResponse<object> { Success = false, Message = "Not authorized to rate this task" });

        var existing = await _context.Ratings.AnyAsync(r => r.TaskId == task.Id && r.RatedByUserId == userId);
        if (existing)
            return Ok(new ApiResponse<object> { Success = false, Message = "Already rated this task" });

        var rating = new Models.Rating
        {
            TaskId = task.Id,
            RatedByUserId = userId.Value,
            RatedUserId = ratedUserId,
            RatingValue = request.RatingValue,
            Review = request.Review
        };

        _context.Ratings.Add(rating);

        // Update user average rating
        var ratedUser = await _context.Users.FindAsync(ratedUserId);
        if (ratedUser != null)
        {
            var allRatings = await _context.Ratings.Where(r => r.RatedUserId == ratedUserId).Select(r => r.RatingValue).ToListAsync();
            allRatings.Add(request.RatingValue);
            ratedUser.Rating = (decimal)allRatings.Average();
        }

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = new { id = rating.Id }, Message = "Rating submitted" });
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<ApiResponse<object>>> GetRatingsForUser(int userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 5)
    {
        var total = await _context.Ratings.CountAsync(r => r.RatedUserId == userId);
        var allValues = await _context.Ratings.Where(r => r.RatedUserId == userId).Select(r => r.RatingValue).ToListAsync();
        var average = allValues.Count > 0 ? allValues.Average() : 0.0;
        var starBreakdown = allValues.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());

        var ratings = await _context.Ratings
            .Include(r => r.RatedByUser)
            .Include(r => r.Task)
            .Where(r => r.RatedUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                id = r.Id,
                ratingValue = r.RatingValue,
                review = r.Review,
                ratedBy = $"{r.RatedByUser.FirstName} {r.RatedByUser.LastName}",
                taskDescription = r.Task.TaskDescription,
                taskName = r.Task.TaskName,
                createdAt = r.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { ratings, count = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), page, pageSize, average, starBreakdown }
        });
    }

    [HttpGet("can-rate/{taskId}")]
    public async Task<ActionResult<ApiResponse<bool>>> CanRate(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return Ok(new ApiResponse<bool> { Success = false, Data = false });

        var isParticipant = task.CreatedByUserId == userId || task.AcceptedByUserId == userId;
        var isCompleted = task.TaskStatus == "RunnerPaid" || task.TaskStatus == "Completed";
        var alreadyRated = await _context.Ratings.AnyAsync(r => r.TaskId == task.Id && r.RatedByUserId == userId);

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = isParticipant && isCompleted && !alreadyRated
        });
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
