using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions options) : base(options)
    {
        if(Database.GetPendingMigrations().Any())
        {
            Database.Migrate();
        }
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<SpeedObservation> SpeedObservations => Set<SpeedObservation>();
    public DbSet<BlockedIp> BlockedIps => Set<BlockedIp>();
    public DbSet<RoadSpeedAggregate> RoadSpeedAggregates => Set<RoadSpeedAggregate>();

    

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.EmailHash).IsUnique();
            e.HasIndex(u => u.VerificationToken).HasFilter("\"VerificationToken\" IS NOT NULL");
            e.HasIndex(u => u.PasswordResetToken).HasFilter("\"PasswordResetToken\" IS NOT NULL");
            e.Property(u => u.Role).HasConversion<string>().HasDefaultValue(UserRole.User);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.KeyPrefix);
            e.HasOne(k => k.User)
             .WithMany(u => u.ApiKeys)
             .HasForeignKey(k => k.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SpeedObservation>(e =>
        {
            // Primary query pattern: recency + bbox
            e.HasIndex(o => o.Timestamp);
            e.HasIndex(o => new { o.Latitude, o.Longitude });
            e.HasIndex(o => o.ValhallaEdgeId).HasFilter("\"ValhallaEdgeId\" IS NOT NULL");
            e.HasIndex(o => o.TripId);
            e.HasIndex(o => o.ApiKeyId);
        });

        modelBuilder.Entity<BlockedIp>(e =>
        {
            e.HasIndex(b => b.IpHash).IsUnique();
        });

        modelBuilder.Entity<RoadSpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.ValhallaEdgeId, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
            e.HasIndex(a => a.ValhallaEdgeId);
        });
    }
}
