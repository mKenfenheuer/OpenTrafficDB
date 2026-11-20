namespace OpenTrafficDB.App.Services;

/// <summary>
/// Restricts map matching to OSM road types that fit what is known about a trace, so a trace is
/// not snapped onto a parallel road of a different class (e.g. a motorway trace onto a secondary
/// road next to it).
/// <list type="bullet">
/// <item>Explicit hint: sources that know the road class (e.g. the TomTom importer) send
/// <c>roadClass</c>; the matcher considers that class and its direct neighbours.</item>
/// <item>No hint (e.g. navigation apps): the class is inferred from the median observed speed.
/// High speeds exclude small roads; low speeds exclude nothing, because a congested motorway
/// can be slow.</item>
/// </list>
/// Measured on a TomTom zoom-10 tile around the A1: motorway points on a compatible road class
/// rose from 76 % to 100 % with the hint and to 97 % with the speed rule alone.
/// </summary>
public static class RoadClassFilter
{
    // Hint codes (stored per point). 0 = no hint.
    public const byte None = 0, Motorway = 1, Trunk = 2, Primary = 3, Secondary = 4, Tertiary = 5, Local = 6;

    private static readonly string[] MotorwayTypes = ["motorway", "motorway_link"];
    private static readonly string[] TrunkTypes = ["trunk", "trunk_link"];
    private static readonly string[] PrimaryTypes = ["primary", "primary_link"];
    private static readonly string[] SecondaryTypes = ["secondary", "secondary_link"];
    private static readonly string[] TertiaryTypes = ["tertiary", "tertiary_link"];
    private static readonly string[] LocalTypes = ["unclassified", "residential"];

    /// <summary>All road types considered for matching when nothing restricts them.</summary>
    public static readonly string[] All =
        [.. MotorwayTypes, .. TrunkTypes, .. PrimaryTypes, .. SecondaryTypes, .. TertiaryTypes, .. LocalTypes];

    public static byte Parse(string? roadClass) => roadClass?.Trim().ToLowerInvariant() switch
    {
        "motorway" => Motorway,
        "trunk" => Trunk,
        "primary" => Primary,
        "secondary" => Secondary,
        "tertiary" => Tertiary,
        "local" => Local,
        _ => None,
    };

    /// <summary>Allowed OSM road types for an explicit hint: the class and its direct neighbours.</summary>
    public static string[] ForHint(byte hint) => hint switch
    {
        Motorway => [.. MotorwayTypes, .. TrunkTypes],
        Trunk => [.. MotorwayTypes, .. TrunkTypes, .. PrimaryTypes],
        Primary => [.. TrunkTypes, .. PrimaryTypes, .. SecondaryTypes],
        Secondary => [.. PrimaryTypes, .. SecondaryTypes, .. TertiaryTypes],
        Tertiary => [.. SecondaryTypes, .. TertiaryTypes, .. LocalTypes],
        Local => [.. TertiaryTypes, .. LocalTypes],
        _ => All,
    };

    /// <summary>Allowed OSM road types for a median speed (km/h) when no hint is known.</summary>
    public static string[] ForSpeed(double medianKmh) => medianKmh switch
    {
        >= 90 => [.. MotorwayTypes, .. TrunkTypes, .. PrimaryTypes],
        >= 70 => [.. MotorwayTypes, .. TrunkTypes, .. PrimaryTypes, .. SecondaryTypes],
        >= 55 => [.. MotorwayTypes, .. TrunkTypes, .. PrimaryTypes, .. SecondaryTypes, .. TertiaryTypes],
        _ => All,
    };
}
