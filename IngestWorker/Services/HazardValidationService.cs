using System.Text.Json;
using OpenTrafficDB.Shared.Models.DTOs;

namespace OpenTrafficDB.IngestWorker.Services;

public class HazardValidationService(IConfiguration config)
{
    private readonly double _maxAccuracy = double.TryParse(config["Security:MaxGpsAccuracyMeters"], out var a) ? a : 100.0;
    private readonly double _warnAccuracy = double.TryParse(config["Security:WarnGpsAccuracyMeters"], out var w) ? w : 50.0;
    private readonly int _maxDriftSeconds = int.TryParse(config["Security:MaxTimestampDriftSeconds"], out var d) ? d : 300;
    private readonly int _maxMetadataLength = int.TryParse(config["Security:MaxHazardMetadataLength"], out var m) ? m : 2048;

    public ValidationResult Validate(HazardObservationRequest req)
    {
        var errors = new List<string>();

        if (req.Latitude == 0.0 && req.Longitude == 0.0)
            errors.Add("Coordinates (0, 0) rejected — likely a GPS initialization error.");

        if (req.GpsAccuracyMeters <= 0)
            errors.Add("GPS accuracy must be a positive value.");
        else if (req.GpsAccuracyMeters > _maxAccuracy)
            errors.Add($"GPS accuracy {req.GpsAccuracyMeters:F0} m exceeds threshold of {_maxAccuracy:F0} m.");

        var drift = Math.Abs((DateTime.UtcNow - req.Timestamp.ToUniversalTime()).TotalSeconds);
        if (drift > _maxDriftSeconds)
            errors.Add($"Timestamp is {drift:F0}s from server time. Maximum drift is {_maxDriftSeconds}s.");

        if (!Enum.IsDefined(req.HazardType))
            errors.Add($"Unknown hazard type: {req.HazardType}.");

        if (req.Metadata is { Length: > 0 } && req.Metadata.Length > _maxMetadataLength)
            errors.Add($"Metadata exceeds maximum length of {_maxMetadataLength} characters.");
        try
        {
            var data = JsonSerializer.Deserialize<dynamic>(req.Metadata ?? "{}");
        }
        catch
        {
            errors.Add($"Metadata must be valid JSON.");
        }

        if (errors.Count > 0)
            return ValidationResult.Fail([.. errors]);

        string? warning = null;
        if (req.GpsAccuracyMeters > _warnAccuracy)
            warning = $"GPS accuracy {req.GpsAccuracyMeters:F0} m is marginal (>{_warnAccuracy:F0} m). Data quality may be reduced.";

        return ValidationResult.Ok(warning);
    }
}
