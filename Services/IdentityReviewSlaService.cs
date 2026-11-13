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
                await QueueReviewerNotificationsAsync(db, $"identity-sla:{nearEvent}", "identity_review_near_deadline",
                    "DFY Document Review approaching deadline",
                    $"Review case {submission.Id} has used 75% of its one-business-day review target.",
                    includeBackupReviewers: false, cancellationToken);
                db.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, nearEvent, "alerted"));
                await db.SaveChangesAsync(cancellationToken);
            }

            if (now >= dueAt && !await HasEventAsync(db, submission.UserId, overdueEvent, cancellationToken))
            {
                await QueueReviewerNotificationsAsync(db, $"identity-sla:{overdueEvent}", "identity_review_overdue",
                    "DFY Document Review overdue",
                    $"Review case {submission.Id} has breached its one-business-day review target. Please assign a reviewer immediately.",
                    includeBackupReviewers: false, cancellationToken);
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
                await QueueReviewerNotificationsAsync(db, $"identity-sla:{escalationEvent}", "identity_review_escalated",
                    "DFY Document Review requires escalation",
                    $"Review case {submission.Id} has exceeded the internal two-business-day maximum. A configured backup reviewer must take ownership.",
                    includeBackupReviewers: true, cancellationToken);
                db.IdentityVerificationEvents.Add(CreateEvent(submission.UserId, escalationEvent, "escalated"));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Routes operational alerts only to explicitly configured reviewer accounts.
    /// Configure IdentityVerification:PrimaryReviewerUserIds and
    /// IdentityVerification:BackupReviewerUserIds as comma-separated internal user IDs.
    /// An empty configuration deliberately sends no administrator-wide broadcast.
    /// </summary>
    private async System.Threading.Tasks.Task QueueReviewerNotificationsAsync(
        AppDbContext db,
        string keyPrefix,
        string type,
        string title,
        string message,
        bool includeBackupReviewers,
        CancellationToken cancellationToken)
    {
        var reviewerIds = ReadConfiguredIds(configuration["IdentityVerification:PrimaryReviewerUserIds"]);
        if (includeBackupReviewers)
            reviewerIds.UnionWith(ReadConfiguredIds(configuration["IdentityVerification:BackupReviewerUserIds"]));

        if (reviewerIds.Count == 0)
        {
            logger.LogWarning(
                "Identity review SLA alert {AlertType} has no configured {ReviewerGroup} recipients. Configure reviewer user IDs.",
                type,
                includeBackupReviewers ? "primary or backup reviewer" : "primary reviewer");
            return;
        }

        var configuredUsers = await db.Users.AsNoTracking()
            .Where(user => reviewerIds.Contains(user.Id) && !user.IsArchived)
            .Select(user => new { user.Id, user.Email, user.Roles })
            .ToListAsync(cancellationToken);

        var reviewers = configuredUsers
            .Where(user => user.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(role => string.Equals(role.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

        foreach (var reviewer in reviewers)
        {
            QueueUserNotification(db, $"{keyPrefix}:reviewer:{reviewer.Id}", reviewer.Id, type, title, message);
            if (!string.IsNullOrWhiteSpace(reviewer.Email))
            {
                db.IdentityNotificationOutbox.Add(new IdentityNotificationOutbox
                {
                    IdempotencyKey = $"{keyPrefix}:reviewer:{reviewer.Id}:email",
                    Channel = "email",
                    RecipientUserId = reviewer.Id,
                    RecipientEmail = reviewer.Email.Trim(),
                    Type = type,
                    Title = title,
                    Message = message,
                    Status = "pending",
                    AvailableAt = DateTime.UtcNow
                });
            }
        }

        if (reviewers.Count == 0)
        {
            logger.LogWarning(
                "Identity review SLA alert {AlertType} had configured recipients, but none were active administrator accounts.",
                type);
        }
    }

    private static HashSet<int> ReadConfiguredIds(string? configuredIds)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(configuredIds))
            return ids;

        foreach (var item in configuredIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(item, out var id) && id > 0)
                ids.Add(id);
        }

        return ids;
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
