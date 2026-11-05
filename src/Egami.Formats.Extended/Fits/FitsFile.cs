using Egami.Pixels;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// A whole FITS file, for the numbers rather than a picture: every HDU's header cards, and an image's physical values,
/// <c>BZERO + BSCALE × stored</c>, unstretched, with missing pixels (<c>BLANK</c>, NaN, damaged tiles, data past the file's end)
/// as NaN. Tile-compressed images decode as plain ones. World coordinate (WCS) keywords are in the header; Egami does no sky
/// coordinate math.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-file"/>
/// </example>
public sealed class FitsFile
{
    private readonly byte[] _data;
    private readonly int _length;
    private readonly EgamiContext _context;
    private readonly EgamiInput _limits;

    private FitsFile(byte[] data, int length, List<FitsHdu> hdus, EgamiContext context, EgamiInput limits)
    {
        _limits = limits;
        _data = data;
        _length = length;
        _context = context;
        Hdus = hdus;
    }

    /// <summary>Gets every HDU, the primary one first.</summary>
    public IReadOnlyList<FitsHdu> Hdus { get; }

    /// <summary>Gets the first HDU that holds an image, or <see langword="null"/>.</summary>
    public FitsHdu? FirstImage => Hdus.FirstOrDefault(h => h.IsImage);

    /// <summary>Reads a FITS file from a path.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The file, its headers read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The primary header is not FITS or is damaged.</exception>
    public static FitsFile Load(string path, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, context);
    }

    /// <summary>Reads a FITS file from a stream, to its end.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The file, its headers read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The primary header is not FITS or is damaged.</exception>
    public static FitsFile Load(Stream stream, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EgamiContext limits = context ?? EgamiContext.Default;
        EgamiInput input = new(stream, limits, default);
        byte[] data = InputData.ReadToEnd(input, Array.MaxLength, out int length);
        EgamiInput headers = new(new MemoryStream(data, 0, length, false), limits, default);
        List<FitsHdu> hdus = [];
        for (int index = 0; ; index++)
        {
            FitsHdu? hdu = FitsHeaderReader.Read(headers, index);
            if (hdu is null)
            {
                break;
            }

            hdus.Add(hdu);
            if (hdu.DataStart + hdu.PaddedLength >= length)
            {
                break;
            }

            headers.Skip(hdu.PaddedLength);
        }

        return new FitsFile(data, length, hdus, limits, headers);
    }

    /// <summary>
    /// Returns an image HDU's physical values, every plane, in the file's order: along a row first, then up the rows from the
    /// file's first, then plane by plane.
    /// </summary>
    /// <param name="hdu">An image HDU of this file.</param>
    /// <returns>The values.</returns>
    /// <exception cref="ArgumentException"><paramref name="hdu"/> is not an image of this file.</exception>
    /// <exception cref="UnsupportedFormatException">The image uses a tile compression Egami does not read.</exception>
    public double[] ReadValues(FitsHdu hdu)
    {
        long count = Check(hdu);
        _limits.CheckMemory(count * 8);
        double[] values = new double[count];
        Decode<double>(hdu, values);
        return values;
    }

    /// <summary>
    /// Returns one plane of an image HDU as physical values in <c>GrayF32</c>, the file's first row at the bottom as Load gives
    /// it.
    /// </summary>
    /// <param name="hdu">An image HDU of this file.</param>
    /// <param name="plane">The plane of a cube, from 0.</param>
    /// <returns>The plane.</returns>
    /// <exception cref="ArgumentException"><paramref name="hdu"/> is not an image of this file.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plane"/> is outside the cube.</exception>
    /// <exception cref="UnsupportedFormatException">The image uses a tile compression Egami does not read.</exception>
    public Raster<GrayF32> ReadPlane(FitsHdu hdu, int plane = 0)
    {
        long count = Check(hdu);
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, hdu.Planes);
        _limits.CheckFrameSize(hdu.Width, hdu.Height);
        _limits.CheckMemory(count * 4);
        float[] values = new float[count];
        Decode<float>(hdu, values);
        Raster<GrayF32> raster = Raster.Create<GrayF32>(hdu.Width, hdu.Height, _context);
        long pixels = (long)hdu.Width * hdu.Height;
        for (int y = 0; y < hdu.Height; y++)
        {
            ReadOnlySpan<float> row = values.AsSpan((int)((plane * pixels) + ((long)(hdu.Height - 1 - y) * hdu.Width)), hdu.Width);
            System.Runtime.InteropServices.MemoryMarshal.Cast<float, GrayF32>(row).CopyTo(raster.Row(y));
        }

        raster.Metadata.FormatName = FitsFormat.Info.Name;
        raster.Metadata.FormatDetails = new FitsDetails
        {
            Header = hdu.Header,
            Hdu = hdu.Index,
            Plane = plane,
            Bitpix = hdu.Bitpix,
            Compression = hdu.Compression,
            Stretch = FitsStretch.None,
        };
        return raster;
    }

    private long Check(FitsHdu hdu)
    {
        ArgumentNullException.ThrowIfNull(hdu);
        if (hdu.Index >= Hdus.Count || !ReferenceEquals(Hdus[hdu.Index], hdu) || !hdu.IsImage)
        {
            throw new ArgumentException("The HDU is not an image of this file.", nameof(hdu));
        }

        if (hdu.Kind == FitsHduKind.CompressedImage && !FitsTiles.Supports(hdu.Compression))
        {
            throw new UnsupportedFormatException($"FITS tile compression {hdu.Compression} is not one Egami reads.");
        }

        return checked((long)hdu.Width * hdu.Height * hdu.Planes);
    }

    private void Decode<T>(FitsHdu hdu, Span<T> values)
        where T : unmanaged, System.Numerics.IFloatingPointIeee754<T>
    {
        long start = Math.Min(hdu.DataStart, _length);
        ReadOnlySpan<byte> data = _data.AsSpan((int)start, (int)Math.Min(hdu.DataLength, _length - start));
        if (hdu.Kind == FitsHduKind.CompressedImage)
        {
            FitsTiles.Decode(data, hdu, values);
        }
        else
        {
            FitsData.Decode(data, hdu.Bitpix, hdu.Scale, hdu.Zero, hdu.Blank, values);
        }
    }
}
