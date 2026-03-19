using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NetTopologySuite.Geometries;
using OpenTrafficDB.Shared.Models;

namespace OpenTrafficDB.Shared.Data;

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
    public DbSet<WayGeometry> WayGeometries => Set<WayGeometry>();
    public DbSet<WaySpeedSummary> WaySpeedSummaries => Set<WaySpeedSummary>();
    public DbSet<AggregationMetric> AggregationMetrics => Set<AggregationMetric>();

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
            e.HasIndex(o => o.TripId);
            e.HasIndex(o => o.ApiKeyId);
            e.HasIndex(o => new { o.IsAggregated, o.CreatedAt });
            // Partial indexes for coordinator trip-discovery queries — avoids full table scans every cycle.
            e.HasIndex(o => o.TripId)
             .HasDatabaseName("IX_SpeedObservations_TripId_Unmatched")
             .HasFilter("\"OsmWayId\" IS NULL AND \"IsAggregated\" = FALSE");
            e.HasIndex(o => o.TripId)
             .HasDatabaseName("IX_SpeedObservations_TripId_Unaggregated")
             .HasFilter("\"OsmWayId\" IS NOT NULL AND \"IsAggregated\" = FALSE");
        });

        modelBuilder.Entity<BlockedIp>(e =>
        {
            e.HasIndex(b => b.IpHash).IsUnique();
        });

        modelBuilder.Entity<WaySpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
        });

        modelBuilder.Entity<WayGeometry>(e =>
        {
            e.HasIndex(g => g.OsmWayId).IsUnique();
            e.HasIndex(g => g.BBox).HasMethod("GIST");
            e.Property(g => g.BBox).HasColumnType("geometry(Polygon,4326)");
            e.Property(g => g.Geometry).HasColumnType("geometry(LineString,4326)");
        });

        modelBuilder.Entity<WaySpeedSummary>(e =>
        {
            e.HasIndex(s => s.OsmWayId).IsUnique();
            // Used by the tile query: WHERE wss."LastUpdated" >= NOW() - INTERVAL '30 minutes'
            e.HasIndex(s => s.LastUpdated);
        });

        modelBuilder.Entity<AggregationMetric>(e =>
        {
            e.HasIndex(m => m.ProcessedAt);
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

/// <summary>
/// Design-time factory so `dotnet ef migrations` can instantiate AppDbContext
/// without a running host. Connection string is unused at design time.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=opentrafficdb;Username=postgres",
                o => o.UseNetTopologySuite())
            .Options;
        return new AppDbContext(options);
    }
}
