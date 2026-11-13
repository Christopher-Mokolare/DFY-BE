using DoForYou.API.Data;
using DoForYou.API.Services;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public sealed class DatabaseSeederTests
{
    [Fact]
    public async Task SeedAsync_never_creates_accounts_or_credentials()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"database-seeder-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);

        await DatabaseSeeder.SeedAsync(context);

        Assert.Empty(await context.Users.ToListAsync());
        Assert.NotEmpty(await context.Categories.ToListAsync());
    }
}
