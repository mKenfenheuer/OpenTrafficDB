using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using OpenTrafficDB.App.Models;

namespace OpenTrafficDB.App.Data;

public class AppDbContext : DbContext
{
    // Generic constructor required by EF when multiple DbContext types are registered.
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Protected overload for derived types (e.g. ReadOnlyAppDbContext).
    protected AppDbContext(DbContextOptions options) : base(options) { }

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
        modelBuilder.HasPostgresExtension("postgis");

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
            e.HasIndex(o => new { o.Timestamp, o.Latitude, o.Longitude });
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
            // GIST index on BBox geometry — replaces the four-column B-tree index.
            // Enables O(log n) ST_Intersects queries for tile bbox lookups.
            e.HasIndex(g => g.BBox).HasMethod("GIST");
            e.Property(g => g.BBox).HasColumnType("geometry(Polygon,4326)");
        });

        modelBuilder.Entity<WaySpeedSummary>(e =>
        {
            e.HasIndex(s => s.OsmWayId).IsUnique();
        });
    }
}

/// <summary>
/// Read-only DbContext pointed at the read replica (falls back to primary if
/// ConnectionStrings:ReadConnection is not configured).
/// Inject this into services that never write — tiles, CSV exports, routing queries.
/// </summary>
public class ReadOnlyAppDbContext(DbContextOptions<ReadOnlyAppDbContext> options)
    : AppDbContext(options) { }
