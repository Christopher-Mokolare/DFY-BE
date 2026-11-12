using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Services;

/// <summary>
/// Delivers durable identity-review notification records. A lease plus conditional
/// update prevents multiple instances from processing the same row concurrently.
/// Delivery is at-least-once; in-app rows are idempotent via Notification.OutboxId.
/// </summary>
public sealed class IdentityNotificationOutboxService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdentityNotificationOutboxService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private const int MaxAttempts = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    protected override async System.Threading.Tasks.Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DFY identity notification outbox processing failed.");
            }

            try
            {
                await System.Threading.Tasks.Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async System.Threading.Tasks.Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var candidates = await db.IdentityNotificationOutbox
            .AsNoTracking()
            .Where(item =>
                (item.Status == "pending" && item.AvailableAt <= now) ||
                (item.Status == "processing" && item.LeaseExpiresAt <= now))
            .OrderBy(item => item.AvailableAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            var claimedAt = DateTime.UtcNow;
            var claimed = await db.IdentityNotificationOutbox
                .Where(item => item.Id == id &&
                    ((item.Status == "pending" && item.AvailableAt <= claimedAt) ||
                     (item.Status == "processing" && item.LeaseExpiresAt <= claimedAt)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "processing")
                    .SetProperty(item => item.LeaseExpiresAt, claimedAt.Add(LeaseDuration))
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1),
                    cancellationToken);

            if (claimed != 1) continue;

            var item = await db.IdentityNotificationOutbox
                .FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
            if (item == null) continue;

            try
            {
                await DeliverAsync(scope.ServiceProvider, item, cancellationToken);
                item.Status = "delivered";
                item.DeliveredAt = DateTime.UtcNow;
                item.LeaseExpiresAt = null;
                item.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedAt = DateTime.UtcNow;
                item.LeaseExpiresAt = null;
                item.LastError = SafeError(ex);
                if (item.AttemptCount >= MaxAttempts)
                {
                    item.Status = "dead_letter";
                    logger.LogError(ex, "DFY identity notification {OutboxId} moved to dead letter after {Attempts} attempts.", item.Id, item.AttemptCount);
                }
                else
                {
                    item.Status = "pending";
                    var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(item.AttemptCount, 8)));
                    item.AvailableAt = failedAt.AddMinutes(delayMinutes);
                    logger.LogWarning(ex, "DFY identity notification {OutboxId} delivery failed; retry {Attempt} scheduled.", item.Id, item.AttemptCount);
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static async System.Threading.Tasks.Task DeliverAsync(
        IServiceProvider services,
        IdentityNotificationOutbox item,
        CancellationToken cancellationToken)
    {
        if (item.Channel == "in_app")
        {
            if (!item.RecipientUserId.HasValue)
                throw new InvalidOperationException("In-app identity notification has no recipient user ID.");

            var notifications = services.GetRequiredService<INotificationService>();
            await notifications.CreateNotificationFromOutboxAsync(
                item.Id, item.RecipientUserId.Value, item.Type, item.Title, item.Message, cancellationToken);
            return;
        }

        if (item.Channel == "email")
        {
            if (string.IsNullOrWhiteSpace(item.RecipientEmail))
                throw new InvalidOperationException("Email identity notification has no recipient address.");

            var email = services.GetRequiredService<IEmailSender>();
            await email.SendAsync(item.RecipientEmail, item.Title, item.Message, cancellationToken);
            return;
        }

        throw new InvalidOperationException("Unsupported identity notification channel.");
    }

    private static string SafeError(Exception exception)
    {
        // Do not persist provider response bodies, credentials, or message content.
        var value = exception.GetType().Name + ": " + exception.Message;
        return value.Length <= 500 ? value : value[..500];
    }
}
