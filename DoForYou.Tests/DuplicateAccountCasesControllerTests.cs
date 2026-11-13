using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class DuplicateAccountCasesControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Create_opens_audited_case_without_mutating_accounts()
    {
        await using var db = CreateContext();
        db.Users.AddRange(UserAccount(1), UserAccount(2));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 7).Create(new CreateDuplicateAccountCaseRequest
        {
            PrimaryUserId = 1,
            CandidateUserId = 2,
            Reason = "Matching account details require a manual investigation.",
            EvidenceReference = "protected-evidence-ref-42"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, await db.Users.CountAsync());
        var investigation = await db.DuplicateAccountCases.SingleAsync();
        Assert.Equal("Open", investigation.Status);
        Assert.Equal(7, investigation.CreatedByUserId);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("OpenDuplicateAccountInvestigation", audit.Action);
        Assert.Equal(investigation.Id, audit.EntityId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Existing_open_pair_and_self_pair_are_rejected()
    {
        await using var db = CreateContext();
        db.Users.AddRange(UserAccount(1), UserAccount(2));
        db.DuplicateAccountCases.Add(new DuplicateAccountCase
        {
            PrimaryUserId = 1, CandidateUserId = 2,
            Reason = "Existing manual duplicate investigation.",
            CreatedByUserId = 7, Status = "Open"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, 7);
        var duplicate = await controller.Create(new CreateDuplicateAccountCaseRequest
        {
            PrimaryUserId = 2, CandidateUserId = 1,
            Reason = "Reversed duplicate case should not be allowed."
        }, CancellationToken.None);
        var self = await controller.Create(new CreateDuplicateAccountCaseRequest
        {
            PrimaryUserId = 1, CandidateUserId = 1,
            Reason = "An account cannot be compared to itself."
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(duplicate);
        Assert.IsType<BadRequestObjectResult>(self);
        Assert.Single(await db.DuplicateAccountCases.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Raw_identity_number_is_rejected_as_evidence_reference()
    {
        await using var db = CreateContext();
        db.Users.AddRange(UserAccount(1), UserAccount(2));
        await db.SaveChangesAsync();

        var result = await CreateController(db, 7).Create(new CreateDuplicateAccountCaseRequest
        {
            PrimaryUserId = 1, CandidateUserId = 2,
            Reason = "Investigate possible duplicate identity details.",
            EvidenceReference = "identity number 8001015009087"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var rawInReason = await CreateController(db, 7).Create(new CreateDuplicateAccountCaseRequest
        {
            PrimaryUserId = 1, CandidateUserId = 2,
            Reason = "Investigate identity number 8001015009087 for duplicates."
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(rawInReason);
        Assert.Empty(await db.DuplicateAccountCases.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Creator_cannot_resolve_case_but_independent_admin_can()
    {
        await using var db = CreateContext();
        var investigation = new DuplicateAccountCase
        {
            PrimaryUserId = 1, CandidateUserId = 2,
            Reason = "Matching account details require a manual investigation.",
            CreatedByUserId = 7, Status = "Open"
        };
        db.DuplicateAccountCases.Add(investigation);
        await db.SaveChangesAsync();

        var request = new ResolveDuplicateAccountCaseRequest
        {
            Outcome = "FalsePositive",
            ResolutionNote = "The investigation found separate verified account owners."
        };
        var selfDecision = await CreateController(db, 7).Resolve(investigation.Id, request, CancellationToken.None);
        var independentDecision = await CreateController(db, 8).Resolve(investigation.Id, request, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(selfDecision);
        Assert.IsType<OkObjectResult>(independentDecision);
        Assert.Equal("FalsePositive", investigation.Status);
        Assert.Equal(8, investigation.DecidedByUserId);
        Assert.Equal("ResolveDuplicateAccountInvestigation", (await db.AuditLogs.SingleAsync()).Action);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal("User", (await db.Users.SingleAsync(item => item.Id == 1)).Roles);
        Assert.Equal("User", (await db.Users.SingleAsync(item => item.Id == 2)).Roles);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"duplicate-account-case-{Guid.NewGuid():N}").Options;
        return new AppDbContext(options);
    }

    private static DuplicateAccountCasesController CreateController(AppDbContext db, int actorId)
    {
        var controller = new DuplicateAccountCasesController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "test"))
            }
        };
        return controller;
    }

    private static User UserAccount(int id) => new()
    {
        Id = id,
        Email = $"user{id}@example.test",
        NormalizedEmail = $"user{id}@example.test",
        PasswordHash = "test-hash",
        Roles = "User",
        EmailVerified = true
    };
}
