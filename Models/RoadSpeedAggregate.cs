namespace OpenTrafficDB.Models;

/// <summary>
/// Pre-aggregated speed data per Valhalla edge per 5-minute time bucket.
/// Used to generate predicted traffic CSV for valhalla_add_predicted_traffic.
/// BucketIndex = dayOfWeek * 288 + minuteOfDay / 5  (range 0–2015)
/// </summary>
public class RoadSpeedAggregate
{
    public long Id { get; set; }

    /// <summary>Valhalla GraphId (64-bit edge identifier)</summary>
    public long ValhallaEdgeId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (matches C# DayOfWeek)</summary>
    public int DayOfWeek { get; set; }

    /// <summary>5-minute slot within the day (0–287)</summary>
    public int FiveMinuteBucket { get; set; }

    /// <summary>Running average speed in km/h (Valhalla uses km/h)</summary>
    public double AvgSpeedKmh { get; set; }

    public int SampleCount { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
