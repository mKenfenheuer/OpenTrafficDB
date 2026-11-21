using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NetTopologySuite.Geometries;
using OpenTrafficDBModels;

namespace OpenTrafficDBData;

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
    public DbSet<WayProfile> WayProfiles => Set<WayProfile>();
    public DbSet<WayClosure> WayClosures => Set<WayClosure>();
    public DbSet<WayGeometry> WayGeometries => Set<WayGeometry>();
    public DbSet<WaySpeedSummary> WaySpeedSummaries => Set<WaySpeedSummary>();
    public DbSet<AggregationMetric> AggregationMetrics => Set<AggregationMetric>();
    public DbSet<HazardObservation> HazardObservations => Set<HazardObservation>();
    public DbSet<HazardAggregate> HazardAggregates => Set<HazardAggregate>();

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
            // Composite PK required for PARTITION BY RANGE ("Timestamp").
            // Id is still the identity/surrogate key; Timestamp is the partition key.
            e.HasKey(o => new { o.Id, o.Timestamp });
            e.Property(o => o.Id).UseIdentityByDefaultColumn();

            // Partitioning is applied via raw SQL in the migration (EF doesn't generate PARTITION BY RANGE).
            // Child partitions (monthly) are also created there.

            e.HasIndex(o => o.Timestamp);
            e.HasIndex(o => new { o.Timestamp, o.Latitude, o.Longitude });
            e.HasIndex(o => new { o.Timestamp, o.OsmWayId }).HasFilter("\"OsmWayId\" IS NOT NULL");
            e.HasIndex(o => new { o.Latitude, o.Longitude });
            e.HasIndex(o => o.OsmWayId).HasFilter("\"OsmWayId\" IS NOT NULL");
            e.HasIndex(o => o.ApiKeyId);
            e.HasIndex(o => new { o.IsAggregated, o.CreatedAt });
            e.Property(o => o.MatchAttempts).HasDefaultValue((short)0);
            // Map-matching queue: unmatched observations grouped by trip, FIFO by Id.
            e.HasIndex(o => new { o.TripId, o.Id }, "IX_SpeedObservations_MatchQueue")
             .HasFilter("\"OsmWayId\" IS NULL AND \"IsAggregated\" = FALSE");
            // Aggregation queue: matched but not yet aggregated observations.
            e.HasIndex(o => o.TripId)
             .HasDatabaseName("IX_SpeedObservations_TripId_Unaggregated")
             .HasFilter("\"OsmWayId\" IS NOT NULL AND \"IsAggregated\" = FALSE");

            // Autovacuum storage parameters are set on each leaf partition by PartitionMaintenanceService
            // (PostgreSQL does not allow WITH (...) on partitioned parent tables).
        });

        modelBuilder.Entity<BlockedIp>(e =>
        {
            e.HasIndex(b => b.IpHash).IsUnique();
        });

        modelBuilder.Entity<WaySpeedAggregate>(e =>
        {
            e.HasIndex(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket }).IsUnique();
            // fillfactor=80 leaves room for HOT updates (SumSpeedKmh, SampleCount updated every aggregation cycle).
            e.HasStorageParameter("fillfactor", 80);
        });

        modelBuilder.Entity<WayClosure>(e =>
        {
            e.HasKey(c => c.OsmWayId);
            e.Property(c => c.OsmWayId).ValueGeneratedNever();
            e.Property(c => c.Source).HasMaxLength(64);
            e.HasIndex(c => c.ExpiresAt);
        });

        modelBuilder.Entity<WayProfile>(e =>
        {
            e.HasKey(p => p.OsmWayId);
            e.Property(p => p.OsmWayId).ValueGeneratedNever();
            // Rows are rewritten on every profile flush; leave room for HOT updates.
            e.HasStorageParameter("fillfactor", 80);
        });

        modelBuilder.Entity<WayGeometry>(e =>
        {
            e.HasIndex(g => g.OsmWayId).IsUnique();
            e.HasIndex(g => g.BBox).HasMethod("GIST");
            e.Property(g => g.BBox).HasColumnType("geometry(Polygon,4326)");
            e.Property(g => g.Geometry).HasColumnType("geometry(LineString,4326)");
            e.Property(g => g.RoadClass).HasDefaultValue((short)5);
            // Low-zoom traffic tiles show only the higher road classes (TrafficTileService.MaxRoadClass):
            // one partial spatial index per level so they never scan the residential streets.
            foreach (var maxClass in new short[] { 1, 2, 3, 4 })
                e.HasIndex(g => g.BBox, $"IX_WayGeometries_BBox_Class{maxClass}")
                    .HasMethod("GIST")
                    .HasFilter($"\"RoadClass\" <= {maxClass}");
        });

        modelBuilder.Entity<WaySpeedSummary>(e =>
        {
            e.HasIndex(s => s.OsmWayId).IsUnique();
            // Used by the tile query: WHERE wss."LastUpdated" >= NOW() - INTERVAL '60 minutes'
            e.HasIndex(s => s.LastUpdated);
            // fillfactor=80: ConstrainedSpeed, FreeflowSpeed, EncodedSpeeds updated on every aggregation cycle.
            e.HasStorageParameter("fillfactor", 80);
        });

        modelBuilder.Entity<AggregationMetric>(e =>
        {
            e.HasIndex(m => m.ProcessedAt);
        });

        modelBuilder.Entity<HazardObservation>(e =>
        {
            // Aggregation reads "WHERE NOT IsProcessed ORDER BY Id LIMIT n": this partial index serves
            // it directly instead of sorting every unprocessed row.
            e.HasIndex(o => o.Id, "IX_HazardObservations_Unprocessed").HasFilter("\"IsProcessed\" = FALSE");
            e.Property(h => h.HazardType).HasConversion<string>();
            e.HasIndex(h => new { h.IsProcessed, h.CreatedAt });
            e.HasIndex(h => new { h.Latitude, h.Longitude });
            e.HasIndex(h => h.OsmWayId).HasFilter("\"OsmWayId\" IS NOT NULL");
            e.HasIndex(h => h.ApiKeyId);
            e.HasOne(h => h.ApiKey)
             .WithMany()
             .HasForeignKey(h => h.ApiKeyId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HazardAggregate>(e =>
        {
            e.Property(h => h.HazardType).HasConversion<string>();
            e.Property(h => h.Metadata).HasColumnType("jsonb");
            e.HasIndex(h => new { h.ClusterGridX, h.ClusterGridY, h.HeadingBucket, h.HazardType }).IsUnique();
            e.HasIndex(h => h.ExpiresAt);
            e.HasIndex(h => new { h.Latitude, h.Longitude });
            e.HasIndex(h => h.OsmWayId).HasFilter("\"OsmWayId\" IS NOT NULL");
            // Aggregation pass looks up rows whose centroid has drifted since LastMatchedAt.
            e.HasIndex(h => h.LastMatchedAt);
            e.HasStorageParameter("fillfactor", 80);
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
