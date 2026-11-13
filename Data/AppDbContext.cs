using Microsoft.EntityFrameworkCore;
using DoForYou.API.Models;

namespace DoForYou.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Models.Task> Tasks { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<PaymentAttempt> PaymentAttempts { get; set; } = null!;
    public DbSet<Rating> Ratings { get; set; }
    public DbSet<TaskMessage> TaskMessages { get; set; }
    public DbSet<TaskProgressUpdate> TaskProgressUpdates { get; set; }
    public DbSet<Dispute> Disputes { get; set; }
    public DbSet<TaskIncident> TaskIncidents { get; set; } = null!;
    public DbSet<WalletTransaction> WalletTransactions { get; set; }
    public DbSet<BusinessRule> BusinessRules { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<BankAccount> BankAccounts { get; set; }
    public DbSet<WithdrawalRequest> WithdrawalRequests { get; set; }
    public DbSet<Payout> Payouts { get; set; } = null!;
    public DbSet<Refund> Refunds { get; set; } = null!;
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<AdminInvitation> AdminInvitations { get; set; } = null!;
    public DbSet<SuperAdminChangeRequest> SuperAdminChangeRequests { get; set; } = null!;
    public DbSet<FinancialApprovalRequest> FinancialApprovalRequests { get; set; } = null!;
    public DbSet<AdminPermissionGrant> AdminPermissionGrants { get; set; } = null!;
    public DbSet<DuplicateAccountCase> DuplicateAccountCases { get; set; } = null!;
    public DbSet<SupportTicket> SupportTickets { get; set; }
    public DbSet<Testimonial> Testimonials { get; set; }
    public DbSet<UserConsentEvent> UserConsentEvents { get; set; } = null!;
    public DbSet<IdentityVerificationEvent> IdentityVerificationEvents { get; set; } = null!;
    public DbSet<IdentityVerificationSubmission> IdentityVerificationSubmissions { get; set; } = null!;
    public DbSet<IdentityNotificationOutbox> IdentityNotificationOutbox { get; set; } = null!;

    public override int SaveChanges()
    {
        EnsureAuditLogsAppendOnly();
        NormalizeUserEmails();
        NormalizeUtcDates();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAppendOnly();
        NormalizeUserEmails();
        NormalizeUtcDates();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Audit history is append-only through the application data layer.
    // Database-level permissions/retention controls are still required against privileged DB access.
    private void EnsureAuditLogsAppendOnly()
    {
        var changedAudit = ChangeTracker.Entries<AuditLog>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (changedAudit != null)
            throw new InvalidOperationException("Audit log entries are append-only and cannot be modified or deleted.");
    }

    private void NormalizeUserEmails()
    {
        foreach (var entry in ChangeTracker.Entries<User>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Email = entry.Entity.Email.Trim();
            entry.Entity.NormalizedEmail = entry.Entity.Email.ToLowerInvariant();
        }
    }

    private void NormalizeUtcDates()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            foreach (var prop in entry.Properties)
            {
                if (prop.CurrentValue is DateTime dt && dt.Kind == DateTimeKind.Unspecified)
                    prop.CurrentValue = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AdminPermissionGrant>()
            .HasIndex(grant => new { grant.UserId, grant.PermissionKey })
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("IX_AdminPermissionGrants_Active_UserId_PermissionKey");

        modelBuilder.Entity<AdminPermissionGrant>()
            .HasIndex(grant => new { grant.UserId, grant.ExpiresAt })
            .HasDatabaseName("IX_AdminPermissionGrants_UserId_ExpiresAt");

        modelBuilder.Entity<AdminPermissionGrant>()
            .HasIndex(grant => grant.GrantedByUserId)
            .HasDatabaseName("IX_AdminPermissionGrants_GrantedByUserId");

        modelBuilder.Entity<AdminPermissionGrant>()
            .HasIndex(grant => grant.RevokedByUserId)
            .HasDatabaseName("IX_AdminPermissionGrants_RevokedByUserId");

        modelBuilder.Entity<DuplicateAccountCase>()
            .HasIndex(item => new { item.Status, item.CreatedAt })
            .HasDatabaseName("IX_DuplicateAccountCases_Status_CreatedAt");

        modelBuilder.Entity<DuplicateAccountCase>()
            .HasIndex(item => new { item.PrimaryUserId, item.CandidateUserId })
            .IsUnique()
            .HasFilter("\"Status\" = 'Open'")
            .HasDatabaseName("IX_DuplicateAccountCases_OpenPair");

        modelBuilder.Entity<FinancialApprovalRequest>()
            .HasIndex(request => request.IdempotencyKey)
            .IsUnique();

        modelBuilder.Entity<FinancialApprovalRequest>()
            .HasIndex(request => new { request.TaskId, request.Action })
            .IsUnique()
            .HasFilter("\"Status\" IN ('Pending', 'Approved', 'Executing')")
            .HasDatabaseName("IX_FinancialApprovalRequests_OpenTaskAction");

        modelBuilder.Entity<FinancialApprovalRequest>()
            .HasIndex(request => new { request.Status, request.ExpiresAt });

        modelBuilder.Entity<SuperAdminChangeRequest>()
            .HasIndex(request => new { request.Status, request.ExpiresAt })
            .HasDatabaseName("IX_SuperAdminChangeRequests_Status_ExpiresAt");

        modelBuilder.Entity<SuperAdminChangeRequest>()
            .HasIndex(request => new { request.TargetUserId, request.Status })
            .HasDatabaseName("IX_SuperAdminChangeRequests_TargetUserId_Status");

        modelBuilder.Entity<AdminInvitation>()
            .HasIndex(invitation => invitation.TokenHash)
            .IsUnique()
            .HasDatabaseName("IX_AdminInvitations_TokenHash");

        modelBuilder.Entity<AdminInvitation>()
            .HasIndex(invitation => new { invitation.NormalizedEmail, invitation.Status })
            .HasDatabaseName("IX_AdminInvitations_NormalizedEmail_Status");

        modelBuilder.Entity<IdentityNotificationOutbox>()
            .HasIndex(item => item.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("IX_IdentityNotificationOutbox_IdempotencyKey");

        modelBuilder.Entity<IdentityNotificationOutbox>()
            .HasIndex(item => new { item.Status, item.AvailableAt })
            .HasDatabaseName("IX_IdentityNotificationOutbox_Status_AvailableAt");

        modelBuilder.Entity<IdentityNotificationOutbox>()
            .HasIndex(item => item.LeaseExpiresAt)
            .HasDatabaseName("IX_IdentityNotificationOutbox_LeaseExpiresAt");

        modelBuilder.Entity<Notification>()
            .HasIndex(item => item.OutboxId)
            .IsUnique()
            .HasFilter("\"OutboxId\" IS NOT NULL")
            .HasDatabaseName("IX_Notifications_OutboxId");

        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("IX_Users_NormalizedEmail");
        modelBuilder.Entity<User>()
            .HasIndex(user => user.IdentityDocumentIdHmac)
            .HasDatabaseName("IX_Users_IdentityDocumentIdHmac");

        modelBuilder.Entity<User>()
            .HasIndex(user => user.GoogleSubject)
            .IsUnique()
            .HasDatabaseName("IX_Users_GoogleSubject");

        modelBuilder.Entity<User>()
            .HasIndex(user => user.IdentityVerificationExternalUserId)
            .IsUnique()
            .HasFilter("\"IdentityVerificationExternalUserId\" IS NOT NULL")
            .HasDatabaseName("IX_Users_IdentityVerificationExternalUserId");

        modelBuilder.Entity<UserConsentEvent>()
            .HasIndex(e => new { e.UserId, e.ConsentType, e.RecordedAt })
            .HasDatabaseName("IX_UserConsentEvents_UserId_ConsentType_RecordedAt");

        modelBuilder.Entity<UserConsentEvent>()
            .HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => s.UserId)
            .IsUnique()
            .HasFilter("\"Status\" = 'pending'")
            .HasDatabaseName("IX_IdentityVerificationSubmissions_UserId_Pending");

        modelBuilder.Entity<IdentityVerificationEvent>()
            .HasIndex(e => new { e.Provider, e.EventHash })
            .IsUnique()
            .HasDatabaseName("IX_IdentityVerificationEvents_Provider_EventHash");

        modelBuilder.Entity<IdentityVerificationEvent>()
            .HasIndex(e => new { e.UserId, e.ReceivedAt })
            .HasDatabaseName("IX_IdentityVerificationEvents_UserId_ReceivedAt");

        modelBuilder.Entity<IdentityVerificationEvent>()
            .HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => s.DocumentIdHmac)
            .HasDatabaseName("IX_IdentityVerificationSubmissions_DocumentIdHmac");

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => s.ReviewClaimExpiresAt)
            .HasDatabaseName("IX_IdentityVerificationSubmissions_ReviewClaimExpiresAt");

        modelBuilder.Entity<IdentityVerificationEvent>()
            .HasIndex(e => new { e.SubmissionId, e.ActorUserId, e.ReceivedAt })
            .HasDatabaseName("IX_IdentityVerificationEvents_Submission_Actor_ReceivedAt");

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => new { s.Status, s.SubmittedAt })
            .HasDatabaseName("IX_IdentityVerificationSubmissions_Status_SubmittedAt");

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => new { s.UserId, s.SubmittedAt })
            .HasDatabaseName("IX_IdentityVerificationSubmissions_UserId_SubmittedAt");

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasIndex(s => s.ReviewedByUserId)
            .HasDatabaseName("IX_IdentityVerificationSubmissions_ReviewedByUserId");

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<IdentityVerificationSubmission>()
            .HasOne(s => s.ReviewedByUser)
            .WithMany()
            .HasForeignKey(s => s.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TaskIncident>()
            .HasOne(i => i.Task)
            .WithMany()
            .HasForeignKey(i => i.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TaskIncident>()
            .HasIndex(i => new { i.ReporterUserId, i.CreatedAt });

        modelBuilder.Entity<TaskIncident>()
            .HasIndex(i => new { i.TaskId, i.CreatedAt });

        modelBuilder.Entity<Models.Task>()
            .HasQueryFilter(t => !t.IsDeleted);
        modelBuilder.Entity<Models.Task>()
            .HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Task>()
            .HasOne(t => t.AcceptedByUser)
            .WithMany()
            .HasForeignKey(t => t.AcceptedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Testimonial>().Property(t => t.Name).HasMaxLength(100);
        modelBuilder.Entity<Testimonial>().Property(t => t.Role).HasMaxLength(80);
        modelBuilder.Entity<Testimonial>().Property(t => t.Text).HasMaxLength(1200);
        modelBuilder.Entity<Testimonial>().Property(t => t.IsPublished).HasDefaultValue(false);
        modelBuilder.Entity<Testimonial>().Property(t => t.SortOrder).HasDefaultValue(0);
        modelBuilder.Entity<Testimonial>().HasIndex(t => new { t.IsPublished, t.SortOrder });

        modelBuilder.Entity<PaymentAttempt>()
            .HasIndex(p => p.Reference)
            .IsUnique();

        modelBuilder.Entity<PaymentAttempt>()
            .HasIndex(p => p.TransactionId)
            .IsUnique()
            .HasFilter("\"TransactionId\" IS NOT NULL");

        modelBuilder.Entity<PaymentAttempt>()
            .HasOne(p => p.Task)
            .WithMany()
            .HasForeignKey(p => p.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasIndex(p => p.MerchantReference)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasIndex(r => r.TaskId)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasIndex(r => r.RefundId)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasOne(r => r.Task)
            .WithMany()
            .HasForeignKey(r => r.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasIndex(p => p.ProviderReference)
            .IsUnique()
            .HasFilter("\"ProviderReference\" IS NOT NULL");

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.Task)
            .WithMany()
            .HasForeignKey(p => p.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.Runner)
            .WithMany()
            .HasForeignKey(p => p.RunnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.BankAccount)
            .WithMany()
            .HasForeignKey(p => p.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}