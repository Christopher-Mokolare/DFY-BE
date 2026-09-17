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
    public ActionResult<ApiResponse<object>> Retiredwithdraw()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
        });
    }



    [HttpPost("verify-withdrawal")]
    public ActionResult<ApiResponse<object>> Retiredverifywithdrawal()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal OTP verification has been retired."
        });
    }



    [HttpGet("withdrawals")]
    public ActionResult<ApiResponse<object>> Retiredwithdrawals()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal history has been retired."
        });
    }



    [HttpGet("withdrawal-fee")]
    public ActionResult<ApiResponse<object>> Retiredwithdrawalfee()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal fees are no longer applicable."
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
