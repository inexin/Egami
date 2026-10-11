using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>
/// A tone step that reads the image first: the chain runs up to it, it measures the result (inside its scope, over every frame or
/// each frame alone), and a plain filter takes its place.
/// </summary>
internal abstract class MeasuredToneStep(bool perFrame) : IScopedMeasuredStep
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException("A measured step never runs itself.");
    }

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Measure(frames, MeasureScope.Whole(frames[0].Width, frames[0].Height), context, cancellationToken);
    }

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (!perFrame || frames.Count == 1)
        {
            List<float[]> owned = [];
            return new FilterOperation(Filter(frames, scope, context, owned, cancellationToken), owned);
        }

        return new PerFrameOperation([.. frames.Select(frame =>
        {
            List<float[]> owned = [];
            return new FilterOperation(Filter([frame], scope, context, owned, cancellationToken), owned);
        })]);
    }

    /// <summary>The filter the step becomes for these frames; tables it rents go in <paramref name="owned"/>, returned when the chain ends.</summary>
    protected abstract ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

internal sealed class AutoLevelStep(bool linked, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        return ToneMeasures.AutoLevel(frames, scope, linked, context, cancellationToken);
    }
}

internal sealed class ContrastStretchStep(float black, float white, bool linked, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        return ToneMeasures.ContrastStretch(frames, scope, black, white, linked, context, cancellationToken);
    }
}

internal sealed class AutoGammaStep(bool linked, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        return ToneMeasures.AutoGamma(frames, scope, linked, context, cancellationToken);
    }
}

internal sealed class AutoWhiteBalanceStep(WhiteBalanceMethod method, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        return ToneMeasures.WhiteBalance(frames, scope, method, context, cancellationToken);
    }
}

internal sealed class EqualizeStep(EqualizeMode mode, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        return ToneMeasures.Equalize(frames, scope, mode, context, owned, cancellationToken);
    }
}

internal sealed class AutoThresholdStep(ThresholdMethod method, ThresholdMode mode, Rgba8? below, Rgba8? above, bool perFrame) : MeasuredToneStep(perFrame)
{
    protected override ColorFilter Filter<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, List<float[]> owned, CancellationToken cancellationToken)
    {
        int bin = ToneMeasures.FindThresholdBin(frames, scope, method, mode, context, cancellationToken);
        double level = bin < 0 ? ThresholdStage.Quantum(0.5f) : ThresholdStage.Quantum(bin);
        return ColorFilter.ThresholdAt(level, mode, below ?? Rgba8.Black, above ?? Rgba8.White);
    }
}
