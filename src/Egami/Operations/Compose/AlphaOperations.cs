using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Operations.Scopes;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>Sets alpha to a mask's coverage and keeps the straight color, as ImageMagick's CopyOpacity composite does.</summary>
internal sealed class CopyAlphaOperation(Mask mask) : IRasterOperation, IAreaTranslatable
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public IRasterOperation? InArea(Rect area)
    {
        return new CopyAlphaOperation(mask with { Offset = new Point(mask.Offset.X - area.X, mask.Offset.Y - area.Y) });
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        if (TPixel.Layout.Alpha == AlphaKind.None)
        {
            return;
        }

        Raster? snapshot = mask.Source.HasPlane(input.Destination) ? mask.Source.Derive(_ => { }) : null;
        try
        {
            MaskCoverage coverage = new(snapshot is null ? mask : mask with { Source = snapshot }, scan: false);
            bool straight8 = typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8);
            int width = input.Destination.Width;
            input.ForEachBand((start, end) =>
            {
                byte[]? bytes = straight8 ? ArrayPool<byte>.Shared.Rent(width) : null;
                float[]? floats = straight8 ? null : ArrayPool<float>.Shared.Rent(width);
                RgbaF32[]? rgba = straight8 ? null : ArrayPool<RgbaF32>.Shared.Rent(width);
                try
                {
                    for (int y = start; y < end; y++)
                    {
                        Span<TPixel> row = input.Destination.Row(y);
                        if (bytes is not null)
                        {
                            Span<byte> m = bytes.AsSpan(0, width);
                            coverage.Row8(y, 0, m);
                            Span<uint> pixels = MemoryMarshal.Cast<TPixel, uint>(row);
                            for (int x = 0; x < width; x++)
                            {
                                pixels[x] = (pixels[x] & 0x00FFFFFFu) | ((uint)m[x] << 24);
                            }

                            continue;
                        }

                        Span<float> k = floats.AsSpan(0, width);
                        Span<RgbaF32> p = rgba.AsSpan(0, width);
                        coverage.RowFloat(y, 0, k);
                        PixelConvert.Convert<TPixel, RgbaF32>(row, p);
                        for (int x = 0; x < width; x++)
                        {
                            p[x] = new RgbaF32(p[x].R, p[x].G, p[x].B, k[x]);
                        }

                        PixelConvert.Convert<RgbaF32, TPixel>(p, row);
                    }
                }
                finally
                {
                    if (bytes is not null)
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }

                    if (floats is not null)
                    {
                        ArrayPool<float>.Shared.Return(floats);
                        ArrayPool<RgbaF32>.Shared.Return(rgba!);
                    }
                }
            });
        }
        finally
        {
            snapshot?.Dispose();
        }
    }
}

/// <summary>
/// Composites every pixel over an opaque background color and makes it opaque (ImageMagick's -alpha
/// remove). 8-bit straight pixels on stored values take the exact integer kernel the JPEG writer's Background uses.
/// </summary>
internal sealed class FlattenOperation(Rgba8 background, bool linear, Colors.LinearCurve? curve = null) : IRasterOperation, ILinearLight, IRunsOnViews
{
    public bool UsesLinearLight => linear;

    public IRasterOperation WithCurve(Colors.LinearCurve curve) => new FlattenOperation(background, linear, curve);

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        if (TPixel.Layout.Alpha == AlphaKind.None)
        {
            return;
        }

        if (!linear && (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8)))
        {
            Rgba8 under = typeof(TPixel) == typeof(Bgra8) ? new Rgba8(background.B, background.G, background.R, 255) : background;
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    FlattenKernels.Flatten8(MemoryMarshal.Cast<TPixel, Rgba8>(input.Destination.Row(y)), under);
                }
            });
            return;
        }

        Colors.LinearCurve? light = linear ? curve ?? Colors.LinearCurve.Srgb : null;
        RowConverter<TPixel> converter = new(light, inksAsRgb: true);
        (float br, float bg, float bb) = light is null
            ? (background.R / 255f, background.G / 255f, background.B / 255f)
            : (light.LinearFrom8[background.R], light.LinearFrom8[background.G], light.LinearFrom8[background.B]);
        int width = input.Destination.Width;
        input.ForEachBand((start, end) =>
        {
            float[] floats = ArrayPool<float>.Shared.Rent(4 * width);
            try
            {
                Span<float> p = floats.AsSpan(0, 4 * width);
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> row = input.Destination.Row(y);
                    converter.Load(row, p);
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        float keep = 1f - p[i + 3];
                        p[i] += br * keep;
                        p[i + 1] += bg * keep;
                        p[i + 2] += bb * keep;
                        p[i + 3] = 1f;
                    }

                    converter.Store(p, row, opaque: true);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floats);
            }
        });
    }
}

/// <summary>Flattening onto an opaque background in exact integers.</summary>
internal static class FlattenKernels
{
    /// <summary>Blends each straight pixel over <paramref name="background"/> by its alpha, round((c·a + b·(255 − a)) / 255), then makes it opaque.</summary>
    internal static void Flatten8(Span<Rgba8> pixels, Rgba8 background)
    {
        for (int x = 0; x < pixels.Length; x++)
        {
            Rgba8 p = pixels[x];
            int a = p.A;
            int inverse = 255 - a;
            pixels[x] = new Rgba8(
                (byte)(((p.R * a) + (background.R * inverse) + 127) / 255),
                (byte)(((p.G * a) + (background.G * inverse) + 127) / 255),
                (byte)(((p.B * a) + (background.B * inverse) + 127) / 255),
                255);
        }
    }
}
