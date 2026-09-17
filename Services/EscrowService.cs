using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DoForYou.API.Data;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public interface IEscrowService
{
    Task<bool> ReleaseEscrowAsync(int taskId, bool force = false);
    Task<int> AutoReleaseExpiredEscrowsAsync();
    decimal CalculateCommission(decimal amount);
}

public class EscrowService : IEscrowService
{
    private readonly AppDbContext _context;
    private readonly ILogger<EscrowService> _logger;

    public EscrowService(AppDbContext context, ILogger<EscrowService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> ReleaseEscrowAsync(int taskId, bool force = false)
    {
        IDbContextTransaction? transaction = null;
        if (_context.Database.IsRelational())
        {
            transaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            var task = await _context.Tasks
                .Include(t => t.AcceptedByUser)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task == null || task.AcceptedByUserId == null)
                return false;

            if (task.EscrowStatus != "held" && task.PaymentStatus != "EscrowHeld")
                return false;

            if (task.EscrowHoldUntil.HasValue && task.EscrowHoldUntil > DateTime.UtcNow)
                return false;

            // The runner is paid directly to a verified bank account.
            // No runner wallet is credited.

            var existingPayout = await _context.Payouts
                .FirstOrDefaultAsync(p =>
                    p.TaskId == task.Id &&
                    p.Status != "Cancelled");

            if (existingPayout != null)
            {
                if (transaction != null)
                {
                    await transaction.CommitAsync();
                }

                return true;
            }

            var bankAccount = await _context.BankAccounts
                .FirstOrDefaultAsync(b =>
                    b.UserId == task.AcceptedByUserId.Value &&
                    b.IsActive &&
                    b.IsVerified);

            if (bankAccount == null)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }

                _logger.LogWarning(
                    "Cannot release task {TaskId}: runner {RunnerId} has no active verified bank account.",
                    task.TaskId,
                    task.AcceptedByUserId.Value);

                return false;
            }

            var merchantReference = $"DFY-PAYOUT-{task.TaskId}";

            var payout = new Payout
            {
                TaskId = task.Id,
                RunnerId = task.AcceptedByUserId.Value,
                BankAccountId = bankAccount.Id,
                Amount = task.PayoutAmount,
                Status = "Pending",
                Provider = "Ozow",
                MerchantReference = merchantReference,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Payouts.Add(payout);

            task.EscrowStatus = "released";
            task.PaymentStatus = "EscrowReleased";
            task.TaskStatus = "PayoutPending";
            task.PayoutStatus = "Pending";
            task.PayoutReference = merchantReference;
            task.PayoutInitiatedAt = DateTime.UtcNow;
            task.PaidToRunnerAt = null;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            if (transaction != null)
            {
                await transaction.CommitAsync();
            }

            _logger.LogInformation("Escrow released for task {TaskId}: R{Payout}", task.TaskId, task.PayoutAmount);
            return true;
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync();
            }
            _logger.LogError(ex, "Error releasing escrow for task {TaskId}", taskId);
            return false;
        }
    }

    public async Task<int> AutoReleaseExpiredEscrowsAsync()
    {
        var expiredTasks = await _context.Tasks
            .Where(t => t.EscrowStatus == "held" &&
                       t.EscrowHoldUntil.HasValue &&
                       t.EscrowHoldUntil <= DateTime.UtcNow)
            .ToListAsync();

        int count = 0;
        foreach (var task in expiredTasks)
        {
            if (await ReleaseEscrowAsync(task.Id)) count++;
        }
        return count;
    }

    public decimal CalculateCommission(decimal amount)
    {
        return Math.Max(amount * 0.15m, 5.00m);
    }
}
