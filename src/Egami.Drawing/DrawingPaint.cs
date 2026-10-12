using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;

namespace Egami.Drawing;

/// <summary>
/// A <see cref="Drawing"/> as a paint: a tiling pattern, the shape of PDF's and SVG's tiling patterns and Skia's picture shader.
/// The <see cref="Tile"/> rectangle of the drawing, in its own coordinates, is repeated by <see cref="TileX"/> and
/// <see cref="TileY"/> under <see cref="Paint.LocalMatrix"/> and the canvas transform. The raster backend renders one tile at the
/// device scale the paint is drawn at and keeps it with the drawing, so a pattern stays sharp when zoomed and is rendered once
/// for repeated fills at one scale. A tile is at most 4096 pixels a side.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="pattern"/>
/// </example>
public sealed record class DrawingPaint : Paint
{
    /// <summary>The largest rendered tile, in pixels a side.</summary>
    internal const int MaxTilePixels = 4096;

    private static readonly SamplingOptions TileSampling = new() { Filter = ResampleFilter.Bilinear };

    private readonly Drawing _drawing;
    private readonly TileMode _tileX;
    private readonly TileMode _tileY;
    private readonly RectF _tile;

    /// <summary>Initializes a new instance of the <see cref="DrawingPaint"/> class.</summary>
    /// <param name="drawing">The drawing.</param>
    /// <param name="tileX">What lies left and right of the tile.</param>
    /// <param name="tileY">What lies above and below it.</param>
    /// <param name="tile">The tile, in the drawing's coordinates; the drawing's bounds when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="drawing"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tile"/> is empty or not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tile mode is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="pattern"/>
    /// </example>
    public DrawingPaint(Drawing drawing, TileMode tileX = TileMode.Repeat, TileMode tileY = TileMode.Repeat, RectF? tile = null)
    {
        _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
        TileX = tileX;
        TileY = tileY;
        Tile = tile ?? drawing.Bounds;
    }

    /// <summary>Gets the drawing.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Drawing Drawing
    {
        get => _drawing;
        init => _drawing = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets what lies left and right of the tile.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TileMode TileX
    {
        get => _tileX;
        init => _tileX = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets what lies above and below the tile.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TileMode TileY
    {
        get => _tileY;
        init => _tileY = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets the tile, in the drawing's coordinates: the part of the drawing repeated, which may be larger than its bounds to space the copies.</summary>
    /// <exception cref="ArgumentException">The value is empty or not finite.</exception>
    public RectF Tile
    {
        get => _tile;
        init => _tile = value.IsFinite && !value.IsEmpty ? value : throw new ArgumentException("The tile must be finite and not empty.", nameof(value));
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace) => TileImage(toDevice)?.CreateShader(toDevice, colorSpace);

    /// <summary>The tile rendered at the device scale, as an image paint that places it where the pattern's tile lies.</summary>
    internal ImagePaint? TileImage(Matrix3x2 toDevice)
    {
        if (!TryInverse(toDevice, out _))
        {
            return null;
        }

        Matrix3x2 m = LocalMatrix * toDevice;
        float sx = new Vector2(m.M11, m.M12).Length(), sy = new Vector2(m.M21, m.M22).Length();
        int w = (int)Math.Clamp(Math.Ceiling(_tile.Width * (double)sx), 1, MaxTilePixels), h = (int)Math.Clamp(Math.Ceiling(_tile.Height * (double)sy), 1, MaxTilePixels);
        Raster image = _drawing.Cached(_tile, w, h);
        return new ImagePaint(image, _tileX, _tileY, TileSampling)
        {
            LocalMatrix = Matrix3x2.CreateScale(_tile.Width / w, _tile.Height / h) * Matrix3x2.CreateTranslation(_tile.X, _tile.Y) * LocalMatrix,
        };
    }
}
