using Egami.Formats;

namespace Egami.Metafiles;

/// <summary>How <see cref="MetafileFormat"/> draws a metafile for <c>Raster.Load</c> and sizes it for <c>Raster.Probe</c>.</summary>
public sealed record MetafileDecodeOptions : ReaderOptions
{
    /// <summary>Gets the resolution the picture's size is drawn at; 96 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Dpi
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Dpi must be positive and finite.");
    } = 96;
}
