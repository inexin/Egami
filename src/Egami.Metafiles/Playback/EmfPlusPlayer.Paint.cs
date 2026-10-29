using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>What a fill draws: a paint, or for a path gradient a Gouraud mesh clipped to the shape.</summary>
internal readonly record struct PlusFill(Paint? Paint, Mesh? Mesh);

/// <summary>EMF+ brushes and pens as Egami paints and strokes.</summary>
internal sealed partial class EmfPlusPlayer
{
    /// <summary>The fill for a record's brush field: an ARGB color when the S flag (0x8000) is set, else a brush object's index.</summary>
    private PlusFill BrushPaint(ushort flags, uint value) =>
        (flags & ColorBrush) != 0 ? new PlusFill(Solid(PlusObjectReader.Argb(value)), null)
            : _objects[value & 0xFF] is PlusBrush brush ? BrushPaint(brush) : default;

    private SolidPaint Solid(Rgba8 color) => new(color) { Antialias = Smooth, BlendMode = Blend };

    /// <summary>CompositingModeSourceCopy (1) replaces what is below; SourceOver (0) blends.</summary>
    private BlendMode Blend => _state.Compositing == 1 ? BlendMode.Source : BlendMode.SourceOver;

    private PlusFill BrushPaint(PlusBrush brush)
    {
        Matrix3x2 local = brush.Transform * Full;
        switch (brush.Type)
        {
            case PlusBrush.SolidType:
                return new PlusFill(Solid(brush.Color), null);

            case PlusBrush.HatchType:
                {
                    // The tile is 8 reference pixels, as for GDI hatches (question 11), from the device origin.
                    Picture tile = GdiPlusBrushes.HatchTile(brush.HatchStyle, brush.Color, brush.BackColor);
                    Matrix3x2 pixels = Matrix3x2.CreateScale(GdiPlusBrushes.HatchScale(_gdi.Device));
                    return new PlusFill(new PicturePaint(tile, TileMode.Repeat, TileMode.Repeat, new RectF(0, 0, 8, 8)) { LocalMatrix = pixels, Antialias = Smooth, BlendMode = Blend }, null);
                }

            case PlusBrush.TextureType when brush.Texture?.Bitmap is Raster<Rgba8> image:
                {
                    // Like images, a texture is placed without the pixel offset.
                    (TileMode tx, TileMode ty) = GdiPlusBrushes.Wrap(brush.WrapMode);
                    return new PlusFill(new ImagePaint(image, tx, ty, Sampling) { LocalMatrix = brush.Transform * Unshifted, Antialias = Smooth, BlendMode = Blend }, null);
                }

            case PlusBrush.LinearGradientType:
                {
                    RectF r = brush.Rect;
                    GradientStop[] stops = brush.Stops ?? [new GradientStop(0, brush.Color), new GradientStop(1, brush.EndColor)];

                    (TileMode tile, _) = GdiPlusBrushes.Wrap(brush.WrapMode);
                    return new PlusFill(
                        new LinearGradient(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), stops)
                        {
                            LocalMatrix = local,
                            Tile = tile,
                            Antialias = Smooth,
                            BlendMode = Blend,
                            Interpolation = brush.GammaCorrected ? GradientInterpolation.GdiPlusGamma : GradientInterpolation.Encoded,
                        },
                        null);
                }

            case PlusBrush.PathGradientType:
                // A path gradient is a Gouraud mesh in device space (design, stage C).
                return new PlusFill(null, GdiPlusBrushes.PathGradient(brush.BoundaryPoints ?? GdiPlusBrushes.Flatten(brush.Boundary), brush.Center, brush.Color, brush.Surround, brush.Stops, local));

            default:
                return new PlusFill(Solid(brush.Color), null);
        }
    }

    /// <summary>
    /// The filter for GDI+'s interpolation mode, as measured in 2.7.6: NearestNeighbor (5), Bicubic (4), HighQualityBilinear (6),
    /// HighQualityBicubic (7) and HighQuality (2); Default, LowQuality and Bilinear take GDI+'s bilinear.
    /// </summary>
    private SamplingOptions Sampling => Samplings[_state.Interpolation is >= 0 and <= 7 ? _state.Interpolation : 0];

    private static readonly SamplingOptions[] Samplings =
    [
        new() { Filter = ResampleFilter.GdiPlusBilinear },
        new() { Filter = ResampleFilter.GdiPlusBilinear },
        new() { Filter = ResampleFilter.GdiPlusHighQualityBicubic },
        new() { Filter = ResampleFilter.GdiPlusBilinear },
        new() { Filter = ResampleFilter.GdiPlusBicubic },
        new() { Filter = ResampleFilter.Nearest },
        new() { Filter = ResampleFilter.GdiPlusHighQualityBilinear },
        new() { Filter = ResampleFilter.GdiPlusHighQualityBicubic },
    ];

    // ---- Pens ----

    /// <summary>
    /// Strokes a world-space path with a pen: its width in world units (at least one device pixel), caps, join, miter limit,
    /// dashes in multiples of the width, inset alignment for closed figures, compound lines as strips, and anchor and custom caps
    /// drawn at the ends. The stroke is computed in world space and filled in device space, so it follows the world transform.
    /// </summary>
    private void Stroke(Path world, PlusPen? plus)
    {
        if (plus is null || world.IsEmpty)
        {
            return;
        }

        if (!GdiPlayer.Sane(world.Transform(Full)))
        {
            WarnOnce("Metafile.BadRecord", "A shape lies more than a million pixels away; it is not drawn.", "insane");
            return;
        }

        GdiPlusPen pen = plus.Pen;
        Matrix3x2 full = Full;
        float scale = MathF.Sqrt(MathF.Abs((full.M11 * full.M22) - (full.M12 * full.M21)));
        float unit = pen.Unit is 0 or 2 or 1 ? 1 : UnitScale(pen.Unit) / UnitScale(_state.PageUnit) / _state.PageScale;
        float width = GdiPlusPens.DeviceWidth(pen.Width * unit * MathF.Sqrt(MathF.Abs((pen.Transform.M11 * pen.Transform.M22) - (pen.Transform.M12 * pen.Transform.M21))), scale);
        GdiPlusPenOutline outline = GdiPlusPens.Outline(world, pen, width);

        PlusFill fill = BrushPaint(plus.Brush);
        Paint paint = fill.Paint ?? Solid(plus.Brush.Color);
        int count = _canvas.Save();
        try
        {
            if (outline.Clip is Path clip)
            {
                _canvas.ClipPath(clip.Transform(full), ClipOp.Intersect, antialias: Smooth);
            }

            _canvas.FillPath(outline.Outline.WithFillRule(FillRule.NonZero).Transform(full), paint);
            foreach (Path cap in outline.Caps)
            {
                _canvas.FillPath(cap.Transform(full), paint);
            }
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
    }
}
