using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public class AdminControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task UpdateUserRole_RotatesSecurityStampAndWritesAuditRecord()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-role-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            Id = 42,
            FirstName = "Test",
            LastName = "User",
            Email = "role-change@example.test",
            PhoneNumber = "0000000000",
            PasswordHash = "not-a-real-password-hash",
            Roles = "Admin",
            SecurityStamp = "existing-session-stamp"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = new AdminController(context, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        using var request = JsonDocument.Parse("""{"role":"User","reason":"Least privilege"}""");

        var result = await controller.UpdateUserRole(user.Id, request.RootElement);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("User", user.Roles);
        Assert.NotEqual("existing-session-stamp", user.SecurityStamp);
        Assert.Equal("UpdateUserRole", Assert.Single(context.AuditLogs).Action);
    }
}
