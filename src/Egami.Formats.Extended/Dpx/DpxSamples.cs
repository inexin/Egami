using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dpx;

/// <summary>How samples are laid out in the bytes of a DPX or Cineon row.</summary>
internal enum DpxStorage
{
    /// <summary>One byte a sample.</summary>
    Bytes,

    /// <summary>One 16-bit word a sample, the value in its top bits (DPX method A, Cineon packing 3).</summary>
    Words16,

    /// <summary>One 16-bit word a sample, the value in its low bits (DPX method B, Cineon packing 4).</summary>
    Words16Low,

    /// <summary>Three 10-bit samples a 32-bit word from the top, the 2 padding bits at the bottom (DPX method A, Cineon packing 5).</summary>
    Filled10A,

    /// <summary>Three 10-bit samples a 32-bit word from bit 29 down, the 2 padding bits at the top (DPX method B, Cineon packing 6).</summary>
    Filled10B,

    /// <summary>Samples packed back to back into 32-bit words from the lowest bit up (DPX packing 0).</summary>
    PackedLsb,

    /// <summary>Samples packed back to back into 32-bit words from the highest bit down (Cineon packing 0).</summary>
    PackedMsb,

    /// <summary>32-bit IEEE floats.</summary>
    Float32,

    /// <summary>64-bit IEEE floats.</summary>
    Float64,
}

/// <summary>What the samples of a pixel are.</summary>
internal enum DpxColor
{
    /// <summary>Channels of the output pixel, in the file's order or through the map.</summary>
    Direct,

    /// <summary>Cb, Y, Cr, Y for each two pixels (descriptor 100).</summary>
    CbYCrY,

    /// <summary>Cb, Y, A, Cr, Y, A for each two pixels (descriptor 101).</summary>
    CbYACrYA,

    /// <summary>Cb, Y, Cr for each pixel (descriptor 102).</summary>
    CbYCr,

    /// <summary>Cb, Y, Cr, A for each pixel (descriptor 103).</summary>
    CbYCrA,
}

/// <summary>
/// Turns the bytes of one row of DPX or Cineon samples into pixels: unpacks the stored code values, then maps them into the
/// output pixel through a table (code values scaled to 8 or 16 bits, or log code values turned into linear light), or converts
/// CbYCr to RGB. Integer samples are scaled as round(v × max / (2^bits − 1)).
/// </summary>
internal sealed class DpxSamples
{
    /// <summary>Kodak's printing-density constants: reference black and white code values (10-bit), density a code value, negative gamma.</summary>
    private const double RefBlack = 95;
    private const double RefWhite = 685;
    private const double Density = 0.002;
    private const double NegativeGamma = 0.6;

    private readonly int _width;
    private readonly int _pixelSamples;
    private readonly int _outChannels;
    private readonly int[] _map;
    private readonly bool _identity;
    private readonly DpxColor _color;
    private readonly bool _big;
    private readonly int _max;
    private readonly ushort[] _samples;
    private readonly float[] _floats;
    private readonly byte[]? _byteLut;
    private readonly ushort[]? _wordLut;
    private readonly float[]? _floatLut;
    private readonly float[]? _alphaLut;
    private readonly double _kr;
    private readonly double _kb;
    private readonly double _yLow;
    private readonly double _yRange;
    private readonly double _cMid;
    private readonly double _cRange;
    private readonly int _filledShift;
    private readonly bool _lowFirst;

    /// <summary>Makes an unpacker for one row of one image element.</summary>
    /// <param name="width">The pixels a row.</param>
    /// <param name="color">What the samples are.</param>
    /// <param name="map">For <see cref="DpxColor.Direct"/>, the file sample of each output channel within a pixel.</param>
    /// <param name="pixelSamples">For <see cref="DpxColor.Direct"/>, the samples a pixel in the file.</param>
    /// <param name="bits">The bits a stored sample.</param>
    /// <param name="storage">How the samples are packed.</param>
    /// <param name="big">Whether the words are big endian.</param>
    /// <param name="output">The output pixel layout.</param>
    /// <param name="linearize">Whether log code values become linear light (float output only).</param>
    /// <param name="rec601">Whether CbYCr uses the BT.601 matrix rather than BT.709.</param>
    public DpxSamples(int width, DpxColor color, int[] map, int pixelSamples, int bits, DpxStorage storage, bool big, PixelLayoutInfo output, bool linearize, bool rec601)
    {
        _width = width;
        _color = color;
        _map = map;
        _big = big;
        Bits = bits;
        Storage = storage;
        Shift = storage == DpxStorage.Words16 ? 16 - bits : 0;
        _filledShift = storage == DpxStorage.Filled10A ? 2 : 0;

        // One-channel 10-bit rows fill each word from the low end, as ImageMagick and ffmpeg write and read them; pixels of
        // several channels fill it from the top.
        _lowFirst = color == DpxColor.Direct && pixelSamples == 1;
        _outChannels = output.ChannelCount;
        _pixelSamples = color switch
        {
            DpxColor.CbYCrY => 2,
            DpxColor.CbYACrYA => 3,
            DpxColor.CbYCr => 3,
            DpxColor.CbYCrA => 4,
            _ => pixelSamples,
        };
        _identity = color == DpxColor.Direct && pixelSamples == _outChannels && map.Select((m, i) => m == i).All(b => b);
        long samples = color is DpxColor.CbYCrY or DpxColor.CbYACrYA
            ? ((width + 1L) / 2) * _pixelSamples * 2
            : (long)width * _pixelSamples;
        RowSamples = checked((int)samples);
        bool isFloat = storage is DpxStorage.Float32 or DpxStorage.Float64;
        _samples = isFloat ? [] : new ushort[RowSamples];
        _floats = isFloat ? new float[RowSamples] : [];
        _max = bits >= 32 ? 0 : (int)((1L << bits) - 1);
        if (isFloat)
        {
            return;
        }

        int entries = _max + 1;
        if (output.ChannelType == PixelChannelType.Float32)
        {
            _floatLut = new float[entries];
            _alphaLut = new float[entries];
            double gain = 1 / (1 - Math.Pow(10, (RefBlack - RefWhite) * Density / NegativeGamma));
            for (int v = 0; v < entries; v++)
            {
                double unit = (double)v / _max;
                _alphaLut[v] = (float)unit;
                _floatLut[v] = linearize ? (float)((Math.Pow(10, ((unit * 1023) - RefWhite) * Density / NegativeGamma) * gain) - (gain - 1)) : (float)unit;
            }
        }
        else if (output.BitsPerChannel > 8)
        {
            _wordLut = new ushort[entries];
            for (int v = 0; v < entries; v++)
            {
                _wordLut[v] = (ushort)((((long)v * 65535) + (_max >> 1)) / _max);
            }
        }
        else
        {
            _byteLut = new byte[entries];
            for (int v = 0; v < entries; v++)
            {
                _byteLut[v] = (byte)((((long)v * 255) + (_max >> 1)) / _max);
            }
        }

        if (color != DpxColor.Direct)
        {
            (_kr, _kb) = rec601 ? (0.299, 0.114) : (0.2126, 0.0722);
            double scale = (1 << bits) / 256.0;
            _yLow = 16 * scale;
            _yRange = 219 * scale;
            _cMid = 128 * scale;
            _cRange = 224 * scale;
        }
    }

    /// <summary>Gets the bits a stored sample.</summary>
    public int Bits { get; }

    /// <summary>Gets how the samples are packed.</summary>
    public DpxStorage Storage { get; }

    /// <summary>Gets how far a <see cref="DpxStorage.Words16"/> word is shifted down.</summary>
    public int Shift { get; }

    /// <summary>Gets the samples in a row.</summary>
    public int RowSamples { get; }

    /// <summary>Gets the samples one run-length unit holds: a pixel, or for 4:2:2 a chroma sample and its luma (and alpha).</summary>
    public int UnitSamples => _pixelSamples;

    /// <summary>Gets the samples of the row, as stored, for run-length decoding to fill.</summary>
    public Span<ushort> Raw => _samples;

    /// <summary>Gets whether the samples are floats, which run-length decoding does not take.</summary>
    public bool IsFloat => _floats.Length > 0;

    /// <summary>Returns the bytes of <paramref name="samples"/> samples, without the padding to a 32-bit word.</summary>
    public long ByteLength(long samples)
    {
        return Storage switch
        {
            DpxStorage.Bytes => samples,
            DpxStorage.Words16 or DpxStorage.Words16Low => samples * 2,
            DpxStorage.Filled10A or DpxStorage.Filled10B => (samples + 2) / 3 * 4,
            DpxStorage.Float32 => samples * 4,
            DpxStorage.Float64 => samples * 8,
            _ => ((samples * Bits) + 31) / 32 * 4,
        };
    }

    /// <summary>Unpacks a row from <paramref name="bytes"/>; samples past its end are 0.</summary>
    public void Unpack(ReadOnlySpan<byte> bytes)
    {
        int count = RowSamples;
        switch (Storage)
        {
            case DpxStorage.Bytes:
                {
                    int n = Math.Min(count, bytes.Length);
                    for (int i = 0; i < n; i++)
                    {
                        _samples[i] = bytes[i];
                    }

                    _samples.AsSpan(n).Clear();
                    return;
                }

            case DpxStorage.Words16:
            case DpxStorage.Words16Low:
                {
                    int n = Math.Min(count, bytes.Length / 2);
                    Span<ushort> s = _samples.AsSpan(0, n);
                    MemoryMarshal.Cast<byte, ushort>(bytes[..(n * 2)]).CopyTo(s);
                    if (_big == BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.ReverseEndianness(s, s);
                    }

                    if (Shift > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            s[i] >>= Shift;
                        }
                    }
                    else if (Bits < 16)
                    {
                        ushort mask = (ushort)_max;
                        for (int i = 0; i < n; i++)
                        {
                            s[i] &= mask;
                        }
                    }

                    _samples.AsSpan(n).Clear();
                    return;
                }

            case DpxStorage.Filled10A:
            case DpxStorage.Filled10B:
                {
                    int shift = _filledShift;
                    int first = _lowFirst ? 0 : 20;
                    int last = 20 - first;
                    int words = Math.Min((count + 2) / 3, bytes.Length / 4);
                    ref ushort dst = ref MemoryMarshal.GetArrayDataReference(_samples);
                    int full = Math.Min(words, count / 3);
                    for (int w = 0; w < full; w++)
                    {
                        uint word = _big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(w * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(w * 4)..]);
                        word >>= shift;
                        Unsafe.Add(ref dst, w * 3) = (ushort)((word >> first) & 0x3FF);
                        Unsafe.Add(ref dst, (w * 3) + 1) = (ushort)((word >> 10) & 0x3FF);
                        Unsafe.Add(ref dst, (w * 3) + 2) = (ushort)((word >> last) & 0x3FF);
                    }

                    int done = full * 3;
                    if (full < words)
                    {
                        uint word = (_big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(full * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(full * 4)..])) >> shift;
                        for (int k = 0; done < count && k < 3; k++)
                        {
                            _samples[done++] = (ushort)((word >> (_lowFirst ? k * 10 : 20 - (k * 10))) & 0x3FF);
                        }
                    }

                    _samples.AsSpan(done).Clear();
                    return;
                }

            case DpxStorage.Float32:
            case DpxStorage.Float64:
                {
                    int size = Storage == DpxStorage.Float32 ? 4 : 8;
                    int n = Math.Min(count, bytes.Length / size);
                    for (int i = 0; i < n; i++)
                    {
                        ReadOnlySpan<byte> at = bytes[(i * size)..];
                        _floats[i] = size == 4
                            ? (_big ? BinaryPrimitives.ReadSingleBigEndian(at) : BinaryPrimitives.ReadSingleLittleEndian(at))
                            : (float)(_big ? BinaryPrimitives.ReadDoubleBigEndian(at) : BinaryPrimitives.ReadDoubleLittleEndian(at));
                    }

                    _floats.AsSpan(n).Clear();
                    return;
                }

            default:
                {
                    SampleCursor cursor = new(this, _big);
                    int i = 0;
                    for (; i < count && cursor.Next(bytes, out uint v); i++)
                    {
                        _samples[i] = (ushort)v;
                    }

                    _samples.AsSpan(i).Clear();
                    return;
                }
        }
    }

    /// <summary>Writes the unpacked row into <paramref name="row"/>, of the output layout given at construction.</summary>
    public void Emit<TPixel>(Span<TPixel> row)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (_floats.Length > 0)
        {
            EmitFloats(MemoryMarshal.Cast<TPixel, float>(row));
        }
        else if (_color != DpxColor.Direct)
        {
            EmitYCbCr(row);
        }
        else if (_floatLut is not null)
        {
            Span<float> dst = MemoryMarshal.Cast<TPixel, float>(row);
            float[] lut = _floatLut;
            float[] alpha = _alphaLut!;
            int alphaChannel = _outChannels == 4 ? 3 : -1;
            for (int x = 0, at = 0; x < _width; x++, at += _pixelSamples)
            {
                for (int k = 0; k < _outChannels; k++)
                {
                    dst[(x * _outChannels) + k] = (k == alphaChannel ? alpha : lut)[_samples[at + _map[k]]];
                }
            }
        }
        else if (_wordLut is not null)
        {
            EmitDirect(_samples, MemoryMarshal.Cast<TPixel, ushort>(row), _wordLut);
        }
        else
        {
            EmitDirect(_samples, MemoryMarshal.Cast<TPixel, byte>(row), _byteLut!);
        }
    }

    private void EmitDirect<TOut>(ushort[] samples, Span<TOut> dst, TOut[] lut)
    {
        ref TOut table = ref MemoryMarshal.GetArrayDataReference(lut);
        ref ushort src = ref MemoryMarshal.GetArrayDataReference(samples);
        if (_identity)
        {
            int n = _width * _outChannels;
            for (int i = 0; i < n; i++)
            {
                dst[i] = Unsafe.Add(ref table, Unsafe.Add(ref src, i));
            }

            return;
        }

        for (int x = 0, at = 0; x < _width; x++, at += _pixelSamples)
        {
            for (int k = 0; k < _outChannels; k++)
            {
                dst[(x * _outChannels) + k] = Unsafe.Add(ref table, Unsafe.Add(ref src, at + _map[k]));
            }
        }
    }

    private void EmitFloats(Span<float> dst)
    {
        float[] samples = _floats;
        for (int x = 0, at = 0; x < _width; x++, at += _pixelSamples)
        {
            for (int k = 0; k < _outChannels; k++)
            {
                dst[(x * _outChannels) + k] = samples[at + _map[k]];
            }
        }
    }

    private void EmitYCbCr<TPixel>(Span<TPixel> row)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool pairs = _color is DpxColor.CbYCrY or DpxColor.CbYACrYA;
        bool alpha = _color is DpxColor.CbYACrYA or DpxColor.CbYCrA;
        double kg = 1 - _kr - _kb;
        for (int x = 0; x < _width; x++)
        {
            int cb, y, cr, a = _max;
            if (pairs)
            {
                int group = (x / 2) * _pixelSamples * 2;
                int half = _pixelSamples;
                cb = _samples[group];
                cr = _samples[group + half];
                y = _samples[group + (x % 2 == 0 ? 1 : half + 1)];
                if (alpha)
                {
                    a = _samples[group + (x % 2 == 0 ? 2 : half + 2)];
                }
            }
            else
            {
                int at = x * _pixelSamples;
                cb = _samples[at];
                y = _samples[at + 1];
                cr = _samples[at + 2];
                if (alpha)
                {
                    a = _samples[at + 3];
                }
            }

            double luma = (y - _yLow) / _yRange;
            double pb = (cb - _cMid) / _cRange;
            double pr = (cr - _cMid) / _cRange;
            double r = luma + (2 * (1 - _kr) * pr);
            double b = luma + (2 * (1 - _kb) * pb);
            double g = (luma - (_kr * r) - (_kb * b)) / kg;
            Put(row, x, 0, r);
            Put(row, x, 1, g);
            Put(row, x, 2, b);
            if (_outChannels == 4)
            {
                Put(row, x, 3, (double)a / _max);
            }
        }
    }

    private void Put<TPixel>(Span<TPixel> row, int x, int channel, double value)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int at = (x * _outChannels) + channel;
        value = Math.Clamp(value, 0, 1);
        if (_floatLut is not null)
        {
            MemoryMarshal.Cast<TPixel, float>(row)[at] = (float)value;
        }
        else if (_wordLut is not null)
        {
            MemoryMarshal.Cast<TPixel, ushort>(row)[at] = (ushort)Math.Round(value * 65535);
        }
        else
        {
            MemoryMarshal.Cast<TPixel, byte>(row)[at] = (byte)Math.Round(value * 255);
        }
    }

    /// <summary>Reads integer samples one at a time, in any storage, from whole 32-bit words (or bytes and 16-bit words).</summary>
    internal struct SampleCursor
    {
        private readonly DpxStorage _storage;
        private readonly int _bits;
        private readonly int _shift;
        private readonly int _filledShift;
        private readonly bool _lowFirst;
        private readonly bool _big;
        private readonly uint _mask;
        private ulong _acc;
        private int _have;
        private int _at;

        public SampleCursor(DpxSamples samples, bool big)
        {
            _storage = samples.Storage;
            _bits = samples.Bits;
            _shift = samples.Shift;
            _filledShift = samples._filledShift;
            _lowFirst = samples._lowFirst;
            _big = big;
            _mask = (uint)((1L << _bits) - 1);
        }

        /// <summary>Gets the bytes taken so far.</summary>
        public readonly int Position => _at;

        public bool Next(ReadOnlySpan<byte> bytes, out uint value)
        {
            switch (_storage)
            {
                case DpxStorage.Bytes:
                    if (_at >= bytes.Length)
                    {
                        value = 0;
                        return false;
                    }

                    value = bytes[_at++];
                    return true;
                case DpxStorage.Words16:
                case DpxStorage.Words16Low:
                    if (_at + 2 > bytes.Length)
                    {
                        value = 0;
                        return false;
                    }

                    value = (uint)(_big ? BinaryPrimitives.ReadUInt16BigEndian(bytes[_at..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[_at..]));
                    value = (value >> _shift) & _mask;
                    _at += 2;
                    return true;
                case DpxStorage.Filled10A:
                case DpxStorage.Filled10B:
                    if (_have == 0)
                    {
                        if (!Word(bytes))
                        {
                            value = 0;
                            return false;
                        }

                        _have = 3;
                    }

                    _have--;
                    value = (uint)(_acc >> (_filledShift + ((_lowFirst ? 2 - _have : _have) * 10))) & 0x3FF;
                    return true;
                case DpxStorage.PackedMsb:
                    if (_have < _bits)
                    {
                        ulong before = _acc;
                        if (!Word(bytes))
                        {
                            value = 0;
                            return false;
                        }

                        _acc = (before << 32) | _acc;
                        _have += 32;
                    }

                    _have -= _bits;
                    value = (uint)(_acc >> _have) & _mask;
                    return true;
                default:
                    if (_have < _bits)
                    {
                        ulong before = _acc;
                        if (!Word(bytes))
                        {
                            value = 0;
                            return false;
                        }

                        _acc = before | (_acc << _have);
                        _have += 32;
                    }

                    value = (uint)_acc & _mask;
                    _acc >>= _bits;
                    _have -= _bits;
                    return true;
            }
        }

        /// <summary>Puts the next 32-bit word in the accumulator.</summary>
        private bool Word(ReadOnlySpan<byte> bytes)
        {
            if (_at + 4 > bytes.Length)
            {
                return false;
            }

            _acc = _big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[_at..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[_at..]);
            _at += 4;
            return true;
        }
    }
}
