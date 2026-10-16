using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Ccitt;

/// <summary>
/// Reads raw CCITT fax data with the parameters <see cref="CcittReaderOptions"/> gives, as <see cref="Gray8"/>, black 0 and
/// white 255. With <see cref="CcittFaxParameters.Rows"/> given, Probe reads no data and rows stream one at a time; with Rows 0
/// the rows are counted first.
/// </summary>
internal sealed class CcittReader : IFormatReader, IRowSourceReader
{
    internal static readonly CcittReader Instance = new();

    /// <summary>Each byte of packed bits as the eight gray bytes it expands to, little-endian: a 1 bit (black) to 0, a 0 bit to 255.</summary>
    private static readonly ulong[] GrayBytes = BuildGrayBytes();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        CcittFaxParameters p = Parameters(options);
        int height = p.Rows;
        if (height == 0)
        {
            (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
            try
            {
                height = CountRows(input, data.AsSpan(0, length), p);
            }
            finally
            {
                Return(data, pooled);
            }
        }

        input.Limits.CheckFrameSize(p.Columns, height);
        return new RasterInfo
        {
            Format = CcittFormat.Info,
            Width = p.Columns,
            Height = height,
            Layout = Gray8.Layout,
            FrameCount = 1,
            HasMetadata = false,
            EstimatedBytes = (long)p.Columns * height,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytesOf(p.Columns),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        CcittFaxParameters p = Parameters(options);
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        byte[] packed;
        int rows;
        List<(string Id, string Message)> warnings;
        try
        {
            packed = Decode(input, data.AsSpan(0, length), p, out rows, out warnings);
        }
        finally
        {
            Return(data, pooled);
        }

        Strict(input, options, warnings);
        int rowBytes = (p.Columns + 7) / 8;
        Raster<TPixel> raster = input.CreateRaster<TPixel>(p.Columns, rows);
        try
        {
            byte[] gray = ArrayPool<byte>.Shared.Rent(p.Columns);
            try
            {
                for (int y = 0; y < rows; y++)
                {
                    ReadOnlySpan<byte> bits = packed.AsSpan(y * rowBytes, rowBytes);
                    if (typeof(TPixel) == typeof(Gray8))
                    {
                        Expand(bits, MemoryMarshal.AsBytes(raster.Row(y)));
                        continue;
                    }

                    Span<byte> row = gray.AsSpan(0, p.Columns);
                    Expand(bits, row);
                    PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(row), raster.Row(y));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(gray);
            }

            foreach ((string id, string message) in warnings)
            {
                input.Warn(raster, id, message);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<Gray8>(input, options);

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        CcittFaxParameters p = Parameters(options);
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            int height = p.Rows > 0 ? p.Rows : CountRows(input, data.AsSpan(0, length), p);
            input.Limits.CheckFrameSize(p.Columns, height);
            return new CcittRows(input, data, length, pooled, p, height, options.Recovery == RecoveryMode.Strict);
        }
        catch
        {
            Return(data, pooled);
            input.Dispose();
            throw;
        }
    }

    /// <summary>Expands a row of packed bits to gray bytes: a 1 bit (black) to 0, a 0 bit to 255.</summary>
    internal static void Expand(ReadOnlySpan<byte> bits, Span<byte> row)
    {
        int width = row.Length;
        int whole = width >> 3;
        ref byte target = ref MemoryMarshal.GetReference(row);
        ref ulong table = ref MemoryMarshal.GetArrayDataReference(GrayBytes);
        for (int i = 0; i < whole; i++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i << 3), Unsafe.Add(ref table, bits[i]));
        }

        for (int x = whole << 3; x < width; x++)
        {
            row[x] = (byte)(((bits[x >> 3] >> (7 - (x & 7))) & 1) != 0 ? 0 : 255);
        }
    }

    /// <summary>Returns what a streaming read holds: one packed row, its reference row and one row of gray bytes.</summary>
    internal static long WorkingBytesOf(int width) => (((width + 7L) / 8) + (2L * (width + 4) * sizeof(int))) + width;

    private static CcittFaxParameters Parameters(ReaderOptions options)
    {
        CcittFaxParameters p = (options as CcittReaderOptions)?.Parameters ?? new CcittFaxParameters();
        p.Check(nameof(options));
        return p;
    }

    private static byte[] Decode(EgamiInput input, ReadOnlySpan<byte> data, CcittFaxParameters p, out int rows, out List<(string Id, string Message)> warnings)
    {
        try
        {
            return CcittFax.DecodeRows(data, p, input.Limits, out rows, out warnings);
        }
        catch (InvalidDataException e)
        {
            throw input.Corrupt(0, e.Message);
        }
    }

    /// <summary>The rows Rows 0 data holds, decoded once without keeping them.</summary>
    private static int CountRows(EgamiInput input, ReadOnlySpan<byte> data, CcittFaxParameters p)
    {
        long cap = CcittFax.MaxRows(p.Columns, input.Limits);
        CcittDecoder decoder = new(p.K, p.EndOfLine, p.EncodedByteAlign, p.Columns, 0, p.DamagedRowsBeforeError);
        byte[] row = ArrayPool<byte>.Shared.Rent((p.Columns + 7) / 8);
        try
        {
            while (decoder.NextRow(data, row))
            {
                if (decoder.RowsDone > cap)
                {
                    input.Limits.CheckFrameSize(p.Columns, decoder.RowsDone);
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, (long)decoder.RowsDone * ((p.Columns + 7) / 8));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }

        if (decoder.RowsDone == 0)
        {
            throw input.Corrupt(0, decoder.Damage ?? "The CCITT data holds no row.");
        }

        return decoder.RowsDone;
    }

    /// <summary>With <see cref="RecoveryMode.Strict"/>, damaged or short data throws instead of warning.</summary>
    private static void Strict(EgamiInput input, ReaderOptions options, List<(string Id, string Message)> warnings)
    {
        if (options.Recovery == RecoveryMode.Strict && warnings.Count > 0)
        {
            throw input.Corrupt(-1, warnings[^1].Message);
        }
    }

    private static void Return(byte[] data, bool pooled)
    {
        if (pooled)
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static ulong[] BuildGrayBytes()
    {
        ulong[] table = new ulong[256];
        for (int bits = 0; bits < 256; bits++)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                ulong gray = ((bits >> (7 - i)) & 1) != 0 ? 0UL : 255UL;
                value |= gray << (8 * i);
            }

            table[bits] = value;
        }

        return table;
    }

    /// <summary>Rows decoded as they are asked for; only the current row and its reference row are held.</summary>
    private sealed class CcittRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly CcittFaxParameters _parameters;
        private readonly CcittDecoder _decoder;
        private readonly int _length;
        private readonly bool _pooled;
        private readonly bool _strict;
        private readonly int _height;
        private byte[]? _data;
        private byte[]? _packed;
        private byte[]? _gray;
        private int _current = -1;
        private bool _started;
        private bool _ended;

        public CcittRows(EgamiInput input, byte[] data, int length, bool pooled, CcittFaxParameters parameters, int height, bool strict)
        {
            _input = input;
            _data = data;
            _length = length;
            _pooled = pooled;
            _parameters = parameters;
            _height = height;
            _strict = strict;
            _decoder = new CcittDecoder(parameters.K, parameters.EndOfLine, parameters.EncodedByteAlign, parameters.Columns, height, parameters.DamagedRowsBeforeError);
            _packed = ArrayPool<byte>.Shared.Rent((parameters.Columns + 7) / 8);
            _gray = ArrayPool<byte>.Shared.Rent(parameters.Columns);
            Metadata.FormatName = CcittFormat.Info.Name;
        }

        public override int StoredWidth => _parameters.Columns;

        public override int StoredHeight => _height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => Gray8.Layout;

        public override RasterMetadata Metadata { get; } = new();

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WorkingBytesOf(_parameters.Columns);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<Gray8>();

        public override FrameMetadata? NextFrame()
        {
            if (!_started)
            {
                _started = true;
                return new FrameMetadata();
            }

            EndFrame();
            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            int rowBytes = (_parameters.Columns + 7) / 8;
            Span<byte> packed = _packed.AsSpan(0, rowBytes);
            while (_current < y)
            {
                _input.CancellationToken.ThrowIfCancellationRequested();
                if (!_decoder.NextRow(_data.AsSpan(0, _length), packed))
                {
                    // Rows past the end of the data, or after damage, are white.
                    packed.Clear();
                }

                _current++;
            }

            Span<byte> gray = _gray.AsSpan(0, _parameters.Columns);
            Expand(packed, gray);
            PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(gray).Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Span<byte> packed = _packed.AsSpan(0, (_parameters.Columns + 7) / 8);
            if (_strict)
            {
                // Strict loading checks every row, as Load does, even when fewer rows were asked for.
                while (_decoder.NextRow(_data.AsSpan(0, _length), packed))
                {
                }
            }

            if (_decoder.SkippedRows > 0)
            {
                Metadata.WarningList.Add("Ccitt.RowsSkipped", $"{_decoder.SkippedRows} damaged rows were skipped to the next EOL, each repeating the row before it.");
            }

            if (_decoder.Damage is not null)
            {
                Warn("Ccitt.Damaged", $"{_decoder.Damage} Decoding stopped after {_decoder.RowsDone} rows; the rest are white.");
            }
            else if (_decoder.Ended && _decoder.RowsDone < _height)
            {
                Warn("Ccitt.Truncated", $"The CCITT data ends after {_decoder.RowsDone} of {_height} rows; the rest are white.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_data is not null)
            {
                Return(_data, _pooled);
                _data = null;
                ArrayPool<byte>.Shared.Return(_packed!);
                ArrayPool<byte>.Shared.Return(_gray!);
                _packed = null;
                _gray = null;
                _input.Dispose();
            }
        }

        private void Warn(string id, string message)
        {
            if (_strict)
            {
                throw _input.Corrupt(-1, message);
            }

            Metadata.WarningList.Add(id, message);
        }
    }
}
