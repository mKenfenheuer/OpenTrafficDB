using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public string? Warning { get; init; }

    public static ValidationResult Ok(string? warning = null) =>
        new() { IsValid = true, Warning = warning };

    public static ValidationResult Fail(params string[] errors) =>
        new() { IsValid = false, Errors = [.. errors] };
}

public class SpeedValidationService(IConfiguration config)
{
    private readonly double _maxSpeedMs = double.TryParse(config["Security:MaxSpeedMs"], out var s) ? s : 111.11;
    private readonly double _maxAccuracy = double.TryParse(config["Security:MaxGpsAccuracyMeters"], out var a) ? a : 100.0;
    private readonly double _warnAccuracy = double.TryParse(config["Security:WarnGpsAccuracyMeters"], out var w) ? w : 50.0;
    private readonly int _maxDriftSeconds = int.TryParse(config["Security:MaxTimestampDriftSeconds"], out var d) ? d : 300;

    public ValidationResult Validate(SpeedObservationRequest req)
    {
        var errors = new List<string>();

        // Speed range
        if (req.SpeedMs < 0)
            errors.Add("Speed cannot be negative.");
        else if (req.SpeedMs > _maxSpeedMs)
            errors.Add($"Speed {req.SpeedMs:F1} m/s ({req.SpeedMs * 3.6:F0} km/h) exceeds maximum of {_maxSpeedMs * 3.6:F0} km/h.");

        // Coordinate sanity — (0,0) is the Null Island GPS default
        if (req.Latitude == 0.0 && req.Longitude == 0.0)
            errors.Add("Coordinates (0, 0) rejected — likely a GPS initialization error.");

        // GPS accuracy
        if (req.GpsAccuracyMeters <= 0)
            errors.Add("GPS accuracy must be a positive value.");
        else if (req.GpsAccuracyMeters > _maxAccuracy)
            errors.Add($"GPS accuracy {req.GpsAccuracyMeters:F0} m exceeds threshold of {_maxAccuracy:F0} m. Improve GPS signal before contributing.");

        // Timestamp drift
        var drift = Math.Abs((DateTime.UtcNow - req.Timestamp.ToUniversalTime()).TotalSeconds);
        if (drift > _maxDriftSeconds)
            errors.Add($"Timestamp is {drift:F0}s from server time. Maximum drift is {_maxDriftSeconds}s. Ensure device clock is synced. {req.Timestamp} and clock {DateTime.UtcNow}");

        // TripId must not be empty
        if (req.TripId == string.Empty)
            errors.Add("TripId must be a non-empty string.");

        if (errors.Count > 0)
            return ValidationResult.Fail([.. errors]);

        // Soft warning — marginal accuracy
        string? warning = null;
        if (req.GpsAccuracyMeters > _warnAccuracy)
            warning = $"GPS accuracy {req.GpsAccuracyMeters:F0} m is marginal (>{_warnAccuracy:F0} m). Data quality may be reduced.";

        return ValidationResult.Ok(warning);
    }
}
