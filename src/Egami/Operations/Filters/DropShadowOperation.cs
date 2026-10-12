using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// DropShadow, as Skia's DropShadow image filter: the input's alpha, moved by (dx, dy) (bilinearly for fractions), blurred by a
/// Gaussian of (σx, σy) with nothing but transparency past the input, filled with the color times its alpha, and the input drawn
/// over it source-over, on premultiplied encoded values. The alpha lives in a float plane wide enough that the blur never meets
/// its edge, so the shadow fades as if the canvas went on. With Expand the canvas grows by 3σ plus the offset on the sides the
/// shadow reaches past the image; without it the size stays and the step works inside scopes (on the whole image, since the blur
/// reads it all).
/// </summary>
internal sealed class DropShadowOperation : IRasterOperation, INeedsNewPlane, IUpdatesMetadata, Scopes.INeighbourhoodStep
{
    private readonly float _dx;
    private readonly float _dy;
    private readonly float _sigmaX;
    private readonly float _sigmaY;
    private readonly Rgba8 _color;
    private readonly bool _shadowOnly;
    private readonly bool _fast;

    internal DropShadowOperation(float dx, float dy, float sigmaX, float sigmaY, Rgba8 color, DropShadowOptions options)
    {
        _dx = dx;
        _dy = dy;
        _sigmaX = sigmaX;
        _sigmaY = sigmaY;
        _color = color;
        Expands = options.Expand;
        _shadowOnly = options.ShadowOnly;
        _fast = options.Method == BlurMethod.Fast && sigmaX >= RasterOps.FastBlurFloor && sigmaY >= RasterOps.FastBlurFloor;
    }

    /// <summary>Gets a value indicating whether the canvas grows to hold the shadow, so the step changes the size.</summary>
    internal bool Expands { get; }

    public (int X, int Y)? Reach => null;

    /// <summary>Returns how far the canvas grows on each side: 3σ plus the offset where the shadow reaches past the image.</summary>
    internal (int Left, int Top, int Right, int Bottom) Margins()
    {
        if (!Expands)
        {
            return default;
        }

        return (Grow((3 * _sigmaX) - _dx), Grow((3 * _sigmaY) - _dy), Grow((3 * _sigmaX) + _dx), Grow((3 * _sigmaY) + _dy));

        static int Grow(float reach) => reach > 0 ? (int)MathF.Ceiling(reach) : 0;
    }

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input)
    {
        (int left, int top, int right, int bottom) = Margins();
        long width = (long)input.Width + left + right, height = (long)input.Height + top + bottom;
        if (width > int.MaxValue / 4 || height > int.MaxValue / 4)
        {
            throw new ArgumentException($"The shadow would make the {input.Width}×{input.Height} image too large.");
        }

        return new Size((int)width, (int)height);
    }

    public void UpdateMetadata(RasterMetadata metadata, Size input, Size output)
    {
        // The shadow changes pixels, so a gain map no longer matches them.
        metadata.GainMap = null;
        if (input != output)
        {
            (int left, int top, _, _) = Margins();
            ExifUpkeep.Dimensions(metadata, output);
            ExifUpkeep.Subject(metadata, SubjectMap.Pad(new Point(left, top), output), input);
        }
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        (int left, int top, _, _) = Margins();
        int width = destination.Width, height = destination.Height;

        // The alpha plane covers the canvas and the moved image, plus the blur's reach, so the blur never meets its edge.
        BoxCascade? bx = _fast ? BoxCascade.For(_sigmaX) : null, by = _fast ? BoxCascade.For(_sigmaY) : null;
        float[]? wx = !_fast && _sigmaX > 0 ? ConvolutionOperation.GaussianWeights(_sigmaX) : null;
        float[]? wy = !_fast && _sigmaY > 0 ? ConvolutionOperation.GaussianWeights(_sigmaY) : null;
        int reachX = bx?.Reach ?? (wx is null ? 0 : wx.Length / 2), reachY = by?.Reach ?? (wy is null ? 0 : wy.Length / 2);
        int extX = reachX + (int)MathF.Ceiling(MathF.Abs(_dx)) + 1, extY = reachY + (int)MathF.Ceiling(MathF.Abs(_dy)) + 1;
        int planeWidth = width + (2 * extX), planeHeight = height + (2 * extY);
        IPixelMemorySource memory = (IPixelMemorySource?)input.Memory ?? input.Context.Memory;
        PixelPlane<GrayF32> alpha = PixelPlane.Allocate<GrayF32>(planeWidth, planeHeight, memory);
        try
        {
            // Plane pixel (i, j) is canvas pixel (i − extX, j − extY), which reads the image at (i − extX − left − dx, …).
            Fill(input, alpha, source, extX + left + _dx, extY + top + _dy);
            if (_fast)
            {
                FastBlur.Run(alpha, 1, bx!, by!, input.Context.MaxParallelism, input.CancellationToken);
            }
            else if (wx is not null || wy is not null)
            {
                Exact(input, alpha, wx ?? [1], wy ?? [1], memory);
            }

            Compose(input, alpha, source, destination, extX, extY, left, top);
        }
        finally
        {
            alpha.Dispose();
        }
    }

    /// <summary>Writes the image's alpha, moved by (<paramref name="shiftX"/>, <paramref name="shiftY"/>) with bilinear weights, into the plane; 0 elsewhere.</summary>
    private static void Fill<TPixel>(OperationInput<TPixel> input, PixelPlane<GrayF32> alpha, PixelPlane<TPixel> source, float shiftX, float shiftY)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int ix = (int)MathF.Floor(shiftX), iy = (int)MathF.Floor(shiftY);
        float fx = shiftX - ix, fy = shiftY - iy;
        int width = source.Width, height = source.Height;
        RowConverter<TPixel> converter = new(null, inksAsRgb: true);
        RowRunner.Run(alpha.Height, (long)alpha.Width * 4, input.Context.MaxParallelism, (start, end) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent((width * 4) + (2 * (width + 1)));
            try
            {
                Span<float> pixels = buffer.AsSpan(0, width * 4);
                Span<float> upper = buffer.AsSpan(width * 4, width + 1), lower = buffer.AsSpan((width * 4) + width + 1, width + 1);
                for (int j = start; j < end; j++)
                {
                    // Plane row j takes image row j − iy with weight 1 − fy and row j − iy − 1 with weight fy.
                    Span<float> row = MemoryMarshal.Cast<GrayF32, float>(alpha.Row(j));
                    row.Clear();
                    int y0 = j - iy;
                    bool hasUpper = Moved(source, converter, y0 - 1, fx, pixels, upper);
                    bool hasLower = Moved(source, converter, y0, fx, pixels, lower);
                    if (!hasUpper && !hasLower)
                    {
                        continue;
                    }

                    // The moved row covers plane columns ix to ix + width.
                    Span<float> target = row.Slice(ix, width + 1);
                    for (int i = 0; i <= width; i++)
                    {
                        target[i] = ((1 - fy) * (hasLower ? lower[i] : 0)) + (fy * (hasUpper ? upper[i] : 0));
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }

    /// <summary>Loads image row <paramref name="y"/>'s alpha moved right by the fraction <paramref name="fx"/>: width + 1 values.</summary>
    private static bool Moved<TPixel>(PixelPlane<TPixel> source, RowConverter<TPixel> converter, int y, float fx, Span<float> pixels, Span<float> moved)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (y < 0 || y >= source.Height)
        {
            return false;
        }

        int width = source.Width;
        converter.Load(source.Row(y), pixels);
        float previous = 0;
        for (int i = 0; i < width; i++)
        {
            float a = pixels[(i * 4) + 3];
            moved[i] = ((1 - fx) * a) + (fx * previous);
            previous = a;
        }

        moved[width] = fx * previous;
        return true;
    }

    /// <summary>The exact Gaussian, separable, through a second plane: across into it, then down back.</summary>
    private static void Exact<TPixel>(OperationInput<TPixel> input, PixelPlane<GrayF32> alpha, float[] wx, float[] wy, IPixelMemorySource memory)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = alpha.Width, height = alpha.Height, rx = wx.Length / 2, ry = wy.Length / 2;
        PixelPlane<GrayF32> across = PixelPlane.Allocate<GrayF32>(width, height, memory);
        try
        {
            RowRunner.Run(height, (long)width * 4, input.Context.MaxParallelism, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<float> row = MemoryMarshal.Cast<GrayF32, float>(alpha.Row(y));
                    Span<float> output = MemoryMarshal.Cast<GrayF32, float>(across.Row(y));
                    for (int x = 0; x < width; x++)
                    {
                        int first = Math.Max(0, rx - x), last = Math.Min(wx.Length, width - x + rx);
                        float sum = 0;
                        for (int k = first; k < last; k++)
                        {
                            sum += wx[k] * row[x + k - rx];
                        }

                        output[x] = sum;
                    }
                }
            }, input.CancellationToken);

            RowRunner.Run(height, (long)width * 4 * wy.Length, input.Context.MaxParallelism, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<float> output = MemoryMarshal.Cast<GrayF32, float>(alpha.Row(y));
                    output.Clear();
                    for (int k = Math.Max(0, ry - y), last = Math.Min(wy.Length, height - y + ry); k < last; k++)
                    {
                        ReadOnlySpan<float> row = MemoryMarshal.Cast<GrayF32, float>(across.Row(y + k - ry));
                        float w = wy[k];
                        for (int x = 0; x < width; x++)
                        {
                            output[x] += w * row[x];
                        }
                    }
                }
            }, input.CancellationToken);
        }
        finally
        {
            across.Dispose();
        }
    }

    /// <summary>Fills the canvas with the colored shadow and, unless only the shadow is wanted, draws the image over it.</summary>
    private void Compose<TPixel>(
        OperationInput<TPixel> input, PixelPlane<GrayF32> alpha, PixelPlane<TPixel> source, PixelPlane<TPixel> destination, int extX, int extY, int left, int top)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = destination.Width, imageWidth = source.Width;
        float ca = _color.A / 255f;
        Vector4 color = new Vector4(_color.R / 255f, _color.G / 255f, _color.B / 255f, 1) * ca;
        if (typeof(TPixel) == typeof(PremulBgra8))
        {
            // Its rows load as they are stored, blue first.
            (color.X, color.Z) = (color.Z, color.X);
        }
        RowConverter<TPixel> converter = new(null, inksAsRgb: true);
        RowRunner.Run(destination.Height, (long)width * 16, input.Context.MaxParallelism, (start, end) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent((width + imageWidth) * 4);
            try
            {
                Span<float> row = buffer.AsSpan(0, width * 4), image = buffer.AsSpan(width * 4, imageWidth * 4);
                Span<Vector4> pixels = MemoryMarshal.Cast<float, Vector4>(row);
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<float> shadow = MemoryMarshal.Cast<GrayF32, float>(alpha.Row(y + extY)).Slice(extX, width);
                    for (int x = 0; x < width; x++)
                    {
                        pixels[x] = color * Math.Clamp(shadow[x], 0, 1);
                    }

                    int sy = y - top;
                    if (!_shadowOnly && sy >= 0 && sy < source.Height)
                    {
                        converter.Load(source.Row(sy), image);
                        ReadOnlySpan<Vector4> over = MemoryMarshal.Cast<float, Vector4>(image);
                        Span<Vector4> under = pixels.Slice(left, imageWidth);
                        for (int x = 0; x < imageWidth; x++)
                        {
                            Vector4 s = over[x];
                            under[x] = s + (under[x] * (1 - s.W));
                        }
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }
}
