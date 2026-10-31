namespace Egami.Formats.Jpeg;

/// <summary>
/// How a lossless JPEG (T.81 annex H; SOF3, or SOF11 with <see cref="JpegWriterOptions.Arithmetic"/>) is written: no DCT and no
/// quantization, each sample predicted from its neighbors and the difference coded, so the samples come back exactly. Set as
/// <see cref="JpegWriterOptions.Lossless"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-lossless"/>
/// </example>
public sealed class JpegLosslessOptions
{
    private readonly int? _predictor;
    private readonly int _pointTransform;
    private readonly int? _precision;

    /// <summary>
    /// Gets the predictor, 1 to 7 as T.81 table H.1 numbers them (1 the sample to the left, 2 above, 3 above left, 4 to 7
    /// combinations), or <see langword="null"/> (the default) to pick the one that codes the image smallest, estimated on rows
    /// spread through it; the choice is the same for any thread count.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 7.</exception>
    public int? Predictor
    {
        get => _predictor;
        init
        {
            if (value is < 1 or > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The lossless JPEG predictor is 1 to 7.");
            }

            _predictor = value;
        }
    }

    /// <summary>
    /// Gets the point transform: the number of low bits dropped from every sample before it is coded, 0 (the default, lossless) to
    /// 15. Above 0 the file is smaller but no longer lossless, and the save reports it as a warning. It must be below the precision.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 15.</exception>
    public int PointTransform
    {
        get => _pointTransform;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 15);
            _pointTransform = value;
        }
    }

    /// <summary>
    /// Gets the bits per sample, 2 to 16, or <see langword="null"/> (the default) for the pixel type's: 8 for 8-bit types, 16 for
    /// 16-bit and floating-point ones. Fewer bits than the pixel type holds round the samples, and the save reports it as a warning.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 2 to 16.</exception>
    public int? Precision
    {
        get => _precision;
        init
        {
            if (value is < 2 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Lossless JPEG precision is 2 to 16 bits.");
            }

            _precision = value;
        }
    }
}
