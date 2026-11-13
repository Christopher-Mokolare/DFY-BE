using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class AppDbContextAuditLogTests
{
    [Fact]
    public async System.Threading.Tasks.Task Audit_logs_can_be_appended()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"audit-append-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);

        context.AuditLogs.Add(new AuditLog
        {
            Action = "TestAction",
            EntityType = "Test",
            IpAddress = "127.0.0.1",
            UserAgent = "test"
        });

        await context.SaveChangesAsync();

        Assert.Single(context.AuditLogs);
    }

    [Fact]
    public async System.Threading.Tasks.Task Audit_logs_cannot_be_updated_or_deleted()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"audit-immutable-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        var entry = new AuditLog
        {
            Action = "OriginalAction",
            EntityType = "Test",
            IpAddress = "127.0.0.1",
            UserAgent = "test"
        };
        context.AuditLogs.Add(entry);
        await context.SaveChangesAsync();

        entry.Action = "TamperedAction";
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        context.Entry(entry).State = EntityState.Unchanged;
        context.AuditLogs.Remove(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }
}
