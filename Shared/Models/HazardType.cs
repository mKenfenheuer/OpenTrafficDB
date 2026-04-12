namespace OpenTrafficDB.Shared.Models;

public enum HazardType
{
    // Traffic cameras
    SpeedCamera,
    DistanceCamera,
    RedLightCamera,
    MultiCamera,
    SectionControl,
    WeightControl,

    // Road hazards
    Accident,
    BrokenDownCar,
    Debris,
    Pedestrian,
    OtherHazard,

    // Police activity
    PoliceGeneral,
    PoliceRoadBlock,
    PoliceControl,
    PoliceAlcohol,

    // Road works
    RoadWorks,

    // Traffic
    TrafficJam,

    // Other
    Other
}

public static class HazardTypeExtensions
{
    /// <summary>
    /// Returns the default expiry duration for a hazard type after its last confirmation.
    /// Camera types persist for 30 days; temporary hazards expire in 1-24 hours.
    /// </summary>
    public static TimeSpan GetDefaultExpiry(this HazardType type) => type switch
    {
        // Cameras are semi-permanent
        HazardType.SpeedCamera or
        HazardType.DistanceCamera or
        HazardType.RedLightCamera or
        HazardType.MultiCamera or
        HazardType.SectionControl or
        HazardType.WeightControl => TimeSpan.FromDays(30),

        // Police activity is short-lived
        HazardType.PoliceGeneral or
        HazardType.PoliceRoadBlock or
        HazardType.PoliceControl or
        HazardType.PoliceAlcohol => TimeSpan.FromHours(1),

        // Road works persist longer
        HazardType.RoadWorks => TimeSpan.FromHours(24),

        // Traffic jams are short-lived
        HazardType.TrafficJam => TimeSpan.FromHours(1),

        // Road hazards
        HazardType.Accident or
        HazardType.BrokenDownCar or
        HazardType.Debris or
        HazardType.Pedestrian or
        HazardType.OtherHazard => TimeSpan.FromHours(2),

        _ => TimeSpan.FromHours(2)
    };

    public static bool IsCamera(this HazardType type) => type is
        HazardType.SpeedCamera or
        HazardType.DistanceCamera or
        HazardType.RedLightCamera or
        HazardType.MultiCamera or
        HazardType.SectionControl or
        HazardType.WeightControl;
}
