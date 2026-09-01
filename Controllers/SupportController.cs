using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/support")]
public class SupportController : ControllerBase
{
    private readonly AppDbContext _context;

    public SupportController(AppDbContext context) => _context = context;

    [HttpPost("tickets")]
    public async Task<ActionResult<ApiResponse<object>>> CreateTicket([FromBody] CreateSupportTicketRequest request)
    {
        var userId = GetCurrentUserId();
        var user = userId.HasValue ? await _context.Users.FindAsync(userId.Value) : null;

        var reference = $"DFY-SUP-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Random.Shared.Next(100, 999)}";

        var ticket = new SupportTicket
        {
            UserId = userId,
            Name = request.Name ?? $"{user?.FirstName} {user?.LastName}".Trim(),
            Email = request.Email ?? user?.Email ?? string.Empty,
            Subject = request.Subject,
            Message = request.Message,
            Category = request.Category ?? "General",
            Priority = request.Priority ?? "Normal",
            Status = "Open",
            Reference = reference
        };

        _context.SupportTickets.Add(ticket);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { id = ticket.Id, reference = ticket.Reference, status = ticket.Status },
            Message = $"Support ticket created. Reference: {reference}"
        });
    }

    [HttpGet("tickets")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> GetMyTickets()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var tickets = await _context.SupportTickets
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                id = t.Id,
                reference = t.Reference,
                subject = t.Subject,
                category = t.Category,
                priority = t.Priority,
                status = t.Status,
                createdAt = t.CreatedAt,
                resolvedAt = t.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = tickets });
    }

    [HttpGet("tickets/{id}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> GetTicket(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var ticket = await _context.SupportTickets.FindAsync(id);
        if (ticket == null || (ticket.UserId != userId && !User.IsInRole("Admin")))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Ticket not found" });

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                id = ticket.Id,
                reference = ticket.Reference,
                subject = ticket.Subject,
                message = ticket.Message,
                category = ticket.Category,
                priority = ticket.Priority,
                status = ticket.Status,
                adminNotes = ticket.AdminNotes,
                createdAt = ticket.CreatedAt,
                resolvedAt = ticket.ResolvedAt
            }
        });
    }

    // Admin endpoints
    [HttpGet("admin/tickets")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> GetAllTickets(
        [FromQuery] string? status = null,
        [FromQuery] string? priority = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _context.SupportTickets.AsQueryable();
        if (!string.IsNullOrEmpty(status)) query = query.Where(t => t.Status == status);
        if (!string.IsNullOrEmpty(priority)) query = query.Where(t => t.Priority == priority);

        var total = await query.CountAsync();
        var tickets = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                id = t.Id,
                reference = t.Reference,
                name = t.Name,
                email = t.Email,
                subject = t.Subject,
                category = t.Category,
                priority = t.Priority,
                status = t.Status,
                assignedTo = t.AssignedTo,
                createdAt = t.CreatedAt,
                resolvedAt = t.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = new { tickets, total, page, pageSize } });
    }

    [HttpPatch("admin/tickets/{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateTicket(int id, [FromBody] UpdateSupportTicketRequest request)
    {
        var ticket = await _context.SupportTickets.FindAsync(id);
        if (ticket == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Ticket not found" });

        if (!string.IsNullOrEmpty(request.Status)) ticket.Status = request.Status;
        if (!string.IsNullOrEmpty(request.AssignedTo)) ticket.AssignedTo = request.AssignedTo;
        if (request.AdminNotes != null) ticket.AdminNotes = request.AdminNotes;
        if (request.Status == "Resolved" && ticket.ResolvedAt == null)
            ticket.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Ticket updated" });
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
