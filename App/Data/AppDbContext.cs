using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.App.Models;

namespace OpenTrafficDB.App.Data;

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
    public DbSet<WaySpeedAggregate> WaySpeedAggregates => Set<WaySpeedAggregate>();
    public DbSet<H3SpeedAggregate> H3SpeedAggregates => Set<H3SpeedAggregate>();
    public DbSet<WayGeometry> WayGeometries => Set<WayGeometry>();
    public DbSet<WaySpeedSummary> WaySpeedSummaries => Set<WaySpeedSummary>();

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
            // OSM way reference — filters unmatched rows
            e.HasIndex(o => o.OsmWayId).HasFilter("\"OsmWayId\" IS NOT NULL");
            // H3 spatial index
            e.HasIndex(o => o.H3CellIndex).HasFilter("\"H3CellIndex\" IS NOT NULL");
            e.HasIndex(o => o.TripId);
            e.HasIndex(o => o.ApiKeyId);
            // For aggregation cleanup queries
            e.HasIndex(o => new { o.IsAggregated, o.CreatedAt });
        });

        modelBuilder.Entity<BlockedIp>(e =>
        {
            e.HasIndex(b => b.IpHash).IsUnique();
        });

        modelBuilder.Entity<WaySpeedAggregate>(e =>
        {
            // Unique business key: OSM way + time bucket
            e.HasIndex(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
        });

        modelBuilder.Entity<H3SpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.H3CellIndex, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
            e.HasIndex(a => a.H3CellIndex);
        });

        modelBuilder.Entity<WayGeometry>(e =>
        {
            // Fast lookup by OSM way ID (primary access pattern for tile generation)
            e.HasIndex(g => g.OsmWayId).IsUnique();
            // Spatial pre-filter: bbox overlap queries
            e.HasIndex(g => new { g.MinLat, g.MaxLat });
            e.HasIndex(g => new { g.MinLon, g.MaxLon });
        });

        modelBuilder.Entity<WaySpeedSummary>(e =>
        {
            e.HasIndex(s => s.OsmWayId).IsUnique();
        });
    }
}
