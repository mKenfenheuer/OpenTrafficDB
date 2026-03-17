using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.App.Models;

namespace OpenTrafficDB.App.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions options) : base(options)
    {
        if (Database.GetPendingMigrations().Any())
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
            e.HasIndex(o => o.Timestamp);
            // Composite index for live bbox queries (Timestamp + lat/lon range filter)
            e.HasIndex(o => new { o.Timestamp, o.Latitude, o.Longitude });
            // Composite index for aggregation: fetch recent unmatched by way
            e.HasIndex(o => new { o.Timestamp, o.OsmWayId }).HasFilter("\"OsmWayId\" IS NOT NULL");
            e.HasIndex(o => new { o.Latitude, o.Longitude });
            e.HasIndex(o => o.OsmWayId).HasFilter("\"OsmWayId\" IS NOT NULL");
            e.HasIndex(o => o.H3CellIndex).HasFilter("\"H3CellIndex\" IS NOT NULL");
            e.HasIndex(o => o.TripId);
            e.HasIndex(o => o.ApiKeyId);
            e.HasIndex(o => new { o.IsAggregated, o.CreatedAt });
        });

        modelBuilder.Entity<BlockedIp>(e =>
        {
            e.HasIndex(b => b.IpHash).IsUnique();
        });

        modelBuilder.Entity<WaySpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
        });

        modelBuilder.Entity<H3SpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.H3CellIndex, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
            e.HasIndex(a => a.H3CellIndex);
        });

        modelBuilder.Entity<WayGeometry>(e =>
        {
            e.HasIndex(g => g.OsmWayId).IsUnique();
            // Combined 4-column index lets Postgres do a single range scan for bbox overlap queries.
            // Replace with a PostGIS GIST index on a geometry column for best spatial performance.
            e.HasIndex(g => new { g.MinLat, g.MaxLat, g.MinLon, g.MaxLon });
        });

        modelBuilder.Entity<WaySpeedSummary>(e =>
        {
            e.HasIndex(s => s.OsmWayId).IsUnique();
        });
    }
}
