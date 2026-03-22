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

    public override bool Equals(object obj)
    {
        if (obj is Trip response)
        {
            var left = new string(response.Legs.SelectMany(m => m.Shape).ToArray());
            var right = new string(this.Legs.SelectMany(m => m.Shape).ToArray());
            return left == right;
        }
        else
        {
            return false;
        }
    }
}

