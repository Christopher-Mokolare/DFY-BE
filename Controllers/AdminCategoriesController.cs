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
        if (await _context.Categories.AnyAsync(c => c.Name == request.Name))
            return Ok(new ApiResponse<object> { Success = false, Message = "Category already exists" });

        var maxOrder = await _context.Categories.MaxAsync(c => (int?)c.SortOrder) ?? 0;
        var cat = new Category
        {
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            SortOrder = request.SortOrder ?? maxOrder + 1
        };
        _context.Categories.Add(cat);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { cat.Id, cat.Name }, Message = "Category created" });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> Update(int id, [FromBody] CategoryRequest request)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Not found" });

        cat.Name = request.Name;
        if (request.Description != null) cat.Description = request.Description;
        if (request.Icon != null) cat.Icon = request.Icon;
        if (request.SortOrder.HasValue) cat.SortOrder = request.SortOrder.Value;

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Category updated" });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(int id)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Not found" });

        _context.Categories.Remove(cat);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Category deleted" });
    }

    [HttpPatch("{id}/reorder")]
    public async Task<ActionResult<ApiResponse<bool>>> Reorder(int id, [FromBody] ReorderRequest request)
    {
        var cat = await _context.Categories.FindAsync(id);
        if (cat == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Not found" });

        cat.SortOrder = request.SortOrder;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true });
    }
}
