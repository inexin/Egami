using Egami.Operations;
using Egami.Operations.Tone;
using Egami.Pixels;

namespace Egami;

/// <summary>The filters the measured tone steps would apply, measured without changing anything.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns the filter <see cref="RasterOps.AutoLevel"/> would apply, measured on every frame: apply it to other images to give
    /// them the same correction.
    /// </summary>
    /// <param name="linked">Whether the channels share one black and one white point (the default) or each gets its own.</param>
    /// <returns>The filter, a level per channel.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="measure"/>
    /// </example>
    public ColorFilter MeasureAutoLevel(bool linked = true)
    {
        return Accept(new ToneVisitor(ToneKind.AutoLevel, linked));
    }

    /// <summary>Returns the filter <see cref="RasterOps.ContrastStretch"/> would apply, measured on every frame.</summary>
    /// <param name="black">The fraction of pixels that becomes black, from 0 to 1.</param>
    /// <param name="white">The fraction that becomes white, from 0 to 1; with <paramref name="black"/> less than 1.</param>
    /// <param name="linked">Whether the channels share one pair of points (the default) or each gets its own.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A fraction is outside 0..1, or the two add up to 1 or more.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="measure"/>
    /// </example>
    public ColorFilter MeasureContrastStretch(float black, float white, bool linked = true)
    {
        ColorFilter.CheckUnit(black);
        ColorFilter.CheckUnit(white);
        if (!(black + white < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(white), white, "The two fractions must add up to less than 1.");
        }

        return Accept(new ToneVisitor(ToneKind.ContrastStretch, linked, black, white));
    }

    /// <summary>Returns the filter <see cref="RasterOps.AutoGamma"/> would apply, measured on every frame.</summary>
    /// <param name="linked">Whether the channels share one gamma (the default) or each gets its own.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="measure"/>
    /// </example>
    public ColorFilter MeasureAutoGamma(bool linked = true)
    {
        return Accept(new ToneVisitor(ToneKind.AutoGamma, linked));
    }

    /// <summary>
    /// Returns the filter <see cref="RasterOps.AutoWhiteBalance"/> would apply, measured on every frame: measure a reference shot once
    /// and apply the result to the whole shoot.
    /// </summary>
    /// <param name="method">How the cast is found. Defaults to <see cref="WhiteBalanceMethod.GrayWorld"/>.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="measure"/>
    /// </example>
    public ColorFilter MeasureWhiteBalance(WhiteBalanceMethod method = WhiteBalanceMethod.GrayWorld)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "The method is not defined.");
        }

        return Accept(new ToneVisitor(ToneKind.WhiteBalance, Option: (int)method));
    }

    /// <summary>Returns the filter <see cref="RasterOps.Equalize"/> would apply, measured on every frame.</summary>
    /// <param name="mode">What is equalized. Defaults to <see cref="EqualizeMode.Luminance"/>.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="measure"/>
    /// </example>
    public ColorFilter MeasureEqualize(EqualizeMode mode = EqualizeMode.Luminance)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is not defined.");
        }

        return Accept(new ToneVisitor(ToneKind.Equalize, Option: (int)mode));
    }

    /// <summary>
    /// Returns the level <see cref="RasterOps.AutoThreshold"/> would use, from 0 to 1, found by <paramref name="method"/> in a 256-bin
    /// histogram of every frame, as ImageMagick's -auto-threshold finds it; 0.5 for an image of one value.
    /// </summary>
    /// <param name="method">How the level is found. Defaults to <see cref="ThresholdMethod.Otsu"/>.</param>
    /// <param name="mode">What is measured; <see cref="ThresholdMode.Channels"/> measures red, green and blue together. Defaults to <see cref="ThresholdMode.Luminance"/>.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto-threshold"/>
    /// </example>
    public float FindThreshold(ThresholdMethod method = ThresholdMethod.Otsu, ThresholdMode mode = ThresholdMode.Luminance)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "The method is not defined.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is not defined.");
        }

        return Accept(new ThresholdVisitor(method, mode));
    }

    private enum ToneKind
    {
        AutoLevel,
        ContrastStretch,
        AutoGamma,
        WhiteBalance,
        Equalize,
    }

    private sealed record ToneVisitor(ToneKind Kind, bool Linked = true, float Black = 0, float White = 0, int Option = 0) : IRasterVisitor<ColorFilter>
    {
        public ColorFilter Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            PixelPlane<TPixel>[] frames = [.. raster.Frames.Select(f => f.Plane)];
            MeasureScope scope = MeasureScope.Whole(raster.Width, raster.Height);
            CancellationToken none = CancellationToken.None;
            return Kind switch
            {
                ToneKind.AutoLevel => ToneMeasures.AutoLevel(frames, scope, Linked, raster.Context, none),
                ToneKind.ContrastStretch => ToneMeasures.ContrastStretch(frames, scope, Black, White, Linked, raster.Context, none),
                ToneKind.AutoGamma => ToneMeasures.AutoGamma(frames, scope, Linked, raster.Context, none),
                ToneKind.WhiteBalance => ToneMeasures.WhiteBalance(frames, scope, (WhiteBalanceMethod)Option, raster.Context, none),
                _ => ToneMeasures.Equalize(frames, scope, (EqualizeMode)Option, raster.Context, null, none),
            };
        }
    }

    private sealed class ThresholdVisitor(ThresholdMethod method, ThresholdMode mode) : IRasterVisitor<float>
    {
        public float Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            PixelPlane<TPixel>[] frames = [.. raster.Frames.Select(f => f.Plane)];
            return ToneMeasures.FindThreshold(frames, MeasureScope.Whole(raster.Width, raster.Height), method, mode, raster.Context, CancellationToken.None);
        }
    }
}
