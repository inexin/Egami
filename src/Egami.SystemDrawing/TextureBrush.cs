using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Operations;
using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>A brush that tiles an image, from the world origin, through its transform.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed partial class TextureBrush : Brush
{
    private readonly Bitmap _image;
    private Raster<Rgba8>? _pixels;
    private WrapMode _wrap;

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class, tiled.</summary>
    /// <param name="bitmap">The image.</param>
    public TextureBrush(Image bitmap)
        : this(bitmap, WrapMode.Tile)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class.</summary>
    /// <param name="image">The image.</param>
    /// <param name="wrapMode">How it repeats.</param>
    public TextureBrush(Image image, WrapMode wrapMode)
        : this(image, wrapMode, null, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="wrapMode">How it repeats.</param>
    /// <param name="dstRect">The part of the image.</param>
    public TextureBrush(Image image, WrapMode wrapMode, RectangleF dstRect)
        : this(image, wrapMode, dstRect, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="wrapMode">How it repeats.</param>
    /// <param name="dstRect">The part of the image.</param>
    public TextureBrush(Image image, WrapMode wrapMode, Rectangle dstRect)
        : this(image, wrapMode, (RectangleF)dstRect, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image, tiled.</summary>
    /// <param name="image">The image.</param>
    /// <param name="dstRect">The part of the image.</param>
    public TextureBrush(Image image, RectangleF dstRect)
        : this(image, WrapMode.Tile, dstRect, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image, tiled, its colors adjusted.</summary>
    /// <param name="image">The image.</param>
    /// <param name="dstRect">The part of the image.</param>
    /// <param name="imageAttr">Color adjustments, or null.</param>
    public TextureBrush(Image image, RectangleF dstRect, ImageAttributes? imageAttr)
        : this(image, WrapMode.Tile, dstRect, imageAttr)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image, tiled.</summary>
    /// <param name="image">The image.</param>
    /// <param name="dstRect">The part of the image.</param>
    public TextureBrush(Image image, Rectangle dstRect)
        : this(image, WrapMode.Tile, (RectangleF)dstRect, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image, tiled, its colors adjusted.</summary>
    /// <param name="image">The image.</param>
    /// <param name="dstRect">The part of the image.</param>
    /// <param name="imageAttr">Color adjustments, or null.</param>
    public TextureBrush(Image image, Rectangle dstRect, ImageAttributes? imageAttr)
        : this(image, WrapMode.Tile, (RectangleF)dstRect, imageAttr)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextureBrush"/> class from part of an image.</summary>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The part reaches outside the image, as in GDI+.</exception>
    private TextureBrush(Image image, WrapMode wrapMode, RectangleF? part, ImageAttributes? attributes)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not Bitmap bitmap)
        {
            throw new NotSupportedException("Egami.SystemDrawing textures are bitmaps.");
        }

        WrapMode = wrapMode;
        Rectangle area = new(0, 0, bitmap.Width, bitmap.Height);
        if (part is RectangleF r)
        {
            area = System.Drawing.Rectangle.Round(r);
            if (area.X < 0 || area.Y < 0 || area.Right > bitmap.Width || area.Bottom > bitmap.Height || area.Width <= 0 || area.Height <= 0)
            {
                throw GdiErrors.Generic();
            }
        }

        _image = bitmap.Clone(area, PixelFormat.Format32bppArgb);
        if (attributes is not null)
        {
            // With attributes, GDI+ takes the wrap mode from them: drawn once (clamped) unless they set one.
            WrapMode = attributes.WrapSet ? (WrapMode)attributes.State.WrapMode : WrapMode.Clamp;
            using Raster<Rgba8> raw = _image.ToRgba8();
            _pixels = attributes.State.Apply(raw, GdiPlusImageAttributes.TypeBrush);
            if (ReferenceEquals(_pixels, raw))
            {
                _pixels = raw.Copy();
            }
        }
    }

    private TextureBrush(Bitmap image, Raster<Rgba8>? pixels)
    {
        _image = (Bitmap)image.Clone();
        _pixels = pixels?.Copy();
    }

    /// <summary>Gets a copy of the image.</summary>
    public Image Image => (Image)_image.Clone();

    /// <summary>Gets or sets how the image repeats; <see cref="WrapMode.Clamp"/> draws it once.</summary>
    public WrapMode WrapMode
    {
        get => _wrap;
        set => _wrap = value is >= WrapMode.Tile and <= WrapMode.Clamp ? value : throw new System.ComponentModel.InvalidEnumArgumentException(nameof(value), (int)value, typeof(WrapMode));
    }

    /// <inheritdoc/>
    public override object Clone() => new TextureBrush(_image, _pixels) { WrapMode = WrapMode, Brushspace = Brushspace };

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pixels?.Dispose();
            _pixels = null;
        }

        base.Dispose(disposing);
    }

    internal override BrushFill Fill(Graphics graphics)
    {
        _pixels ??= _image.ToRgba8();
        (TileMode tx, TileMode ty) = GdiPlusBrushes.Wrap((int)WrapMode);
        // As for images, without the half-pixel offset GDI+ puts the texture's pixel centers on whole coordinates.
        Matrix3x2 centers = graphics.Offset ? Matrix3x2.Identity : Matrix3x2.CreateTranslation(-0.5f, -0.5f);
        return new(new ImagePaint(_pixels, tx, ty, new SamplingOptions { Filter = graphics.Filter }) { LocalMatrix = centers * Brushspace * graphics.ShapeTransform }, null);
    }
}
