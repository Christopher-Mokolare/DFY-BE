using DoForYou.API.Models;
using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace DoForYou.API.Services;

public record BankVerificationResult(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IBankingService
{
    Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode);

    Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}

public sealed class BankingService(
    IBankProvider bankProvider,
    AppDbContext context) : IBankingService
{
    public Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode)
    {
        var valid = !string.IsNullOrWhiteSpace(bankName) &&
                    !string.IsNullOrWhiteSpace(accountNumber) &&
                    accountNumber.Length is >= 9 and <= 11 &&
                    accountNumber.All(char.IsDigit) &&
                    !string.IsNullOrWhiteSpace(branchCode) &&
                    branchCode.Length == 6 &&
                    branchCode.All(char.IsDigit);

        return Task.FromResult(valid);
    }

    public async Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        var result = await bankProvider.VerifyBankAccountAsync(
            user,
            bankAccount,
            cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.ProviderReference))
        {
            var payouts = await context.Payouts
                .Include(p => p.Task)
                .Where(p =>
                    p.RunnerId == user.Id &&
                    (p.Status == "Failed" || p.Status == "AwaitingBankDetails") &&
                    (
                        (p.BankAccountId == bankAccount.Id &&
                         p.FailureReason == "Runner bank account is missing its Paystack recipient reference.") ||
                        (p.BankAccountId == null &&
                         p.FailureReason == "Runner has no active verified Paystack bank account.") ||
                        p.FailureReason == "Runner bank account is no longer verified or active."
                    ))
                .ToListAsync(cancellationToken);

            foreach (var payout in payouts)
            {
                payout.BankAccountId = bankAccount.Id;
                payout.BankAccount = bankAccount;
                payout.Status = "Pending";
                payout.FailureReason = null;
                payout.ProcessingError = null;
                payout.NextAttemptAt = null;
                payout.UpdatedAt = DateTime.UtcNow;
                payout.Task.PayoutStatus = "Pending";
                payout.Task.TaskStatus = "PayoutPending";
                payout.Task.UpdatedAt = DateTime.UtcNow;
            }

            if (payouts.Count > 0)
                await context.SaveChangesAsync(cancellationToken);
        }

        return new BankVerificationResult(
            result.Success,
            result.Message,
            result.ProviderReference);
    }
}
