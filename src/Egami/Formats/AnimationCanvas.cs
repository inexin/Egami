using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats;

/// <summary>
/// The canvas animated formats draw their frames on, shared by the GIF and APNG readers so both follow the same rules. It starts
/// fully transparent. A frame is drawn into its rectangle, copied out as a full-size frame, and then disposed of: left alone,
/// cleared to transparent, or put back from a copy of its area saved before it was drawn.
/// </summary>
/// <typeparam name="TCanvas">The canvas pixel type: Rgba8, or Rgba16 for 16-bit APNG.</typeparam>
internal sealed class AnimationCanvas<TCanvas> : IDisposable
    where TCanvas : unmanaged, IPixelType<TCanvas>
{
    private readonly IPixelMemorySource _memory;
    private PixelPlane<TCanvas>? _saved;
    private Rect _bounds;
    private FrameDisposal _disposal;

    internal AnimationCanvas(int width, int height, IPixelMemorySource memory)
    {
        _memory = memory;
        Width = width;
        Height = height;
        Plane = PixelPlane.Allocate<TCanvas>(width, height, memory, ClearMode.Zero);
    }

    internal int Width { get; }

    internal int Height { get; }

    internal PixelPlane<TCanvas> Plane { get; }

    /// <summary>Starts a frame in <paramref name="bounds"/> (inside the canvas), saving its area first if it is to be restored.</summary>
    internal void Begin(Rect bounds, FrameDisposal disposal)
    {
        _bounds = bounds;
        _disposal = disposal;
        if (disposal == FrameDisposal.RestorePrevious && bounds.Width > 0 && bounds.Height > 0)
        {
            _saved ??= PixelPlane.Allocate<TCanvas>(Width, Height, _memory);
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                Plane.Row(y).Slice(bounds.X, bounds.Width).CopyTo(_saved.Row(y).Slice(bounds.X, bounds.Width));
            }
        }
    }

    /// <summary>Returns canvas row <paramref name="y"/>.</summary>
    internal Span<TCanvas> Row(int y) => Plane.Row(y);

    /// <summary>Copies the whole canvas into <paramref name="frame"/>, converting each row.</summary>
    internal void CopyTo<TPixel>(PixelPlane<TPixel> frame)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < Height; y++)
        {
            PixelConvert.Convert<TCanvas, TPixel>(Plane.Row(y), frame.Row(y));
        }
    }

    /// <summary>Disposes of the frame begun last, as its disposal method says.</summary>
    internal void End()
    {
        if (_bounds.Width <= 0 || _bounds.Height <= 0)
        {
            return;
        }

        for (int y = _bounds.Y; y < _bounds.Y + _bounds.Height; y++)
        {
            Span<TCanvas> row = Plane.Row(y).Slice(_bounds.X, _bounds.Width);
            switch (_disposal)
            {
                case FrameDisposal.ClearToBackground:
                    row.Clear();
                    break;
                case FrameDisposal.RestorePrevious:
                    _saved!.Row(y).Slice(_bounds.X, _bounds.Width).CopyTo(row);
                    break;
            }
        }
    }

    public void Dispose()
    {
        Plane.Dispose();
        _saved?.Dispose();
    }
}

/// <summary>Source-over blending for 16-bit APNG frames.</summary>
internal static class AnimationBlend
{
    /// <summary>Blends <paramref name="source"/> over <paramref name="destination"/>, rounding each result to nearest.</summary>
    internal static void SourceOver16(ReadOnlySpan<Rgba16> source, Span<Rgba16> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Rgba16 s = source[i];
            if (s.A == 0)
            {
                continue;
            }

            if (s.A == 65535)
            {
                destination[i] = s;
                continue;
            }

            Rgba16 d = destination[i];
            double sa = s.A / 65535.0;
            double wd = d.A / 65535.0 * (1 - sa);
            double outA = sa + wd;
            destination[i] = new Rgba16(
                (ushort)Math.Round(((s.R * sa) + (d.R * wd)) / outA),
                (ushort)Math.Round(((s.G * sa) + (d.G * wd)) / outA),
                (ushort)Math.Round(((s.B * sa) + (d.B * wd)) / outA),
                (ushort)Math.Round(outA * 65535));
        }
    }
}
