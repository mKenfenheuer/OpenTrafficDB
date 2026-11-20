namespace OpenTrafficDBModels;

/// <summary>
/// Weekly speed profile of one road: 2,016 five-minute slots (Monday 00:00 first), each stored as
/// two bytes — average speed in km/h and a sample count capped at <c>Pipeline:ProfileCountCap</c>.
/// One compact row per road (compressed by Postgres) replaces the former per-slot rows of
/// <c>WaySpeedAggregates</c>, so writes touch one small row per road instead of random rows of a
/// 200M-row table. The Valhalla predicted-speed encoding is derived from it.
/// </summary>
public class WayProfile
{
    public const int Slots = 2016;

    public long OsmWayId { get; set; }
    /// <summary>Slots × [avg km/h, count] = 4,032 bytes.</summary>
    public byte[] Profile { get; set; } = new byte[Slots * 2];
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastUpdated { get; set; }
}
