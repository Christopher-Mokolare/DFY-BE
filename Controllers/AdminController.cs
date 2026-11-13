using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Authorization;
using DoForYou.API.DTOs;
using DoForYou.API.Services;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "Admin")]
    [Authorize(Policy = "AdminMfa")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEscrowService _escrowService;
    private readonly IUserPolicyService _userPolicyService;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IBankingService _bankingService;
    private readonly IPaystackSettlementProvider _paystackSettlementProvider;

    public AdminController(AppDbContext context, IEscrowService escrowService, IUserPolicyService userPolicyService, IPaymentProvider paymentProvider, IBankingService bankingService, IPaystackSettlementProvider paystackSettlementProvider)
    {
        _context = context;
        _escrowService = escrowService;
        _userPolicyService = userPolicyService;
        _paymentProvider = paymentProvider;
        _bankingService = bankingService;
        _paystackSettlementProvider = paystackSettlementProvider;
    }

    private static string GetMarketplaceRole(string? userType) =>
        userType?.Trim().ToLowerInvariant() switch
        {
            "creator" => "Creator",
            "runner" => "Runner",
            "both" => "Creator & Runner",
            _ => "Not selected"
        };

    private async Task<bool> IsFreshActiveSuperAdminAsync(int userId)
    {
        var actor = await _context.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsArchived && !user.IsSuspended)
            .Select(user => new { user.Roles, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
            .FirstOrDefaultAsync();
        return actor?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true &&
            actor.AuthenticatorTwoFactorEnabled &&
            actor.LastTwoFactorVerifiedAt.HasValue &&
            actor.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.AddMinutes(-15);
    }

    private async System.Threading.Tasks.Task ExpireOpenFinancialApprovalsAsync(int taskId, DateTime now, CancellationToken cancellationToken = default)
    {
        var expiredQuery = _context.FinancialApprovalRequests.Where(item =>
            item.TaskId == taskId &&
            (item.Status == "Pending" || item.Status == "Approved") &&
            item.ExpiresAt <= now);
        if (_context.Database.IsRelational())
        {
            var expiredIds = await expiredQuery.Select(item => item.Id).ToListAsync(cancellationToken);
            if (expiredIds.Count == 0) return;
            await expiredQuery.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "Expired"), cancellationToken);
            foreach (var expiredId in expiredIds)
            {
                _context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = GetAdminId(),
                    Action = "FinancialApprovalExpired",
                    EntityType = "FinancialApprovalRequest",
                    EntityId = expiredId,
                    OldValues = "Pending or Approved",
                    NewValues = $"Expired at {now:O}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
            }
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var expired = await expiredQuery.ToListAsync(cancellationToken);
            foreach (var item in expired)
            {
                item.Status = "Expired";
                _context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = GetAdminId(),
                    Action = "FinancialApprovalExpired",
                    EntityType = "FinancialApprovalRequest",
                    EntityId = item.Id,
                    OldValues = "Pending or Approved",
                    NewValues = $"Expired at {now:O}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
            }
            if (expired.Count > 0)
                await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private int? GetAdminId() =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private async System.Threading.Tasks.Task WriteAuditAsync(string action, string entityType, int? entityId, string? oldVal = null, string? newVal = null)
    {
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = GetAdminId(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldVal,
            NewValues = newVal,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Retrieves Paystack settlement batches and their transactions, then compares transaction
    /// references and amounts with DFY's persisted payment records. Read-only; no money moves.
    /// </summary>
    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("financial-reconciliation/paystack-settlements")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaystackSettlementReconciliation(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int maxSettlements = 25,
        CancellationToken cancellationToken = default)
    {
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var start = from?.ToUniversalTime() ?? end.AddDays(-30);
        maxSettlements = Math.Clamp(maxSettlements, 1, 50);

        if (end > DateTimeOffset.UtcNow.AddMinutes(5))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The settlement end date cannot be in the future."
            });
        if (start > end)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The settlement start date must be on or before the end date."
            });
        if ((end - start).TotalDays > 90)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "Use a date range of 90 days or less; run separate reports for longer periods."
            });

        var providerResult = await _paystackSettlementProvider.GetSettlementsAsync(
            start, end, maxSettlements, cancellationToken);

        if (!providerResult.Success)
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Success = false,
                Message = providerResult.Error ?? "Paystack settlement records could not be retrieved.",
                Data = new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    fromUtc = start,
                    toUtc = end,
                    provider = "Paystack",
                    providerRecordsRetrieved = false
                }
            });

        var references = providerResult.Transactions
            .Select(t => t.Reference)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToArray();

        var attempts = references.Length == 0
            ? new List<Models.PaymentAttempt>()
            : await _context.PaymentAttempts.AsNoTracking()
                .Where(p => p.Provider == "Paystack" && references.Contains(p.Reference))
                .ToListAsync(cancellationToken);

        var attemptByReference = attempts
            .GroupBy(p => p.Reference, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var taskIds = attempts.Select(p => p.TaskId).Distinct().ToArray();
        var tasks = taskIds.Length == 0
            ? new List<Models.Task>()
            : await _context.Tasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

        var taskById = tasks.ToDictionary(t => t.Id);
        var payments = taskIds.Length == 0
            ? new List<Models.Payment>()
            : await _context.Payments.AsNoTracking()
                .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
                .ToListAsync(cancellationToken);

        var transactionRows = providerResult.Transactions.Select(transaction =>
        {
            attemptByReference.TryGetValue(transaction.Reference, out var matchingAttempts);
            matchingAttempts ??= Array.Empty<Models.PaymentAttempt>();
            var matchingTaskIds = matchingAttempts.Select(a => a.TaskId).Distinct().ToArray();
            var matchingTasks = matchingTaskIds.Where(taskById.ContainsKey).Select(id => taskById[id]).ToArray();
            var matchingPayments = payments.Where(p =>
                matchingTaskIds.Contains(p.TaskId) &&
                matchingAttempts.Any(a => a.TaskId == p.TaskId && a.Reference == transaction.Reference)).ToArray();
            var findings = new List<string>();

            if (!string.Equals(transaction.Currency, "ZAR", StringComparison.OrdinalIgnoreCase))
                findings.Add("Transaction currency is not ZAR; do not compare it with DFY's ZAR task ledger.");
            if (!string.Equals(transaction.Status, "success", StringComparison.OrdinalIgnoreCase))
                findings.Add("Settlement transaction status is not success; review provider status.");
            if (matchingAttempts.Length == 0)
                findings.Add("Paystack settlement transaction has no matching DFY payment attempt.");
            if (matchingAttempts.Length > 1)
                findings.Add("Multiple DFY payment attempts share this Paystack reference.");
            if (matchingAttempts.Any(a => a.Amount != transaction.Amount))
                findings.Add("Paystack transaction amount differs from the DFY payment-attempt amount.");
            if (matchingTasks.Any(t => t.Budget != transaction.Amount))
                findings.Add("Paystack transaction amount differs from the persisted task budget.");
            if (matchingTasks.Any(t => t.CommissionAmount != _escrowService.CalculateCommission(t.Budget)))
                findings.Add("Persisted DFY commission differs from the backend commission calculation.");
            if (matchingTasks.Any(t => t.PayoutAmount != decimal.Round(
                    t.Budget - _escrowService.CalculateCommission(t.Budget), 2, MidpointRounding.AwayFromZero)))
                findings.Add("Persisted runner payout differs from task budget minus backend commission.");
            if (matchingPayments.Any(p => p.Amount != transaction.Amount))
                findings.Add("Paystack transaction amount differs from a persisted DFY payment record.");

            return new
            {
                transaction.SettlementId,
                transaction.Reference,
                transaction.TransactionId,
                transaction.Status,
                transaction.Currency,
                providerAmount = transaction.Amount,
                providerFees = transaction.Fees,
                transaction.PaidAt,
                matchedTaskIds = matchingTasks.Select(t => t.TaskId),
                taskFinancials = matchingTasks.Select(t => new
                {
                    t.TaskId,
                    t.Budget,
                    t.CommissionAmount,
                    expectedCommission = _escrowService.CalculateCommission(t.Budget),
                    t.PayoutAmount,
                    expectedRunnerAmount = decimal.Round(
                        t.Budget - _escrowService.CalculateCommission(t.Budget), 2, MidpointRounding.AwayFromZero)
                }),
                matchingAttemptCount = matchingAttempts.Length,
                localAttemptAmounts = matchingAttempts.Select(a => a.Amount),
                localPaymentAmounts = matchingPayments.Select(p => p.Amount),
                findings,
                isConsistent = findings.Count == 0
            };
        }).ToList();

        var providerReferences = new HashSet<string>(references, StringComparer.Ordinal);
        var missingLocalAttempts = await _context.PaymentAttempts.AsNoTracking()
            .Where(p => p.Provider == "Paystack" &&
                p.CompletedAt.HasValue &&
                p.CompletedAt.Value >= start.UtcDateTime &&
                p.CompletedAt.Value <= end.UtcDateTime &&
                p.Status == "Completed")
            .Select(p => new { p.Reference, p.Amount, p.Status, p.CompletedAt, p.TaskId })
            .ToListAsync(cancellationToken);
        var localSuccessfulAttemptsNotMatchedByReturnedSettlementReference = missingLocalAttempts
            .Where(p => !providerReferences.Contains(p.Reference))
            .Select(p => new { p.Reference, p.Amount, p.Status, p.CompletedAt, p.TaskId })
            .ToList();

        var settlementSummary = providerResult.Settlements.Select(s => new
        {
            s.Id,
            s.Status,
            s.Currency,
            s.Domain,
            grossProcessed = s.TotalProcessed,
            settlementAmount = s.TotalAmount,
            effectiveAmount = s.EffectiveAmount,
            providerFees = s.TotalFees,
            s.SettlementDate
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = providerResult.IsComplete
                ? "Paystack settlement records were retrieved and compared with DFY payment records. Review findings before any corrective action."
                : "Partial Paystack settlement data was retrieved. Narrow the date window and rerun before treating this report as complete.",
            Data = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                fromUtc = start,
                toUtc = end,
                provider = "Paystack",
                providerDomain = providerResult.Settlements.Select(s => s.Domain).Distinct(),
                providerRecordsRetrieved = true,
                reconciliationComplete = providerResult.IsComplete,
                providerError = providerResult.Error,
                settlementCount = settlementSummary.Count,
                settlementTotalsByCurrency = settlementSummary
                    .GroupBy(s => s.Currency)
                    .Select(group => new
                    {
                        currency = group.Key,
                        grossProcessed = group.Sum(s => s.grossProcessed),
                        settlementAmount = group.Sum(s => s.settlementAmount),
                        effectiveAmount = group.Sum(s => s.effectiveAmount),
                        providerFees = group.Sum(s => s.providerFees)
                    }),
                transactionCount = transactionRows.Count,
                transactionFindings = transactionRows.Count(t => !t.isConsistent),
                localSuccessfulAttemptsNotMatchedByReturnedSettlementReference,
                settlements = settlementSummary,
                transactions = transactionRows,
                note = "Settlement batches represent funds Paystack sends to the merchant settlement account. Unmatched local successful attempts are candidates for review, not confirmed missing settlements, because transaction completion and settlement dates can differ. This report does not reconcile runner transfer fees, chargebacks outside the returned date window, bank-statement credits, or legal escrow/safeguarding status. It never changes financial state."
            }
        });
    }

    /// <summary>
    /// Reads Paystack transfer records and compares them with DFY payout records.
    /// This endpoint is strictly read-only and never retries or changes a payout.
    /// </summary>
    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("financial-reconciliation/paystack-transfers")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaystackTransferReconciliation(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int maxPages = 20,
        CancellationToken cancellationToken = default)
    {
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var start = from?.ToUniversalTime() ?? end.AddDays(-30);

        if (end > DateTimeOffset.UtcNow.AddMinutes(5))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The transfer end date cannot be in the future."
            });
        if (start > end)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The transfer start date must be on or before the end date."
            });
        if ((end - start).TotalDays > 90)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "Use a date range of 90 days or less; run separate reports for longer periods."
            });

        var providerResult = await _paystackSettlementProvider.GetTransfersAsync(
            start, end, maxPages, 100, cancellationToken);

        if (!providerResult.Success)
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Success = false,
                Message = providerResult.Error ?? "Paystack transfer records could not be retrieved.",
                Data = new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    fromUtc = start,
                    toUtc = end,
                    provider = "Paystack",
                    providerRecordsRetrieved = false
                }
            });

        var localPayouts = await _context.Payouts.AsNoTracking()
            .Where(p => p.Provider == "Paystack" &&
                p.CreatedAt >= start.UtcDateTime &&
                p.CreatedAt <= end.UtcDateTime)
            .Select(p => new
            {
                p.Id,
                p.TaskId,
                p.RunnerId,
                p.Amount,
                p.Status,
                p.MerchantReference,
                p.ProviderReference,
                p.CreatedAt,
                p.UpdatedAt,
                p.CompletedAt
            })
            .ToListAsync(cancellationToken);

        var matchedLocalPayoutIds = new HashSet<int>();
        var transferRows = providerResult.Transfers.Select(transfer =>
        {
            var matches = localPayouts.Where(p =>
                string.Equals(p.MerchantReference, transfer.Reference, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(p.ProviderReference) &&
                    (string.Equals(p.ProviderReference, transfer.TransferCode, StringComparison.Ordinal) ||
                     string.Equals(p.ProviderReference, transfer.Id, StringComparison.Ordinal))))
                .ToArray();

            foreach (var match in matches)
                matchedLocalPayoutIds.Add(match.Id);

            var findings = new List<string>();
            if (!string.Equals(transfer.Currency, "ZAR", StringComparison.OrdinalIgnoreCase))
                findings.Add("Provider transfer currency is not ZAR.");
            if (matches.Length == 0)
                findings.Add("Paystack transfer has no matching DFY payout record.");
            if (matches.Length > 1)
                findings.Add("Multiple DFY payout records match this Paystack transfer reference.");
            if (matches.Any(p => p.Amount != transfer.Amount))
                findings.Add("Paystack transfer amount differs from the DFY runner payout amount.");

            var providerStatus = transfer.Status.Trim().ToLowerInvariant();
            if (providerStatus == "success" && matches.Any(p => p.Status is not ("Completed" or "Paid")))
                findings.Add("Paystack reports transfer success but the matching DFY payout is not completed.");
            if (providerStatus is "failed" or "reversed" &&
                matches.Any(p => p.Status is not ("Failed" or "Returned" or "Reversed")))
                findings.Add("Paystack reports a failed or reversed transfer but the DFY payout status does not reflect it.");

            return new
            {
                transfer.Id,
                transfer.TransferCode,
                transfer.Reference,
                transfer.Status,
                transfer.Currency,
                providerAmount = transfer.Amount,
                providerFees = transfer.Fees,
                transfer.TransferredAt,
                transfer.Domain,
                matchedPayouts = matches.Select(p => new
                {
                    p.Id,
                    p.TaskId,
                    p.RunnerId,
                    p.Amount,
                    p.Status,
                    p.MerchantReference,
                    p.ProviderReference,
                    p.CreatedAt,
                    p.UpdatedAt,
                    p.CompletedAt
                }),
                findings,
                isConsistent = findings.Count == 0
            };
        }).ToList();

        var completedLocalPayoutsNotReturnedByProvider = localPayouts
            .Where(p => p.Status is "Completed" or "Paid")
            .Where(p => !matchedLocalPayoutIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.TaskId,
                p.RunnerId,
                p.Amount,
                p.Status,
                p.MerchantReference,
                p.ProviderReference,
                p.CreatedAt,
                p.CompletedAt
            })
            .ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = providerResult.IsComplete
                ? "Paystack transfer records were retrieved and compared with DFY payouts. Review findings before any corrective action."
                : "Partial Paystack transfer data was retrieved. Narrow the date window and rerun before treating this report as complete.",
            Data = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                fromUtc = start,
                toUtc = end,
                provider = "Paystack",
                providerRecordsRetrieved = true,
                reconciliationComplete = providerResult.IsComplete,
                providerError = providerResult.Error,
                providerTransferCount = transferRows.Count,
                transferFindings = transferRows.Count(t => !t.isConsistent),
                completedLocalPayoutsNotReturnedByProvider,
                transfers = transferRows,
                note = "This report is read-only. Provider transfer fees are displayed when returned by Paystack but are not compared with a local fee ledger because DFY does not currently persist a provider transfer-fee field. Unmatched completed payouts are candidates for manual review, not proof of a missing bank credit. No transfer is created, retried, reversed or marked paid by this endpoint. No bank details are returned."
            }
        });
    }

    /// <summary>
    /// Read-only view of the new double-entry financial journal. It does not import
    /// legacy wallet/task totals or mutate financial state.
    /// </summary>
    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("financial-ledger")]
    public async Task<ActionResult<ApiResponse<object>>> GetFinancialLedger(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? accountCode = null,
        [FromQuery] string? eventType = null,
        [FromQuery] string? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var journalQuery = _context.FinancialJournals.AsNoTracking()
            .Include(journal => journal.Lines)
                .ThenInclude(line => line.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            journalQuery = journalQuery.Where(journal => journal.EventType == eventType.Trim());
        if (!string.IsNullOrWhiteSpace(sourceId))
            journalQuery = journalQuery.Where(journal => journal.SourceId == sourceId.Trim());
        if (!string.IsNullOrWhiteSpace(accountCode))
            journalQuery = journalQuery.Where(journal =>
                journal.Lines.Any(line => line.Account.Code == accountCode.Trim()));

        var totalCount = await journalQuery.CountAsync(cancellationToken);
        var journals = await journalQuery
            .OrderByDescending(journal => journal.RecordedAt)
            .ThenByDescending(journal => journal.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(journal => new
            {
                journal.Id,
                journal.JournalNumber,
                journal.EventType,
                journal.SourceType,
                journal.SourceId,
                journal.IdempotencyKey,
                journal.Currency,
                journal.Description,
                journal.Provider,
                journal.ProviderReference,
                journal.ReversalOfJournalId,
                journal.OccurredAt,
                journal.RecordedAt,
                lines = journal.Lines.OrderBy(line => line.LineNumber).Select(line => new
                {
                    line.LineNumber,
                    accountCode = line.Account.Code,
                    accountName = line.Account.Name,
                    accountType = line.Account.AccountType,
                    line.Debit,
                    line.Credit,
                    line.Currency,
                    line.Memo
                }),
                debitTotal = journal.Lines.Sum(line => line.Debit),
                creditTotal = journal.Lines.Sum(line => line.Credit),
                lineCount = journal.Lines.Count()
            })
            .ToListAsync(cancellationToken);

        var accountBalances = await _context.FinancialAccounts.AsNoTracking()
            .OrderBy(account => account.Code)
            .Select(account => new
            {
                account.Code,
                account.Name,
                account.AccountType,
                account.Currency,
                account.IsActive,
                debitTotal = _context.FinancialJournalLines
                    .Where(line => line.AccountId == account.Id)
                    .Sum(line => (decimal?)line.Debit) ?? 0m,
                creditTotal = _context.FinancialJournalLines
                    .Where(line => line.AccountId == account.Id)
                    .Sum(line => (decimal?)line.Credit) ?? 0m
            })
            .ToListAsync(cancellationToken);

        var totalDebits = await _context.FinancialJournalLines
            .SumAsync(line => (decimal?)line.Debit, cancellationToken) ?? 0m;
        var totalCredits = await _context.FinancialJournalLines
            .SumAsync(line => (decimal?)line.Credit, cancellationToken) ?? 0m;
        var unbalancedJournalCount = await _context.FinancialJournalLines
            .GroupBy(line => line.JournalId)
            .Where(group => group.Count() < 2 || group.Sum(line => line.Debit) != group.Sum(line => line.Credit))
            .CountAsync(cancellationToken);
        var firstJournalAt = await _context.FinancialJournals.AsNoTracking()
            .MinAsync(journal => (DateTime?)journal.RecordedAt, cancellationToken);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = "New double-entry journal view. Historical wallet balances, task totals and audit-log references are not imported as ledger entries.",
            Data = new
            {
                generatedAtUtc = DateTime.UtcNow,
                firstJournalAtUtc = firstJournalAt,
                historicalBackfillPerformed = false,
                totalCount,
                page,
                pageSize,
                totalDebits,
                totalCredits,
                isBalanced = totalDebits == totalCredits && unbalancedJournalCount == 0,
                unbalancedJournalCount,
                accounts = accountBalances.Select(account => new
                {
                    account.Code,
                    account.Name,
                    account.AccountType,
                    account.Currency,
                    account.IsActive,
                    account.debitTotal,
                    account.creditTotal,
                    rawDebitMinusCreditBalance = decimal.Round(account.debitTotal - account.creditTotal, 2, MidpointRounding.AwayFromZero)
                }),
                journals
            }
        });
    }

    /// <summary>
    /// Read-only DFY ledger consistency report for paid/escrowed tasks.
    /// This endpoint never calls a payment provider, changes financial state, or triggers payouts.
    /// Provider settlement reconciliation must be performed separately against Paystack records.
    /// </summary>
    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("financial-reconciliation")]
    public async Task<ActionResult<ApiResponse<object>>> GetFinancialReconciliation(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 250)
    {
        days = Math.Clamp(days, 1, 90);
        limit = Math.Clamp(limit, 1, 500);
        var since = DateTime.UtcNow.AddDays(-days);

        var tasks = await _context.Tasks
            .AsNoTracking()
            .Where(t => t.UpdatedAt >= since &&
                (t.PaymentStatus == "Completed" ||
                 t.PaymentStatus == "EscrowHeld" ||
                 t.TaskStatus == "PayoutPending" ||
                 t.TaskStatus == "RunnerPaid"))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(limit)
            .Select(t => new
            {
                t.Id, t.TaskId, t.Budget, t.CommissionPercentage,
                t.CommissionAmount, t.PayoutAmount, t.PaymentStatus,
                t.TaskStatus, t.PayoutStatus, t.PaymentReference, t.UpdatedAt
            })
            .ToListAsync();

        var taskIds = tasks.Select(t => t.Id).ToArray();
        var attempts = await _context.PaymentAttempts.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.Reference, p.Amount, p.Status, p.TransactionId })
            .ToListAsync();
        var payments = await _context.Payments.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.PaymentId, p.Amount, p.Status, p.TransactionId })
            .ToListAsync();
        var payouts = await _context.Payouts.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.Amount, p.Status, p.MerchantReference, p.ProviderReference, p.CreatedAt })
            .ToListAsync();
        var refunds = await _context.Refunds.AsNoTracking()
            .Where(r => taskIds.Contains(r.TaskId) && (r.Provider == null || r.Provider == "Paystack"))
            .Select(r => new { r.TaskId, r.Amount, r.Status, r.RefundId })
            .ToListAsync();

        var rows = tasks.Select(task =>
        {
            var expectedCommission = _escrowService.CalculateCommission(task.Budget);
            var expectedRunnerAmount = decimal.Round(
                task.Budget - expectedCommission, 2, MidpointRounding.AwayFromZero);
            var taskAttempts = attempts.Where(p => p.TaskId == task.Id).ToArray();
            var taskPayments = payments.Where(p => p.TaskId == task.Id).ToArray();
            var taskPayouts = payouts.Where(p => p.TaskId == task.Id).ToArray();
            var taskRefunds = refunds.Where(p => p.TaskId == task.Id).ToArray();
            var findings = new List<string>();

            if (task.CommissionAmount != expectedCommission)
                findings.Add("CommissionAmount differs from the backend commission calculation.");
            if (task.PayoutAmount != expectedRunnerAmount)
                findings.Add("PayoutAmount differs from budget minus backend commission.");
            if (taskAttempts.Any(p => p.Amount != task.Budget))
                findings.Add("At least one Paystack payment attempt amount differs from the task budget.");
            if (taskPayments.Any(p => p.Amount != task.Budget))
                findings.Add("At least one persisted Paystack payment amount differs from the task budget.");
            if (taskPayouts.Any(p => p.Amount != task.PayoutAmount))
                findings.Add("At least one Paystack payout amount differs from the persisted runner amount.");
            if (taskPayouts.Count(p => p.Status is "Pending" or "Processing" or "Queued" or "Initiated") > 1)
                findings.Add("Multiple non-terminal Paystack payout records exist for this task.");
            if (taskRefunds.Any(r => r.Status is "Complete" or "Completed" or "Processed") &&
                taskPayouts.Any(p => p.Status is "Pending" or "Processing" or "Queued" or "Initiated"))
                findings.Add("A completed refund coexists with a pending/processing payout; manual review required.");

            return new
            {
                taskId = task.TaskId,
                internalTaskId = task.Id,
                task.Budget,
                task.CommissionPercentage,
                persistedCommission = task.CommissionAmount,
                expectedCommission,
                persistedRunnerAmount = task.PayoutAmount,
                expectedRunnerAmount,
                task.PaymentStatus,
                task.TaskStatus,
                task.PayoutStatus,
                task.PaymentReference,
                paymentAttemptCount = taskAttempts.Length,
                paymentAttempts = taskAttempts.Select(p => new { p.Reference, p.Amount, p.Status, p.TransactionId }),
                paymentRecordCount = taskPayments.Length,
                payments = taskPayments.Select(p => new { p.PaymentId, p.Amount, p.Status, p.TransactionId }),
                payoutRecordCount = taskPayouts.Length,
                payouts = taskPayouts.Select(p => new
                {
                    p.Amount, p.Status, p.MerchantReference, p.ProviderReference, p.CreatedAt
                }),
                refunds = taskRefunds.Select(r => new { r.Amount, r.Status, r.RefundId }),
                findings,
                isConsistent = findings.Count == 0,
                task.UpdatedAt
            };
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = "Read-only DFY ledger consistency report. Paystack settlement is not verified and no financial state is changed.",
            Data = new
            {
                generatedAtUtc = DateTime.UtcNow,
                sinceUtc = since,
                days,
                limit,
                returnedTaskCount = rows.Count,
                tasksWithFindings = rows.Count(r => !r.isConsistent),
                findingCount = rows.Sum(r => r.findings.Count),
                providerSettlementVerified = false,
                rows
            }
        });
    }

    /// <summary>
    /// Read-only financial operations queue for admin review. No payout, refund or task state is changed.
    /// Chargeback deadlines are not represented here because the current data model has no dedicated
    /// Paystack chargeback case entity.
    /// </summary>
    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("payment-operations")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentOperations(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        limit = Math.Clamp(limit, 1, 500);
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);

        var payoutRows = await _context.Payouts.AsNoTracking()
            .Where(p => p.CreatedAt >= since ||
                p.Status == "Pending" || p.Status == "Processing" ||
                p.Status == "Queued" || p.Status == "Initiated")
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                taskReference = p.Task.TaskId,
                taskStatus = p.Task.TaskStatus,
                paymentStatus = p.Task.PaymentStatus,
                p.Amount,
                p.Provider,
                p.Status,
                p.MerchantReference,
                p.ProviderReference,
                p.AttemptCount,
                p.LastAttemptAt,
                p.NextAttemptAt,
                p.FailureReason,
                p.ProcessingError,
                p.CreatedAt,
                updatedAt = p.UpdatedAt ?? p.CreatedAt,
                p.CompletedAt
            })
            .ToListAsync(cancellationToken);

        var refundRows = await _context.Refunds.AsNoTracking()
            .Where(r => r.CreatedAt >= since ||
                r.Status == "Pending" || r.Status == "Submitted" ||
                r.Status == "Processing" || r.Status == "Failed" ||
                r.Status == "Returned")
            .OrderByDescending(r => r.LastReconciledAt ?? r.CreatedAt)
            .Take(limit)
            .Select(r => new
            {
                r.Id,
                taskReference = r.Task.TaskId,
                r.Provider,
                r.RefundId,
                r.TransactionId,
                r.Amount,
                r.Status,
                r.FailureReason,
                r.CreatedAt,
                r.SubmittedAt,
                r.CompletedAt,
                r.LastReconciledAt
            })
            .ToListAsync(cancellationToken);

        var disputeRows = await _context.Disputes.AsNoTracking()
            .Where(d => d.CreatedAt >= since ||
                d.Status == "Open" || d.Status == "Pending" ||
                d.Status == "RefundPending")
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .Select(d => new
            {
                d.Id,
                taskReference = d.Task.TaskId,
                d.Category,
                d.Status,
                d.CreatedAt,
                d.ResolvedAt
            })
            .ToListAsync(cancellationToken);

        var failedPaymentAttempts = await _context.PaymentAttempts.AsNoTracking()
            .Where(p => p.Provider == "Paystack" && p.CreatedAt >= since &&
                (p.Status == "Failed" || p.Status == "Abandoned" || p.Status == "Duplicate"))
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .Select(p => new
            {
                p.Reference,
                p.Amount,
                p.Status,
                p.FailureReason,
                p.CreatedAt,
                taskReference = p.Task.TaskId
            })
            .ToListAsync(cancellationToken);

        var stalePayouts = payoutRows.Count(p =>
            ((p.Status is "Pending" or "Queued" or "Initiated") &&
             now - p.updatedAt > TimeSpan.FromHours(24)) ||
            (p.Status == "Processing" && now - p.updatedAt > TimeSpan.FromMinutes(30)));
        var unresolvedRefunds = refundRows.Count(r =>
            r.Status is "Pending" or "Submitted" or "Processing" or "Failed" or "Returned");
        var openDisputes = disputeRows.Count(d =>
            d.Status is "Open" or "Pending" or "RefundPending");

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = "Read-only financial operations snapshot. Review provider references and dashboard records before corrective actions.",
            Data = new
            {
                generatedAtUtc = now,
                sinceUtc = since,
                days,
                limit,
                summary = new
                {
                    payoutRecords = payoutRows.Count,
                    pendingOrProcessingPayouts = payoutRows.Count(p =>
                        p.Status is "Pending" or "Processing" or "Queued" or "Initiated"),
                    failedOrReturnedPayouts = payoutRows.Count(p =>
                        p.Status is "Failed" or "Returned" or "Reversed"),
                    stalePayouts,
                    refundRecords = refundRows.Count,
                    unresolvedRefunds,
                    openDisputes,
                    failedPaymentAttempts = failedPaymentAttempts.Count,
                    chargebackCaseTrackingAvailable = false
                },
                payouts = payoutRows,
                refunds = refundRows,
                disputes = disputeRows,
                failedPaymentAttempts,
                note = "This view does not create or retry transfers/refunds and does not change financial state. Paystack chargeback cases and external bank settlement credits require separate operational evidence. No full bank details or customer contact details are returned."
            }
        });
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var totalUsers = await _context.Users.CountAsync(u => !u.IsArchived);
        var archivedUsers = await _context.Users.CountAsync(u => u.IsArchived);
        var totalTasks = await _context.Tasks.CountAsync();
        var pendingTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "PendingPayment");
        var activeTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Posted");
        var completedTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Completed");
        var totalRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.Budget);

        var platformEarnings = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.CommissionAmount);

        var openDisputes = await _context.Disputes.CountAsync(d => d.Status == "Open");

        var recentTasks = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .Select(t => new
            {
                taskId = t.TaskId,
                description = t.TaskDescription,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                budget = t.Budget,
                status = t.TaskStatus,
                createdAt = t.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalUsers,
                archivedUsers,
                totalTasks,
                pendingTasks,
                activeTasks,
                completedTasks,
                totalRevenue,
                platformEarnings,
                grossVolume = totalRevenue,
                openDisputes,
                recentTasks
            }
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpGet("tasks")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetAllTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? taskStatus = null,
        [FromQuery] string? paymentStatus = null,
        [FromQuery] string? priority = null,
        [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Tasks.Include(t => t.CreatedByUser).AsQueryable();

        var resolvedTaskStatus = taskStatus ?? status;
        if (!string.IsNullOrEmpty(resolvedTaskStatus))
            query = query.Where(t => t.TaskStatus == resolvedTaskStatus);

        if (!string.IsNullOrEmpty(paymentStatus))
            query = query.Where(t => t.PaymentStatus == paymentStatus);

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(t => t.Priority == priority);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.TaskDescription.Contains(search) || t.TaskId.Contains(search));

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var tasks = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskDto
            {
                Id = t.Id,
                TaskId = t.TaskId,
                UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                UserContact = string.Empty,
                CreatedByUserId = t.CreatedByUserId,
                TaskDescription = t.TaskDescription,
                Category = t.Category,
                Area = t.Area,
                DateNeeded = t.DateNeeded,
                Budget = t.Budget,
                Notes = t.Notes,
                PaymentStatus = t.PaymentStatus,
                TaskStatus = t.TaskStatus,
                HelperName = t.HelperName,
                HelperContact = t.HelperContact,
                Priority = t.Priority,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt
            })
            .ToListAsync();

        var response = new PaginatedResponse<TaskDto>
        {
            Success = true,
            Count = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Tasks = tasks
        };

        return Ok(new ApiResponse<PaginatedResponse<TaskDto>>
        {
            Success = true,
            Data = response
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpPatch("tasks/{taskId}/verify")]
    public ActionResult<ApiResponse<bool>> VerifyTask(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        return StatusCode(StatusCodes.Status410Gone, new ApiResponse<bool>
        {
            Success = false,
            Message = "Manual payment verification is retired. Only a provider-verified payment webhook can place funds into escrow."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpPatch("tasks/{taskId}/unverify")]
    public ActionResult<ApiResponse<bool>> UnverifyTask(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        return StatusCode(StatusCodes.Status410Gone, new ApiResponse<bool>
        {
            Success = false,
            Message = "Manual payment unverification is retired. Refunds and financial corrections must use the provider reconciliation and two-person approval workflows."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("payments")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentsOverview()
    {
        var paidStatuses = new[] { "EscrowHeld", "Completed", "RunnerPaid" };

        var totalRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.Budget);

        var platformRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.CommissionAmount);

        var helperPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "RunnerPaid")
            .SumAsync(t => t.PayoutAmount);

        var pendingRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Pending" && t.TaskStatus == "PendingPayment")
            .SumAsync(t => t.Budget);

        var pendingPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "Completed" && t.EscrowStatus == "held")
            .SumAsync(t => t.PayoutAmount);

        var totalRefunded = await _context.Refunds
            .Where(r => r.Status == "Complete")
            .SumAsync(r => r.Amount);

        var recentPayments = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Include(t => t.AcceptedByUser)
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(10)
            .Select(t => new
            {
                taskId = t.TaskId,
                taskName = t.TaskDescription,
                amount = t.Budget,
                commission = t.CommissionAmount,
                payout = t.PayoutAmount,
                status = t.PaymentStatus,
                taskStatus = t.TaskStatus,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                runnerName = t.AcceptedByUser != null ? $"{t.AcceptedByUser.FirstName} {t.AcceptedByUser.LastName}" : null,
                runnerContact = t.AcceptedByUser != null ? t.AcceptedByUser.PhoneNumber : null,
                date = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalRevenue,
                platformEarnings = platformRevenue,
                grossVolume = totalRevenue,
                platformRevenue,
                helperPayouts,
                pendingRevenue,
                pendingCommission = pendingRevenue,
                pendingPayouts,
                totalRefunded,
                netRevenue = totalRevenue - totalRefunded,
                recentPayments
            }
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] bool includeArchived = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        IQueryable<Models.User> query = _context.Users.AsNoTracking();
        if (!includeArchived)
            query = query.Where(u => !u.IsArchived);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var userRows = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var users = userRows.Select(u => new
        {
            id = u.Id,
            name = $"{u.FirstName} {u.LastName}",
            email = u.Email,
            contact = u.PhoneNumber == null ? null : "***",
            // System authorization roles and marketplace participation are separate concepts.
            role = u.Roles,
            userType = u.UserType,
            marketplaceRole = GetMarketplaceRole(u.UserType),
            isVerified = u.IsVerified,
            emailVerified = u.EmailVerified,
            profileCompleted = _userPolicyService.IsProfileComplete(u),
            profileCompletion = _userPolicyService.GetProfileCompletion(u),
            missingProfileFields = _userPolicyService.GetMissingProfileFields(u),
            canCreateTasks = _userPolicyService.CanCreateTasks(u),
            canAcceptTasks = _userPolicyService.CanAcceptTasks(u),
            tasksPosted = _context.Tasks.Count(t => t.CreatedByUserId == u.Id),
            tasksCompleted = u.CompletedTasks,
            rating = u.Rating,
            lastLoginAt = u.LastLoginAt,
            createdAt = u.CreatedAt,
            isArchived = u.IsArchived,
            isSuspended = u.IsSuspended,
            suspendedAt = u.SuspendedAt,
            archivedAt = u.ArchivedAt,
            archivedReason = u.ArchivedReason
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                users,
                totalCount,
                page,
                pageSize,
                totalPages,
                includeArchived
            }
        });
    }

    /// <summary>
    /// Lists accounts for the dedicated SuperAdmin access-management workspace.
    /// The caller is checked against persisted roles; frontend route guards are not security boundaries.
    /// </summary>
    [HttpGet("superadmin/accounts")]
    public async Task<ActionResult<ApiResponse<object>>> GetSuperAdminAccounts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        [FromQuery] bool includeArchived = false)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue)
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "An authenticated administrator is required." });

        var actorRoles = await _context.Users.AsNoTracking()
            .Where(candidate => candidate.Id == actorId.Value && !candidate.IsArchived)
            .Select(candidate => candidate.Roles)
            .FirstOrDefaultAsync();
        var actorIsSuperAdmin = actorRoles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(value.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true;
        if (!actorIsSuperAdmin)
            return Forbid();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        IQueryable<Models.User> query = _context.Users.AsNoTracking();
        if (!includeArchived)
            query = query.Where(user => !user.IsArchived);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(user =>
                user.Email.ToLower().Contains(term) ||
                user.FirstName.ToLower().Contains(term) ||
                user.LastName.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync();
        var accountRows = await query
            .OrderByDescending(user => user.Roles != null && user.Roles.Contains("Admin"))
            .ThenBy(user => user.FirstName)
            .ThenBy(user => user.LastName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new
            {
                id = user.Id,
                name = (user.FirstName + " " + user.LastName).Trim(),
                email = user.Email,
                roles = user.Roles,
                emailVerified = user.EmailVerified,
                authenticatorTwoFactorEnabled = user.AuthenticatorTwoFactorEnabled,
                isArchived = user.IsArchived,
                isSuspended = user.IsSuspended,
                suspendedAt = user.SuspendedAt,
                lastLoginAt = user.LastLoginAt,
                createdAt = user.CreatedAt
            })
            .ToListAsync();
        var accounts = accountRows.Select(user =>
        {
            var roles = user.roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(role => role.Trim()).ToArray() ?? Array.Empty<string>();
            return new
            {
                user.id,
                user.name,
                user.email,
                user.roles,
                isAdministrator = roles.Contains("Admin", StringComparer.OrdinalIgnoreCase) ||
                    roles.Contains("SuperAdmin", StringComparer.OrdinalIgnoreCase),
                isSuperAdmin = roles.Contains("SuperAdmin", StringComparer.OrdinalIgnoreCase),
                user.emailVerified,
                user.authenticatorTwoFactorEnabled,
                user.isArchived,
                user.isSuspended,
                user.suspendedAt,
                user.lastLoginAt,
                user.createdAt
            };
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                accounts,
                totalCount,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                includeArchived
            }
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpPatch("users/{userId}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserStatus(int userId, [FromBody] UpdateUserStatusRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });
        if (user.IsArchived || user.IsSuspended)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Restore and reinstate the account before changing its admin verification status." });

        var old = user.IsVerified;
        user.IsVerified = request.IsVerified;
        await _context.SaveChangesAsync();
        await WriteAuditAsync(request.IsVerified ? "VerifyUser" : "UnverifyUser", "User", userId, old.ToString(), $"{request.IsVerified}; reason={reason}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User status updated" });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpPatch("users/{userId}/suspension")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserSuspension(
        int userId,
        [FromBody] UpdateUserSuspensionRequest request)
    {
        var actorId = GetAdminId();
        if (actorId == null) return Unauthorized();

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        var target = await _context.Users.FirstOrDefaultAsync(user => user.Id == userId);
        if (target == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found." });
        if (target.IsArchived)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Restore the account before changing its suspension state." });
        if (target.Id == actorId || target.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Administrators cannot suspend themselves or another administrative account through user management." });
        if (target.IsSuspended == request.IsSuspended)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Account suspension state is unchanged." });

        var oldState = target.IsSuspended ? "Suspended" : "Active";
        target.IsSuspended = request.IsSuspended;
        target.SuspendedAt = request.IsSuspended ? DateTime.UtcNow : null;
        target.SuspendedReason = request.IsSuspended ? reason : null;
        target.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId,
            Action = request.IsSuspended ? "SuspendUser" : "ReinstateUser",
            EntityType = "User",
            EntityId = target.Id,
            OldValues = oldState,
            NewValues = $"{(request.IsSuspended ? "Suspended" : "Active")}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = request.IsSuspended ? "Account suspended and active sessions revoked." : "Account reinstated. The user must sign in again."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpPatch("users/{userId}/role")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserRole(int userId, [FromBody] JsonElement request)
    {
        // Legacy Admin accounts must not be able to grant or revoke administrative access.
        // This endpoint is temporarily locked to an already-authorized SuperAdmin until
        // the persisted permission-based role-management workflow is deployed.
        var actorId = GetAdminId();
        if (!actorId.HasValue)
            return Unauthorized(new ApiResponse<bool> { Success = false, Message = "An authenticated administrator is required." });

        var actor = await _context.Users.AsNoTracking()
            .Where(candidate => candidate.Id == actorId.Value && !candidate.IsArchived)
            .Select(candidate => new
            {
                candidate.Roles,
                candidate.AuthenticatorTwoFactorEnabled,
                candidate.LastTwoFactorVerifiedAt
            })
            .FirstOrDefaultAsync();

        var actorIsSuperAdmin = actor?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(value.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true;
        if (actor == null || !actorIsSuperAdmin)
            return Forbid();

        if (actorId.Value == userId)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Administrators cannot change their own role." });

        if (actor.AuthenticatorTwoFactorEnabled != true ||
            !actor.LastTwoFactorVerifiedAt.HasValue ||
            actor.LastTwoFactorVerifiedAt.Value < DateTime.UtcNow.AddMinutes(-15))
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<bool> { Success = false, Message = "Fresh authenticator verification is required for role changes." });

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var role = request.TryGetProperty("role", out var roleElement)
            ? roleElement.GetString()?.Trim()
            : null;
        var reason = request.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(role) ||
            (role != "User" && role != "Admin" && role != "Admin,User"))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A valid role is required." });

        if (user.IsArchived)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Archived accounts cannot receive or change administrative access." });

        if ((role == "Admin" || role == "Admin,User") && !user.EmailVerified)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The account must verify its email address before receiving Admin access." });

        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });

        var oldRole = user.Roles;
        var targetIsSuperAdmin = oldRole?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(value.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true;
        if (targetIsSuperAdmin)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Super Admin changes require the separate two-person approval workflow." });

        if (string.Equals(oldRole, role, StringComparison.Ordinal))
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User role unchanged." });

        user.Roles = role;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await _context.SaveChangesAsync();
        await WriteAuditAsync("UpdateUserRole", "User", userId, oldRole, $"{role}; reason={reason}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User role updated" });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpGet("users/{userId}/tasks")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserTaskHistory(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        var postedTasks = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, budget = t.Budget, createdAt = t.CreatedAt })
            .ToListAsync();

        var acceptedTasks = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, payout = t.PayoutAmount, createdAt = t.CreatedAt })
            .ToListAsync();

        var profileChanges = await _context.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "User" && log.EntityId == userId &&
                log.Action == "AdminUserProfileCorrected")
            .OrderByDescending(log => log.CreatedAt)
            .Take(50)
            .Select(log => new { log.CreatedAt, log.UserId, log.OldValues, log.NewValues, log.IpAddress })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                user = new { user.Id, user.Rating, user.CompletedTasks },
                postedTasks,
                acceptedTasks,
                profileChanges
            }
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpPatch("tasks/bulk-verify")]
    public async Task<ActionResult<ApiResponse<bool>>> BulkVerifyTasks([FromBody] BulkVerifyRequest request)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var reason = request?.Reason?.Trim();
        var requestedIds = request?.TaskIds?.Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        if (requestedIds.Length == 0 || string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "E2E payment simulation requires task IDs and a reason between 5 and 500 characters." });

        var tasks = await _context.Tasks
            .Where(task => requestedIds.Contains(task.TaskId))
            .ToListAsync();
        if (tasks.Count != requestedIds.Length ||
            tasks.Any(task => task.TaskStatus != "PendingPayment" ||
                task.PaymentStatus != "Pending" ||
                (task.EscrowStatus != "pending" && task.EscrowStatus != "none") ||
                task.Budget <= 0))
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Only unpaid, newly created E2E tasks can receive a simulated payment." });

        var alreadyAttempted = await _context.PaymentAttempts.AnyAsync(attempt => tasks.Select(task => task.Id).Contains(attempt.TaskId));
        if (alreadyAttempted)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "An E2E payment attempt already exists for one or more selected tasks." });

        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var ledger = new FinancialLedgerService(_context);
        var transactionIds = new Dictionary<int, string>();

        foreach (var task in tasks)
        {
            var transactionId = $"E2E-{Guid.NewGuid():N}";
            var reference = $"E2E-{task.TaskId}";
            transactionIds[task.Id] = transactionId;
            _context.PaymentAttempts.Add(new PaymentAttempt
            {
                TaskId = task.Id,
                Provider = "E2E",
                Reference = reference,
                Amount = task.Budget,
                Status = "Completed",
                TransactionId = transactionId,
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "Posted";
            task.EscrowStatus = "held";
            task.UpdatedAt = DateTime.UtcNow;

            await ledger.PostAsync(new FinancialJournalDraft(
                EventType: "payment_collected",
                SourceType: "e2e_transaction",
                SourceId: transactionId,
                IdempotencyKey: $"e2e:charge:{transactionId}",
                Currency: "ZAR",
                Description: "TEST-ONLY simulated payment for staging E2E. No Paystack request or money movement occurred.",
                OccurredAt: DateTime.UtcNow,
                Lines: new[]
                {
                    new FinancialJournalLineDraft("1000", task.Budget, 0m, "E2E test provider clearing"),
                    new FinancialJournalLineDraft("2000", 0m, task.Budget, "E2E test task escrow liability")
                },
                Provider: "E2E",
                ProviderReference: reference));
        }

        await _context.SaveChangesAsync();
        if (transaction != null)
            await transaction.CommitAsync();

        foreach (var task in tasks)
            await WriteAuditAsync(
                "E2EPaymentSimulated",
                "Task",
                task.Id,
                "PendingPayment",
                $"TEST ONLY; transactionId={transactionIds[task.Id]}; amount={task.Budget:F2}; reason={reason}");

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = $"{tasks.Count} staging E2E task payment(s) simulated. No Paystack request or live money movement occurred."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpPatch("tasks/{taskId}/expire-confirmation")]
    public async Task<ActionResult<ApiResponse<bool>>> ExpireConfirmation(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Data = false, Message = "Task not found" });

        if (task.TaskStatus != "AwaitingConfirmation" ||
            task.EscrowStatus != "held" ||
            task.PaymentStatus != "EscrowHeld")
        {
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Task is not awaiting creator confirmation." });
        }

        if (task.AcceptedByUserId.HasValue)
        {
            var hasPaystackRecipient = await _context.BankAccounts.AnyAsync(b =>
                b.UserId == task.AcceptedByUserId.Value &&
                b.IsActive &&
                b.IsVerified &&
                !string.IsNullOrWhiteSpace(b.ProviderReference));
            if (!hasPaystackRecipient)
            {
                return Conflict(new ApiResponse<bool>
                {
                    Success = false,
                    Data = false,
                    Error = "PAYSTACK_RECIPIENT_REQUIRED",
                    Message = "E2E escrow release requires the runner's active, Paystack-verified bank account."
                });
            }
        }

        // Test/operations harness: backdate the same 24-hour deadline used by
        // normal completion, then call the exact release operation the background
        // worker uses for each expired task. EscrowReleaseService owns the expiry query/loop.
        task.EscrowHoldUntil = DateTime.UtcNow.AddMinutes(-1);
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var released = await _escrowService.ReleaseEscrowAsync(task.Id);
        if (!released)
        {
            // The hosted expiry worker may win the same atomic state transition
            // after the deadline is backdated. Treat its completed transition as
            // success rather than reporting a false negative to the E2E caller.
            var currentState = await _context.Tasks
                .AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => new { t.TaskStatus, t.PaymentStatus, t.EscrowStatus })
                .FirstOrDefaultAsync();
            released = currentState is not null &&
                       currentState.TaskStatus == "PayoutPending" &&
                       currentState.PaymentStatus == "EscrowReleased" &&
                       currentState.EscrowStatus == "released";
        }

        await WriteAuditAsync(
            "ExpireCreatorConfirmation",
            "Task",
            task.Id,
            "AwaitingConfirmation",
            $"AutoReleaseExpiredEscrows; released={released}; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = released,
            Data = released,
            Message = released
                ? "Creator confirmation expired and the runner payout was queued."
                : "Creator confirmation was expired, but the escrow could not be released."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpGet("financial-approvals")]
    public async Task<ActionResult<ApiResponse<object>>> GetFinancialApprovals([FromQuery] string? status = null)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var query = _context.FinancialApprovalRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(item => item.Status == status.Trim());
        var requests = await query
            .OrderByDescending(item => item.RequestedAt)
            .Take(100)
            .Select(item => new
            {
                id = item.Id,
                taskId = item.TaskId,
                taskReference = _context.Tasks.Where(task => task.Id == item.TaskId).Select(task => task.TaskId).FirstOrDefault(),
                action = item.Action,
                reason = item.Reason,
                status = item.Status,
                requestedByUserId = item.RequestedByUserId,
                requestedAt = item.RequestedAt,
                expiresAt = item.ExpiresAt,
                decidedByUserId = item.DecidedByUserId,
                decidedAt = item.DecidedAt,
                decisionReason = item.DecisionReason,
                executionAttempts = item.ExecutionAttempts,
                executedAt = item.ExecutedAt,
                lastExecutionError = item.LastExecutionError
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = requests });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/{id:int}/reconcile")]
    public async Task<ActionResult<ApiResponse<bool>>> ReconcileFinancialApproval(int id, CancellationToken cancellationToken)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var approval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (approval == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Financial approval request not found." });
        if (approval.Status != "Executing")
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Only an interrupted execution can be reconciled." });
        if (!approval.DecidedByUserId.HasValue || approval.RequestedByUserId == approval.DecidedByUserId.Value ||
            !await IsFreshActiveSuperAdminAsync(approval.DecidedByUserId.Value))
            return Forbid();

        if (approval.Action == "release_to_runner")
        {
            var task = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == approval.TaskId, cancellationToken);
            var payoutExists = task?.EscrowStatus == "released" && task.PaymentStatus == "EscrowReleased" &&
                await _context.Payouts.AnyAsync(payout => payout.TaskId == approval.TaskId &&
                    payout.Status != "Failed" && payout.Status != "Returned" && payout.Status != "Cancelled", cancellationToken);
            if (!payoutExists)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "No persisted payout confirms the interrupted dispute release. Do not retry automatically; reconcile the task and payout manually." });
        }
        else if (approval.Action == "refund_creator")
        {
            var refund = await _context.Refunds.FirstOrDefaultAsync(item => item.TaskId == approval.TaskId, cancellationToken);
            if (refund == null || string.IsNullOrWhiteSpace(refund.TransactionId))
                return Conflict(new ApiResponse<bool> { Success = false, Message = "No persisted refund reservation exists. Provider state must be checked manually; no resubmission is allowed." });

            IReadOnlyList<RefundStatusResult> providerRefunds;
            try
            {
                providerRefunds = await _paymentProvider.GetRefundsByTransactionIdAsync(refund.TransactionId, cancellationToken);
            }
            catch
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ApiResponse<bool> { Success = false, Message = "Paystack refund state is unavailable. No refund was resubmitted; keep the task frozen and retry reconciliation later." });
            }

            var matches = providerRefunds.Where(item =>
                item.Amount == refund.Amount &&
                string.Equals(item.TransactionId, refund.TransactionId, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1 || providerRefunds.Count != 1)
            {
                refund.Status = "ReconciliationRequired";
                refund.FailureReason = providerRefunds.Count == 0
                    ? "No provider refund was found after an interrupted submission. Do not resubmit automatically."
                    : "Provider refund history is ambiguous; manual reconciliation is required.";
                refund.LastReconciledAt = DateTime.UtcNow;
                approval.LastExecutionError = refund.FailureReason;
                await _context.SaveChangesAsync(cancellationToken);
                return Conflict(new ApiResponse<bool> { Success = false, Message = refund.FailureReason });
            }

            var providerRefund = matches[0];
            refund.RefundId = providerRefund.RefundId;
            refund.LastReconciledAt = DateTime.UtcNow;
            var providerStatus = providerRefund.Status.Trim().ToLowerInvariant();
            refund.Status = providerStatus switch
            {
                "processed" => "Complete",
                "failed" => "Failed",
                "needs-attention" => "PendingInvestigation",
                "pending" or "processing" => "Submitted",
                _ => "ReconciliationRequired"
            };
            if (refund.Status == "ReconciliationRequired")
            {
                refund.FailureReason = $"Unrecognized Paystack refund status '{providerRefund.Status}'. Manual reconciliation is required.";
                approval.LastExecutionError = refund.FailureReason;
                await _context.SaveChangesAsync(cancellationToken);
                return Conflict(new ApiResponse<bool> { Success = false, Message = refund.FailureReason });
            }

            var refundTask = await _context.Tasks.FirstOrDefaultAsync(item => item.Id == approval.TaskId, cancellationToken);
            if (refundTask == null)
                return NotFound(new ApiResponse<bool> { Success = false, Message = "Refund task no longer exists." });
            var disputes = await _context.Disputes.Where(item => item.TaskId == approval.TaskId && item.Status != "Resolved").ToListAsync(cancellationToken);
            if (refund.Status == "Complete")
            {
                refund.CompletedAt ??= DateTime.UtcNow;
                refundTask.PaymentStatus = "Refunded";
                refundTask.TaskStatus = "Cancelled";
                refundTask.EscrowStatus = "refunded";
                refundTask.PayoutStatus = "NotStarted";
                refundTask.PayoutReference = null;
                refundTask.PayoutInitiatedAt = null;
                refundTask.PayoutCompletedAt = null;
                refundTask.PaidToRunnerAt = null;
                foreach (var dispute in disputes)
                {
                    dispute.Status = "Resolved";
                    dispute.ResolvedAt = DateTime.UtcNow;
                }
            }
            else if (refund.Status == "Failed")
            {
                refund.FailureReason = "Paystack reports the refund failed; dispute reopened for review.";
                refundTask.PaymentStatus = "DisputePending";
                refundTask.TaskStatus = "Completed";
                refundTask.EscrowStatus = "disputed";
                foreach (var dispute in disputes)
                {
                    dispute.Status = "Open";
                    dispute.ResolvedAt = null;
                }
            }
            else
            {
                refundTask.PaymentStatus = "RefundPending";
                refundTask.TaskStatus = "RefundPending";
                refundTask.EscrowStatus = "refund_pending";
                foreach (var dispute in disputes)
                {
                    dispute.Status = "RefundPending";
                    dispute.ResolvedAt = null;
                }
            }
            refundTask.UpdatedAt = DateTime.UtcNow;
            if (refund.Status == "Failed")
                approval.Status = "Failed";
            else
            {
                approval.Status = "Executed";
                approval.ExecutedAt = DateTime.UtcNow;
            }
            approval.LastExecutionError = refund.Status == "Failed" ? refund.FailureReason : null;
            _context.AuditLogs.Add(new Models.AuditLog
            {
                UserId = actorId.Value,
                Action = "FinancialRefundExecutionReconciled",
                EntityType = "FinancialApprovalRequest",
                EntityId = id,
                OldValues = "Executing",
                NewValues = $"status={approval.Status}; providerRefundId={refund.RefundId}; providerStatus={providerRefund.Status}; taskId={approval.TaskId}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = $"Paystack refund reconciled as {refund.Status}." });
        }
        else
        {
            return Conflict(new ApiResponse<bool> { Success = false, Message = "This financial action does not have a supported interrupted-execution recovery path." });
        }

        approval.Status = "Executed";
        approval.ExecutedAt = DateTime.UtcNow;
        approval.LastExecutionError = null;
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialDisputeReleaseExecutionReconciled",
            EntityType = "FinancialApprovalRequest",
            EntityId = id,
            OldValues = "Executing",
            NewValues = $"Executed from existing payout state; taskId={approval.TaskId}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Existing dispute-release payout state reconciled; no second payout was submitted." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/{id:int}/reject")]
    public async Task<ActionResult<ApiResponse<bool>>> RejectFinancialRequest(int id, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A rejection reason between 10 and 500 characters is required." });

        var approval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
        if (approval == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Financial approval request not found." });
        if (approval.RequestedByUserId == actorId.Value)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The requester cannot reject their own financial action." });
        if (approval.Status != "Pending" || approval.ExpiresAt <= DateTime.UtcNow)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Only an unexpired pending request can be rejected." });

        await using var rejectionTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var decidedAt = DateTime.UtcNow;
        var changed = 0;
        if (_context.Database.IsRelational())
        {
            changed = await _context.FinancialApprovalRequests
                .Where(item => item.Id == id && item.Status == "Pending" && item.ExpiresAt > decidedAt && item.RequestedByUserId != actorId.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "Rejected")
                    .SetProperty(item => item.DecidedByUserId, actorId.Value)
                    .SetProperty(item => item.DecidedAt, decidedAt)
                    .SetProperty(item => item.DecisionReason, reason));
        }
        else
        {
            approval.Status = "Rejected";
            approval.DecidedByUserId = actorId.Value;
            approval.DecidedAt = decidedAt;
            approval.DecisionReason = reason;
            changed = 1;
        }
        if (changed != 1)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The request changed while rejection was being recorded. Refresh and review it again." });

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialApprovalRejected",
            EntityType = "FinancialApprovalRequest",
            EntityId = id,
            OldValues = "Pending",
            NewValues = $"Rejected; requestedBy={approval.RequestedByUserId}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (rejectionTransaction != null)
            await rejectionTransaction.CommitAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Financial action rejected. No money moved." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<int>>> RequestFinancialApproval(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<int> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        var task = await _context.Tasks.FirstOrDefaultAsync(item => item.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<int> { Success = false, Message = "Task not found." });
        var verifiedChargeIds = await _context.PaymentAttempts
            .Where(payment => payment.TaskId == task.Id &&
                payment.Provider == "Paystack" &&
                payment.Status == "Completed" &&
                payment.TransactionId != null &&
                payment.Amount == task.Budget)
            .Select(payment => payment.TransactionId!)
            .ToListAsync();
        var chargeIsJournalled = verifiedChargeIds.Count == 1 &&
            await _context.FinancialJournals.AnyAsync(journal =>
                journal.SourceType == "paystack_transaction" &&
                journal.SourceId == verifiedChargeIds[0] &&
                journal.EventType == "payment_collected");
        if (task.AcceptedByUserId == null || task.TaskStatus != "AwaitingConfirmation" || task.EscrowStatus != "held" || task.PaymentStatus != "EscrowHeld" || task.PayoutAmount <= 0 || !chargeIsJournalled)
            return Conflict(new ApiResponse<int> { Success = false, Message = "Exceptional release requires an assigned task, held escrow and exactly one verified Paystack charge already recorded in the new financial journal. Legacy task totals and audit references are not sufficient." });

        var now = DateTime.UtcNow;
        await ExpireOpenFinancialApprovalsAsync(task.Id, now, HttpContext.RequestAborted);

        var hasOpenRequest = await _context.FinancialApprovalRequests.AnyAsync(item =>
            item.TaskId == task.Id &&
            (item.Status == "Pending" || item.Status == "Approved" || item.Status == "Executing"));
        if (hasOpenRequest)
            return Conflict(new ApiResponse<int> { Success = false, Message = "An active financial approval request already exists for this task." });

        var approval = new Models.FinancialApprovalRequest
        {
            TaskId = task.Id,
            PaymentTransactionId = verifiedChargeIds[0],
            Amount = task.Budget,
            Action = "force_escrow_release",
            Reason = reason,
            Status = "Pending",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RequestedByUserId = actorId.Value,
            RequestedAt = now,
            ExpiresAt = now.AddMinutes(30)
        };
        await using var requestTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        _context.FinancialApprovalRequests.Add(approval);
        await _context.SaveChangesAsync();
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialApprovalRequested",
            EntityType = "FinancialApprovalRequest",
            EntityId = approval.Id,
            NewValues = $"action={approval.Action}; taskId={task.Id}; expiresAt={approval.ExpiresAt:O}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (requestTransaction != null)
            await requestTransaction.CommitAsync();

        return Ok(new ApiResponse<int> { Success = true, Data = approval.Id, Message = "Financial approval request created. No money has moved." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/refunds/tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<int>>> RequestFinancialRefundApproval(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<int> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        var task = await _context.Tasks.FirstOrDefaultAsync(item => item.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<int> { Success = false, Message = "Task not found." });

        var openDispute = await _context.Disputes
            .Where(item => item.TaskId == task.Id && item.Status == "Open")
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync();
        var successfulPayments = await _context.PaymentAttempts
            .Where(item => item.TaskId == task.Id && item.Provider == "Paystack" &&
                item.Status == "Completed" && item.TransactionId != null && item.Amount == task.Budget)
            .Select(item => item.TransactionId!)
            .ToListAsync();
        var successfulTransactionId = successfulPayments.Count == 1 ? successfulPayments[0] : null;
        var refundChargeIsJournalled = successfulTransactionId != null &&
            await _context.FinancialJournals.AnyAsync(journal =>
                journal.SourceType == "paystack_transaction" &&
                journal.SourceId == successfulTransactionId &&
                journal.EventType == "payment_collected");
        var existingRefund = await _context.Refunds.FirstOrDefaultAsync(item => item.TaskId == task.Id);
        var activePayout = await _context.Payouts.AnyAsync(item => item.TaskId == task.Id &&
            (item.Status == "Pending" || item.Status == "Processing" || item.Status == "AwaitingFunds" ||
             item.Status == "AwaitingEligibility" || item.Status == "AwaitingBankDetails" ||
             item.Status == "Queued" || item.Status == "Initiated" || item.Status == "Completed"));

        if (openDispute == null || task.PaymentStatus != "DisputePending" || task.EscrowStatus != "disputed" ||
            task.TaskStatus is not ("AwaitingConfirmation" or "Completed") ||
            task.PayoutStatus == "Completed" || activePayout ||
            !refundChargeIsJournalled ||
            task.Budget <= 0)
            return Conflict(new ApiResponse<int> { Success = false, Message = "A refund approval requires an open dispute, a frozen unpaid task, a verified Paystack charge and no existing payout." });

        if (existingRefund != null && existingRefund.Status is "Complete" or "Submitted" or "Pending" or "PendingInvestigation" or "ReconciliationRequired")
            return Conflict(new ApiResponse<int> { Success = false, Message = "A refund already exists or is awaiting provider action; reconcile it before creating another approval." });

        var now = DateTime.UtcNow;
        await ExpireOpenFinancialApprovalsAsync(task.Id, now, HttpContext.RequestAborted);
        await using var requestTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var hasOpenRequest = await _context.FinancialApprovalRequests.AnyAsync(item =>
            item.TaskId == task.Id &&
            (item.Status == "Pending" || item.Status == "Approved" || item.Status == "Executing"));
        if (hasOpenRequest)
            return Conflict(new ApiResponse<int> { Success = false, Message = "An active refund approval already exists for this task." });

        var approval = new Models.FinancialApprovalRequest
        {
            TaskId = task.Id,
            DisputeId = openDispute.Id,
            PaymentTransactionId = successfulTransactionId!,
            Amount = task.Budget,
            Action = "refund_creator",
            Reason = reason,
            Status = "Pending",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RequestedByUserId = actorId.Value,
            RequestedAt = now,
            ExpiresAt = now.AddMinutes(30)
        };
        _context.FinancialApprovalRequests.Add(approval);
        await _context.SaveChangesAsync();
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialRefundApprovalRequested",
            EntityType = "FinancialApprovalRequest",
            EntityId = approval.Id,
            NewValues = $"action=refund_creator; taskId={task.Id}; transactionId={successfulTransactionId}; amount={task.Budget:F2}; expiresAt={approval.ExpiresAt:O}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (requestTransaction != null)
            await requestTransaction.CommitAsync();

        return Ok(new ApiResponse<int> { Success = true, Data = approval.Id, Message = "Two-person refund approval request created. No refund has been submitted." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/disputes/release/tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<int>>> RequestDisputeReleaseApproval(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<int> { Success = false, Message = "A reason between 10 and 500 characters is required." });

        var task = await _context.Tasks.FirstOrDefaultAsync(item => item.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<int> { Success = false, Message = "Task not found." });

        var openDispute = await _context.Disputes.AnyAsync(item => item.TaskId == task.Id && item.Status == "Open");
        var successfulPaymentTransactionIds = await _context.PaymentAttempts
            .Where(item => item.TaskId == task.Id && item.Provider == "Paystack" && item.Status == "Completed" &&
                item.TransactionId != null && item.Amount == task.Budget)
            .Select(item => item.TransactionId!)
            .ToListAsync();
        var hasSuccessfulPayment = successfulPaymentTransactionIds.Count == 1 &&
            await _context.FinancialJournals.AnyAsync(journal =>
                journal.SourceType == "paystack_transaction" &&
                journal.SourceId == successfulPaymentTransactionIds[0] &&
                journal.EventType == "payment_collected");
        var activePayout = await _context.Payouts.AnyAsync(item => item.TaskId == task.Id &&
            (item.Status == "Pending" || item.Status == "Processing" || item.Status == "AwaitingFunds" ||
             item.Status == "AwaitingEligibility" || item.Status == "AwaitingBankDetails" ||
             item.Status == "Queued" || item.Status == "Initiated" || item.Status == "Completed"));

        if (!openDispute || task.AcceptedByUserId == null ||
            (task.TaskStatus != "AwaitingConfirmation" && task.TaskStatus != "Completed") ||
            task.PaymentStatus != "DisputePending" || task.EscrowStatus != "disputed" ||
            task.PayoutAmount <= 0 || !hasSuccessfulPayment || activePayout)
            return Conflict(new ApiResponse<int> { Success = false, Message = "A dispute release requires an open dispute, assigned runner, verified paid charge and frozen escrow with no active payout." });

        var now = DateTime.UtcNow;
        await ExpireOpenFinancialApprovalsAsync(task.Id, now, HttpContext.RequestAborted);
        var hasOpenRequest = await _context.FinancialApprovalRequests.AnyAsync(item =>
            item.TaskId == task.Id &&
            (item.Status == "Pending" || item.Status == "Approved" || item.Status == "Executing"));
        if (hasOpenRequest)
            return Conflict(new ApiResponse<int> { Success = false, Message = "An active financial approval already exists for this task." });

        await using var requestTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var approval = new Models.FinancialApprovalRequest
        {
            TaskId = task.Id,
            DisputeId = openDispute.Id,
            PaymentTransactionId = successfulPaymentTransactionIds[0],
            Amount = task.Budget,
            Action = "release_to_runner",
            Reason = reason,
            Status = "Pending",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RequestedByUserId = actorId.Value,
            RequestedAt = now,
            ExpiresAt = now.AddMinutes(30)
        };
        _context.FinancialApprovalRequests.Add(approval);
        await _context.SaveChangesAsync();
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialDisputeReleaseApprovalRequested",
            EntityType = "FinancialApprovalRequest",
            EntityId = approval.Id,
            NewValues = $"action=release_to_runner; taskId={task.Id}; expiresAt={approval.ExpiresAt:O}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (requestTransaction != null)
            await requestTransaction.CommitAsync();

        return Ok(new ApiResponse<int> { Success = true, Data = approval.Id, Message = "Two-person dispute-release approval request created. No payout was queued." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/{id:int}/approve")]
    public async Task<ActionResult<ApiResponse<bool>>> ApproveFinancialRequest(int id, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var decisionReason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(decisionReason) || decisionReason.Length < 10 || decisionReason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "An approval reason between 10 and 500 characters is required." });

        var approval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
        if (approval == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Financial approval request not found." });
        if (approval.RequestedByUserId == actorId.Value)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The requester cannot approve their own financial action." });
        if (approval.Status != "Pending" || approval.ExpiresAt <= DateTime.UtcNow)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Only an unexpired pending request can be approved." });

        var taskIsStillEligible = approval.Action switch
        {
            "force_escrow_release" => await _context.Tasks.AnyAsync(task =>
                task.Id == approval.TaskId && task.AcceptedByUserId != null &&
                task.TaskStatus == "AwaitingConfirmation" && task.EscrowStatus == "held" &&
                task.PaymentStatus == "EscrowHeld" && task.PayoutAmount > 0) &&
                await _context.PaymentAttempts.AnyAsync(payment => payment.TaskId == approval.TaskId &&
                    payment.Provider == "Paystack" && payment.Status == "Completed" && payment.TransactionId != null &&
                    payment.Amount == approval.Amount &&
                    payment.TransactionId == approval.PaymentTransactionId &&
                    _context.FinancialJournals.Any(journal => journal.SourceType == "paystack_transaction" &&
                        journal.SourceId == approval.PaymentTransactionId && journal.EventType == "payment_collected")),
            "refund_creator" or "release_to_runner" => await _context.Tasks.AnyAsync(task =>
                task.Id == approval.TaskId &&
                (task.TaskStatus == "AwaitingConfirmation" || task.TaskStatus == "Completed") &&
                task.AcceptedByUserId != null && task.EscrowStatus == "disputed" &&
                task.PaymentStatus == "DisputePending" && task.PayoutAmount > 0) &&
                approval.DisputeId.HasValue &&
                await _context.Disputes.AnyAsync(dispute =>
                    dispute.Id == approval.DisputeId.Value &&
                    dispute.TaskId == approval.TaskId &&
                    dispute.Status == "Open") &&
                await _context.PaymentAttempts.AnyAsync(payment => payment.TaskId == approval.TaskId &&
                    payment.Provider == "Paystack" && payment.Status == "Completed" && payment.TransactionId != null &&
                    payment.Amount == approval.Amount &&
                    payment.TransactionId == approval.PaymentTransactionId &&
                    _context.FinancialJournals.Any(journal => journal.SourceType == "paystack_transaction" &&
                        journal.SourceId == approval.PaymentTransactionId && journal.EventType == "payment_collected")) &&
                !await _context.Payouts.AnyAsync(payout => payout.TaskId == approval.TaskId &&
                    (payout.Status == "Pending" || payout.Status == "Processing" || payout.Status == "AwaitingFunds" ||
                     payout.Status == "AwaitingEligibility" || payout.Status == "AwaitingBankDetails" ||
                     payout.Status == "Queued" || payout.Status == "Initiated" || payout.Status == "Completed")),
            _ => false
        };
        if (!taskIsStillEligible)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The task no longer meets the eligibility rules for this financial action." });

        await using var approvalTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var now = DateTime.UtcNow;
        var changed = 0;
        if (_context.Database.IsRelational())
        {
            changed = await _context.FinancialApprovalRequests
                .Where(item => item.Id == id && item.Status == "Pending" && item.ExpiresAt > now && item.RequestedByUserId != actorId.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "Approved")
                    .SetProperty(item => item.DecidedByUserId, actorId.Value)
                    .SetProperty(item => item.DecidedAt, now)
                    .SetProperty(item => item.DecisionReason, decisionReason));
        }
        else
        {
            approval.Status = "Approved";
            approval.DecidedByUserId = actorId.Value;
            approval.DecidedAt = now;
            approval.DecisionReason = decisionReason;
            changed = 1;
        }
        if (changed != 1)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "The request changed while approval was being recorded. Refresh and review it again." });

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialApprovalGranted",
            EntityType = "FinancialApprovalRequest",
            EntityId = id,
            OldValues = "Pending",
            NewValues = $"Approved; requestedBy={approval.RequestedByUserId}; reason={decisionReason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (approvalTransaction != null)
            await approvalTransaction.CommitAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Independent approval recorded. Execution remains a separate action." });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [Authorize(Policy = "SuperAdminMfa")]
    [HttpPost("financial-approvals/{id:int}/execute")]
    public async Task<ActionResult<ApiResponse<bool>>> ExecuteFinancialApproval(int id)
    {
        var actorId = GetAdminId();
        if (!actorId.HasValue || !await IsFreshActiveSuperAdminAsync(actorId.Value))
            return Forbid();

        var approval = await _context.FinancialApprovalRequests.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        if (approval == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Financial approval request not found." });
        if (approval.Status == "Executed")
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "This approved action has already been executed." });
        if (approval.Action != "force_escrow_release")
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Dispute financial approvals are executed only through the linked dispute-resolution workflow, which revalidates the dispute and payout state." });
        if (!approval.DecidedByUserId.HasValue ||
            !await IsFreshActiveSuperAdminAsync(approval.DecidedByUserId.Value))
            return Forbid();

        if (approval.Status == "Executing")
        {
            var interruptedTask = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == approval.TaskId);
            var interruptedPayoutExists = interruptedTask?.EscrowStatus == "released" &&
                interruptedTask.PaymentStatus == "EscrowReleased" &&
                await _context.Payouts.AnyAsync(payout => payout.TaskId == approval.TaskId &&
                    payout.Status != "Failed" && payout.Status != "Returned" && payout.Status != "Cancelled");
            if (!interruptedPayoutExists)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "A prior execution is marked in progress but has no confirmed queued payout. Do not retry automatically; reconcile the task and payout before recovery." });

            await using var reconciliationTransaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;
            var reconciledAt = DateTime.UtcNow;
            var reconciled = 0;
            if (_context.Database.IsRelational())
            {
                reconciled = await _context.FinancialApprovalRequests
                    .Where(item => item.Id == id && item.Status == "Executing")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, "Executed")
                        .SetProperty(item => item.ExecutedAt, reconciledAt));
            }
            else
            {
                var trackedApproval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
                if (trackedApproval?.Status == "Executing")
                {
                    trackedApproval.Status = "Executed";
                    trackedApproval.ExecutedAt = reconciledAt;
                    reconciled = 1;
                }
            }
            if (reconciled == 1)
            {
                _context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = actorId.Value,
                    Action = "FinancialApprovalExecutionReconciled",
                    EntityType = "FinancialApprovalRequest",
                    EntityId = id,
                    NewValues = $"Recovered existing payout state; taskId={approval.TaskId}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
                await _context.SaveChangesAsync();
                if (reconciliationTransaction != null)
                    await reconciliationTransaction.CommitAsync();
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Existing payout state reconciled; no second payout was submitted." });
            }
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Execution state changed during reconciliation. Refresh the approval record." });
        }
        if (approval.Status != "Approved")
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Only an approved financial action can be executed." });

        var now = DateTime.UtcNow;
        var claim = 0;
        if (_context.Database.IsRelational())
        {
            claim = await _context.FinancialApprovalRequests
                .Where(item => item.Id == id && item.Status == "Approved" && item.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "Executing")
                    .SetProperty(item => item.ExecutionStartedAt, now)
                    .SetProperty(item => item.ExecutionAttempts, item => item.ExecutionAttempts + 1)
                    .SetProperty(item => item.LastExecutionError, (string?)null));
        }
        else
        {
            var trackedApproval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
            if (trackedApproval != null &&
                trackedApproval.Status == "Approved" && trackedApproval.ExpiresAt > now)
            {
                trackedApproval.Status = "Executing";
                trackedApproval.ExecutionStartedAt = now;
                trackedApproval.ExecutionAttempts++;
                trackedApproval.LastExecutionError = null;
                claim = 1;
            }
        }
        if (claim != 1)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "This action is already being executed, has expired, or is no longer eligible." });

        var task = await _context.Tasks.FirstOrDefaultAsync(item => item.Id == approval.TaskId);
        if (task == null)
            return await ResetFinancialExecutionAsync(id, actorId.Value, "Approved task no longer exists.");

        // Recover safely if the payout was created but the approval status update was interrupted.
        var payoutAlreadyQueued = task.EscrowStatus == "released" &&
            task.PaymentStatus == "EscrowReleased" &&
            await _context.Payouts.AnyAsync(payout => payout.TaskId == task.Id &&
                payout.Status != "Failed" && payout.Status != "Returned" && payout.Status != "Cancelled");

        var released = payoutAlreadyQueued;
        if (!released)
        {
            if (task.EscrowStatus != "held" || task.PaymentStatus != "EscrowHeld" || task.AcceptedByUserId == null)
                return await ResetFinancialExecutionAsync(id, actorId.Value, "Task escrow state changed before execution.");

            released = await _escrowService.ReleaseEscrowAsync(
                task.Id,
                force: true,
                expectedPaymentTransactionId: approval.PaymentTransactionId);
            if (released)
            {
                task = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == approval.TaskId);
                released = task?.EscrowStatus == "released" &&
                    task.PaymentStatus == "EscrowReleased" &&
                    await _context.Payouts.AnyAsync(payout => payout.TaskId == approval.TaskId &&
                        payout.Status != "Failed" && payout.Status != "Returned" && payout.Status != "Cancelled");
            }
        }

        if (!released)
            return await ResetFinancialExecutionAsync(id, actorId.Value, "Escrow release did not produce a persisted payout record. No successful execution was recorded.");

        await using var executionTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var completedAt = DateTime.UtcNow;
        var updated = 0;
        if (_context.Database.IsRelational())
        {
            updated = await _context.FinancialApprovalRequests
                .Where(item => item.Id == id && item.Status == "Executing")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "Executed")
                    .SetProperty(item => item.ExecutedAt, completedAt)
                    .SetProperty(item => item.LastExecutionError, (string?)null));
        }
        else
        {
            var trackedApproval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
            if (trackedApproval?.Status == "Executing")
            {
                trackedApproval.Status = "Executed";
                trackedApproval.ExecutedAt = completedAt;
                trackedApproval.LastExecutionError = null;
                updated = 1;
            }
        }
        if (updated != 1)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Payout was queued, but approval status could not be finalized. Retry this same idempotent execution after reviewing task payout state." });

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId.Value,
            Action = "FinancialApprovalExecuted",
            EntityType = "FinancialApprovalRequest",
            EntityId = id,
            OldValues = "Approved",
            NewValues = $"Executed; taskId={approval.TaskId}; requester={approval.RequestedByUserId}; approver={approval.DecidedByUserId}; payoutQueued=true",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (executionTransaction != null)
            await executionTransaction.CommitAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Two-person-approved escrow release was executed and a payout was queued for the normal payout processor." });
    }

    private async Task<ActionResult<ApiResponse<bool>>> ResetFinancialExecutionAsync(int id, int actorId, string error)
    {
        await using var resetTransaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync()
            : null;
        if (_context.Database.IsRelational())
        {
            await _context.FinancialApprovalRequests
                .Where(item => item.Id == id && item.Status == "Executing")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "Approved")
                    .SetProperty(item => item.LastExecutionError, error));
        }
        else
        {
            var trackedApproval = await _context.FinancialApprovalRequests.FirstOrDefaultAsync(item => item.Id == id);
            if (trackedApproval?.Status == "Executing")
            {
                trackedApproval.Status = "Approved";
                trackedApproval.LastExecutionError = error;
            }
        }
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = actorId,
            Action = "FinancialApprovalExecutionFailed",
            EntityType = "FinancialApprovalRequest",
            EntityId = id,
            NewValues = error,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
        if (resetTransaction != null)
            await resetTransaction.CommitAsync();
        return Conflict(new ApiResponse<bool> { Success = false, Message = error });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpPatch("tasks/{taskId}/force-release-escrow")]
    [Authorize(Policy = "SuperAdminMfa")]
    public async Task<ActionResult<ApiResponse<bool>>> ForceReleaseEscrow(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var actorId = GetAdminId();
        var actor = actorId.HasValue
            ? await _context.Users.AsNoTracking().Where(user => user.Id == actorId.Value && !user.IsArchived && !user.IsSuspended)
                .Select(user => new { user.Roles, user.AuthenticatorTwoFactorEnabled, user.LastTwoFactorVerifiedAt })
                .FirstOrDefaultAsync()
            : null;
        var isFreshSuperAdmin = actor?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true &&
            actor.AuthenticatorTwoFactorEnabled &&
            actor.LastTwoFactorVerifiedAt.HasValue &&
            actor.LastTwoFactorVerifiedAt.Value >= DateTime.UtcNow.AddMinutes(-15);
        if (!isFreshSuperAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<bool> { Success = false, Message = "A SuperAdmin with fresh authenticator verification is required for forced escrow release." });

        var task = await _context.Tasks
            .FirstOrDefaultAsync(t => t.TaskId == taskId);

        if (task == null)
        {
            return NotFound(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task not found"
            });
        }

        if (task.AcceptedByUserId == null)
        {
            return Ok(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task has no assigned runner."
            });
        }

        if (task.EscrowStatus != "held" ||
            task.PaymentStatus != "EscrowHeld")
        {
            return Conflict(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task must have both held escrow and EscrowHeld payment status."
            });
        }

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "A reason between 10 and 500 characters is required."
            });

        // A one-person SuperAdmin action must not move money. Keep this endpoint
        // closed until the separate request/approval/execution workflow is available.
        await WriteAuditAsync(
            "ForceReleaseEscrowBlockedPendingDualApproval",
            "Task",
            task.Id,
            null,
            $"No money moved; dual approval workflow required; reason={reason}");

        return Conflict(new ApiResponse<bool>
        {
            Success = false,
            Data = false,
            Message = "Forced escrow release is temporarily blocked. A two-person approval workflow is required; no payout was initiated."
        });
    }


    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpDelete("tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTask(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });

        var hasFinancialActivity =
            task.PaymentStatus != "Pending" ||
            task.EscrowStatus != "pending" ||
            task.PayoutReference != null ||
            task.PayoutInitiatedAt.HasValue ||
            task.PayoutCompletedAt.HasValue ||
            task.PaidToRunnerAt.HasValue ||
            task.AcceptedByUserId.HasValue ||
            await _context.Payouts.AnyAsync(p => p.TaskId == task.Id);

        if (hasFinancialActivity)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Financially active tasks cannot be hard-deleted. Retain the record for audit and financial reconciliation." });

        task.IsDeleted = true;
        task.DeletedAt = DateTime.UtcNow;
        task.TaskStatus = "Cancelled";
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await WriteAuditAsync("SoftDeleteTask", "Task", task.Id, "Active", $"Cancelled; reason={reason}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task archived" });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpDelete("users/{userId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteUser(int userId, [FromBody] UserLifecycleReasonRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var adminId = GetAdminId();
        if (adminId == userId)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "You cannot archive your own administrator account." });

        if (string.Equals(user.UserType, "admin", StringComparison.OrdinalIgnoreCase) ||
            user.Roles?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("Admin", StringComparer.OrdinalIgnoreCase) == true)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Administrator accounts cannot be archived through user management." });

        if (user.IsArchived)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User is already archived." });
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });

        var oldValues = System.Text.Json.JsonSerializer.Serialize(new
        {
            user.IsArchived,
            user.ArchivedAt
        });
        user.IsArchived = true;
        user.ArchivedAt = DateTime.UtcNow;
        user.ArchivedReason = reason;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = adminId,
            Action = "ArchiveUser",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = oldValues,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                user.IsArchived,
                user.ArchivedAt,
                user.ArchivedReason
            }) + $"; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User archived. Audit and task history have been retained." });
    }

    [Authorize(Policy = AdminPermissionPolicies.UsersManagePolicy)]
    [HttpPost("users/{userId}/restore")]
    public async Task<ActionResult<ApiResponse<bool>>> RestoreUser(int userId, [FromBody] UserLifecycleReasonRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        if (!user.IsArchived)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User is already active." });
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });

        var adminId = GetAdminId();
        var oldValues = System.Text.Json.JsonSerializer.Serialize(new
        {
            user.IsArchived,
            user.ArchivedAt,
            user.ArchivedReason
        });
        user.IsArchived = false;
        user.ArchivedAt = null;
        user.ArchivedReason = null;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = adminId,
            Action = "RestoreUser",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = oldValues,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                user.IsArchived,
                user.ArchivedAt,
                user.ArchivedReason
            }) + $"; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = user.IsSuspended ? "User restored but remains suspended; reinstate separately." : "User restored. Active sessions have been revoked." });
    }

    [Authorize(Policy = AdminPermissionPolicies.AuditReadPolicy)]
    [HttpGet("audit-logs")]
    public async Task<ActionResult<ApiResponse<object>>> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var total = await _context.AuditLogs.CountAsync();
        var logs = await _context.AuditLogs
            .Include(a => a.User)
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                id = a.Id,
                userId = a.UserId,
                adminName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}" : "System",
                userName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}" : "System",
                action = a.Action,
                entityType = a.EntityType,
                entityId = a.EntityId,
                oldValues = a.OldValues,
                newValues = a.NewValues,
                ipAddress = a.IpAddress,
                createdAt = a.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { logs, total, totalCount = total, page, pageSize, totalPages }
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.TasksManagePolicy)]
    [HttpGet("tasks/{taskId}/messages")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetTaskMessages(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<List<object>> { Success = false, Message = "Task not found" });

        var messages = await _context.TaskMessages
            .Include(m => m.Sender)
            .Where(m => m.TaskId == task.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                id = m.Id,
                senderId = m.SenderId,
                senderName = $"{m.Sender.FirstName} {m.Sender.LastName}",
                senderEmail = m.Sender.Email,
                content = m.Content,
                timestamp = m.CreatedAt,
                isRead = m.IsRead
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>> { Success = true, Data = messages });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("refunds")]
    public async Task<ActionResult<ApiResponse<object>>> GetRefunds([FromQuery] string? status = null)
    {
        var query = _context.Refunds
            .Include(r => r.Task)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var refunds = await query
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                id = r.Id,
                taskId = r.Task.TaskId,
                refundId = r.RefundId,
                transactionId = r.TransactionId,
                amount = r.Amount,
                reason = r.Reason,
                status = r.Status,
                provider = r.Provider,
                failureReason = r.FailureReason,
                createdAt = r.CreatedAt,
                submittedAt = r.SubmittedAt,
                completedAt = r.CompletedAt,
                lastReconciledAt = r.LastReconciledAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = refunds });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpPost("refunds/reconcile")]
    public async Task<ActionResult<ApiResponse<object>>> ReconcileRefunds()
    {
        var pending = await _context.Refunds
            .Where(r => r.Status == "Pending" || r.Status == "Submitted" || r.Status == "PendingInvestigation")
            .ToListAsync();

        var reconciled = 0;
        foreach (var refund in pending)
        {
            var providerRefunds = await _paymentProvider.GetRefundsByTransactionIdAsync(
                refund.TransactionId,
                HttpContext.RequestAborted);

            var match = providerRefunds
                .Where(r => r.Amount == refund.Amount)
                .OrderByDescending(r => string.Equals(r.Status, "processed", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (match == null)
            {
                refund.LastReconciledAt = DateTime.UtcNow;
                continue;
            }

            refund.RefundId = match.RefundId;
            refund.Status = match.Status.ToLowerInvariant() switch
            {
                "pending" or "processing" => "Submitted",
                "processed" => "Complete",
                "failed" => "Failed",
                "needs-attention" => "PendingInvestigation",
                _ => "Submitted"
            };
            refund.LastReconciledAt = DateTime.UtcNow;
            if (refund.Status == "Complete")
                refund.CompletedAt ??= DateTime.UtcNow;

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == refund.TaskId);
            if (task != null && refund.Status == "Complete")
            {
                task.PaymentStatus = "Refunded";
                task.TaskStatus = "Cancelled";
                task.EscrowStatus = "refunded";
                task.PayoutStatus = "NotStarted";
                task.PayoutReference = null;
                task.PayoutInitiatedAt = null;
                task.PayoutCompletedAt = null;
                task.PaidToRunnerAt = null;
                task.UpdatedAt = DateTime.UtcNow;
            }
            reconciled++;
        }

        await _context.SaveChangesAsync();
        await WriteAuditAsync("ReconcileRefunds", "Refund", null, null, $"reconciled={reconciled}");

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { scanned = pending.Count, reconciled },
            Message = "Refund reconciliation completed."
        });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("withdrawal-requests")]
    public async Task<ActionResult<ApiResponse<object>>> GetWithdrawalRequests([FromQuery] string? status = null)
    {
        var query = _context.WithdrawalRequests
            .Include(w => w.User)
            .Include(w => w.BankAccount)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(w => w.Status == status);

        var requests = await query
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                id = w.Id,
                userId = w.UserId,
                userName = $"{w.User.FirstName} {w.User.LastName}",
                amount = w.Amount,
                fee = w.Fee,
                status = w.Status,
                reference = w.Reference,
                bankName = w.BankAccount.BankName,
                accountNumber = "****",
                createdAt = w.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = requests });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpGet("bank-accounts")]
    public async Task<ActionResult<ApiResponse<object>>> GetBankAccounts([FromQuery] bool? unverifiedOnly = null)
    {
        var query = _context.BankAccounts.Include(b => b.User).AsQueryable();

        if (unverifiedOnly == true)
            query = query.Where(b => !b.IsVerified);

        var accounts = await query
            .Select(b => new
            {
                id = b.Id,
                userId = b.UserId,
                userName = $"{b.User.FirstName} {b.User.LastName}",
                bankName = b.BankName,
                accountNumber = b.AccountNumber,
                accountHolderName = b.AccountHolderName,
                isVerified = b.IsVerified,
                createdAt = b.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = accounts });
    }

    [Authorize(Policy = AdminPermissionPolicies.FinanceManagePolicy)]
    [HttpPatch("bank-accounts/{id}/verify")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyBankAccount(int id)
    {
        var account = await _context.BankAccounts
            .Include(b => b.User)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (account == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Bank account not found" });

        var verification = await _bankingService.VerifyBankAccountAsync(
            account.User,
            account,
            HttpContext.RequestAborted);

        account.IsVerified = verification.Success;
        account.VerifiedAt = verification.Success ? DateTime.UtcNow : null;
        account.ProviderReference = verification.ProviderReference;
        await _context.SaveChangesAsync();
        await WriteAuditAsync(
            verification.Success ? "VerifyBankAccount" : "BankAccountVerificationFailed",
            "BankAccount",
            id,
            null,
            verification.Message);

        return Ok(new ApiResponse<bool>
        {
            Success = verification.Success,
            Data = verification.Success,
            Message = verification.Message
        });
    }
}