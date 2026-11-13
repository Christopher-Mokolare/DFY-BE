using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public sealed record FinancialJournalLineDraft(
    string AccountCode,
    decimal Debit,
    decimal Credit,
    string? Memo = null);

public sealed record FinancialJournalDraft(
    string EventType,
    string SourceType,
    string SourceId,
    string IdempotencyKey,
    string Currency,
    string Description,
    DateTime OccurredAt,
    IReadOnlyCollection<FinancialJournalLineDraft> Lines,
    string? Provider = null,
    string? ProviderReference = null,
    int? CreatedByUserId = null,
    long? ReversalOfJournalId = null);

public interface IFinancialLedgerService
{
    Task<FinancialJournal> PostAsync(FinancialJournalDraft draft, CancellationToken cancellationToken = default);
    Task<FinancialJournal> ReverseAsync(
        long originalJournalId,
        string sourceType,
        string sourceId,
        string idempotencyKey,
        string description,
        string? provider = null,
        string? providerReference = null,
        int? createdByUserId = null,
        DateTime? occurredAt = null,
        CancellationToken cancellationToken = default);
    Task<decimal> GetAccountBalanceAsync(string accountCode, CancellationToken cancellationToken = default);
}

public sealed class FinancialLedgerService : IFinancialLedgerService
{
    private readonly AppDbContext _context;

    public FinancialLedgerService(AppDbContext context) => _context = context;

    public async Task<FinancialJournal> PostAsync(
        FinancialJournalDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        var existing = await _context.FinancialJournals
            .Include(journal => journal.Lines)
                .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(journal => journal.IdempotencyKey == draft.IdempotencyKey, cancellationToken);
        if (existing != null)
        {
            EnsureSamePosting(existing, draft);
            return existing;
        }

        var duplicateSource = await _context.FinancialJournals
            .FirstOrDefaultAsync(journal =>
                journal.SourceType == draft.SourceType &&
                journal.SourceId == draft.SourceId &&
                journal.EventType == draft.EventType,
                cancellationToken);
        if (duplicateSource != null)
            throw new InvalidOperationException("A journal already exists for this source event under a different idempotency key.");

        var codes = draft.Lines.Select(line => line.AccountCode).Distinct(StringComparer.Ordinal).ToArray();
        var accounts = await _context.FinancialAccounts
            .Where(account => codes.Contains(account.Code) && account.IsActive)
            .ToDictionaryAsync(account => account.Code, StringComparer.Ordinal, cancellationToken);

        if (accounts.Count != codes.Length)
            throw new InvalidOperationException("One or more ledger accounts are missing or inactive.");

        foreach (var line in draft.Lines)
        {
            var account = accounts[line.AccountCode];
            if (!string.Equals(account.Currency, draft.Currency, StringComparison.Ordinal))
                throw new InvalidOperationException("All journal lines must use the journal currency.");
        }

        if (draft.ReversalOfJournalId.HasValue)
        {
            var original = await _context.FinancialJournals
                .Include(journal => journal.Lines)
                    .ThenInclude(line => line.Account)
                .FirstOrDefaultAsync(journal => journal.Id == draft.ReversalOfJournalId.Value, cancellationToken);
            if (original == null || original.Currency != draft.Currency)
                throw new InvalidOperationException("The original journal for this reversal does not exist or has a different currency.");

            var alreadyReversed = await _context.FinancialJournals.AnyAsync(
                journal => journal.ReversalOfJournalId == original.Id,
                cancellationToken);
            if (alreadyReversed)
                throw new InvalidOperationException("The original journal has already been reversed.");

            var expectedReversal = original.Lines
                .GroupBy(line => line.Account.Code, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (
                    AccountCode: group.Key,
                    Debit: group.Sum(line => line.Credit),
                    Credit: group.Sum(line => line.Debit)))
                .ToArray();
            var actualReversal = draft.Lines
                .GroupBy(line => line.AccountCode, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (
                    AccountCode: group.Key,
                    Debit: group.Sum(line => line.Debit),
                    Credit: group.Sum(line => line.Credit)))
                .ToArray();
            if (!expectedReversal.SequenceEqual(actualReversal))
                throw new InvalidOperationException("A reversal must exactly invert every line in the original journal.");
        }

        await using var transaction = _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var journal = new FinancialJournal
        {
            JournalNumber = $"DFY-JRN-{Guid.NewGuid():N}",
            EventType = draft.EventType,
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            IdempotencyKey = draft.IdempotencyKey,
            Currency = draft.Currency,
            Description = draft.Description.Trim(),
            Provider = draft.Provider,
            ProviderReference = draft.ProviderReference,
            CreatedByUserId = draft.CreatedByUserId,
            OccurredAt = draft.OccurredAt.Kind switch
            {
                DateTimeKind.Utc => draft.OccurredAt,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(draft.OccurredAt, DateTimeKind.Utc),
                _ => draft.OccurredAt.ToUniversalTime()
            },
            RecordedAt = DateTime.UtcNow,
            ReversalOfJournalId = draft.ReversalOfJournalId
        };

        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines.ElementAt(index);
            journal.Lines.Add(new FinancialJournalLine
            {
                LineNumber = index + 1,
                Account = accounts[line.AccountCode],
                Debit = line.Debit,
                Credit = line.Credit,
                Currency = draft.Currency,
                Memo = string.IsNullOrWhiteSpace(line.Memo) ? null : line.Memo.Trim()
            });
        }

        _context.FinancialJournals.Add(journal);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);
            return journal;
        }
        catch (DbUpdateException)
        {
            if (transaction != null)
                await transaction.RollbackAsync(cancellationToken);

            foreach (var line in journal.Lines)
                _context.Entry(line).State = EntityState.Detached;
            _context.Entry(journal).State = EntityState.Detached;

            var concurrent = await _context.FinancialJournals
                .Include(item => item.Lines)
                    .ThenInclude(line => line.Account)
                .FirstOrDefaultAsync(item => item.IdempotencyKey == draft.IdempotencyKey, cancellationToken);
            if (concurrent != null)
            {
                EnsureSamePosting(concurrent, draft);
                return concurrent;
            }
            throw;
        }
    }

    public async Task<FinancialJournal> ReverseAsync(
        long originalJournalId,
        string sourceType,
        string sourceId,
        string idempotencyKey,
        string description,
        string? provider = null,
        string? providerReference = null,
        int? createdByUserId = null,
        DateTime? occurredAt = null,
        CancellationToken cancellationToken = default)
    {
        var original = await _context.FinancialJournals
            .Include(journal => journal.Lines)
                .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(journal => journal.Id == originalJournalId, cancellationToken);
        if (original == null)
            throw new KeyNotFoundException("The original journal for this reversal was not found.");

        var lines = original.Lines
            .OrderBy(line => line.LineNumber)
            .Select(line => new FinancialJournalLineDraft(
                line.Account.Code,
                line.Credit,
                line.Debit,
                "Reversal of original financial journal"))
            .ToArray();

        return await PostAsync(new FinancialJournalDraft(
            EventType: "reversal",
            SourceType: sourceType,
            SourceId: sourceId,
            IdempotencyKey: idempotencyKey,
            Currency: original.Currency,
            Description: description,
            OccurredAt: occurredAt ?? DateTime.UtcNow,
            Lines: lines,
            Provider: provider,
            ProviderReference: providerReference,
            CreatedByUserId: createdByUserId,
            ReversalOfJournalId: original.Id), cancellationToken);
    }

    public async Task<decimal> GetAccountBalanceAsync(
        string accountCode,
        CancellationToken cancellationToken = default)
    {
        var account = await _context.FinancialAccounts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Code == accountCode, cancellationToken);
        if (account == null)
            throw new KeyNotFoundException("Ledger account was not found.");

        var debit = await _context.FinancialJournalLines.AsNoTracking()
            .Where(line => line.AccountId == account.Id)
            .SumAsync(line => (decimal?)line.Debit, cancellationToken) ?? 0m;
        var credit = await _context.FinancialJournalLines.AsNoTracking()
            .Where(line => line.AccountId == account.Id)
            .SumAsync(line => (decimal?)line.Credit, cancellationToken) ?? 0m;

        // Raw debit-minus-credit is retained; presentation applies normal balance
        // conventions based on the account type.
        return decimal.Round(debit - credit, 2, MidpointRounding.AwayFromZero);
    }

    private static void ValidateDraft(FinancialJournalDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.EventType) || draft.EventType.Length > 40 ||
            string.IsNullOrWhiteSpace(draft.SourceType) || draft.SourceType.Length > 40 ||
            string.IsNullOrWhiteSpace(draft.SourceId) || draft.SourceId.Length > 120 ||
            string.IsNullOrWhiteSpace(draft.IdempotencyKey) || draft.IdempotencyKey.Length > 120 ||
            string.IsNullOrWhiteSpace(draft.Description) || draft.Description.Length > 500)
            throw new ArgumentException("Journal event, source, idempotency key and description are required and must fit their limits.");

        if (!string.Equals(draft.Currency, "ZAR", StringComparison.Ordinal))
            throw new ArgumentException("Only ZAR ledger postings are currently supported.");

        if (draft.Lines == null || draft.Lines.Count < 2)
            throw new ArgumentException("A journal requires at least two lines.");

        decimal totalDebits = 0m;
        decimal totalCredits = 0m;
        foreach (var line in draft.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.AccountCode) ||
                line.Debit < 0m || line.Credit < 0m ||
                (line.Debit > 0m && line.Credit > 0m) ||
                (line.Debit == 0m && line.Credit == 0m) ||
                decimal.Round(line.Debit, 2) != line.Debit ||
                decimal.Round(line.Credit, 2) != line.Credit)
                throw new ArgumentException("Each journal line must contain exactly one positive ZAR debit or credit with no fractional cents.");

            totalDebits += line.Debit;
            totalCredits += line.Credit;
        }

        if (totalDebits <= 0m || totalDebits != totalCredits)
            throw new ArgumentException("Journal debits and credits must be equal and greater than zero.");

        if (draft.ReversalOfJournalId.HasValue && draft.EventType != "reversal")
            throw new ArgumentException("A reversal reference is only valid for a reversal journal.");
        if (draft.EventType == "reversal" && !draft.ReversalOfJournalId.HasValue)
            throw new ArgumentException("A reversal journal must identify the original journal.");
    }

    private static void EnsureSamePosting(FinancialJournal existing, FinancialJournalDraft draft)
    {
        var expected = draft.Lines
            .OrderBy(line => line.AccountCode, StringComparer.Ordinal)
            .ThenBy(line => line.Debit)
            .ThenBy(line => line.Credit)
            .Select(line => (line.AccountCode, line.Debit, line.Credit))
            .ToArray();
        var actual = existing.Lines
            .OrderBy(line => line.Account.Code, StringComparer.Ordinal)
            .ThenBy(line => line.Debit)
            .ThenBy(line => line.Credit)
            .Select(line => (line.Account.Code, line.Debit, line.Credit))
            .ToArray();

        if (existing.EventType != draft.EventType ||
            existing.SourceType != draft.SourceType ||
            existing.SourceId != draft.SourceId ||
            existing.Currency != draft.Currency ||
            existing.ReversalOfJournalId != draft.ReversalOfJournalId ||
            !expected.SequenceEqual(actual))
            throw new InvalidOperationException("The idempotency key is already bound to a different financial posting.");
    }
}
