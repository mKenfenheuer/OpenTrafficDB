// Root myDeserializedClass = JsonSerializer.Deserialize<Root>(myJsonResponse);
using System.Text.Json.Serialization;

public class Alternate
{
    [JsonPropertyName("trip")]
    public Trip Trip { get; set; }
}

public class ExitBranchElement
{
    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("consecutive_count")]
    public int ConsecutiveCount { get; set; }
}

public class ExitNameElement
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}

public class ExitNumberElement
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}

public class ExitTowardElement
{
    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("consecutive_count")]
    public int? ConsecutiveCount { get; set; }
}

public class Jam
{
    [JsonPropertyName("encodedPolyline")]
    public string EncodedPolyline { get; set; }

    [JsonPropertyName("delayMinutes")]
    public double DelayMinutes { get; set; }

    [JsonPropertyName("liveSpeedKmh")]
    public double LiveSpeedKmh { get; set; }

    [JsonPropertyName("freeflowSpeedKmh")]
    public double FreeflowSpeedKmh { get; set; }

    [JsonPropertyName("delayPercent")]
    public double DelayPercent { get; set; }

    [JsonPropertyName("lengthKm")]
    public double LengthKm { get; set; }
}

public class Leg
{
    [JsonPropertyName("maneuvers")]
    public List<Maneuver> Maneuvers { get; set; }

    [JsonPropertyName("summary")]
    public Summary Summary { get; set; }

    [JsonPropertyName("shape")]
    public string Shape { get; set; }
}

public class Location
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("side_of_street")]
    public string SideOfStreet { get; set; }

    [JsonPropertyName("original_index")]
    public int OriginalIndex { get; set; }

    [JsonPropertyName("date_time")]
    public string DateTime { get; set; }

    [JsonPropertyName("time_zone_offset")]
    public string TimeZoneOffset { get; set; }

    [JsonPropertyName("time_zone_name")]
    public string TimeZoneName { get; set; }
}

public class Maneuver
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("instruction")]
    public string Instruction { get; set; }

    [JsonPropertyName("verbal_succinct_transition_instruction")]
    public string VerbalSuccinctTransitionInstruction { get; set; }

    [JsonPropertyName("verbal_pre_transition_instruction")]
    public string VerbalPreTransitionInstruction { get; set; }

    [JsonPropertyName("verbal_post_transition_instruction")]
    public string VerbalPostTransitionInstruction { get; set; }

    [JsonPropertyName("street_names")]
    public List<string> StreetNames { get; set; }

    [JsonPropertyName("bearing_after")]
    public int BearingAfter { get; set; }

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("length")]
    public double Length { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }

    [JsonPropertyName("begin_shape_index")]
    public int BeginShapeIndex { get; set; }

    [JsonPropertyName("end_shape_index")]
    public int EndShapeIndex { get; set; }

    [JsonPropertyName("verbal_multi_cue")]
    public bool VerbalMultiCue { get; set; }

    [JsonPropertyName("travel_mode")]
    public string TravelMode { get; set; }

    [JsonPropertyName("travel_type")]
    public string TravelType { get; set; }

    [JsonPropertyName("verbal_transition_alert_instruction")]
    public string VerbalTransitionAlertInstruction { get; set; }

    [JsonPropertyName("bearing_before")]
    public int? BearingBefore { get; set; }

    [JsonPropertyName("begin_street_names")]
    public List<string> BeginStreetNames { get; set; }

    [JsonPropertyName("sign")]
    public Sign Sign { get; set; }

    [JsonPropertyName("roundabout_exit_count")]
    public int? RoundaboutExitCount { get; set; }

    [JsonPropertyName("highway")]
    public bool? Highway { get; set; }
}

public class ValhallaResponse
{
    [JsonPropertyName("trip")]
    public Trip Trip { get; set; }

    [JsonPropertyName("alternates")]
    public List<Alternate> Alternates { get; set; }
}

public class Sign
{
    [JsonPropertyName("exit_branch_elements")]
    public List<ExitBranchElement> ExitBranchElements { get; set; }

    [JsonPropertyName("exit_toward_elements")]
    public List<ExitTowardElement> ExitTowardElements { get; set; }

    [JsonPropertyName("exit_number_elements")]
    public List<ExitNumberElement> ExitNumberElements { get; set; }

    [JsonPropertyName("exit_name_elements")]
    public List<ExitNameElement> ExitNameElements { get; set; }
}

public class Summary
{
    [JsonPropertyName("has_time_restrictions")]
    public bool HasTimeRestrictions { get; set; }

    [JsonPropertyName("has_toll")]
    public bool HasToll { get; set; }

    [JsonPropertyName("has_highway")]
    public bool HasHighway { get; set; }

    [JsonPropertyName("has_ferry")]
    public bool HasFerry { get; set; }

    [JsonPropertyName("min_lat")]
    public double MinLat { get; set; }

    [JsonPropertyName("min_lon")]
    public double MinLon { get; set; }

    [JsonPropertyName("max_lat")]
    public double MaxLat { get; set; }

    [JsonPropertyName("max_lon")]
    public double MaxLon { get; set; }

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("length")]
    public double Length { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }
}

public class TrafficAnalysis
{
    [JsonPropertyName("routeIndex")]
    public int RouteIndex { get; set; }

    [JsonPropertyName("freeflowDurationMinutes")]
    public double FreeflowDurationMinutes { get; set; }

    [JsonPropertyName("liveDurationMinutes")]
    public double LiveDurationMinutes { get; set; }

    [JsonPropertyName("totalDelayMinutes")]
    public double TotalDelayMinutes { get; set; }

    [JsonPropertyName("delayPercent")]
    public double DelayPercent { get; set; }

    [JsonPropertyName("trafficCoverageRatio")]
    public double TrafficCoverageRatio { get; set; }

    [JsonPropertyName("distanceKm")]
    public double DistanceKm { get; set; }

    [JsonPropertyName("distancePerMinuteKm")]
    public double DistancePerMinuteKm { get; set; }

    [JsonPropertyName("averageSpeedKmh")]
    public double AverageSpeedKmh { get; set; }

    [JsonPropertyName("co2GramsEstimate")]
    public int Co2GramsEstimate { get; set; }

    [JsonPropertyName("ecoScore")]
    public int EcoScore { get; set; }

    [JsonPropertyName("jams")]
    public List<Jam> Jams { get; set; }

    [JsonPropertyName("hazards")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VHazard>? Hazards { get; set; }
}

public class VHazard
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("osmWayId")]
    public long OsmWayId { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metadata { get; set; }
}

public class Trip
{
    [JsonPropertyName("locations")]
    public List<Location> Locations { get; set; }

    [JsonPropertyName("legs")]
    public List<Leg> Legs { get; set; }

    [JsonPropertyName("summary")]
    public Summary Summary { get; set; }

    [JsonPropertyName("status_message")]
    public string StatusMessage { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; }

    [JsonPropertyName("trafficAnalysis")]
    public TrafficAnalysis TrafficAnalysis { get; set; }

    /// <summary>
    /// Two trips are considered equal (duplicates) when they share the same
    /// "core corridor" — i.e. the fraction of each route's distance that
    /// overlaps the other is >= 90%. This is length-independent: an 8 km
    /// divergence on a 500 km route is treated the same as on a 50 km route,
    /// because it's measured as a fraction of total distance, not sample count.
    /// </summary>
    public override bool Equals(object obj)
    {
        if (obj is Trip other)
            return ShapeSimilarity(other) >= 0.90;
        return false;
    }

    public override int GetHashCode() => 0; // Equals is shape-based, not stable for hashing

    /// <summary>
    /// Computes what fraction of each route's distance overlaps the other
    /// (within <paramref name="thresholdKm"/>), returning the minimum of
    /// both directions. 1.0 = identical paths, 0.0 = completely different.
    /// Unlike point-count similarity, this is proportional to actual km
    /// so a short divergence scores the same regardless of total route length.
    /// </summary>
    public double ShapeSimilarity(Trip other, double thresholdKm = 0.15)
    {
        const int MaxSamples = 300;

        var coordsA = DecodeAllLegs(this);
        var coordsB = DecodeAllLegs(other);
        if (coordsA.Count < 2 || coordsB.Count < 2) return 0;

        var sampledA = SampleByDistance(coordsA, MaxSamples);
        var sampledB = SampleByDistance(coordsB, MaxSamples);

        double abOverlap = OverlapFractionByDistance(sampledA, sampledB, thresholdKm);
        double baOverlap = OverlapFractionByDistance(sampledB, sampledA, thresholdKm);

        return Math.Min(abOverlap, baOverlap);
    }

    private static List<(double Lat, double Lon)> DecodeAllLegs(Trip trip)
    {
        var all = new List<(double Lat, double Lon)>();
        if (trip.Legs == null) return all;
        foreach (var leg in trip.Legs)
        {
            if (string.IsNullOrEmpty(leg.Shape)) continue;
            var decoded = DecodePolyline6(leg.Shape);
            if (all.Count > 0 && decoded.Count > 0)
                decoded.RemoveAt(0);
            all.AddRange(decoded);
        }
        return all;
    }

    private static List<(double Lat, double Lon)> DecodePolyline6(string encoded)
    {
        var coords = new List<(double, double)>();
        int index = 0, lat = 0, lng = 0;
        while (index < encoded.Length)
        {
            int shift = 0, result = 0, b;
            do { b = encoded[index++] - 63; result |= (b & 0x1f) << shift; shift += 5; } while (b >= 0x20);
            lat += (result & 1) != 0 ? ~(result >> 1) : result >> 1;
            shift = 0; result = 0;
            do { b = encoded[index++] - 63; result |= (b & 0x1f) << shift; shift += 5; } while (b >= 0x20);
            lng += (result & 1) != 0 ? ~(result >> 1) : result >> 1;
            coords.Add((lat / 1e6, lng / 1e6));
        }
        return coords;
    }

    /// <summary>
    /// Sample points at roughly even distance intervals along the route.
    /// Each sample carries the segment length (km) it represents.
    /// </summary>
    private static List<(double Lat, double Lon, double SegmentKm)> SampleByDistance(
        List<(double Lat, double Lon)> coords, int maxSamples)
    {
        // Compute cumulative distance
        var cumDist = new double[coords.Count];
        for (int i = 1; i < coords.Count; i++)
            cumDist[i] = cumDist[i - 1] + EquirectDist(coords[i - 1], coords[i]);

        double totalKm = cumDist[^1];
        if (totalKm <= 0) return [(coords[0].Lat, coords[0].Lon, 0)];

        int n = Math.Min(maxSamples, coords.Count);
        double step = totalKm / n;
        var result = new List<(double, double, double)>(n);

        int ci = 0;
        for (int i = 0; i < n; i++)
        {
            double target = i * step + step / 2; // midpoint of each segment
            while (ci < coords.Count - 1 && cumDist[ci + 1] < target) ci++;
            result.Add((coords[ci].Lat, coords[ci].Lon, step));
        }
        return result;
    }

    /// <summary>
    /// What fraction of route A's total distance is within threshold of route B?
    /// Each sample point is weighted by the distance it represents.
    /// </summary>
    private static double OverlapFractionByDistance(
        List<(double Lat, double Lon, double SegmentKm)> from,
        List<(double Lat, double Lon, double SegmentKm)> to,
        double thresholdKm)
    {
        double matchedKm = 0, totalKm = 0;
        double threshDeg = thresholdKm / 111.32;
        double threshSq = threshDeg * threshDeg;

        foreach (var (lat1, lon1, segKm) in from)
        {
            totalKm += segKm;
            double cosLat = Math.Cos(lat1 * Math.PI / 180.0);
            foreach (var (lat2, lon2, _) in to)
            {
                double dlat = lat1 - lat2;
                double dlon = (lon1 - lon2) * cosLat;
                if (dlat * dlat + dlon * dlon <= threshSq)
                {
                    matchedKm += segKm;
                    break;
                }
            }
        }

        return totalKm > 0 ? matchedKm / totalKm : 0;
    }

    private static double EquirectDist((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        const double DegToKm = 111.32;
        double dlat = b.Lat - a.Lat;
        double dlon = (b.Lon - a.Lon) * Math.Cos(a.Lat * Math.PI / 180.0);
        return Math.Sqrt(dlat * dlat + dlon * dlon) * DegToKm;
    }
}

