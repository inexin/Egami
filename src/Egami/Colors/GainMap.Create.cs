using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// How <see cref="GainMap.Create"/> and <see cref="Formats.Jpeg.JpegWriterOptions.GainMap"/> make a gain map (HDR Design question
/// 10). The defaults are libultrahdr's: a full-size map with a gain for each of red, green and blue, gamma 1, encoded at quality 95.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map-write"/>
/// </example>
public sealed record GainMapOptions
{
    private readonly int _scale = 1;
    private readonly int _quality = 95;
    private readonly double _gamma = 1;

    /// <summary>Gets the default options.</summary>
    public static GainMapOptions Default { get; } = new();

    /// <summary>
    /// Gets how many times smaller than the image the map is on each side, 1 to 128: 4 makes a map a sixteenth of the pixels, as
    /// phone cameras write. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 128.</exception>
    public int Scale
    {
        get => _scale;
        init => _scale = value is >= 1 and <= 128 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The gain map scale is 1 to 128.");
    }

    /// <summary>
    /// Gets a value indicating whether the map holds a gain for each of red, green and blue, so HDR colors brighter than SDR can
    /// show (a color map), rather than one gain from luminance (a gray map, a third of the size). Defaults to <see langword="true"/>.
    /// </summary>
    public bool MultiChannel { get; init; } = true;

    /// <summary>Gets the gamma the gains are stored with; above 1 spends more of the 256 steps on small gains. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public double Gamma
    {
        get => _gamma;
        init => _gamma = double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The gain map gamma is positive.");
    }

    /// <summary>Gets the JPEG quality of the gain map image, 1 to 100. Defaults to 95.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = value is >= 1 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The gain map quality is 1 to 100.");
    }

    /// <summary>
    /// Gets the smallest gain to store, linear (0.5 lets the HDR rendition be half as bright as SDR in places), or
    /// <see langword="null"/> (the default) for the smallest the image needs.
    /// </summary>
    public double? MinContentBoost { get; init; }

    /// <summary>Gets the largest gain to store, linear, or <see langword="null"/> (the default) for the largest the image needs.</summary>
    public double? MaxContentBoost { get; init; }

    /// <summary>
    /// Gets the display headroom at which the full HDR rendition shows (<see cref="GainMapInfo.HdrCapacityMax"/>, linear), or
    /// <see langword="null"/> (the default) for the largest gain the map stores; displays with less headroom show part of it.
    /// </summary>
    public double? Headroom { get; init; }

    /// <summary>Gets the offset added to SDR and HDR values before their ratio, which keeps gains near black small. Defaults to 1/64.</summary>
    public double Offset { get; init; } = 1.0 / 64;
}

/// <content>Making a gain map from an HDR rendition and an SDR one.</content>
public sealed partial class GainMap
{
    /// <summary>The largest log2 gain stored, as libultrahdr limits it: beyond it, rounding of the map costs more than it gains.</summary>
    private const double MaxLogGain = 15.6;

    /// <summary>The smallest log2 gain stored.</summary>
    private const double MinLogGain = -14.3;

    /// <summary>Gets the JPEG quality the map image is written at when it has no stored image.</summary>
    internal int Quality { get; init; } = 95;

    /// <summary>
    /// Computes the gain map that turns <paramref name="sdr"/> into <paramref name="hdr"/>: for each map pixel, the log2 ratio of the
    /// HDR light to the SDR light, averaged over the pixels it covers, stored in 8 bits between the smallest and largest gain.
    /// </summary>
    /// <param name="hdr">The HDR rendition: linear light with 1.0 as SDR white (as Radiance, OpenEXR and HDR decodes give), or PQ or HLG
    /// with its color profile.</param>
    /// <param name="sdr">The SDR rendition to show on SDR displays, the same size; or <see langword="null"/> to make one with
    /// <see cref="ToneMapping.Bt2390"/>, as <see cref="Formats.Jpeg.JpegWriterOptions.GainMap"/> does.</param>
    /// <param name="options">How to make the map, or <see langword="null"/> for the defaults.</param>
    /// <returns>The gain map, for <see cref="Metadata.RasterMetadata.GainMap"/> of the SDR rendition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hdr"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sdr"/> is not the size of <paramref name="hdr"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map-write"/>
    /// </example>
    public static GainMap Create(Raster hdr, Raster? sdr = null, GainMapOptions? options = null)
    {
        (Raster<Rgb8> rendition, GainMap map) = Encode(hdr, sdr, options ?? GainMapOptions.Default);
        rendition.Dispose();
        return map;
    }

    /// <summary>
    /// Returns the SDR base image (<paramref name="sdr"/> as 8-bit, or <paramref name="hdr"/> tone mapped) and its gain map, which
    /// the base image's metadata also carries.
    /// </summary>
    internal static (Raster<Rgb8> Base, GainMap Map) Encode(Raster hdr, Raster? sdr, GainMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        if (sdr is not null && (sdr.Width != hdr.Width || sdr.Height != hdr.Height))
        {
            throw new ArgumentException($"The SDR rendition is {sdr.Width} × {sdr.Height}; the HDR one is {hdr.Width} × {hdr.Height}.", nameof(sdr));
        }

        ColorProfile hdrProfile = hdr.Metadata.GetColorProfile();
        Raster<Rgb8> rendition = sdr is null ? ToneMapped(hdr, hdrProfile) : sdr.As<Rgb8>();
        try
        {
            ColorProfile baseProfile = rendition.Metadata.GetColorProfile();
            ColorProfile linear = ColorProfile.Create(baseProfile.Primaries ?? ColorPrimaries.Rec709, TransferFunction.Linear);
            using Raster<RgbF32> hdrLinear = Linear(hdr, linear);
            using Raster<RgbF32> sdrLinear = Linear(rendition, linear);
            GainMap map = Compute(hdrLinear, sdrLinear, options, linear);
            rendition.Metadata.GainMap = map;
            rendition.Metadata.Hdr = null;
            return (rendition, map);
        }
        catch
        {
            rendition.Dispose();
            throw;
        }
    }

    /// <summary>The HDR rendition tone mapped with BT.2390 to 8-bit SDR, in its primaries when they are Rec. 709 or Display P3, else in Display P3.</summary>
    private static Raster<Rgb8> ToneMapped(Raster hdr, ColorProfile profile)
    {
        ColorPrimaries primaries = profile.Primaries is { } p && (p.IsCloseTo(ColorPrimaries.Rec709) || p.IsCloseTo(ColorPrimaries.DisplayP3)) ? p : ColorPrimaries.DisplayP3;
        ColorProfile target = primaries.IsCloseTo(ColorPrimaries.Rec709) ? ColorProfile.Srgb : primaries.IsCloseTo(ColorPrimaries.DisplayP3) ? ColorProfile.DisplayP3 : ColorProfile.Create(primaries, TransferFunction.Srgb);
        using Raster<RgbaF32> wide = hdr.As<RgbaF32>();
        wide.Apply(ops => ops.ToneMap(ToneMapping.Bt2390).ConvertColor(target));
        Raster<Rgb8> result = wide.As<Rgb8>();
        result.Metadata.Hdr = null;
        return result;
    }

    /// <summary>The raster as linear light in <paramref name="linear"/>'s primaries.</summary>
    private static Raster<RgbF32> Linear(Raster raster, ColorProfile linear)
    {
        Raster<RgbF32> result = raster.As<RgbF32>();
        try
        {
            result.Apply(ops => ops.ConvertColor(linear));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static GainMap Compute(Raster<RgbF32> hdr, Raster<RgbF32> sdr, GainMapOptions options, ColorProfile linear)
    {
        int width = hdr.Width, height = hdr.Height, scale = options.Scale;
        int mapW = Math.Max(1, width / scale), mapH = Math.Max(1, height / scale);
        int channels = options.MultiChannel ? 3 : 1;
        float offset = (float)options.Offset;
        float[] gains = new float[mapW * mapH * channels];
        Vector3d luma = ColorPrimariesLuma(linear.Primaries!);
        int threads = (long)width * height < 256 * 1024 ? 1 : Math.Max(1, Math.Min(hdr.Context.MaxParallelism, mapH));
        Parallel.For(0, mapH, new ParallelOptions { MaxDegreeOfParallelism = threads }, my =>
        {
            int y0 = my * scale, y1 = my == mapH - 1 ? height : y0 + scale;
            for (int mx = 0; mx < mapW; mx++)
            {
                int x0 = mx * scale, x1 = mx == mapW - 1 ? width : x0 + scale;
                double hr = 0, hg = 0, hb = 0, sr = 0, sg = 0, sb = 0;
                for (int y = y0; y < y1; y++)
                {
                    ReadOnlySpan<RgbF32> h = hdr.Row(y)[x0..x1], s = sdr.Row(y)[x0..x1];
                    for (int x = 0; x < h.Length; x++)
                    {
                        hr += Math.Max(h[x].R, 0);
                        hg += Math.Max(h[x].G, 0);
                        hb += Math.Max(h[x].B, 0);
                        sr += Math.Max(s[x].R, 0);
                        sg += Math.Max(s[x].G, 0);
                        sb += Math.Max(s[x].B, 0);
                    }
                }

                double n = (y1 - y0) * (x1 - x0);
                int at = ((my * mapW) + mx) * channels;
                if (channels == 3)
                {
                    gains[at] = Gain(sr / n, hr / n, offset);
                    gains[at + 1] = Gain(sg / n, hg / n, offset);
                    gains[at + 2] = Gain(sb / n, hb / n, offset);
                }
                else
                {
                    gains[at] = Gain(((luma.X * sr) + (luma.Y * sg) + (luma.Z * sb)) / n, ((luma.X * hr) + (luma.Y * hg) + (luma.Z * hb)) / n, offset);
                }
            }
        });

        double min = double.MaxValue, max = double.MinValue;
        foreach (float gain in gains)
        {
            min = Math.Min(min, gain);
            max = Math.Max(max, gain);
        }

        // One range for every channel, as libultrahdr writes it, so Ultra HDR 1.0 readers of the XMP see the same values.
        min = Math.Clamp(min, MinLogGain, MaxLogGain);
        max = Math.Clamp(max, MinLogGain, MaxLogGain);
        if (options.MaxContentBoost is double maxBoost && maxBoost > 0)
        {
            max = Math.Min(max, Math.Log2(maxBoost));
        }

        if (options.MinContentBoost is double minBoost && minBoost > 0)
        {
            min = Math.Max(min, Math.Log2(minBoost));
        }

        if (max - min < 1e-6)
        {
            max = min + 0.1;
        }

        byte[] pixels = new byte[gains.Length];
        for (int i = 0; i < gains.Length; i++)
        {
            double v = Math.Clamp((gains[i] - min) / (max - min), 0, 1);
            if (options.Gamma != 1)
            {
                v = Math.Pow(v, options.Gamma);
            }

            pixels[i] = (byte)Math.Round(v * 255, MidpointRounding.AwayFromZero);
        }

        double capacity = options.Headroom is double headroom && headroom >= 1 ? Math.Log2(headroom) : Math.Max(max, 0);
        GainMapInfo info = new()
        {
            GainMapMin = new(min),
            GainMapMax = new(max),
            Gamma = new(options.Gamma),
            OffsetSdr = new(options.Offset),
            OffsetHdr = new(options.Offset),
            HdrCapacityMin = 0,
            HdrCapacityMax = capacity,
            Format = GainMapFormat.Iso21496,
        };
        return new GainMap(info, mapW, mapH, channels, pixels) { Quality = options.Quality };
    }

    /// <summary>The log2 gain from <paramref name="sdr"/> to <paramref name="hdr"/>; a near-black SDR value may not ask for much, as libultrahdr limits it.</summary>
    private static float Gain(double sdr, double hdr, float offset)
    {
        double gain = Math.Log2((hdr + offset) / (sdr + offset));
        return (float)(sdr < 2.0 / 255 ? Math.Min(gain, 2.3) : gain);
    }

    /// <summary>The luminance weights of linear RGB in <paramref name="primaries"/>: the Y row of its RGB to XYZ matrix.</summary>
    private static Vector3d ColorPrimariesLuma(ColorPrimaries primaries)
    {
        Matrix3d m = primaries.ToXyz();
        return new Vector3d(m[1, 0], m[1, 1], m[1, 2]);
    }
}
