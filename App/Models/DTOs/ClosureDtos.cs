using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDBModels.DTOs;

/// <summary>Closed road sections, each as a polyline along the road in driving direction.</summary>
public class ClosureBatchRequest
{
    [Required, MaxLength(2000)]
    public List<ClosureLine> Closures { get; set; } = [];
}

public class ClosureLine
{
    /// <summary>Vertices as [lat, lon] pairs, in driving direction (at least two).</summary>
    [Required, MinLength(2), MaxLength(5000)]
    public List<double[]> Points { get; set; } = [];

    /// <summary>Optional road class hint (motorway, trunk, primary, secondary, tertiary, local).</summary>
    public string? RoadClass { get; set; }

    /// <summary>How long the closure stays active without a new report (default 60, max 1440).</summary>
    [Range(5, 1440)]
    public int TtlMinutes { get; set; } = 60;

    [MaxLength(64)]
    public string? Source { get; set; }
}

public class ClosureBatchResponse
{
    public int Lines { get; set; }
    public int MatchedLines { get; set; }
    public int ClosedWays { get; set; }
}
