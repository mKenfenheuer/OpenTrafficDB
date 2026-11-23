namespace OpenTrafficDBModels;

/// <summary>
/// Weekly speed profile of one road: 2,016 five-minute slots (Monday 00:00 first), each stored as
/// two bytes — average speed in km/h and a sample count capped at <c>Pipeline:ProfileCountCap</c>.
/// One compact row per road; the Valhalla predicted-speed encoding is derived from it.
/// </summary>
public class WayProfile
{
    public const int Slots = 2016;

    public long OsmWayId { get; set; }
    /// <summary>
    /// Slots × [avg km/h, count] (4,032 bytes) in the <see cref="WayProfileCodec"/> format (~235 bytes);
    /// rows written before that format hold the raw 4,032 bytes. Always read through the codec.
    /// </summary>
    public byte[] Profile { get; set; } = [];
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastUpdated { get; set; }
}
