namespace OpenTrafficDB.Models;

/// <summary>
/// Pre-aggregated speed data per H3 cell (resolution 9, ~174 m) per 5-minute time bucket.
/// Used for spatial analytics and grid-level traffic visualisation.
/// Key: (H3CellIndex, DayOfWeek, FiveMinuteBucket)
/// </summary>
public class H3SpeedAggregate
{
    public long Id { get; set; }

    /// <summary>H3 cell index at resolution 9, stored as the raw ulong bits cast to long</summary>
    public long H3CellIndex { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday</summary>
    public int DayOfWeek { get; set; }

    /// <summary>5-minute slot within the day (0–287)</summary>
    public int FiveMinuteBucket { get; set; }

    /// <summary>Sum of speeds in km/h for incremental averaging</summary>
    public double SumSpeedKmh { get; set; }

    public int SampleCount { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
