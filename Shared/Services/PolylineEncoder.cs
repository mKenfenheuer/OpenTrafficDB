using System.Text;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Google / Valhalla polyline encoding at precision 6.
/// </summary>
public static class PolylineEncoder
{
    /// <summary>Encode a sequence of (lat, lon) pairs to a precision-6 polyline string.</summary>
    public static string Encode(IEnumerable<(double Lat, double Lon)> coords)
    {
        var sb = new StringBuilder();
        int prevLat = 0, prevLon = 0;
        foreach (var (lat, lon) in coords)
        {
            int iLat = (int)Math.Round(lat * 1e6);
            int iLon = (int)Math.Round(lon * 1e6);
            EncodeValue(sb, iLat - prevLat);
            EncodeValue(sb, iLon - prevLon);
            prevLat = iLat;
            prevLon = iLon;
        }
        return sb.ToString();
    }

    /// <summary>Decode a precision-6 polyline string back to (lat, lon) pairs.</summary>
    public static IEnumerable<(double Lat, double Lon)> Decode(string encoded)
    {
        int index = 0, lat = 0, lon = 0;
        while (index < encoded.Length)
        {
            lat += DecodeNext(encoded, ref index);
            lon += DecodeNext(encoded, ref index);
            yield return (lat / 1e6, lon / 1e6);
        }
    }

    private static void EncodeValue(StringBuilder sb, int value)
    {
        int v = value < 0 ? ~(value << 1) : value << 1;
        while (v >= 0x20)
        {
            sb.Append((char)(((v & 0x1f) | 0x20) + 63));
            v >>= 5;
        }
        sb.Append((char)(v + 63));
    }

    private static int DecodeNext(string encoded, ref int index)
    {
        int result = 0, shift = 0, b;
        do
        {
            b = encoded[index++] - 63;
            result |= (b & 0x1f) << shift;
            shift += 5;
        } while (b >= 0x20);
        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }
}
