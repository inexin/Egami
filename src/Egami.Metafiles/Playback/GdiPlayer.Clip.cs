using System.Numerics;

using Egami.Drawing;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Clipping: GDI clips can be replaced, united or xored, which a canvas cannot undo, so the player keeps the region as a path in
/// device space and redoes one canvas clip whenever it changes (design, stage B). Region records draw regions.
/// </summary>
internal sealed partial class GdiPlayer
{
    /// <summary>The whole device, for operations that start from "no clip".</summary>
    private static Path Everything => Path.Rect(new RectF(-1e6f, -1e6f, 2e6f, 2e6f));

    /// <summary>Puts the current region on the canvas: back to the unclipped save, then one clip.</summary>
    private void ApplyClip()
    {
        _canvas.RestoreToCount(_clipToken);
        _clipToken = _canvas.Save();
        if (_dc.Clip is Path clip)
        {
            _canvas.ClipPath(clip, ClipOp.Intersect, antialias: !_aliased);
        }
    }

    /// <summary>
    /// Combines a device-space region with the clip: 1 RGN_AND, 2 RGN_OR, 3 RGN_XOR, 4 RGN_DIFF, 5 RGN_COPY. A null region with
    /// RGN_COPY removes the clip.
    /// </summary>
    private void CombineClip(Path? region, int mode)
    {
        if (region is not null && !Sane(region))
        {
            Insane();
            return;
        }

        Path current = _dc.Clip ?? Everything;
        Path? result = mode switch
        {
            1 when region is not null => current.Intersect(region),
            2 when region is not null => _dc.Clip is null ? null : current.Union(region),
            3 when region is not null => current.Xor(region),
            4 when region is not null => current.Difference(region),
            5 => region,
            _ => _dc.Clip,
        };
        _dc.Clip = result;
        ApplyClip();
    }

    /// <summary>A logical rectangle as a device region: whole pixels, the right and bottom edges excluded.</summary>
    private Path ClipRect(float l, float t, float r, float b)
    {
        if (AxisAligned)
        {
            Vector2 a = Dev(new Vector2(l, t)), c = Dev(new Vector2(r, b));
            return Path.Rect(new RectF(MathF.Min(a.X, c.X), MathF.Min(a.Y, c.Y), MathF.Abs(c.X - a.X), MathF.Abs(c.Y - a.Y)));
        }

        return Path.Polygon([Dev(new(l, t)), Dev(new(r, t)), Dev(new(r, b)), Dev(new(l, b))]);
    }

    private void IntersectClipRect(float l, float t, float r, float b) => CombineClip(ClipRect(l, t, r, b), 1);

    private void ExcludeClipRect(float l, float t, float r, float b) => CombineClip(ClipRect(l, t, r, b), 4);

    private void OffsetClip(Vector2 logical)
    {
        if (_dc.Clip is Path clip)
        {
            Vector2 d = Vector2.TransformNormal(logical, Full);
            _dc.Clip = clip.Transform(Matrix3x2.CreateTranslation(_aliased ? Snap(d) : d));
            ApplyClip();
        }
    }

    /// <summary>SelectClipPath: the closed path combined with the clip by the mode.</summary>
    private void SelectClipPath(int mode)
    {
        if (TakePath() is Path path)
        {
            CombineClip(path, mode);
        }
    }

    /// <summary>A WMF region object selected as the clip (it replaces the clip).</summary>
    private void SelectClipRegion(Path logical) => CombineClip(logical.Transform(Full), 5);

    /// <summary>An EMF RegionData (RGNDATAHEADER and rectangles) in reference device units, as a device-space path; null when empty or broken.</summary>
    private Path? RegionData(ReadOnlySpan<byte> data, Matrix3x2 transform, bool clip = false)
    {
        if (data.Length < 32)
        {
            return null;
        }

        int count = (int)Math.Min(Reading.MetafileReader.U32(data, 8), (uint)((data.Length - 32) / 16));
        PathBuilder builder = new();
        for (int i = 0; i < count; i++)
        {
            int at = 32 + (i * 16);
            int l = Reading.MetafileReader.I32(data, at), t = Reading.MetafileReader.I32(data, at + 4);
            int r = Reading.MetafileReader.I32(data, at + 8), b = Reading.MetafileReader.I32(data, at + 12);
            if (r > l && b > t)
            {
                builder.AddRect(new RectF(l, t, r - l, b - t));
            }
        }

        if (builder.IsEmpty)
        {
            return null;
        }

        Path rects = builder.ToPath(FillRule.NonZero).Transform(transform);
        return _aliased ? SnapPath(rects, clip ? Vector2.One : Vector2.Abs(new Vector2(transform.M11, transform.M22))) : rects;
    }

    /// <summary>
    /// Snaps a path of region rectangles to whole pixels: a scaled region edge goes to the first pixel boundary at or after the
    /// edge moved by (1 - scale) / 2, as rectangles do when a region is drawn; clip regions round to the nearest boundary
    /// (scale 1 here). Both measured against PlayEnhMetaFile.
    /// </summary>
    private static Path SnapPath(Path path, Vector2 scale)
    {
        Vector2 o = (Vector2.One - scale) / 2;
        Vector2 Snap(Vector2 p) => scale == Vector2.One ? GdiPlayer.Snap(p) : new(MathF.Ceiling(p.X + o.X - 1e-4f), MathF.Ceiling(p.Y + o.Y - 1e-4f));

        PathBuilder builder = new();
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    builder.MoveTo(Snap(points[p++]));
                    break;
                case PathVerb.Line:
                    builder.LineTo(Snap(points[p++]));
                    break;
                case PathVerb.Close:
                    builder.Close();
                    break;
                default:
                    return path;
            }
        }

        return builder.ToPath(path.FillRule);
    }

    /// <summary>The transform region rectangles use: EMF clip regions are in device units, so only the device mapping applies.</summary>
    private Matrix3x2 DeviceRegion => _device;

    private void FillRegion(Path? region, GdiBrush? brush)
    {
        if (region is null || brush is null)
        {
            return;
        }

        GdiBrush saved = _dc.Brush;
        _dc.Brush = brush;
        if (BrushPaint() is Paint paint)
        {
            _canvas.FillPath(region, paint);
        }

        _dc.Brush = saved;
    }

    /// <summary>FrameRgn: the brush along the region's border, a given width and height inside it.</summary>
    private void FrameRegion(Path? region, GdiBrush? brush, Vector2 size)
    {
        if (region is null || brush is null)
        {
            return;
        }

        Vector2 d = Vector2.Abs(Vector2.TransformNormal(size, Full));
        FillRegion(region.Difference(Shrink(region, d)), brush);
    }

    /// <summary>The part of a region at least <paramref name="d"/> inside it: the region intersected with itself moved each way.</summary>
    private static Path Shrink(Path region, Vector2 d) =>
        region.Intersect(region.Transform(Matrix3x2.CreateTranslation(d.X, 0)))
            .Intersect(region.Transform(Matrix3x2.CreateTranslation(-d.X, 0)))
            .Intersect(region.Transform(Matrix3x2.CreateTranslation(0, d.Y)))
            .Intersect(region.Transform(Matrix3x2.CreateTranslation(0, -d.Y)));

    private void InvertRegion(Path? region)
    {
        if (region is not null)
        {
            _canvas.FillPath(region, new SolidPaint(new Pixels.Rgba8(255, 255, 255, 255)) { Antialias = !_aliased, BlendMode = Operations.BlendMode.Difference });
        }
    }

    private void PaintRegion(Path? region) => FillRegion(region, _dc.Brush);
}
