using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OpenTrafficDBModels;

/// <summary>
/// Storage format of <see cref="WayProfile.Profile"/>: the 4,032-byte in-memory profile
/// (2,016 slots × [avg km/h, count]) packed into a bit stream of its filled slots.
///
/// A road has data in ~230 of 2,016 slots on average, and neighbouring samples repeat their gap and
/// their values, so per filled slot the stream holds:
/// <list type="bullet">
/// <item>the gap to the previous filled slot: the index into the last <see cref="GapHistory"/>
/// gaps (move-to-front, unary), or an escape and the Elias-gamma code of gap + 1;</item>
/// <item>the values: the index into the last <see cref="ValueHistory"/> (avg, count) pairs
/// (move-to-front, unary), or an escape, the count (1 bit "same as previous", else Elias gamma)
/// and the avg as a zig-zag delta to the previous avg (Rice, k = 3).</item>
/// </list>
/// On production data this is ~270 bytes per road on average, smaller than Postgres' pglz
/// compression of the raw profile (~300 bytes), and encodes in about a microsecond. Rows stay
/// below the 2 KB TOAST threshold, so Postgres no longer compresses them (pglz cost ~40 µs per row,
/// ~50 s of CPU per profile flush), and a flush transfers ~0.3 KB instead of 4 KB per road and direction.
///
/// Layout: byte 0 = <see cref="FormatV1"/>, then the bit stream (LSB first), starting with the
/// number of filled slots (12 bits). A value of exactly 4,032 bytes is the legacy raw profile; an
/// encoding of that length gets one padding byte.
/// </summary>
public static class WayProfileCodec
{
    public const byte FormatV1 = 1;
    public const int RawLength = WayProfile.Slots * 2;
    private const int GapHistory = 3;
    private const int ValueHistory = 3;
    private const int AvgRiceK = 3;

    [ThreadStatic] private static byte[]? _scratch;

    /// <summary>Encodes a raw profile (<see cref="RawLength"/> bytes).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> profile)
    {
        if (profile.Length != RawLength) throw new ArgumentException($"Profile must be {RawLength} bytes.", nameof(profile));
        // Worst case per slot: gap 3+21 bits, value 3+1+15+(510>>3)+4 bits → < 128 bits.
        var scratch = _scratch ??= new byte[1 + 2 + WayProfile.Slots * 16 + 16];
        var w = new BitWriter(scratch, 1);
        scratch[0] = FormatV1;

        // Filled slots: a slot read as little-endian ushort is avg | count << 8.
        Span<short> slots = stackalloc short[WayProfile.Slots];
        int filled = 0;
        var words = MemoryMarshal.Cast<byte, ushort>(profile);
        var countMask = Vector128.Create((ushort)0xFF00);
        for (int b = 0; b < WayProfile.Slots; b += 8)   // 2016 = 252 × 8
        {
            var v = Vector128.Create<ushort>(words.Slice(b, 8));
            uint bits = ~Vector128.Equals(v & countMask, Vector128<ushort>.Zero).ExtractMostSignificantBits() & 0xFF;
            while (bits != 0)
            {
                slots[filled++] = (short)(b + BitOperations.TrailingZeroCount(bits));
                bits &= bits - 1;
            }
        }
        w.Write((uint)filled, 12);

        var gaps = new History(); var values = new History();
        int last = -1, prevAvg = 0, prevCount = 0;
        foreach (int i in slots[..filled])
        {
            int count = profile[2 * i + 1];
            int avg = profile[2 * i];

            int gap = i - last - 1; last = i;
            int gi = gaps.MoveToFront(gap);
            if (gi >= 0) w.Unary(gi, GapHistory);
            else { w.Unary(GapHistory, GapHistory); w.Gamma((uint)gap + 1); }

            int value = avg << 8 | count;
            int vi = values.MoveToFront(value);
            if (vi >= 0) w.Unary(vi, ValueHistory);
            else
            {
                w.Unary(ValueHistory, ValueHistory);
                if (count == prevCount) w.Write(0, 1);
                else { w.Write(1, 1); w.Gamma((uint)count); }
                w.Rice(ZigZag(avg - prevAvg), AvgRiceK);
            }
            prevAvg = avg; prevCount = count;
        }
        var length = w.Flush();
        // Exactly RawLength bytes would read as a legacy raw profile: pad with a zero byte (only
        // possible for nearly full profiles of random values).
        if (length == RawLength) scratch[length++] = 0;
        return scratch.AsSpan(0, length).ToArray();
    }

    /// <summary>Decodes a stored profile (either format) into <paramref name="profile"/> (<see cref="RawLength"/> bytes).</summary>
    public static void Decode(ReadOnlySpan<byte> stored, Span<byte> profile)
    {
        if (profile.Length != RawLength) throw new ArgumentException($"Profile must be {RawLength} bytes.", nameof(profile));
        if (stored.Length == RawLength) { stored.CopyTo(profile); return; }   // legacy raw profile
        if (stored.Length < 3 || stored[0] != FormatV1) Invalid("Unknown profile format.");

        profile.Clear();
        var r = new BitReader(stored[1..]);
        int filled = (int)r.Read(12);
        var gaps = new History(); var values = new History();
        int last = -1, prevAvg = 0, prevCount = 0;
        for (int n = 0; n < filled; n++)
        {
            int gi = r.Unary(GapHistory);
            int gap = gi < GapHistory ? gaps[gi] : (int)r.Gamma() - 1;
            if (gap < 0) Invalid("Invalid gap.");
            gaps.Promote(gi, gap);
            int slot = last + gap + 1; last = slot;
            if ((uint)slot >= WayProfile.Slots) Invalid("Slot out of range.");

            int vi = r.Unary(ValueHistory);
            int avg, count;
            if (vi < ValueHistory)
            {
                int value = values[vi];
                if (value < 0) Invalid("Invalid value reference.");
                avg = value >> 8; count = value & 0xFF;
            }
            else
            {
                count = r.Read(1) == 0 ? prevCount : (int)r.Gamma();
                avg = prevAvg + UnZigZag(r.Rice(AvgRiceK));
                if ((uint)avg > 255 || count is <= 0 or > 255) Invalid("Value out of range.");
            }
            values.Promote(vi, avg << 8 | count);
            profile[2 * slot] = (byte)avg;
            profile[2 * slot + 1] = (byte)count;
            prevAvg = avg; prevCount = count;
        }
    }

    /// <summary>The last three distinct values, most recent first (move-to-front); -1 = empty.</summary>
    private struct History()
    {
        private int _a = -1, _b = -1, _c = -1;

        public readonly int this[int i] => i == 0 ? _a : i == 1 ? _b : _c;

        /// <summary>Index of <paramref name="v"/> (then moved to the front), or -1 (then inserted at the front).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MoveToFront(int v)
        {
            int idx = v == _a ? 0 : v == _b ? 1 : v == _c ? 2 : -1;
            Promote(idx, v);
            return idx;
        }

        /// <summary>Moves entry <paramref name="idx"/> (or a new value for idx ≥ 2 or -1) to the front.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Promote(int idx, int v)
        {
            if (idx == 0) return;
            if (idx != 1) _c = _b;
            _b = _a; _a = v;
        }
    }

    [DoesNotReturn]
    private static void Invalid(string message) => throw new FormatException(message);

    private static uint ZigZag(int d) => (uint)((d << 1) ^ (d >> 31));
    private static int UnZigZag(uint z) => (int)(z >> 1) ^ -(int)(z & 1);

    private ref struct BitWriter(Span<byte> buffer, int start)
    {
        private readonly Span<byte> _buf = buffer;
        private int _pos = start;
        private ulong _acc;
        private int _bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value, int count)
        {
            _acc |= (ulong)value << _bits;
            _bits += count;
            if (_bits >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_buf[_pos..], (uint)_acc);
                _pos += 4; _acc >>= 32; _bits -= 32;
            }
        }

        /// <summary>n one-bits followed by a zero, or n one-bits only when n is the maximum (the escape).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Unary(int n, int max) => Write((1u << n) - 1, n == max ? n : n + 1);

        /// <summary>Elias gamma (v ≥ 1): len-1 zeros, a one, then the len-1 low bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Gamma(uint v)
        {
            int low = 31 - BitOperations.LeadingZeroCount(v);
            Write(1u << low, low + 1);
            Write(v & ((1u << low) - 1), low);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Rice(uint v, int k)
        {
            uint q = v >> k;
            while (q >= 24) { Write(0xFFFFFF, 24); q -= 24; }
            Write((1u << (int)q) - 1, (int)q + 1);
            Write(v & ((1u << k) - 1), k);
        }

        public int Flush()
        {
            while (_bits > 0) { _buf[_pos++] = (byte)_acc; _acc >>= 8; _bits -= 8; }
            _bits = 0;
            return _pos;
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buf = buffer;
        private int _pos;
        private ulong _acc;
        private int _bits;

        /// <summary>Ensures at least 57 bits are buffered (zero padding past the end; a valid stream never reads that far).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Fill()
        {
            if (_bits > 56) return;
            if (_pos + 8 <= _buf.Length)
            {
                _acc |= BinaryPrimitives.ReadUInt64LittleEndian(_buf[_pos..]) << _bits;
                int take = (63 - _bits) >> 3;
                _pos += take; _bits += take * 8;
                return;
            }
            while (_bits <= 56)
            {
                if (_pos < _buf.Length) _acc |= (ulong)_buf[_pos] << _bits;
                else if (_pos > _buf.Length + 16) Invalid("Truncated profile.");
                _pos++; _bits += 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(int count)
        {
            Fill();
            var v = (uint)(_acc & ((1UL << count) - 1));
            _acc >>= count; _bits -= count;
            return v;
        }

        /// <summary>Counts one-bits up to <paramref name="max"/> (the escape, without terminating zero).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Unary(int max)
        {
            Fill();
            int n = Math.Min(BitOperations.TrailingZeroCount(~_acc), max);
            int used = n < max ? n + 1 : n;
            _acc >>= used; _bits -= used;
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Gamma()
        {
            Fill();
            int low = BitOperations.TrailingZeroCount(_acc);
            if (low > 31) Invalid("Invalid gamma code.");
            _acc >>= low + 1; _bits -= low + 1;
            return 1u << low | Read(low);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Rice(int k)
        {
            uint q = 0;
            while (true)
            {
                Fill();
                int ones = BitOperations.TrailingZeroCount(~_acc);
                if (ones < 32) { _acc >>= ones + 1; _bits -= ones + 1; q += (uint)ones; break; }
                _acc >>= 32; _bits -= 32; q += 32;
                if (q > 255) Invalid("Invalid Rice code.");
            }
            return q << k | Read(k);
        }
    }
}
