namespace OpenTrafficDBServices;

/// <summary>
/// Ports Valhalla's compress_speed_buckets, decompress_speed_bucket,
/// encode_compressed_speeds, and decode_compressed_speeds to C#.
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
    private const int BucketsPerWeek = 2016;
    private const int CoefficientCount = 200;
    private const int DecodedSpeedSize = CoefficientCount * 2;

    private const float k1OverSqrt2 = 0.707106781f;
    private const float kPiBucketConstant = 3.14159265f / BucketsPerWeek;
    private const float kSpeedNormalization = 0.031497039f; // sqrt(2 / 2016)

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
    /// Compress 2016 speed values into 200 DCT-II int16 coefficients.
    /// </summary>
    public static short[] CompressSpeedBuckets(ReadOnlySpan<float> speeds)
    {
        Span<float> coefficients = stackalloc float[CoefficientCount];
        coefficients.Clear();

        // Vectorised over the coefficients (same summation order per coefficient as the scalar
        // loop, so the result is identical): ~8x faster. The profile writer encodes ~800k roads
        // per flush, and the scalar loop kept several cores busy for minutes.
        var acc = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(coefficients);
        int vecEnd = acc.Length * System.Numerics.Vector<float>.Count;
        var table = CosTable.AsSpan();
        for (var bucket = 0; bucket < BucketsPerWeek; bucket++)
        {
            var row = table.Slice(bucket * CoefficientCount, CoefficientCount);
            var spd = speeds[bucket];
            var rowVec = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(row);
            var spdVec = new System.Numerics.Vector<float>(spd);
            for (var v = 0; v < acc.Length; v++)
                acc[v] += rowVec[v] * spdVec;
            for (var c = vecEnd; c < CoefficientCount; c++)
                coefficients[c] += row[c] * spd;
        }

        coefficients[0] *= k1OverSqrt2;

        var result = new short[CoefficientCount];
        for (var i = 0; i < CoefficientCount; i++)
            result[i] = (short)MathF.Round(kSpeedNormalization * coefficients[i], MidpointRounding.AwayFromZero);

        return result;
    }

    /// <summary>
    /// Decompress a single speed bucket from DCT-III coefficients.
    /// </summary>
    public static float DecompressSpeedBucket(ReadOnlySpan<short> coefficients, int bucketIdx)
    {
        var offset = bucketIdx * CoefficientCount;

        // DCT-III with speed normalization: first coeff scaled by 1/sqrt(2)
        var speed = coefficients[0] * k1OverSqrt2;
        for (var c = 1; c < CoefficientCount; c++)
            speed += coefficients[c] * CosTable[offset + c];

        return speed * kSpeedNormalization;
    }

    /// <summary>
    /// Encode int16 coefficients as big-endian bytes → Base64.
    /// </summary>
    public static string EncodeCompressedSpeeds(ReadOnlySpan<short> coefficients)
    {
        Span<byte> bytes = stackalloc byte[CoefficientCount * 2];
        for (var i = 0; i < CoefficientCount; i++)
        {
            var val = coefficients[i];
            bytes[i * 2]     = (byte)((val >> 8) & 0xFF);
            bytes[i * 2 + 1] = (byte)(val & 0xFF);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decode a Base64 string back into int16 coefficients (big-endian → little-endian).
    /// </summary>
    public static short[] DecodeCompressedSpeeds(string encoded)
    {
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length != DecodedSpeedSize)
            throw new ArgumentException(
                $"Decoded speed size expected={DecodedSpeedSize} actual={raw.Length}");

        var coefficients = new short[CoefficientCount];
        for (var i = 0; i < CoefficientCount; i++)
        {
            var idx = i * 2;
            coefficients[i] = (short)((raw[idx] << 8) | (raw[idx + 1] & 0xFF));
        }
        return coefficients;
    }

    /// <summary>
    /// Convenience: compress + encode in one call.
    /// </summary>
    public static string Encode(ReadOnlySpan<float> speeds)
        => EncodeCompressedSpeeds(CompressSpeedBuckets(speeds));
}
