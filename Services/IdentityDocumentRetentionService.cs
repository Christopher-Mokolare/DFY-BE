using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Services;

/// <summary>
/// Purges encrypted identity-document payloads 90 days after a review decision.
/// Audit metadata and the decision history remain available after document deletion.
/// </summary>
public sealed class IdentityDocumentRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdentityDocumentRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredDocumentsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DFY identity-document retention purge failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PurgeExpiredDocumentsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var expired = await context.IdentityVerificationSubmissions
            .Where(s => s.DocumentsDeleteAfter != null &&
                        s.DocumentsDeleteAfter <= now &&
                        s.DocumentsPurgedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var submission in expired)
        {
            Array.Clear(submission.FrontCiphertext);
            Array.Clear(submission.FrontNonce);
            Array.Clear(submission.FrontTag);
            submission.FrontCiphertext = [];
            submission.FrontNonce = [];
            submission.FrontTag = [];
            if (submission.BackCiphertext != null) Array.Clear(submission.BackCiphertext);
            if (submission.BackNonce != null) Array.Clear(submission.BackNonce);
            if (submission.BackTag != null) Array.Clear(submission.BackTag);
            submission.BackCiphertext = null;
            submission.BackNonce = null;
            submission.BackTag = null;
            submission.BackContentType = null;
            submission.DocumentsPurgedAt = now;
        }

        if (expired.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Purged encrypted identity documents for {Count} reviewed DFY submissions.", expired.Count);
        }
    }
}
