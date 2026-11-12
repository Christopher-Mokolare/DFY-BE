using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class IdentityNotificationOutboxTests
{
    [Fact]
    public void Outbox_model_enforces_unique_idempotency_and_supports_due_work_indexes()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var outbox = context.Model.FindEntityType(typeof(IdentityNotificationOutbox));
        Assert.NotNull(outbox);

        var idempotency = Assert.Single(outbox!.GetIndexes()
            .Where(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(IdentityNotificationOutbox.IdempotencyKey) })));
        Assert.True(idempotency.IsUnique);

        Assert.Contains(outbox.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(IdentityNotificationOutbox.Status), nameof(IdentityNotificationOutbox.AvailableAt) }));

        Assert.Contains(outbox.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(IdentityNotificationOutbox.LeaseExpiresAt) }));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(8, 256)]
    [InlineData(99, 256)]
    public void Retry_delay_uses_bounded_exponential_backoff(int attemptCount, double expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            IdentityNotificationOutboxService.GetRetryDelay(attemptCount));
    }

    [Fact]
    public void Notification_model_has_unique_outbox_link_for_idempotent_in_app_delivery()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var notification = context.Model.FindEntityType(typeof(Notification));
        Assert.NotNull(notification);
        var outboxIndex = Assert.Single(notification!.GetIndexes()
            .Where(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Notification.OutboxId) })));
        Assert.True(outboxIndex.IsUnique);
    }
}
