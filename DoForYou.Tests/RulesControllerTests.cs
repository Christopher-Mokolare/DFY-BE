using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class RulesControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Create_rule_requires_reason_and_writes_audit()
    {
        await using var db = CreateContext();
        var controller = CreateController(db);
        var result = await controller.CreateRule(Request("Initial rule creation is required."), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var rule = await db.BusinessRules.SingleAsync();
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("CreateBusinessRule", audit.Action);
        Assert.Equal(rule.Id, audit.EntityId);
        Assert.Contains("Initial rule creation is required.", audit.NewValues);
    }

    [Fact]
    public async System.Threading.Tasks.Task Update_rule_records_before_and_after_values()
    {
        await using var db = CreateContext();
        var rule = new BusinessRule
        {
            RuleName = "Old name", RuleType = "validation", Entity = "Task",
            Condition = "{\"old\":true}", Action = "deny", IsActive = true, Priority = 1
        };
        db.BusinessRules.Add(rule);
        await db.SaveChangesAsync();

        var result = await CreateController(db).UpdateRule(rule.Id, Request("The rule needs a reviewed update."), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("UpdateBusinessRule", audit.Action);
        Assert.Contains("Old name", audit.OldValues);
        Assert.Contains("Rule updated", audit.NewValues);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_rule_deactivates_instead_of_destroying_configuration_history()
    {
        await using var db = CreateContext();
        var rule = new BusinessRule
        {
            RuleName = "Retained rule", RuleType = "workflow", Entity = "Task",
            Condition = "{}", Action = "allow", IsActive = true
        };
        db.BusinessRules.Add(rule);
        await db.SaveChangesAsync();

        var result = await CreateController(db).DeleteRule(rule.Id,
            new AdminRuleReasonRequest { Reason = "Rule is superseded by the reviewed replacement." },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(rule.IsActive);
        Assert.Single(await db.BusinessRules.ToListAsync());
        Assert.Equal("DeactivateBusinessRule", (await db.AuditLogs.SingleAsync()).Action);
    }

    private static AdminBusinessRuleRequest Request(string reason) => new()
    {
        RuleName = "Reviewed rule",
        RuleType = "validation",
        Entity = "Task",
        Condition = "{\"budget\":{\"min\":50}}",
        Action = "deny",
        ErrorMessage = "Rule condition was not satisfied.",
        IsActive = true,
        Priority = 2,
        Reason = reason
    };

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"rules-controller-{Guid.NewGuid():N}").Options;
        return new AppDbContext(options);
    }

    private static RulesController CreateController(AppDbContext db)
    {
        var controller = new RulesController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "7")
                }, "test"))
            }
        };
        return controller;
    }
}
