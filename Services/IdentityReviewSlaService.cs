using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Services;

/// <summary>
/// Alerts administrators when a pending DFY Document Review approaches or breaches
/// its one-business-day target. Alerts are recorded in the identity event audit trail
/// so the same threshold is not repeatedly escalated.
/// </summary>
public sealed class IdentityReviewSlaService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<IdentityReviewSlaService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    protected override async System.Threading.Tasks.Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessPendingReviewsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "DFY Document Review SLA monitor failed."); }

            try { await System.Threading.Tasks.Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async System.Threading.Tasks.Task ProcessPendingReviewsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var pending = await db.IdentityVerificationSubmissions
            .Where(s => s.Status == "pending")
            .Select(s => new { s.Id, s.UserId, s.SubmittedAt })
            .ToListAsync(cancellationToken);

        foreach (var submission in pending)
        {
            var dueAt = SouthAfricanBusinessCalendar.AddBusinessHours(
                submission.SubmittedAt, 8,
                configuration["IdentityVerification:SouthAfricanPublicHolidays"]);
            var nearAt = SouthAfricanBusinessCalendar.AddBusinessHours(
                submission.SubmittedAt, 6,
                configuration["IdentityVerification:SouthAfricanPublicHolidays"]);
            var nearEvent = "sla_near_due_" + submission.Id;
            var overdueEvent = "sla_overdue_" + submission.Id;
            var escalationEvent = "sla_escalated_" + submission.Id;

            if (now >= nearAt && !await HasEventAsync(db, submission.UserId, nearEvent, cancellationToken))
            {
                await QueueAdminNotificationsAsync(db, $"identity-sla:{nearEvent}", "identity_review_near_deadline",
                    "DFY Document Review approaching deadline",
                    $"Review case {submission.Id} has used 75% of its one-business-day review target.", cancellationToken);
                db.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, nearEvent, "alerted"));
                await db.SaveChangesAsync(cancellationToken);
            }

            if (now >= dueAt && !await HasEventAsync(db, submission.UserId, overdueEvent, cancellationToken))
            {
                await QueueAdminNotificationsAsync(db, $"identity-sla:{overdueEvent}", "identity_review_overdue",
                    "DFY Document Review overdue",
                    $"Review case {submission.Id} has breached its one-business-day review target. Please assign a reviewer immediately.", cancellationToken);
                QueueUserNotification(db, $"identity-sla:{overdueEvent}:runner", submission.UserId,
                    "identity_review_delayed", "Your DFY Document Review is taking longer than expected",
                    "Your documents are still awaiting review. We are working on your application and will notify you as soon as there is an update.");
                db.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, overdueEvent, "alerted"));
                await db.SaveChangesAsync(cancellationToken);
            }

            var internalDueAt = SouthAfricanBusinessCalendar.AddBusinessHours(
                submission.SubmittedAt, 16, configuration["IdentityVerification:SouthAfricanPublicHolidays"]);
            if (now >= internalDueAt && !await HasEventAsync(db, submission.UserId, escalationEvent, cancellationToken))
            {
                await QueueAdminNotificationsAsync(db, $"identity-sla:{escalationEvent}", "identity_review_escalated",
                    "DFY Document Review requires escalation",
                    $"Review case {submission.Id} has exceeded the internal two-business-day maximum. A backup administrator must take ownership.", cancellationToken);
                db.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, escalationEvent, "escalated"));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static async System.Threading.Tasks.Task QueueAdminNotificationsAsync(
        AppDbContext db,
        string keyPrefix,
        string type,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var admins = await db.Users.AsNoTracking()
            .Where(user => !user.IsArchived && user.Roles != null && user.Roles.Contains("Admin"))
            .Select(user => new { user.Id, user.Email })
            .ToListAsync(cancellationToken);

        foreach (var admin in admins)
        {
            QueueUserNotification(db, $"{keyPrefix}:admin:{admin.Id}", admin.Id, type, title, message);
            if (!string.IsNullOrWhiteSpace(admin.Email))
            {
                db.IdentityNotificationOutbox.Add(new IdentityNotificationOutbox
                {
                    IdempotencyKey = $"{keyPrefix}:admin:{admin.Id}:email",
                    Channel = "email",
                    RecipientUserId = admin.Id,
                    RecipientEmail = admin.Email.Trim(),
                    Type = type,
                    Title = title,
                    Message = message,
                    Status = "pending",
                    AvailableAt = DateTime.UtcNow
                });
            }
        }
    }

    private static void QueueUserNotification(
        AppDbContext db,
        string key,
        int userId,
        string type,
        string title,
        string message)
    {
        db.IdentityNotificationOutbox.Add(new IdentityNotificationOutbox
        {
            IdempotencyKey = key + ":in-app",
            Channel = "in_app",
            RecipientUserId = userId,
            Type = type,
            Title = title,
            Message = message,
            Status = "pending",
            AvailableAt = DateTime.UtcNow
        });
    }

    private static System.Threading.Tasks.Task<bool> HasEventAsync(
        AppDbContext db, int userId, string eventType, CancellationToken cancellationToken) =>
        db.IdentityVerificationEvents.AnyAsync(
            e => e.UserId == userId && e.Provider == "manual_document_review" && e.EventType == eventType,
            cancellationToken);

    private static IdentityVerificationEvent CreateEvent(int userId, string eventType, string resultStatus)
    {
        var id = Guid.NewGuid().ToString("N");
        return new IdentityVerificationEvent
        {
            UserId = userId,
            Provider = "manual_document_review",
            EventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant(),
            EventType = eventType,
            ResultStatus = resultStatus,
            ReceivedAt = DateTime.UtcNow
        };
    }
}
