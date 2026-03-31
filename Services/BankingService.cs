using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Services;

public interface IBankingService
{
    System.Threading.Tasks.Task<bool> ValidateBankAccountAsync(string bankName, string accountNumber, string branchCode);
    System.Threading.Tasks.Task<string> InitiateWithdrawalAsync(int userId, int bankAccountId, decimal amount);
    System.Threading.Tasks.Task<bool> VerifyOtpAsync(int withdrawalId, string otpCode);
    System.Threading.Tasks.Task<bool> ProcessWithdrawalAsync(int withdrawalId);
    System.Threading.Tasks.Task<decimal> CalculateWithdrawalFeeAsync(decimal amount);
}

public class BankingService : IBankingService
{
    private readonly AppDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;

    public BankingService(AppDbContext context, INotificationService notificationService, IConfiguration configuration)
    {
        _context = context;
        _notificationService = notificationService;
        _configuration = configuration;
    }

    public async System.Threading.Tasks.Task<bool> ValidateBankAccountAsync(string bankName, string accountNumber, string branchCode)
    {
        // In production, integrate with bank validation APIs
        // For now, basic validation
        if (string.IsNullOrEmpty(bankName) || string.IsNullOrEmpty(accountNumber) || string.IsNullOrEmpty(branchCode))
            return false;

        // Validate South African account number format
        if (accountNumber.Length < 9 || accountNumber.Length > 11)
            return false;

        // Validate branch code format (6 digits)
        if (branchCode.Length != 6 || !branchCode.All(char.IsDigit))
            return false;

        return true;
    }

    public async System.Threading.Tasks.Task<string> InitiateWithdrawalAsync(int userId, int bankAccountId, decimal amount)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.WalletBalance < amount)
            throw new InvalidOperationException("Insufficient funds");

        var bankAccount = await _context.BankAccounts
            .FirstOrDefaultAsync(ba => ba.Id == bankAccountId && ba.UserId == userId && ba.IsActive && ba.IsVerified);
        
        if (bankAccount == null)
            throw new InvalidOperationException("Invalid or unverified bank account");

        var fee = await CalculateWithdrawalFeeAsync(amount);
        var totalAmount = amount + fee;

        if (user.WalletBalance < totalAmount)
            throw new InvalidOperationException("Insufficient funds including fees");

        // Generate OTP
        var otpCode = GenerateOtp();
        var otpExpiry = DateTime.UtcNow.AddMinutes(10);

        var withdrawal = new WithdrawalRequest
        {
            UserId = userId,
            BankAccountId = bankAccountId,
            Amount = amount,
            Fee = fee,
            Status = "Pending",
            Reference = GenerateReference(),
            OtpCode = HashOtp(otpCode),
            OtpExpiresAt = otpExpiry,
            OtpVerified = false
        };

        _context.WithdrawalRequests.Add(withdrawal);
        await _context.SaveChangesAsync();

        // Send OTP via SMS/Email (implement SMS service)
        await SendOtpAsync(user.PhoneNumber ?? user.Email, otpCode);

        return withdrawal.Reference!;
    }

    public async System.Threading.Tasks.Task<bool> VerifyOtpAsync(int withdrawalId, string otpCode)
    {
        var withdrawal = await _context.WithdrawalRequests
            .Include(w => w.User)
            .FirstOrDefaultAsync(w => w.Id == withdrawalId);

        if (withdrawal == null || withdrawal.OtpExpiresAt < DateTime.UtcNow)
            return false;

        var hashedOtp = HashOtp(otpCode);
        if (withdrawal.OtpCode != hashedOtp)
            return false;

        withdrawal.OtpVerified = true;
        withdrawal.Status = "Verified";
        await _context.SaveChangesAsync();

        return true;
    }

    public async System.Threading.Tasks.Task<bool> ProcessWithdrawalAsync(int withdrawalId)
    {
        var withdrawal = await _context.WithdrawalRequests
            .Include(w => w.User)
            .Include(w => w.BankAccount)
            .FirstOrDefaultAsync(w => w.Id == withdrawalId && w.OtpVerified);

        if (withdrawal == null)
            return false;

        try
        {
            withdrawal.Status = "Processing";
            withdrawal.ProcessedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Integrate with banking API (PayFast, Ozow, etc.)
            var success = await ProcessBankTransferAsync(withdrawal);

            if (success)
            {
                // Deduct from user wallet
                withdrawal.User.WalletBalance -= (withdrawal.Amount + withdrawal.Fee);
                
                // Create transaction records
                var withdrawalTransaction = new WalletTransaction
                {
                    UserId = withdrawal.UserId,
                    Amount = -withdrawal.Amount,
                    TransactionType = "debit",
                    Status = "completed",
                    Description = $"Withdrawal to {withdrawal.BankAccount.BankName}",
                    Reference = withdrawal.Reference,
                    CreatedAt = DateTime.UtcNow
                };

                var feeTransaction = new WalletTransaction
                {
                    UserId = withdrawal.UserId,
                    Amount = -withdrawal.Fee,
                    TransactionType = "debit",
                    Status = "completed",
                    Description = "Withdrawal fee",
                    Reference = withdrawal.Reference,
                    CreatedAt = DateTime.UtcNow
                };

                _context.WalletTransactions.AddRange(withdrawalTransaction, feeTransaction);

                withdrawal.Status = "Completed";
                withdrawal.CompletedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                // Send notification
                await _notificationService.CreateNotificationAsync(
                    withdrawal.UserId,
                    "withdrawal_completed",
                    "Withdrawal Completed",
                    $"R{withdrawal.Amount:F2} has been transferred to your {withdrawal.BankAccount.BankName} account"
                );

                return true;
            }
            else
            {
                withdrawal.Status = "Failed";
                withdrawal.FailureReason = "Bank transfer failed";
                await _context.SaveChangesAsync();

                await _notificationService.CreateNotificationAsync(
                    withdrawal.UserId,
                    "withdrawal_failed",
                    "Withdrawal Failed",
                    $"Your withdrawal of R{withdrawal.Amount:F2} could not be processed. Please try again."
                );

                return false;
            }
        }
        catch (Exception ex)
        {
            withdrawal.Status = "Failed";
            withdrawal.FailureReason = ex.Message;
            await _context.SaveChangesAsync();
            return false;
        }
    }

    public async System.Threading.Tasks.Task<decimal> CalculateWithdrawalFeeAsync(decimal amount)
    {
        // Tiered fee structure
        if (amount <= 100) return 5.00m;
        if (amount <= 500) return 10.00m;
        if (amount <= 1000) return 15.00m;
        return Math.Min(25.00m, amount * 0.025m); // 2.5% capped at R25
    }

    private string GenerateOtp()
    {
        var random = new Random();
        return random.Next(100000, 999999).ToString();
    }

    private string HashOtp(string otp)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(otp + _configuration["Security:OtpSalt"]));
        return Convert.ToBase64String(hashedBytes);
    }

    private string GenerateReference()
    {
        return $"WD{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Random.Shared.Next(1000, 9999)}";
    }

    private async System.Threading.Tasks.Task SendOtpAsync(string contact, string otpCode)
    {
        // Implement SMS/Email service integration
        // For demo purposes, log the OTP
        Console.WriteLine($"OTP for {contact}: {otpCode}");
    }

    private async System.Threading.Tasks.Task<bool> ProcessBankTransferAsync(WithdrawalRequest withdrawal)
    {
        // Integrate with South African banking APIs
        // PayFast, Ozow, or direct bank integration
        
        // For demo, simulate processing delay
        await System.Threading.Tasks.Task.Delay(2000);
        
        // Simulate 95% success rate
        return Random.Shared.NextDouble() > 0.05;
    }
}