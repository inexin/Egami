using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dpx;

/// <summary>Where one DPX image element or Cineon image lies and how its samples become pixels.</summary>
internal sealed class FilmSource
{
    public required FormatInfo Format { get; init; }

    /// <summary>Gets the first part of the warning ids, "Dpx" or "Cineon".</summary>
    public required string Source { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Gets where the samples start in the file.</summary>
    public required long Offset { get; init; }

    public required bool BigEndian { get; init; }

    public required int Bits { get; init; }

    public required DpxReader.Plan Plan { get; init; }

    /// <summary>Gets the bytes from the start of one row to the next, padding included.</summary>
    public required long Stride { get; init; }

    /// <summary>Gets the padding bytes at the end of each row, which the last row may leave out.</summary>
    public required uint LinePadding { get; init; }

    public required FormatDetails Details { get; init; }

    /// <summary>Gets whether the pixels are linear light.</summary>
    public required bool Linear { get; init; }

    /// <summary>Gets the orientation code, for the warning when it transposes.</summary>
    public required int Orientation { get; init; }
}

/// <summary>Opens the rows of a <see cref="FilmSource"/> in its natural pixel type.</summary>
internal static class FilmRows
{
    public static RowReader Open(EgamiInput input, ReaderOptions options, FilmSource source)
    {
        PixelLayoutInfo layout = source.Plan.Layout;
        return layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray when layout.ChannelType == PixelChannelType.Float32 => new FilmRows<GrayF32>(input, options, source),
            PixelChannelOrder.Gray when layout.BitsPerChannel > 8 => new FilmRows<Gray16>(input, options, source),
            PixelChannelOrder.Gray => new FilmRows<Gray8>(input, options, source),
            PixelChannelOrder.Rgb when layout.ChannelType == PixelChannelType.Float32 => new FilmRows<RgbF32>(input, options, source),
            PixelChannelOrder.Rgb when layout.BitsPerChannel > 8 => new FilmRows<Rgb16>(input, options, source),
            PixelChannelOrder.Rgb => new FilmRows<Rgb8>(input, options, source),
            _ when layout.ChannelType == PixelChannelType.Float32 => new FilmRows<RgbaF32>(input, options, source),
            _ when layout.BitsPerChannel > 8 => new FilmRows<Rgba16>(input, options, source),
            _ => new FilmRows<Rgba8>(input, options, source),
        };
    }
}

/// <summary>
/// The rows of one DPX image element or Cineon image: a plain one is read row by row as it is asked for; a run-length coded or
/// bottom-up one is read whole first.
/// </summary>
internal sealed class FilmRows<TNatural> : ExtendedRows<TNatural, TNatural>
    where TNatural : unmanaged, IPixelType<TNatural>
{
    private readonly FilmSource _source;
    private readonly DpxReader.Plan _plan;
    private readonly DpxSamples _samples;
    private readonly int _stride;
    private readonly int _padding;
    private readonly byte[] _row;
    private byte[]? _data;
    private int _length;
    private ushort[]? _decoded;
    private int _decodedRows;
    private bool _missingNoted;

    public FilmRows(EgamiInput input, ReaderOptions options, FilmSource source)
        : base(input, options, source.Format, source.Source, source.Width, source.Height)
    {
        _source = source;
        _plan = source.Plan;
        _samples = new DpxSamples(source.Width, _plan.Color, _plan.Map, _plan.PixelSamples, source.Bits, _plan.Storage, source.BigEndian, TNatural.Layout, _plan.Linearize, _plan.Rec601);
        if (source.Stride > Array.MaxLength)
        {
            throw input.Corrupt(source.Offset, $"A row of {source.Stride} bytes is too long.");
        }

        _stride = (int)source.Stride;
        _padding = (int)Math.Min(source.LinePadding, (uint)_stride);
        // The row buffer holds a row's samples alone; end-of-line padding, which a header may claim to be any size, is read
        // past in pieces.
        _row = _plan.Buffered ? [] : new byte[_stride - _padding];
        Metadata.FormatDetails = source.Details;
        if (source.Linear)
        {
            Metadata.ColorSpace = ColorSpaceHint.Linear;
        }

        if (source.Orientation > 3)
        {
            Warn("OrientationIgnored", $"Orientation {source.Orientation} transposes the image, which Egami does not do; it was read as orientation {source.Orientation & 3}.");
        }
    }

    public override RasterAccess Access => _plan.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed;

    public override long WorkingBytes => base.WorkingBytes + _row.Length + (_data?.Length ?? 0) + ((_decoded?.Length ?? 0) * 2L);

    protected override bool DecodeRow(int y, Span<TNatural> row)
    {
        int fileRow = _plan.BottomUp ? Height - 1 - y : y;
        bool complete;
        if (_plan.RunLength)
        {
            DecodeRunLength();
            complete = fileRow < _decodedRows;
            Span<ushort> raw = _samples.Raw;
            if (complete)
            {
                _decoded.AsSpan(fileRow * raw.Length, raw.Length).CopyTo(raw);
            }
            else
            {
                raw.Clear();
            }
        }
        else if (_plan.Buffered)
        {
            ReadWhole(_stride * (long)Height);
            long at = (long)fileRow * _stride;
            int available = (int)Math.Clamp(_length - at, 0, _stride);
            _samples.Unpack(_data.AsSpan((int)Math.Min(at, _length), available));
            complete = available == _stride;
        }
        else
        {
            int got = 0;
            while (got < _row.Length)
            {
                int read = Input.Read(_row.AsSpan(got));
                if (read == 0)
                {
                    break;
                }

                got += read;
            }

            _samples.Unpack(_row.AsSpan(0, got));
            complete = got == _row.Length;
            if (complete && y < Height - 1)
            {
                SkipPadding();
            }
        }

        _samples.Emit(row);
        if (_plan.FlipX)
        {
            row.Reverse();
        }

        if (!complete && _plan.BottomUp)
        {
            // Rows are missing from the end of the file, which is the top of the image; the rows below still decode.
            if (Strict)
            {
                throw Input.Corrupt(_source.Offset + _length, "The data ends unexpectedly.");
            }

            if (!_missingNoted)
            {
                _missingNoted = true;
                Warn("RowsMissing", "The file ends before the data of some rows; they are left black.");
            }

            return true;
        }

        return complete;
    }

    /// <summary>Reads past a row's end-of-line padding, or to the end of the input when it is shorter.</summary>
    private void SkipPadding()
    {
        long left = _padding;
        while (left > 0)
        {
            int read = Input.Read(_row.AsSpan(0, (int)Math.Min(left, _row.Length)));
            if (read == 0)
            {
                return;
            }

            left -= read;
        }
    }

    private void ReadWhole(long size)
    {
        if (_data is null)
        {
            _data = InputData.ReadToEnd(Input, size, out _length);
        }
    }

    /// <summary>Decodes the whole run-length coded element into stored samples, row after row.</summary>
    private void DecodeRunLength()
    {
        if (_decoded is not null)
        {
            return;
        }

        int rowSamples = _samples.RowSamples;
        long total = (long)rowSamples * Height;
        Input.CheckMemory(total * 2);
        ReadWhole(Math.Min((_samples.ByteLength(total) * 2) + 65536, Array.MaxLength));
        _decoded = new ushort[total];
        ReadOnlySpan<byte> data = _data.AsSpan(0, _length);
        DpxSamples.SampleCursor cursor = new(_samples, _source.BigEndian);
        int unit = _samples.UnitSamples;
        Span<ushort> pixel = stackalloc ushort[unit];
        long at = 0;
        while (at < total)
        {
            if (!cursor.Next(data, out uint flag))
            {
                break;
            }

            long count = flag >> 1;
            if (count == 0)
            {
                Damaged("RunDamaged", "A run of length 0 stops the run-length coded data; the rest is left black.", _source.Offset + cursor.Position);
                break;
            }

            bool ok = true;
            if ((flag & 1) != 0)
            {
                for (int k = 0; k < unit && ok; k++)
                {
                    ok = cursor.Next(data, out uint v);
                    pixel[k] = (ushort)v;
                }

                for (long i = 0; i < count && at < total && ok; i++)
                {
                    for (int k = 0; k < unit && at < total; k++)
                    {
                        _decoded[at++] = pixel[k];
                    }
                }
            }
            else
            {
                for (long i = 0; i < count * unit && at < total && ok; i++)
                {
                    ok = cursor.Next(data, out uint v);
                    _decoded[at] = (ushort)v;
                    at += ok ? 1 : 0;
                }
            }

            if (!ok)
            {
                break;
            }
        }

        _decodedRows = (int)(at / rowSamples);
    }
}
