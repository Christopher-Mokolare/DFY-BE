using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/banking")]
[Authorize]
public class BankingController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IBankingService _bankingService;

    public BankingController(AppDbContext context, IBankingService bankingService)
    {
        _context = context;
        _bankingService = bankingService;
    }

    [HttpPost("bank-accounts")]
    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<object>>> AddBankAccount([FromBody] AddBankAccountRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Validate bank account
        var isValid = await _bankingService.ValidateBankAccountAsync(request.BankName, request.AccountNumber, request.BranchCode);
        if (!isValid)
            return Ok(new ApiResponse<object> { Success = false, Message = "Invalid bank account details" });

        // Check for duplicate
        var exists = await _context.BankAccounts
            .AnyAsync(ba => ba.UserId == userId && ba.AccountNumber == request.AccountNumber && ba.IsActive);
        
        if (exists)
            return Ok(new ApiResponse<object> { Success = false, Message = "Bank account already exists" });

        var bankAccount = new BankAccount
        {
            UserId = userId.Value,
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            AccountHolderName = request.AccountHolderName,
            BranchCode = request.BranchCode,
            AccountType = request.AccountType,
            IsVerified = false // Requires verification process
        };

        _context.BankAccounts.Add(bankAccount);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { bankAccountId = bankAccount.Id },
            Message = "Bank account added successfully. Verification required before use."
        });
    }

    [HttpGet("bank-accounts")]
    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetBankAccounts()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var accounts = await _context.BankAccounts
            .Where(ba => ba.UserId == userId && ba.IsActive)
            .Select(ba => new
            {
                id = ba.Id,
                bankName = ba.BankName,
                accountNumber = MaskAccountNumber(ba.AccountNumber),
                accountHolderName = ba.AccountHolderName,
                accountType = ba.AccountType,
                isVerified = ba.IsVerified,
                createdAt = ba.CreatedAt
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = accounts
        });
    }

    [HttpPost("withdraw")]
    public async Task<ActionResult<ApiResponse<object>>> InitiateWithdrawal([FromBody] WithdrawRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        try
        {
            // Validate minimum withdrawal
            if (request.Amount < 50)
                return Ok(new ApiResponse<object> { Success = false, Message = "Minimum withdrawal amount is R50" });

            // Validate maximum withdrawal
            if (request.Amount > 10000)
                return Ok(new ApiResponse<object> { Success = false, Message = "Maximum withdrawal amount is R10,000" });

            var fee = await _bankingService.CalculateWithdrawalFeeAsync(request.Amount);
            var reference = await _bankingService.InitiateWithdrawalAsync(userId.Value, request.BankAccountId, request.Amount);

            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = new 
                { 
                    reference,
                    amount = request.Amount,
                    fee,
                    total = request.Amount + fee,
                    message = "OTP sent to your registered contact. Please verify to complete withdrawal."
                }
            });
        }
        catch (Exception ex)
        {
            return Ok(new ApiResponse<object> { Success = false, Message = ex.Message });
        }
    }

    [HttpPost("verify-withdrawal")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyWithdrawal([FromBody] VerifyWithdrawalRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var withdrawal = await _context.WithdrawalRequests
            .FirstOrDefaultAsync(w => w.Reference == request.Reference && w.UserId == userId);

        if (withdrawal == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid withdrawal reference" });

        var verified = await _bankingService.VerifyOtpAsync(withdrawal.Id, request.OtpCode);
        if (!verified)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Invalid or expired OTP" });

        // Process in the request scope; never use a scoped DbContext from Task.Run.
        await _bankingService.ProcessWithdrawalAsync(withdrawal.Id);

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Withdrawal verified and is being processed. You will be notified once completed."
        });
    }

    [HttpGet("withdrawals")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetWithdrawals([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var withdrawals = await _context.WithdrawalRequests
            .Include(w => w.BankAccount)
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new
            {
                id = w.Id,
                reference = w.Reference,
                amount = w.Amount,
                fee = w.Fee,
                status = w.Status,
                bankName = w.BankAccount.BankName,
                accountNumber = MaskAccountNumber(w.BankAccount.AccountNumber),
                createdAt = w.CreatedAt,
                completedAt = w.CompletedAt,
                failureReason = w.FailureReason
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = withdrawals
        });
    }

    [HttpGet("withdrawal-fee")]
    public async Task<ActionResult<ApiResponse<decimal>>> GetWithdrawalFee([FromQuery] decimal amount)
    {
        var fee = await _bankingService.CalculateWithdrawalFeeAsync(amount);
        return Ok(new ApiResponse<decimal>
        {
            Success = true,
            Data = fee
        });
    }

    [HttpGet("banks")]
    public ActionResult<ApiResponse<List<object>>> GetSupportedBanks()
    {
        var banks = new List<object>
        {
            new { code = "ABSA", name = "ABSA", branchCode = "632005" },
            new { code = "STD", name = "Standard Bank", branchCode = "051001" },
            new { code = "FNB", name = "FNB", branchCode = "250655" },
            new { code = "NED", name = "Nedbank", branchCode = "198765" },
            new { code = "CAP", name = "Capitec", branchCode = "470010" },
            new { code = "INV", name = "Investec", branchCode = "580105" },
            new { code = "AFB", name = "African Bank", branchCode = "430000" },
            new { code = "TYM", name = "TymeBank", branchCode = "678910" },
            new { code = "DSC", name = "Discovery Bank", branchCode = "679000" }
        };

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = banks
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private string MaskAccountNumber(string accountNumber)
    {
        if (accountNumber.Length <= 4) return accountNumber;
        return "****" + accountNumber.Substring(accountNumber.Length - 4);
    }
}

public class AddBankAccountRequest
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string AccountType { get; set; } = "Savings";
}

public class WithdrawRequest
{
    public int BankAccountId { get; set; }
    public decimal Amount { get; set; }
}

public class VerifyWithdrawalRequest
{
    public string Reference { get; set; } = string.Empty;
    public string OtpCode { get; set; } = string.Empty;
}