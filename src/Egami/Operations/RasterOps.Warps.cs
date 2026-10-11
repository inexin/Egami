using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations;

/// <content>Warps, displacement, offset and tiling (Phase 2.5.2, stage B).</content>
public sealed partial class RasterOps
{
    /// <summary>
    /// Runs a warp: each output pixel shows the source point the warp maps it to, sampled as <see cref="Transform(Homography, TransformOptions?)"/>
    /// samples, so where the warp shrinks the image its fine detail fades instead of aliasing. <see cref="Warps"/> holds the
    /// built-in ones; <see cref="Operations.Warp.FromFunction"/> or an own <see cref="IWarp"/> make others.
    /// </summary>
    /// <param name="warp">The warp.</param>
    /// <param name="options">The filter, color space, edges and background, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="warp"/> is <see langword="null"/>.</exception>
    /// <remarks>Warps that keep the size run inside <c>Within</c>; those that change it (Polar, DePolar, Arc, Wave) throw there.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="warps"/>
    /// </example>
    public RasterOps Warp(IWarp warp, SamplingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(warp);
        return Add(new WarpOperation(warp, options ?? new SamplingOptions()));
    }

    /// <summary>
    /// Moves each pixel by a displacement map, as Skia's DisplacementMap and ImageMagick's -displace do: pixel (x, y) shows the
    /// source at (x + scaleX × (mx − 0.5), y + scaleY × (my − 0.5)), with mx and my the map's chosen channels from 0 to 1, so
    /// mid-gray moves nothing. The map's first frame serves every frame.
    /// </summary>
    /// <param name="map">The displacement map, the image's size (Resize it first if it is not).</param>
    /// <param name="scaleX">The horizontal move, in pixels, for a channel going from 0 to 1.</param>
    /// <param name="scaleY">The vertical move, in pixels, for a channel going from 0 to 1.</param>
    /// <param name="xChannel">The map channel that moves pixels horizontally.</param>
    /// <param name="yChannel">The map channel that moves pixels vertically.</param>
    /// <param name="options">The filter, color space, edges and background, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A scale is not finite or a channel is not defined.</exception>
    /// <exception cref="ArgumentException">The map's size differs from the image's, when the chain is planned.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="displace"/>
    /// </example>
    public RasterOps Displace(Raster map, float scaleX, float scaleY, ColorChannel xChannel = ColorChannel.Red, ColorChannel yChannel = ColorChannel.Green, SamplingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!float.IsFinite(scaleX))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX), scaleX, "The scale must be finite.");
        }

        if (!float.IsFinite(scaleY))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleY), scaleY, "The scale must be finite.");
        }

        if (!Enum.IsDefined(xChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(xChannel), xChannel, "The channel is not defined.");
        }

        if (!Enum.IsDefined(yChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(yChannel), yChannel, "The channel is not defined.");
        }

        return Add(new WarpOperation(new DisplaceWarp(map, scaleX, scaleY, xChannel, yChannel), options ?? new SamplingOptions()));
    }

    /// <summary>
    /// Moves the image by whole pixels, right by <paramref name="dx"/> and down by <paramref name="dy"/>, with no resampling. What
    /// leaves one side comes in at the other with <see cref="EdgeMode.Wrap"/> (the default, ImageMagick's -roll); with
    /// <see cref="EdgeMode.Transparent"/> the uncovered strip is <paramref name="background"/>, as Skia's Offset filter leaves it.
    /// </summary>
    /// <param name="dx">The horizontal move, in pixels.</param>
    /// <param name="dy">The vertical move, in pixels.</param>
    /// <param name="edges">What fills the uncovered strips.</param>
    /// <param name="background">The color of uncovered strips under <see cref="EdgeMode.Transparent"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edges"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="offset-tile"/>
    /// </example>
    public RasterOps Offset(int dx, int dy, EdgeMode edges = EdgeMode.Wrap, Rgba8 background = default)
    {
        if (!Enum.IsDefined(edges))
        {
            throw new ArgumentOutOfRangeException(nameof(edges), edges, "The edge mode is not defined.");
        }

        return dx == 0 && dy == 0 ? this : Add(new OffsetOperation(dx, dy, edges, background));
    }

    /// <summary>
    /// Makes a new <paramref name="width"/> × <paramref name="height"/> canvas filled by repeating the image, or the
    /// <paramref name="source"/> rectangle of it, with one copy's top-left corner at <paramref name="origin"/>, as Skia's Tile
    /// filter and ImageMagick's tile: fill do. Pixels are copied exactly.
    /// </summary>
    /// <param name="width">The canvas width, at least 1.</param>
    /// <param name="height">The canvas height, at least 1.</param>
    /// <param name="origin">Where one copy's top-left corner goes; copies repeat from there in every direction.</param>
    /// <param name="source">The rectangle to repeat, inside the image, or <see langword="null"/> (the default) for the whole image.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, the rectangle is empty, or it leaves the image when the chain runs.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="offset-tile"/>
    /// </example>
    public RasterOps Tile(int width, int height, Point origin = default, Rect? source = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (source is Rect { IsEmpty: true } empty)
        {
            throw new ArgumentOutOfRangeException(nameof(source), empty, "The source rectangle is empty.");
        }

        return Add(new TileOperation(width, height, origin, source));
    }
}
