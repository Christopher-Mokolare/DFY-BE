
namespace DoForYou.API.Services;

public interface IBankingService
{
    Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode);
}

public class BankingService : IBankingService
{
    public async Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode)
    {
        // Basic South African bank-account format validation.
        // Actual account ownership/verification is handled separately.
        if (string.IsNullOrWhiteSpace(bankName) ||
            string.IsNullOrWhiteSpace(accountNumber) ||
            string.IsNullOrWhiteSpace(branchCode))
        {
            return false;
        }

        if (accountNumber.Length < 9 ||
            accountNumber.Length > 11 ||
            !accountNumber.All(char.IsDigit))
        {
            return false;
        }

        if (branchCode.Length != 6 ||
            !branchCode.All(char.IsDigit))
        {
            return false;
        }

        await Task.CompletedTask;
        return true;
    }
}
