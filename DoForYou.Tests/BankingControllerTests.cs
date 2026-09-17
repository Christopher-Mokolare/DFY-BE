using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public class BankingControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_UsesOzowCanonicalBankDetails()
    {
        using var context = CreateContext();

        var user = CreateUser(10);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService
        {
            VerificationResult =
                new BankVerificationResult(
                    true,
                    "Bank account verified.")
        };

        var banks = new FakeOzowBankService(
            new[]
            {
                new OzowBank(
                    "BANK-GROUP-123",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankGroupId = "BANK-GROUP-123",

                    // Deliberately incorrect client display values.
                    BankName = "Fake Client Bank",
                    BranchCode = "999999",

                    AccountNumber = "1234567890",
                    AccountHolderName = "John Runner",
                    AccountType = "Savings"
                });

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        var account =
            await context.BankAccounts.SingleAsync();

        Assert.True(response.Success);

        // Server must use Ozow's canonical values.
        Assert.Equal("Canonical Bank", account.BankName);
        Assert.Equal("BANK-GROUP-123", account.BankGroupId);
        Assert.Equal("123456", account.BranchCode);

        Assert.True(account.IsVerified);
        Assert.NotNull(account.VerifiedAt);

        Assert.Equal(1, banking.VerifyCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_RejectsUnknownOzowBank()
    {
        using var context = CreateContext();

        var user = CreateUser(11);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService();

        var banks = new FakeOzowBankService(
            new[]
            {
                new OzowBank(
                    "BANK-GROUP-123",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankGroupId = "NOT-A-REAL-BANK",
                    BankName = "Canonical Bank",
                    AccountNumber = "1234567890",
                    AccountHolderName = "Runner User",
                    BranchCode = "123456",
                    AccountType = "Savings"
                });

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.False(response.Success);

        Assert.Equal(
            "The selected bank is not currently available for Ozow payouts.",
            response.Message);

        Assert.Empty(
            await context.BankAccounts.ToListAsync());

        // Verification must not run for an invalid bank selection.
        Assert.Equal(0, banking.VerifyCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_FailedVerification_RemainsUnverified()
    {
        using var context = CreateContext();

        var user = CreateUser(12);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService
        {
            VerificationResult =
                new BankVerificationResult(
                    false,
                    "Bank verification is temporarily unavailable.")
        };

        var banks = new FakeOzowBankService(
            new[]
            {
                new OzowBank(
                    "BANK-GROUP-123",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankGroupId = "BANK-GROUP-123",
                    BankName = "Fake Bank",
                    AccountNumber = "1234567890",
                    AccountHolderName = "Jane Runner",
                    BranchCode = "999999",
                    AccountType = "Savings"
                });

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        var account =
            await context.BankAccounts.SingleAsync();

        Assert.False(response.Success);
        Assert.False(account.IsVerified);
        Assert.Null(account.VerifiedAt);

        Assert.Equal(1, banking.VerifyCallCount);
    }

    private static AppDbContext CreateContext()
    {
        var options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(
                    Guid.NewGuid().ToString())
                .Options;

        return new AppDbContext(options);
    }

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = id,
            FirstName = "Test",
            LastName = "Runner",
            Email = $"runner{id}@example.com",
            PhoneNumber = "0712345678",
            IdNumber = "8001015009087",
            UserType = "runner",
            ProfileCompleted = true,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "test-hash",
            IsVerified = true
        };
    }

    private static BankingController CreateController(
        AppDbContext context,
        int userId,
        IBankingService bankingService,
        IOzowBankService ozowBankService)
    {
        var controller =
            new BankingController(
                context,
                bankingService,
                ozowBankService);

        controller.ControllerContext =
            new ControllerContext
            {
                HttpContext =
                    new DefaultHttpContext
                    {
                        User =
                            new ClaimsPrincipal(
                                new ClaimsIdentity(
                                    new[]
                                    {
                                        new Claim(
                                            ClaimTypes.NameIdentifier,
                                            userId.ToString())
                                    },
                                    "TestAuth"))
                    }
            };

        return controller;
    }

    private sealed class FakeBankingService : IBankingService
    {
        public BankVerificationResult VerificationResult { get; set; } =
            new BankVerificationResult(
                false,
                "Verification failed.");

        public int VerifyCallCount { get; private set; }

        public System.Threading.Tasks.Task<bool>
            ValidateBankAccountAsync(
                string bankName,
                string accountNumber,
                string branchCode)
        {
            return System.Threading.Tasks.Task.FromResult(
                !string.IsNullOrWhiteSpace(bankName) &&
                accountNumber.Length == 10 &&
                branchCode.Length == 6);
        }

        public System.Threading.Tasks.Task<BankVerificationResult>
            VerifyBankAccountAsync(
                User user,
                BankAccount bankAccount,
                CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;

            return System.Threading.Tasks.Task.FromResult(
                VerificationResult);
        }
    }

    private sealed class FakeOzowBankService : IOzowBankService
    {
        private readonly IReadOnlyList<OzowBank> _banks;

        public FakeOzowBankService(
            IReadOnlyList<OzowBank> banks)
        {
            _banks = banks;
        }

        public System.Threading.Tasks.Task<IReadOnlyList<OzowBank>>
            GetAvailableBanksAsync(
                CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(_banks);
        }
    }
}
