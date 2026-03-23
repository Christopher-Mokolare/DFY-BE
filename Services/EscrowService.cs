using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public interface IEscrowService
{
    Task<bool> ReleaseEscrowAsync(int taskId);
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

    public async Task<bool> ReleaseEscrowAsync(int taskId)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var task = await _context.Tasks
                .Include(t => t.AcceptedByUser)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task == null || task.AcceptedByUserId == null || task.EscrowStatus != "held")
                return false;

            if (task.EscrowHoldUntil.HasValue && task.EscrowHoldUntil > DateTime.UtcNow)
                return false;

            // Update runner wallet
            task.AcceptedByUser!.WalletBalance += task.PayoutAmount;

            // Update task
            task.EscrowStatus = "released";
            task.PaymentStatus = "EscrowReleased";
            task.TaskStatus = "RunnerPaid";
            task.PaidToRunnerAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation("Escrow released for task {TaskId}: R{Payout}", taskId, task.PayoutAmount);
            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
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
