using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats;

/// <summary>The registers of an <see cref="MqDecoder"/>, which a hot loop copies into a local and decodes with directly.</summary>
internal struct MqRegisters
{
    /// <summary>The interval register A.</summary>
    internal uint A;

    /// <summary>The code register C (Chigh and Clow).</summary>
    internal uint C;

    /// <summary>The bit counter CT.</summary>
    internal int Ct;

    /// <summary>The offset of the byte BP points to.</summary>
    internal int Position;
}

/// <summary>
/// The MQ adaptive binary arithmetic decoder that JBIG2 (ITU-T T.88 Annex E) and JPEG 2000 (ITU-T T.800 Annex C) share, written
/// from T.88's description of INITDEC, DECODE, RENORMD and BYTEIN. A context's state is one byte, its Qe index times two plus its
/// MPS sense, so a model keeps its contexts in a plain byte array and resets them by clearing it.
/// </summary>
/// <remarks>
/// Past the end of the data the decoder sees 0xFF bytes that read as a marker, so it feeds 1-bits as T.88 E.3.4 says for a
/// terminating marker; <see cref="Overrun"/> counts how many bytes it would have read beyond the end, for damage checks. Hot
/// loops take the registers into a local with <see cref="Registers"/>, decode with <see cref="Decode(ref MqRegisters, ref byte)"/>
/// and put them back.
/// </remarks>
internal sealed class MqDecoder
{
    /// <summary>
    /// The probability estimation table (T.88 Table E.1) by context state: Qe in the low 16 bits, the state after an MPS in bits
    /// 16 to 23 and the state after an LPS in bits 24 to 31, the LPS state with the MPS sense already switched where SWITCH says.
    /// </summary>
    private static readonly uint[] States = BuildStates();

    private byte[] _data = [];
    private int _end;

    /// <summary>The registers, which hot loops copy out and back.</summary>
    internal MqRegisters Registers;

    /// <summary>Gets or sets how many bytes the decoder asked for beyond the end of its data; a loop that inlines BYTEIN adds to it.</summary>
    internal int Overrun { get; set; }

    /// <summary>Gets the data being decoded, for loops that inline BYTEIN.</summary>
    internal byte[] Data => _data;

    /// <summary>Gets the offset just past the data being decoded.</summary>
    internal int End => _end;

    /// <summary>Gets the probability estimation table, by context state, for loops that inline DECODE.</summary>
    internal static uint[] StateTable => States;

    /// <summary>Gets the offset of the next byte the decoder would read.</summary>
    internal int Position => Registers.Position;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="data"/> from <paramref name="start"/> (INITDEC).</summary>
    internal void Start(byte[] data, int start, int length)
    {
        _data = data;
        _end = start + length;
        Overrun = 0;
        Registers.Position = start;
        Registers.C = (uint)Byte(start) << 16;
        ByteIn(ref Registers);
        Registers.C <<= 7;
        Registers.Ct -= 7;
        Registers.A = 0x8000;
    }

    /// <summary>Decodes one decision in the context whose state is <paramref name="state"/>, updating the state (DECODE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref byte state) => Decode(ref Registers, ref state);

    /// <summary>Decodes one decision with registers a hot loop holds in a local; see <see cref="Decode(ref byte)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref MqRegisters r, ref byte state)
    {
        uint s = state;
        uint entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(States), (nint)s);
        uint qe = entry & 0xFFFF;
        r.A -= qe;
        int d;
        if ((r.C >> 16) < qe)
        {
            // LPS path, with the conditional exchange (LPS_EXCHANGE).
            if (r.A < qe)
            {
                d = (int)(s & 1);
                state = (byte)(entry >> 16);
            }
            else
            {
                d = (int)(s & 1) ^ 1;
                state = (byte)(entry >> 24);
            }

            r.A = qe;
        }
        else
        {
            r.C -= qe << 16;
            if ((r.A & 0x8000) != 0)
            {
                return (int)(s & 1);
            }

            // MPS path needing renormalization, with the conditional exchange (MPS_EXCHANGE).
            if (r.A < qe)
            {
                d = (int)(s & 1) ^ 1;
                state = (byte)(entry >> 24);
            }
            else
            {
                d = (int)(s & 1);
                state = (byte)(entry >> 16);
            }
        }

        // RENORMD: double A and C until A is at least 0x8000, reading bytes as C empties; all at once when C holds enough bits.
        int shift = System.Numerics.BitOperations.LeadingZeroCount(r.A) - 16;
        if (shift <= r.Ct)
        {
            r.A <<= shift;
            r.C <<= shift;
            r.Ct -= shift;
            return d;
        }

        do
        {
            if (r.Ct == 0)
            {
                ByteIn(ref r);
            }

            r.A <<= 1;
            r.C <<= 1;
            r.Ct--;
        }
        while ((r.A & 0x8000) == 0);

        return d;
    }

    /// <summary>Reads the next byte into C, removing the stuffed bit after 0xFF and stopping at a marker (BYTEIN).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ByteIn(ref MqRegisters r)
    {
        int position = r.Position;
        if (Byte(position) == 0xFF)
        {
            if (Byte(position + 1) > 0x8F)
            {
                r.C += 0xFF00;
                r.Ct = 8;
                if (position >= _end)
                {
                    Overrun++;
                }
            }
            else
            {
                r.Position = position + 1;
                r.C += (uint)Byte(position + 1) << 9;
                r.Ct = 7;
            }
        }
        else
        {
            r.Position = position + 1;
            r.C += (uint)Byte(position + 1) << 8;
            r.Ct = 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Byte(int position)
    {
        return (uint)position < (uint)_end ? _data[position] : (byte)0xFF;
    }

    private static uint[] BuildStates()
    {
        // T.88 Table E.1: Qe, NMPS, NLPS and SWITCH by index.
        ReadOnlySpan<ushort> qe =
        [
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
            0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
            0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
            0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
        ];
        ReadOnlySpan<byte> nmps =
        [
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
        ];
        ReadOnlySpan<byte> nlps =
        [
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
        ];

        // 256 entries, so any byte a context holds indexes the table: states past 93 never occur, but a bounds check costs more.
        uint[] states = new uint[256];
        for (int i = 0; i < 47; i++)
        {
            bool switches = i is 0 or 6 or 14;
            for (int mps = 0; mps < 2; mps++)
            {
                uint afterMps = (uint)((nmps[i] << 1) | mps);
                uint afterLps = (uint)((nlps[i] << 1) | (switches ? mps ^ 1 : mps));
                states[(i << 1) | mps] = qe[i] | (afterMps << 16) | (afterLps << 24);
            }
        }

        return states;
    }
}
