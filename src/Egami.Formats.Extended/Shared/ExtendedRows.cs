using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended;

/// <summary>Reads a whole image from an open one, for <see cref="IFormatReader.Read(EgamiInput, ReaderOptions)"/>.</summary>
internal interface IWholeRead
{
    /// <summary>Reads every row into a new raster of the natural pixel type.</summary>
    Raster ReadNatural(EgamiInput input);

    /// <summary>Reads every row into a new raster of <typeparamref name="TPixel"/>.</summary>
    Raster<TPixel> ReadAll<TPixel>(EgamiInput input)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>
/// One open image of a format in this package: a single frame of rows, decoded top-down into a working row of
/// <typeparamref name="TWork"/> and converted from there. A file that ends early keeps the rows decoded so far and leaves the rest
/// transparent black (Lenient), or throws (Strict).
/// </summary>
/// <typeparam name="TNatural">The pixel type that best fits the file.</typeparam>
/// <typeparam name="TWork">The pixel type a row decodes into.</typeparam>
internal abstract class ExtendedRows<TNatural, TWork> : RowReader<TNatural>, IWholeRead
    where TNatural : unmanaged, IPixelType<TNatural>
    where TWork : unmanaged, IPixelType<TWork>
{
    private readonly IMemoryOwner<byte> _row;
    private readonly string _source;
    private RasterMetadata _warnings;
    private int _next;
    private int _failedAt = -1;
    private bool _started;
    private bool _ended;
    private bool _disposed;

    /// <summary>Creates the open image and rents its working row.</summary>
    /// <param name="input">The data, positioned after the header.</param>
    /// <param name="options">How to read.</param>
    /// <param name="format">The format, whose name goes in the metadata.</param>
    /// <param name="source">The first part of the format's warning ids, such as "Pcx".</param>
    /// <param name="width">The image width, already checked against the limits.</param>
    /// <param name="height">The image height, already checked against the limits.</param>
    protected ExtendedRows(EgamiInput input, ReaderOptions options, FormatInfo format, string source, int width, int height)
    {
        Input = input;
        Strict = options.Recovery == RecoveryMode.Strict;
        _source = source;
        Width = width;
        Height = height;
        Metadata = new RasterMetadata { FormatName = format.Name };
        _warnings = Metadata;
        _row = input.RentScratch(checked(width * Unsafe.SizeOf<TWork>()), ClearMode.Zero);
    }

    public override int Width { get; }

    public override int Height { get; }

    public override RasterMetadata Metadata { get; }

    public override long WorkingBytes => (long)Width * Unsafe.SizeOf<TWork>();

    /// <summary>Gets the data.</summary>
    protected EgamiInput Input { get; }

    /// <summary>Gets whether damage throws rather than being recovered from.</summary>
    protected bool Strict { get; }

    /// <summary>Gets the working row.</summary>
    protected Span<TWork> WorkRow => MemoryMarshal.Cast<byte, TWork>(_row.Memory.Span)[..Width];

    public override FrameMetadata? NextFrame()
    {
        if (_started)
        {
            EndFrame();
            return null;
        }

        _started = true;
        return new FrameMetadata();
    }

    public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
    {
        while (_next <= y)
        {
            Input.CancellationToken.ThrowIfCancellationRequested();
            if (_failedAt < 0 && !DecodeRow(_next, WorkRow))
            {
                Truncated(_next);
            }

            _next++;
        }

        if (_failedAt >= 0 && y > _failedAt)
        {
            destination.Clear();
            return;
        }

        PixelConvert.Convert<TWork, TPixel>(WorkRow.Slice(x, destination.Length), destination);
    }

    public override void EndFrame()
    {
        if (!_ended)
        {
            _ended = true;
            if (_next >= Height && _failedAt < 0)
            {
                Finish();
            }
        }
    }

    public Raster ReadNatural(EgamiInput input) => ReadAll<TNatural>(input);

    public Raster<TPixel> ReadAll<TPixel>(EgamiInput input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(Width, Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatDetails = Metadata.FormatDetails?.Copy();
            metadata.Resolution = Metadata.Resolution;
            metadata.ColorSpace = Metadata.ColorSpace;
            foreach (EgamiDiagnostic warning in Metadata.Warnings)
            {
                input.Warn(raster, warning.Id, warning.Message, warning.Offset);
            }

            _warnings = metadata;
            NextFrame();
            for (int y = 0; y < Height; y++)
            {
                ReadRow(y, 0, raster.Row(y));
            }

            EndFrame();
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes row <paramref name="y"/> into <paramref name="row"/>. Returns <see langword="false"/> if the data ended first,
    /// with the part of the row that did not decode cleared.
    /// </summary>
    protected abstract bool DecodeRow(int y, Span<TWork> row);

    /// <summary>Checks what follows the last row, once every row has decoded. Does nothing by default.</summary>
    protected virtual void Finish()
    {
    }

    /// <summary>Notes a problem a lenient read recovered from, with the id <c>Source.<paramref name="name"/></c>.</summary>
    protected void Warn(string name, string warning, long? offset = null)
    {
        Input.Warn(_warnings, $"{_source}.{name}", warning, offset);
    }

    /// <summary>Throws for damaged data when strict; otherwise notes it as a warning.</summary>
    protected void Damaged(string name, string problem, long? offset = null)
    {
        if (Strict)
        {
            throw Input.Corrupt(offset ?? Input.Position, problem);
        }

        Warn(name, problem, offset);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _row.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Truncated(int y)
    {
        if (Strict)
        {
            throw Input.Corrupt(Input.Position, "The data ends unexpectedly.");
        }

        _failedAt = y;
        Warn("RowsMissing", $"The file ends after {y} of {Height} rows; the rest is left transparent black.");
    }
}
