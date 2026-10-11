using System.Runtime.CompilerServices;

namespace Egami.Formats;

/// <summary>
/// The MQ adaptive binary arithmetic decoder that JBIG2 (ITU-T T.88 Annex E) and JPEG 2000 (ITU-T T.800 Annex C) share, written
/// from T.88's description of INITDEC, DECODE, RENORMD and BYTEIN. A context's state is one byte, its Qe index times two plus its
/// MPS sense, so a model keeps its contexts in a plain byte array and resets them by clearing it.
/// </summary>
/// <remarks>
/// Past the end of the data the decoder sees 0xFF bytes that read as a marker, so it feeds 1-bits as T.88 E.3.4 says for a
/// terminating marker; <see cref="Overrun"/> counts how many bytes it would have read beyond the end, for damage checks.
/// </remarks>
internal sealed class MqDecoder
{
    /// <summary>
    /// The probability estimation table (T.88 Table E.1) by context state: Qe in the low 16 bits, the state after an MPS in bits
    /// 16 to 23 and the state after an LPS in bits 24 to 31, the LPS state with the MPS sense already switched where SWITCH says.
    /// </summary>
    private static readonly uint[] States = BuildStates();

    private byte[] _data = [];
    private int _position;
    private int _end;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>Gets how many bytes the decoder asked for beyond the end of its data.</summary>
    internal int Overrun { get; private set; }

    /// <summary>Gets the offset of the next byte the decoder would read.</summary>
    internal int Position => _position;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="data"/> from <paramref name="start"/> (INITDEC).</summary>
    internal void Start(byte[] data, int start, int length)
    {
        _data = data;
        _position = start;
        _end = start + length;
        Overrun = 0;
        _c = (uint)Byte(_position) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Decodes one decision in the context whose state is <paramref name="state"/>, updating the state (DECODE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref byte state)
    {
        uint s = state;
        uint entry = States[s];
        uint qe = entry & 0xFFFF;
        _a -= qe;
        if ((_c >> 16) < qe)
        {
            // LPS path, with the conditional exchange (LPS_EXCHANGE).
            int d;
            if (_a < qe)
            {
                d = (int)(s & 1);
                state = (byte)(entry >> 16);
            }
            else
            {
                d = (int)(s & 1) ^ 1;
                state = (byte)(entry >> 24);
            }

            _a = qe;
            Renormalize();
            return d;
        }

        _c -= qe << 16;
        if ((_a & 0x8000) != 0)
        {
            return (int)(s & 1);
        }

        // MPS path needing renormalization, with the conditional exchange (MPS_EXCHANGE).
        int decision;
        if (_a < qe)
        {
            decision = (int)(s & 1) ^ 1;
            state = (byte)(entry >> 24);
        }
        else
        {
            decision = (int)(s & 1);
            state = (byte)(entry >> 16);
        }

        Renormalize();
        return decision;
    }

    /// <summary>Doubles A and C until A is at least 0x8000, reading bytes as C empties (RENORMD).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
            {
                ByteIn();
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }

    /// <summary>Reads the next byte into C, removing the stuffed bit after 0xFF and stopping at a marker (BYTEIN).</summary>
    private void ByteIn()
    {
        if (Byte(_position) == 0xFF)
        {
            if (Byte(_position + 1) > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
                if (_position >= _end)
                {
                    Overrun++;
                }
            }
            else
            {
                _position++;
                _c += (uint)Byte(_position) << 9;
                _ct = 7;
            }
        }
        else
        {
            _position++;
            _c += (uint)Byte(_position) << 8;
            _ct = 8;
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
        uint[] states = new uint[94];
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
