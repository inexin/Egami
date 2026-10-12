using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Colors;

/// <content>Applying a gain map: the HDR rendition of a base image at a display headroom.</content>
public sealed partial class GainMap
{
    /// <summary>Entries of the gain factor table over the map's 0 to 1 range; values in between are interpolated.</summary>
    private const int FactorSteps = 4096;

    /// <summary>
    /// Returns the rendition of <paramref name="baseImage"/> for a display with <paramref name="headroom"/> (its peak over SDR
    /// white, linear; <see langword="null"/> for the map's full HDR capacity): linear <see cref="RgbaF16"/> with 1.0 as SDR white
    /// (203 cd/m²), in the base image's primaries, with <see cref="HdrMetadata.MaxContentLightLevel"/> set from the map's
    /// largest boost. The map is upsampled bilinearly row by row, so no full-size copy of it is made.
    /// </summary>
    /// <param name="baseImage">The base image, in its own encoding (usually sRGB), the size the map was made for (it may differ by scale).</param>
    /// <param name="headroom">The display headroom, 1 or more, or <see langword="null"/> for full HDR.</param>
    /// <returns>The rendition. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseImage"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="headroom"/> is below 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map"/>
    /// </example>
    public Raster<RgbaF16> Apply(Raster baseImage, double? headroom = null)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        if (headroom is double h && !(h >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), h, "The headroom is 1 (SDR) or more.");
        }

        ColorProfile profile = baseImage.Metadata.GetColorProfile();
        TransferFunction transfer = profile.Transfer ?? TransferFunction.Srgb;
        Raster<RgbaF16> result = Raster.Create<RgbaF16>(baseImage.Width, baseImage.Height, baseImage.Context);
        try
        {
            double weight = Info.Weight(headroom ?? Math.Pow(2, Math.Max(Info.HdrCapacityMax, Info.HdrCapacityMin)));
            float[][] factors = [Factors(0, weight), Factors(1, weight), Factors(2, weight)];
            float[] toLinear = new float[256];
            for (int i = 0; i < 256; i++)
            {
                toLinear[i] = (float)transfer.ToLinear(i / 255.0);
            }

            ApplyRows(baseImage, result, factors, toLinear, transfer, weight);
            RasterMetadata metadata = result.Metadata;
            metadata.ColorSpace = ColorSpaceHint.Linear;
            metadata.Primaries = profile.Primaries is { } p && !p.IsCloseTo(ColorPrimaries.Rec709) ? p : null;
            double maxBoost = Math.Max(Info.GainMapMax.Red, Math.Max(Info.GainMapMax.Green, Info.GainMapMax.Blue));
            double minBoost = Math.Min(Info.GainMapMin.Red, Math.Min(Info.GainMapMin.Green, Info.GainMapMin.Blue));
            double peak = Math.Pow(2, Math.Max(maxBoost * weight, minBoost * weight));
            metadata.Hdr = new HdrMetadata { MaxContentLightLevel = 203 * Math.Max(1, peak) };
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The gain factor of channel <paramref name="c"/> at <see cref="FactorSteps"/> map values: 2^(lerp(min, max, g^(1/gamma)) × weight).
    /// </summary>
    private float[] Factors(int c, double weight)
    {
        float[] table = new float[FactorSteps + 1];
        double min = Info.GainMapMin[c], max = Info.GainMapMax[c], gamma = Info.Gamma[c];
        for (int i = 0; i <= FactorSteps; i++)
        {
            double g = i / (double)FactorSteps;
            if (gamma != 1)
            {
                g = Math.Pow(g, 1 / gamma);
            }

            table[i] = (float)Math.Pow(2, ((min * (1 - g)) + (max * g)) * weight);
        }

        return table;
    }

    private void ApplyRows(Raster baseImage, Raster<RgbaF16> result, float[][] factors, float[] toLinear, TransferFunction transfer, double weight)
    {
        int width = baseImage.Width, height = baseImage.Height;
        int mapW = Width, mapH = Height, channels = Channels;
        byte[] pixels = Pixels;
        float offsetSdrR = (float)Info.OffsetSdr.Red, offsetSdrG = (float)Info.OffsetSdr.Green, offsetSdrB = (float)Info.OffsetSdr.Blue;
        float offsetHdrR = (float)Info.OffsetHdr.Red, offsetHdrG = (float)Info.OffsetHdr.Green, offsetHdrB = (float)Info.OffsetHdr.Blue;
        bool eightBit = baseImage.Layout.ChannelType == PixelChannelType.Unorm8;

        // Each output column's two map columns and weight. Map pixel n covers base pixel n × scale onward, as libultrahdr samples
        // when it makes the map and when it applies it, so the corners line up rather than the pixel centers.
        int[] x0 = new int[width], x1 = new int[width];
        float[] fx = new float[width];
        for (int x = 0; x < width; x++)
        {
            double mx = (double)x * mapW / width;
            Corners(mx, mapW, out x0[x], out x1[x], out fx[x]);
        }

        // Bands of rows run in parallel; each has its own row buffers.
        const int Band = 32;
        int bands = (height + Band - 1) / Band;
        int threads = (long)width * height < 256 * 1024 ? 1 : Math.Max(1, Math.Min(baseImage.Context.MaxParallelism, bands));
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = threads }, band =>
        {
            Rgb8[] rgb8 = eightBit ? ArrayPool<Rgb8>.Shared.Rent(width) : [];
            RgbF32[] rgbF = eightBit ? [] : ArrayPool<RgbF32>.Shared.Rent(width);
            float[] line = ArrayPool<float>.Shared.Rent(mapW * channels);
            try
            {
                for (int y = band * Band; y < Math.Min(height, (band + 1) * Band); y++)
                {
                    double my = (double)y * mapH / height;
                    Corners(my, mapH, out int y0, out int y1, out float fy);

                    // The map row at this height, interpolated between its two rows, as 0 to 1.
                    Span<float> mapLine = line.AsSpan(0, mapW * channels);
                    ReadOnlySpan<byte> a = pixels.AsSpan(y0 * mapW * channels, mapW * channels), b = pixels.AsSpan(y1 * mapW * channels, mapW * channels);
                    for (int i = 0; i < mapLine.Length; i++)
                    {
                        mapLine[i] = ((a[i] * (1 - fy)) + (b[i] * fy)) * (1f / 255);
                    }

                    Span<RgbaF16> output = result.Row(y);
                    if (eightBit)
                    {
                        baseImage.ReadRow(y, 0, rgb8.AsSpan(0, width));
                    }
                    else
                    {
                        baseImage.ReadRow(y, 0, rgbF.AsSpan(0, width));
                    }

                    for (int x = 0; x < width; x++)
                    {
                        float r, g, bl;
                        if (eightBit)
                        {
                            Rgb8 p = rgb8[x];
                            r = toLinear[p.R];
                            g = toLinear[p.G];
                            bl = toLinear[p.B];
                        }
                        else
                        {
                            RgbF32 p = rgbF[x];
                            r = (float)transfer.ToLinear(p.R);
                            g = (float)transfer.ToLinear(p.G);
                            bl = (float)transfer.ToLinear(p.B);
                        }

                        float wx = fx[x];
                        int i0 = x0[x] * channels, i1 = x1[x] * channels;
                        float gr = (mapLine[i0] * (1 - wx)) + (mapLine[i1] * wx);
                        float gg = gr, gb = gr;
                        if (channels == 3)
                        {
                            gg = (mapLine[i0 + 1] * (1 - wx)) + (mapLine[i1 + 1] * wx);
                            gb = (mapLine[i0 + 2] * (1 - wx)) + (mapLine[i1 + 2] * wx);
                        }

                        float hr = ((r + offsetSdrR) * Lookup(factors[0], gr)) - offsetHdrR;
                        float hg = ((g + offsetSdrG) * Lookup(factors[1], gg)) - offsetHdrG;
                        float hb = ((bl + offsetSdrB) * Lookup(factors[2], gb)) - offsetHdrB;
                        output[x] = new RgbaF16((Half)MathF.Max(hr, 0), (Half)MathF.Max(hg, 0), (Half)MathF.Max(hb, 0), (Half)1f);
                    }
                }
            }
            finally
            {
                if (rgb8.Length > 0)
                {
                    ArrayPool<Rgb8>.Shared.Return(rgb8);
                }

                if (rgbF.Length > 0)
                {
                    ArrayPool<RgbF32>.Shared.Return(rgbF);
                }

                ArrayPool<float>.Shared.Return(line);
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lookup(float[] table, float g)
    {
        float position = Math.Clamp(g, 0, 1) * FactorSteps;
        int i = Math.Min((int)position, FactorSteps - 1);
        float t = position - i;
        return table[i] + ((table[i + 1] - table[i]) * t);
    }

    /// <summary>The two samples around position <paramref name="m"/> and the weight of the second, clamped at the edges.</summary>
    private static void Corners(double m, int size, out int lower, out int upper, out float weight)
    {
        if (m <= 0)
        {
            lower = upper = 0;
            weight = 0;
            return;
        }

        lower = Math.Min((int)m, size - 1);
        upper = Math.Min(lower + 1, size - 1);
        weight = (float)(m - lower);
    }
}
