using DoForYou.API.Data;
using DoForYou.API.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DoForYou.Tests;

public sealed class FinancialLedgerServiceTests
{
    private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async System.Threading.Tasks.Task PostAsync_persists_balanced_double_entry_and_computes_raw_account_balances()
    {
        await using var context = CreateContext(nameof(PostAsync_persists_balanced_double_entry_and_computes_raw_account_balances));
        var service = new FinancialLedgerService(context);
        var draft = new FinancialJournalDraft(
            EventType: "payment_collected",
            SourceType: "paystack_transaction",
            SourceId: "TX-LEDGER-001",
            IdempotencyKey: "paystack:charge:TX-LEDGER-001",
            Currency: "ZAR",
            Description: "Verified Paystack collection for a newly paid task",
            OccurredAt: DateTime.UtcNow,
            Lines: new[]
            {
                new FinancialJournalLineDraft("1000", 250m, 0m, "Paystack clearing"),
                new FinancialJournalLineDraft("2000", 0m, 250m, "Funds held for task")
            },
            Provider: "Paystack",
            ProviderReference: "DFY-CHARGE-001");

        var journal = await service.PostAsync(draft);

        Assert.Equal("payment_collected", journal.EventType);
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal(250m, await service.GetAccountBalanceAsync("1000"));
        Assert.Equal(-250m, await service.GetAccountBalanceAsync("2000"));
        Assert.Equal(250m, await context.FinancialJournalLines.SumAsync(line => line.Debit));
        Assert.Equal(250m, await context.FinancialJournalLines.SumAsync(line => line.Credit));
    }

    [Fact]
    public async System.Threading.Tasks.Task PostAsync_is_idempotent_for_the_same_source_and_posting()
    {
        await using var context = CreateContext(nameof(PostAsync_is_idempotent_for_the_same_source_and_posting));
        var service = new FinancialLedgerService(context);
        var draft = new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-002",
            "paystack:charge:TX-LEDGER-002",
            "ZAR",
            "Verified Paystack collection",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 100m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 100m)
            });

        var first = await service.PostAsync(draft);
        var second = await service.PostAsync(draft);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await context.FinancialJournals.ToListAsync());
        Assert.Equal(2, await context.FinancialJournalLines.CountAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Financial_journals_and_lines_cannot_be_modified_after_posting()
    {
        await using var context = CreateContext(nameof(Financial_journals_and_lines_cannot_be_modified_after_posting));
        var service = new FinancialLedgerService(context);
        var journal = await service.PostAsync(new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-APPEND-ONLY",
            "paystack:charge:TX-LEDGER-APPEND-ONLY",
            "ZAR",
            "Append-only journal test",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 75m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 75m)
            }));

        journal.Description = "Attempted edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        context.Entry(journal).State = EntityState.Unchanged;
        var line = await context.FinancialJournalLines.FirstAsync();
        line.Debit = 74m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveChanges_overloads_reject_mutation_of_posted_journals()
    {
        await using var context = CreateContext(nameof(SaveChanges_overloads_reject_mutation_of_posted_journals));
        var service = new FinancialLedgerService(context);
        var journal = await service.PostAsync(new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-SAVE-OVERLOAD",
            "paystack:charge:TX-LEDGER-SAVE-OVERLOAD",
            "ZAR",
            "Append-only overload test",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 25m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 25m)
            }));

        journal.Description = "Attempted sync edit";
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        context.Entry(journal).State = EntityState.Unchanged;
        journal.Description = "Attempted sync overload edit";
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges(acceptAllChangesOnSuccess: false));

        context.Entry(journal).State = EntityState.Unchanged;
        journal.Description = "Attempted async overload edit";
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync(acceptAllChangesOnSuccess: false));
    }

    [Fact]
    public async System.Threading.Tasks.Task PostAsync_rejects_unbalanced_journals_without_persisting_anything()
    {
        await using var context = CreateContext(nameof(PostAsync_rejects_unbalanced_journals_without_persisting_anything));
        var service = new FinancialLedgerService(context);
        var draft = new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-003",
            "paystack:charge:TX-LEDGER-003",
            "ZAR",
            "Unbalanced test posting",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 100m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 99m)
            });

        await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(draft));
        Assert.Empty(await context.FinancialJournals.ToListAsync());
        Assert.Empty(await context.FinancialJournalLines.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Reversal_must_exactly_invert_one_original_journal_and_cannot_be_repeated()
    {
        await using var context = CreateContext(nameof(Reversal_must_exactly_invert_one_original_journal_and_cannot_be_repeated));
        var service = new FinancialLedgerService(context);
        var original = await service.PostAsync(new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-REVERSAL",
            "paystack:charge:TX-LEDGER-REVERSAL",
            "ZAR",
            "Original charge",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 100m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 100m)
            }));

        var reversal = await service.PostAsync(new FinancialJournalDraft(
            "reversal",
            "refund",
            "REF-LEDGER-REVERSAL",
            "paystack:refund:REF-LEDGER-REVERSAL",
            "ZAR",
            "Reverse confirmed refund",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("2000", 100m, 0m),
                new FinancialJournalLineDraft("1000", 0m, 100m)
            },
            ReversalOfJournalId: original.Id));

        Assert.Equal(original.Id, reversal.ReversalOfJournalId);
        Assert.Equal(0m, await service.GetAccountBalanceAsync("1000"));
        Assert.Equal(0m, await service.GetAccountBalanceAsync("2000"));

        var invalidReversal = new FinancialJournalDraft(
            "reversal",
            "refund",
            "REF-LEDGER-REVERSAL-2",
            "paystack:refund:REF-LEDGER-REVERSAL-2",
            "ZAR",
            "Attempt second reversal",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("2000", 99m, 0m),
                new FinancialJournalLineDraft("1000", 0m, 99m)
            },
            ReversalOfJournalId: original.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(invalidReversal));
    }

    [Fact]
    public async System.Threading.Tasks.Task PostAsync_rejects_reuse_of_an_idempotency_key_for_a_different_amount()
    {
        await using var context = CreateContext(nameof(PostAsync_rejects_reuse_of_an_idempotency_key_for_a_different_amount));
        var service = new FinancialLedgerService(context);
        var draft = new FinancialJournalDraft(
            "payment_collected",
            "paystack_transaction",
            "TX-LEDGER-004",
            "paystack:charge:TX-LEDGER-004",
            "ZAR",
            "Verified Paystack collection",
            DateTime.UtcNow,
            new[]
            {
                new FinancialJournalLineDraft("1000", 100m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 100m)
            });
        await service.PostAsync(draft);

        var altered = draft with
        {
            Lines = new[]
            {
                new FinancialJournalLineDraft("1000", 120m, 0m),
                new FinancialJournalLineDraft("2000", 0m, 120m)
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(altered));
        Assert.Single(await context.FinancialJournals.ToListAsync());
        Assert.Equal(100m, await service.GetAccountBalanceAsync("1000"));
    }
}
