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
    public DbSet<Rating> Ratings { get; set; }
    public DbSet<TaskMessage> TaskMessages { get; set; }
    public DbSet<TaskProgressUpdate> TaskProgressUpdates { get; set; }
    public DbSet<Dispute> Disputes { get; set; }
    public DbSet<WalletTransaction> WalletTransactions { get; set; }
    public DbSet<BusinessRule> BusinessRules { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}