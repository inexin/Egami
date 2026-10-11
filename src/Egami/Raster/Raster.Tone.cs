using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Tone and color: Hald identity images, and the filters the measured tone steps would apply.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Creates the identity Hald CLUT image of <paramref name="level"/>: level³ × level³ pixels holding a (level²)³ table that
    /// changes nothing, red fastest, as ImageMagick's hald:8. Grade it in any editor, then load it back with <see cref="Lut3D.FromHald"/>.
    /// </summary>
    /// <param name="level">The level, from 2 to 16. Defaults to 8 (512 × 512, a 64³ table).</param>
    /// <returns>The image; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 2 to 16.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="hald"/>
    /// </example>
    public static Raster<Rgba8> HaldIdentity(int level = 8) => HaldIdentity<Rgba8>(level);

    /// <summary>Creates the identity Hald CLUT image of <paramref name="level"/> in <typeparamref name="TPixel"/>, such as Rgba16 for 16-bit grading.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="level">The level, from 2 to 16. Defaults to 8.</param>
    /// <returns>The image; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 2 to 16.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="hald"/>
    /// </example>
    public static Raster<TPixel> HaldIdentity<TPixel>(int level = 8)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 16);
        int size = level * level, side = size * level;
        Raster<TPixel> raster = Create<TPixel>(side, side);
        RgbaF32[] row = new RgbaF32[side];
        float step = 1f / (size - 1);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                (int r, int g, int b) = Lut3D.HaldIndex((y * side) + x, size);
                row[x] = new RgbaF32(r * step, g * step, b * step, 1);
            }

            PixelConvert.Convert<RgbaF32, TPixel>(row, raster.Row(y));
        }

        return raster;
    }
}
