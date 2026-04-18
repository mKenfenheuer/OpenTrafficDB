namespace OpenTrafficDBServices;

/// <summary>
/// Web Mercator (XYZ) tile math utilities.
/// All tile coordinates follow the standard XYZ/Slippy-map convention:
/// (0,0) = top-left (NW), Y increases downward (southward).
/// </summary>
public static class TileMath
{
    /// <summary>Number of integer coordinate units per tile side in a vector tile.</summary>
    public const int Extent = 4096;

    /// <summary>
    /// Returns the WGS84 bounding box for a tile.
    /// </summary>
    public static (double MinLat, double MinLon, double MaxLat, double MaxLon) TileBounds(int z, int x, int y)
    {
        double n = Math.Pow(2, z);
        double minLon = x / n * 360.0 - 180.0;
        double maxLon = (x + 1) / n * 360.0 - 180.0;
        double maxLat = TileYToLat(y,     z);
        double minLat = TileYToLat(y + 1, z);
        return (minLat, minLon, maxLat, maxLon);
    }

    /// <summary>
    /// Convert WGS84 lat/lon to integer tile pixel coordinates within [0, Extent).
    /// (0, 0) = top-left of tile. Values outside that range are geometrically outside the tile.
    /// </summary>
    public static (int X, int Y) LatLonToPixel(double lat, double lon, int z, int tileX, int tileY)
    {
        double n   = Math.Pow(2, z);
        double wx  = (lon + 180.0) / 360.0 * n;
        double sinL = Math.Sin(lat * Math.PI / 180.0);
        double wy  = (1.0 - Math.Log((1.0 + sinL) / (1.0 - sinL)) / (2.0 * Math.PI)) / 2.0 * n;

        int px = (int)Math.Round((wx - tileX) * Extent);
        int py = (int)Math.Round((wy - tileY) * Extent);
        return (px, py);
    }

    private static double TileYToLat(int y, int z)
    {
        double n = Math.PI - 2.0 * Math.PI * y / Math.Pow(2, z);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }
}
