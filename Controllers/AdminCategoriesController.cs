using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin/categories")]
[Authorize(Roles = "Admin")]
[Authorize(Policy = "AdminMfa")]
public class AdminCategoriesController : ControllerBase
{
    private readonly AppDbContext _context;

    public AdminCategoriesController(AppDbContext context) => _context = context;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetAll()
    {
        var cats = await _context.Categories
            .OrderBy(c => c.SortOrder)
            .Select(c => new { c.Id, c.Name, c.Description, c.Icon, c.SortOrder, c.CreatedAt })
            .ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = cats });
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create([FromBody] CategoryRequest request)
    {
        var name = request.Name.Trim();
        if (await _context.Categories.AnyAsync(c => c.Name.ToLower() == name.ToLower()))
            return Conflict(new ApiResponse<object> { Success = false, Message = "A category with this name already exists." });

        var maxOrder = await _context.Categories.MaxAsync(c => (int?)c.SortOrder) ?? 0;
        var cat = new Category
        {
            Name = name,
            Description = request.Description?.Trim(),
            Icon = request.Icon?.Trim(),
            SortOrder = request.SortOrder ?? maxOrder + 1
        };
        _context.Categories.Add(cat);
        await _context.SaveChangesAsync();
        AddAudit("CreateCategory", cat.Id, null, new { cat.Name, cat.Description, cat.Icon, cat.SortOrder, reason = request.Reason.Trim() });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { cat.Id, cat.Name }, Message = "Category created" });
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Update(int id, [FromBody] CategoryRequest request)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Category not found." });

        var name = request.Name.Trim();
        if (await _context.Categories.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower()))
            return Conflict(new ApiResponse<bool> { Success = false, Message = "A category with this name already exists." });

        var oldName = cat.Name;
        var oldValues = new { cat.Name, cat.Description, cat.Icon, cat.SortOrder };
        cat.Name = name;
        cat.Description = request.Description?.Trim();
        cat.Icon = request.Icon?.Trim();
        if (request.SortOrder.HasValue) cat.SortOrder = request.SortOrder.Value;

        var affectedTasks = 0;
        if (!string.Equals(oldName, name, StringComparison.Ordinal))
        {
            affectedTasks = await _context.Tasks
                .Where(task => task.Category == oldName)
                .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Category, name));
        }

        await _context.SaveChangesAsync();
        AddAudit("UpdateCategory", id, oldValues, new
        {
            cat.Name, cat.Description, cat.Icon, cat.SortOrder,
            affectedTaskCategoryReferences = affectedTasks,
            reason = request.Reason.Trim()
        });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Category updated." });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(int id, [FromBody] AdminActionReasonRequest request)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Category not found." });

        var taskCount = await _context.Tasks.CountAsync(task => task.Category == cat.Name);
        if (taskCount > 0)
            return Conflict(new ApiResponse<bool>
            {
                Success = false,
                Message = $"This category is used by {taskCount} task(s). Reassign those tasks before deleting the category."
            });

        var oldValues = new { cat.Name, cat.Description, cat.Icon, cat.SortOrder };
        _context.Categories.Remove(cat);
        await _context.SaveChangesAsync();
        AddAudit("DeleteCategory", id, oldValues, new { deleted = true, reason = request.Reason.Trim() });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Category deleted." });
    }

    [HttpPatch("{id:int}/reorder")]
    public async Task<ActionResult<ApiResponse<bool>>> Reorder(int id, [FromBody] ReorderRequest request)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Category not found." });

        var oldOrder = cat.SortOrder;
        cat.SortOrder = request.SortOrder;
        await _context.SaveChangesAsync();
        AddAudit("ReorderCategory", id, new { sortOrder = oldOrder }, new { sortOrder = cat.SortOrder, reason = request.Reason.Trim() });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true });
    }

    private void AddAudit(string action, int categoryId, object? oldValues, object? newValues)
    {
        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        int.TryParse(rawUserId, out var userId);
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId > 0 ? userId : null,
            Action = action,
            EntityType = "Category",
            EntityId = categoryId,
            OldValues = oldValues == null ? null : JsonSerializer.Serialize(oldValues),
            NewValues = newValues == null ? null : JsonSerializer.Serialize(newValues),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
    }
}
