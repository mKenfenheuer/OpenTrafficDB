using System.Text;

namespace OpenTrafficDBServices;

// ── Public surface ────────────────────────────────────────────────────────────

/// <summary>A typed value stored in a Mapbox Vector Tile feature tag.</summary>
public abstract record MvtValue
{
    public sealed record StringValue(string Value)  : MvtValue;
    public sealed record DoubleValue(double Value)  : MvtValue;
    public sealed record Int64Value(long Value)     : MvtValue;
    public sealed record BoolValue(bool Value)      : MvtValue;
}

/// <summary>
/// A LineString feature in a vector tile layer.
/// Geometry is a list of (x, y) points in tile pixel coordinates [0, Extent).
/// Tags are key→MvtValue pairs that become feature attributes.
/// </summary>
public sealed class MvtFeature(
    ulong Id,
    IReadOnlyList<(int X, int Y)> Geometry,
    IReadOnlyDictionary<string, MvtValue> Tags)
{
    public ulong Id       { get; } = Id;
    public IReadOnlyList<(int X, int Y)>             Geometry { get; } = Geometry;
    public IReadOnlyDictionary<string, MvtValue> Tags     { get; } = Tags;
}

/// <summary>A named layer containing LineString features.</summary>
public sealed class MvtLayer(string Name, IReadOnlyList<MvtFeature> Features)
{
    public string                    Name     { get; } = Name;
    public IReadOnlyList<MvtFeature> Features { get; } = Features;
}

// ── Encoder ──────────────────────────────────────────────────────────────────

/// <summary>
/// Minimal Mapbox Vector Tile 2.1 encoder.
/// Produces raw (uncompressed) protobuf bytes conforming to the MVT spec.
/// Callers are responsible for gzip-compressing the result.
/// </summary>
public static class MvtEncoder
{
    /// <summary>Encode a set of layers into a Tile protobuf message.</summary>
    public static byte[] Encode(IReadOnlyList<MvtLayer> layers)
    {
        var tile = new ProtoWriter();
        foreach (var layer in layers)
            tile.WriteEmbedded(3, EncodeLayer(layer));   // Tile.layers = field 3
        return tile.ToArray();
    }

    // ── Layer encoding ────────────────────────────────────────────────────────

    private static byte[] EncodeLayer(MvtLayer layer)
    {
        // ── Build shared key / value tables (MVT deduplicates them per layer) ──
        var keyIndex   = new Dictionary<string, int>();
        var valueIndex = new Dictionary<MvtValue, int>();
        var keys       = new List<string>();
        var values     = new List<MvtValue>();

        foreach (var f in layer.Features)
        {
            foreach (var (k, v) in f.Tags)
            {
                if (!keyIndex.ContainsKey(k))
                {
                    keyIndex[k] = keys.Count;
                    keys.Add(k);
                }
                if (!valueIndex.ContainsKey(v))
                {
                    valueIndex[v] = values.Count;
                    values.Add(v);
                }
            }
        }

        var w = new ProtoWriter();
        w.WriteVarintField(15, 2);              // Layer.version = 2
        w.WriteStringField(1, layer.Name);      // Layer.name
        w.WriteVarintField(5, TileMath.Extent); // Layer.extent

        foreach (var k in keys)
            w.WriteStringField(3, k);           // Layer.keys[]

        foreach (var v in values)
            w.WriteEmbedded(4, EncodeValue(v)); // Layer.values[]

        foreach (var f in layer.Features)
            w.WriteEmbedded(2, EncodeFeature(f, keyIndex, valueIndex)); // Layer.features[]

        return w.ToArray();
    }

    // ── Feature encoding ─────────────────────────────────────────────────────

    private static byte[] EncodeFeature(
        MvtFeature feature,
        Dictionary<string, int> keyIndex,
        Dictionary<MvtValue, int> valueIndex)
    {
        var w = new ProtoWriter();

        // Feature.id
        w.WriteVarintField(1, feature.Id);

        // Feature.tags (packed repeated uint32: key_index, value_index, key_index, value_index, ...)
        var tagInts = new List<uint>();
        foreach (var (k, v) in feature.Tags)
        {
            if (!keyIndex.TryGetValue(k, out var ki) || !valueIndex.TryGetValue(v, out var vi)) continue;
            tagInts.Add((uint)ki);
            tagInts.Add((uint)vi);
        }
        if (tagInts.Count > 0)
            w.WritePackedUint32(2, tagInts);

        // Feature.type = LINESTRING (2)
        w.WriteVarintField(3, 2);

        // Feature.geometry
        var geometry = EncodeLineString(feature.Geometry);
        if (geometry.Count > 0)
            w.WritePackedUint32(4, geometry);

        return w.ToArray();
    }

    // ── Geometry encoding ─────────────────────────────────────────────────────

    /// <summary>
    /// Encode a LineString as MVT geometry commands.
    /// Format: [MoveTo(1), dx0, dy0, LineTo(N-1), dx1, dy1, dx2, dy2, ...]
    /// All deltas are zigzag-encoded int32.
    /// </summary>
    private static List<uint> EncodeLineString(IReadOnlyList<(int X, int Y)> points)
    {
        if (points.Count < 2) return [];

        var cmds = new List<uint>(points.Count * 2 + 2);

        // MoveTo(1)
        cmds.Add(CommandInt(1, 1));
        cmds.Add(ZigZag(points[0].X));
        cmds.Add(ZigZag(points[0].Y));

        // LineTo(N-1)
        cmds.Add(CommandInt(2, points.Count - 1));
        int curX = points[0].X, curY = points[0].Y;
        for (int i = 1; i < points.Count; i++)
        {
            int dx = points[i].X - curX;
            int dy = points[i].Y - curY;
            cmds.Add(ZigZag(dx));
            cmds.Add(ZigZag(dy));
            curX = points[i].X;
            curY = points[i].Y;
        }

        return cmds;
    }

    private static uint CommandInt(int id, int count) => (uint)((count << 3) | id);
    private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

    // ── Value encoding ────────────────────────────────────────────────────────

    private static byte[] EncodeValue(MvtValue value)
    {
        var w = new ProtoWriter();
        switch (value)
        {
            case MvtValue.StringValue sv: w.WriteStringField(1, sv.Value); break;
            // field 3 = double_value (wire type 1)
            case MvtValue.DoubleValue dv: w.WriteDoubleField(3, dv.Value); break;
            // field 4 = int_value (wire type 0, int64 as varint)
            case MvtValue.Int64Value iv:  w.WriteInt64Field(4, iv.Value);  break;
            // field 7 = bool_value (wire type 0)
            case MvtValue.BoolValue bv:   w.WriteBoolField(7, bv.Value);   break;
        }
        return w.ToArray();
    }
}

// ── Minimal protobuf writer ───────────────────────────────────────────────────

internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    // ── Primitive writes ──────────────────────────────────────────────────────

    public void WriteVarint(ulong value)
    {
        while (value > 0x7F)
        {
            _ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }

    private void WriteTag(int field, int wireType) =>
        WriteVarint((ulong)((field << 3) | wireType));

    // wire type 0 — varint
    public void WriteVarintField(int field, ulong value)
    {
        WriteTag(field, 0);
        WriteVarint(value);
    }

    // wire type 0 — int64 (negative values encoded as 64-bit varint)
    public void WriteInt64Field(int field, long value)
    {
        WriteTag(field, 0);
        WriteVarint((ulong)value);
    }

    // wire type 0 — bool
    public void WriteBoolField(int field, bool value)
    {
        WriteTag(field, 0);
        WriteVarint(value ? 1UL : 0UL);
    }

    // wire type 1 — 64-bit double
    public void WriteDoubleField(int field, double value)
    {
        WriteTag(field, 1);
        _ms.Write(BitConverter.GetBytes(value));
    }

    // wire type 2 — string (UTF-8)
    public void WriteStringField(int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(field, 2);
        WriteVarint((ulong)bytes.Length);
        _ms.Write(bytes);
    }

    // wire type 2 — embedded message
    public void WriteEmbedded(int field, byte[] payload)
    {
        WriteTag(field, 2);
        WriteVarint((ulong)payload.Length);
        _ms.Write(payload);
    }

    // wire type 2 — packed repeated uint32
    public void WritePackedUint32(int field, IReadOnlyList<uint> values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values)
            inner.WriteVarint(v);
        var payload = inner.ToArray();
        WriteTag(field, 2);
        WriteVarint((ulong)payload.Length);
        _ms.Write(payload);
    }

    public byte[] ToArray() => _ms.ToArray();
}
