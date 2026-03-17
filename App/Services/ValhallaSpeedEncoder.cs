namespace OpenTrafficDB.App.Services;

/// <summary>
/// Ports Valhalla's compress_speed_buckets + encode_compressed_speeds to C#.
///
/// Source: valhalla/src/baldr/predictedspeeds.cc
///
/// DCT-II formula:
///   coeff[c] = sum_{b=0}^{2015} speed[b] * cos(π/2016 * (b + 0.5) * c)
///   coeff[0] *= 1/sqrt(2)
///   result[c] = round(sqrt(2/2016) * coeff[c])  → int16
///
/// Encoding: each int16 → big-endian uint16 bytes → Base64.
/// </summary>
public static class ValhallaSpeedEncoder
{
    private const int BucketsPerWeek    = 2016;
    private const int CoefficientCount  = 200;

    private static readonly float k1OverSqrt2       = 0.707106781f;
    private static readonly float kPiBucketConstant = MathF.PI / BucketsPerWeek;
    private static readonly float kSpeedNorm        = 0.031497039f; // sqrt(2 / 2016)

    // Precomputed cos table [bucket * CoefficientCount + coeff]
    private static readonly float[] CosTable = BuildCosTable();

    private static float[] BuildCosTable()
    {
        var table = new float[BucketsPerWeek * CoefficientCount];
        for (var bucket = 0; bucket < BucketsPerWeek; bucket++)
        {
            var offset = bucket * CoefficientCount;
            for (var c = 0; c < CoefficientCount; c++)
                table[offset + c] = MathF.Cos(kPiBucketConstant * (bucket + 0.5f) * c);
        }
        return table;
    }

    /// <summary>
    /// Compress 2016 speed values (km/h, Valhalla epoch: Mon 00:00 = slot 0) into
    /// 200 DCT-II int16 coefficients encoded as a Base64 string.
    /// </summary>
    public static string Encode(ReadOnlySpan<float> speeds)
    {
        if (speeds.Length != BucketsPerWeek)
            throw new ArgumentException($"Expected {BucketsPerWeek} speed values, got {speeds.Length}.");

        Span<float> coefficients = stackalloc float[CoefficientCount];
        coefficients.Clear();

        for (var bucket = 0; bucket < BucketsPerWeek; bucket++)
        {
            var spd    = speeds[bucket];
            var offset = bucket * CoefficientCount;
            for (var c = 0; c < CoefficientCount; c++)
                coefficients[c] += CosTable[offset + c] * spd;
        }

        coefficients[0] *= k1OverSqrt2;

        // Scale to int16 and write as big-endian bytes
        Span<byte> bytes = stackalloc byte[CoefficientCount * 2];
        for (var i = 0; i < CoefficientCount; i++)
        {
            var val    = (short)MathF.Round(kSpeedNorm * coefficients[i]);
            var bigEnd = (ushort)System.Net.IPAddress.HostToNetworkOrder(val);
            bytes[i * 2]     = (byte)(bigEnd >> 8);
            bytes[i * 2 + 1] = (byte)(bigEnd & 0xFF);
        }

        return Convert.ToBase64String(bytes);
    }
}
