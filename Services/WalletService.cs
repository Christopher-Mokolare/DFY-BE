using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public interface IWalletService
{
    Task<decimal> GetBalanceAsync(int userId);
}

public class WalletService : IWalletService
{
    private readonly AppDbContext _context;

    public WalletService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<decimal> GetBalanceAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user?.WalletBalance ?? 0;
    }
}
