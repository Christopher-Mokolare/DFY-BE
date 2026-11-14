using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AdminCategoriesControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Category_creation_is_reasoned_and_audited()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"category-create-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        var controller = CreateController(context);

        var result = await controller.Create(new CategoryRequest
        {
            Name = "Cleaning",
            Description = "Home and office cleaning",
            Icon = "fa-broom",
            Reason = "Add a marketplace category requested by operations"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        var category = await context.Categories.SingleAsync();
        Assert.Equal("Cleaning", category.Name);
        var audit = await context.AuditLogs.SingleAsync();
        Assert.Equal("CreateCategory", audit.Action);
        Assert.Equal(category.Id, audit.EntityId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Category_reorder_updates_the_entire_order_and_audits_it()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"category-reorder-{Guid.NewGuid():N}").Options;
        await using var context = new AppDbContext(options);
        context.Categories.AddRange(
            new Category { Id = 1, Name = "A", SortOrder = 1 },
            new Category { Id = 2, Name = "B", SortOrder = 2 },
            new Category { Id = 3, Name = "C", SortOrder = 3 });
        await context.SaveChangesAsync();
        var controller = CreateController(context);

        var result = await controller.Reorder(3, new ReorderRequest
        {
            SortOrder = 1,
            Reason = "Prioritize this category based on marketplace demand"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        var ordered = await context.Categories.OrderBy(item => item.SortOrder).ToListAsync();
        Assert.Equal(new[] { "C", "A", "B" }, ordered.Select(item => item.Name));
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Select(item => item.SortOrder));
        Assert.Equal("ReorderCategory", (await context.AuditLogs.SingleAsync()).Action);
    }

    private static AdminCategoriesController CreateController(AppDbContext context)
    {
        var controller = new AdminCategoriesController(context);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "7"),
            new Claim(ClaimTypes.Role, "Admin")
        }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}
