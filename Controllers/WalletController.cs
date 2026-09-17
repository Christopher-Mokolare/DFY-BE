using DoForYou.API.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/wallet")]
public class WalletController : ControllerBase
{
    [HttpGet("balance")]
    public ActionResult<ApiResponse<object>> GetWalletBalance()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallets have been retired. Runner earnings are paid directly to the verified bank account."
        });
    }

    [HttpGet("transactions")]
    public ActionResult<ApiResponse<object>> GetTransactions()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet transactions have been retired."
        });
    }

    [HttpPost("withdraw")]
    public ActionResult<ApiResponse<object>> RequestWithdrawal()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
        });
    }

    [HttpGet("withdrawals/pending")]
    public ActionResult<ApiResponse<object>> GetPendingWithdrawals()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired."
        });
    }

    [HttpPost("withdrawals/verify")]
    public ActionResult<ApiResponse<object>> VerifyWithdrawal()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals and withdrawal OTP verification have been retired."
        });
    }
}
