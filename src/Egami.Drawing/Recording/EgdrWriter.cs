using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Colors;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Recording;

/// <summary>The constants of the .egdr format, which docs/articles/formats/egdr.md specifies.</summary>
internal static class Egdr
{
    internal const ushort Major = 1;
    internal const ushort Minor = 1;

    /// <summary>The deepest nesting of pictures a file may hold, counting picture paints.</summary>
    internal const int MaxNesting = 64;

    /// <summary>The most calls a loaded picture may replay, nested pictures expanded.</summary>
    internal const long MaxExpandedCalls = 50_000_000;

    internal static ReadOnlySpan<byte> Signature => [0x89, (byte)'E', (byte)'G', (byte)'D', (byte)'R', 0x0D, 0x0A, 0x1A];

    internal static uint Tag(string type) => BinaryPrimitives.ReadUInt32LittleEndian([(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]]);

    internal static readonly uint Image = Tag("IMAG");
    internal static readonly uint PathChunk = Tag("PATH");
    internal static readonly uint DrawingChunk = Tag("DRAW");
    internal static readonly uint End = Tag("DEND");
    internal static readonly uint EncodedImageChunk = Tag("ENCI");

    /// <summary>
    /// What an ink canvas needs beyond the appearances in the DRAW chunk that follows it (minor version 1): entries keyed by a
    /// byte offset in that chunk's payload. Older readers skip it and draw the appearances.
    /// </summary>
    internal static readonly uint InksChunk = Tag("INKS");

    /// <summary>The kinds of entry in an INKS chunk.</summary>
    internal enum InkEntry : byte
    {
        /// <summary>The colour (16 bytes) at the offset is these inks.</summary>
        Color = 1,

        /// <summary>The paint whose kind byte is at the offset overprints so.</summary>
        Overprint = 2,

        /// <summary>The layer whose SaveLayer op is at the offset blends in this ICC profile.</summary>
        LayerSpace = 3,
    }

    internal enum Op : byte
    {
        Save = 1,
        Restore = 2,
        SaveLayer = 3,
        SetMatrix = 4,
        ClipRect = 5,
        ClipRoundRect = 6,
        ClipPath = 7,
        DrawPaint = 8,
        DrawRect = 9,
        DrawRoundRect = 10,
        DrawOval = 11,
        DrawPath = 12,
        DrawPoints = 13,
        DrawImage = 14,
        DrawLattice = 15,
        DrawAtlas = 16,
        DrawMesh = 17,
        DrawPatch = 18,
        DrawPicture = 19,
        DrawEncodedImage = 20,
        DrawAnnotation = 21,
        BeginMarkedContent = 22,
        EndMarkedContent = 23,
        DrawImageMask = 24,
    }

    internal enum PaintKind : byte
    {
        Solid = 1,
        Linear = 2,
        Radial = 3,
        Conical = 4,
        Sweep = 5,
        Image = 6,
        Noise = 7,
        Blend = 8,
        ColorMatrix = 9,
        Picture = 10,
    }
}

/// <summary>
/// Writes a picture as .egdr: every image, path and nested picture once, as its own chunk, in the order first used, then the
/// picture's calls referring to them by number.
/// </summary>
internal sealed class EgdrWriter
{
    private readonly Stream _stream;
    private readonly Dictionary<Raster, int> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Path, int> _paths = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EncodedImage, int> _encoded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Picture, int> _pictures = new(ReferenceEqualityComparer.Instance);

    private EgdrWriter(Stream stream)
    {
        _stream = stream;
    }

    internal static void Write(Picture picture, Stream stream)
    {
        EgdrWriter writer = new(stream);
        stream.Write(Egdr.Signature);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(version, Egdr.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(version[2..], Egdr.Minor);
        stream.Write(version);
        writer.DrawingIndex(picture);
        writer.Chunk(Egdr.End, []);
    }

    private void Chunk(uint type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        _stream.Write(header);
        _stream.Write(payload);
    }

    private int EncodedIndex(EncodedImage image)
    {
        if (!_encoded.TryGetValue(image, out int index))
        {
            Chunk(Egdr.EncodedImageChunk, image.Data.Span);
            index = _encoded.Count;
            _encoded[image] = index;
        }

        return index;
    }

    private int ImageIndex(Raster image)
    {
        if (!_images.TryGetValue(image, out int index))
        {
            using MemoryStream png = new();
            image.Save(png, "png");
            Chunk(Egdr.Image, png.GetBuffer().AsSpan(0, (int)png.Length));
            index = _images.Count;
            _images[image] = index;
        }

        return index;
    }

    private int PathIndex(Path path)
    {
        if (!_paths.TryGetValue(path, out int index))
        {
            Buffer b = new();
            b.U8((byte)path.FillRule);
            b.U32((uint)path.Verbs.Length);
            foreach (PathVerb v in path.Verbs)
            {
                b.U8((byte)v);
            }

            b.U32((uint)path.Points.Length);
            foreach (Vector2 p in path.Points)
            {
                b.Point(p);
            }

            Chunk(Egdr.PathChunk, b.Span);
            index = _paths.Count;
            _paths[path] = index;
        }

        return index;
    }

    private int DrawingIndex(Picture picture)
    {
        if (_pictures.TryGetValue(picture, out int index))
        {
            return index;
        }

        // Everything a call refers to is written before the picture's own chunk; the inks go just before it.
        Buffer b = new();
        b.Rect(picture.Bounds);
        ReadOnlySpan<DrawingCall> calls = picture.Calls;
        b.U32((uint)calls.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            try
            {
                WriteCall(b, calls[i]);
            }
            catch (NotSupportedException e)
            {
                throw new NotSupportedException($"Call {i} of the picture cannot be saved: {e.Message}", e);
            }
        }

        if (b.Marks is { Count: > 0 } marks)
        {
            Buffer inks = new();
            inks.U32((uint)marks.Count);
            foreach ((int offset, Egdr.InkEntry kind, byte[] data) in marks)
            {
                inks.U32((uint)offset);
                inks.U8((byte)kind);
                inks.U32((uint)data.Length);
                inks.Bytes(data);
            }

            Chunk(Egdr.InksChunk, inks.Span);
        }

        Chunk(Egdr.DrawingChunk, b.Span);
        index = _pictures.Count;
        _pictures[picture] = index;
        return index;
    }

    private static bool HasColorGlyphs(GlyphRun run)
    {
        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        if (!font.ColorGlyphs || (face.ColorFormats & ~ColorGlyphFormats.Svg) == 0)
        {
            return false;
        }

        foreach (ushort glyph in run.Glyphs)
        {
            if (face.HasColorGlyph(glyph))
            {
                return true;
            }
        }

        return false;
    }

    private void WriteCall(Buffer b, DrawingCall call)
    {
        switch (call)
        {
            case SaveCall:
                b.Op(Egdr.Op.Save);
                break;
            case RestoreCall:
                b.Op(Egdr.Op.Restore);
                break;
            case SaveLayerCall c:
                LayerOptions o = c.Options;
                if (o.Filter is not null || o.Backdrop is not null || o.Mask is not null)
                {
                    throw new NotSupportedException("a layer's filter, backdrop and soft mask are C# code, which a file cannot hold.");
                }

                if (o.ColorSpace is ColorProfile space)
                {
                    b.Mark(Egdr.InkEntry.LayerSpace, space.ToIcc());
                }

                b.Op(Egdr.Op.SaveLayer);

                b.F32(o.Opacity);
                b.U8((byte)o.BlendMode);
                b.U8((byte)((o.Isolated ? 1 : 0) | (o.Knockout ? 2 : 0)));
                b.Bool(o.Bounds is not null);
                if (o.Bounds is RectF bounds)
                {
                    b.Rect(bounds);
                }

                break;
            case SetMatrixCall c:
                b.Op(Egdr.Op.SetMatrix);
                b.Matrix(c.Matrix);
                break;
            case ClipRectCall c:
                b.Op(Egdr.Op.ClipRect);
                b.Rect(c.Rect);
                b.U8((byte)c.Op);
                b.Bool(c.Antialias);
                break;
            case ClipRoundRectCall c:
                b.Op(Egdr.Op.ClipRoundRect);
                b.RoundRect(c.RoundRect);
                b.U8((byte)c.Op);
                b.Bool(c.Antialias);
                break;
            case ClipPathCall c:
                {
                    int path = PathIndex(c.Path);
                    b.Op(Egdr.Op.ClipPath);
                    b.U32((uint)path);
                    b.U8((byte)c.Op);
                    b.Bool(c.Antialias);
                    break;
                }

            case DrawPaintCall c:
                {
                    Buffer paint = PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawPaint);
                    b.Append(paint);
                    break;
                }

            case DrawRectCall c:
                Shape(b, Egdr.Op.DrawRect, c.Stroke, c.Paint, s => s.Rect(c.Rect));
                break;
            case DrawRoundRectCall c:
                Shape(b, Egdr.Op.DrawRoundRect, c.Stroke, c.Paint, s => s.RoundRect(c.RoundRect));
                break;
            case DrawOvalCall c:
                Shape(b, Egdr.Op.DrawOval, c.Stroke, c.Paint, s => s.Rect(c.Oval));
                break;
            case DrawPathCall c:
                {
                    int path = PathIndex(c.Path);
                    Shape(b, Egdr.Op.DrawPath, c.Stroke, c.Paint, s => s.U32((uint)path));
                    break;
                }

            case DrawGlyphRunCall c:
                {
                    // Text is saved as what the default hook draws for it, since a file holds no fonts: its outlines, and color
                    // glyphs as the clips, fills and images they draw with, in a picture of their own.
                    if (HasColorGlyphs(c.Run))
                    {
                        var recorder = new PictureRecorder(RectF.FromEdges(-1e6f, -1e6f, 1e6f, 1e6f)) { ExpandText = true };
                        recorder.DrawGlyphRun(c.Run, c.Origin, c.Paint);
                        WriteCall(b, new DrawPictureCall(recorder.Finish(), Matrix3x2.Identity, null, false));
                        break;
                    }

                    int path = PathIndex(c.Run.ToPath(c.Origin));
                    Shape(b, Egdr.Op.DrawPath, null, c.Paint, s => s.U32((uint)path));
                    break;
                }

            case DrawPointsCall c:
                {
                    Buffer paint = PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawPoints);
                    b.U8((byte)c.Mode);
                    b.U32((uint)c.Points.Length);
                    foreach (Vector2 p in c.Points)
                    {
                        b.Point(p);
                    }

                    b.Stroke(c.Stroke);
                    b.Append(paint);
                    break;
                }

            case DrawImageCall c:
                {
                    int image = ImageIndex(c.Image);
                    Buffer? paint = c.Paint is null ? null : PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawImage);
                    b.U32((uint)image);
                    b.Rect(c.Source);
                    b.Rect(c.Destination);
                    b.Sampling(c.Sampling);
                    b.OptionalPaint(paint);
                    break;
                }

            case DrawLatticeCall c:
                {
                    int image = ImageIndex(c.Image);
                    Buffer? paint = c.Paint is null ? null : PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawLattice);
                    b.U32((uint)image);
                    Lattice l = c.Lattice;
                    b.U32((uint)l.XDivs.Count);
                    foreach (int x in l.XDivs)
                    {
                        b.I32(x);
                    }

                    b.U32((uint)l.YDivs.Count);
                    foreach (int y in l.YDivs)
                    {
                        b.I32(y);
                    }

                    b.Bool(l.Bounds is not null);
                    if (l.Bounds is Rect r)
                    {
                        b.I32(r.X);
                        b.I32(r.Y);
                        b.I32(r.Width);
                        b.I32(r.Height);
                    }

                    b.Bool(l.CellKinds is not null);
                    foreach (LatticeCellKind k in l.CellKinds ?? [])
                    {
                        b.U8((byte)k);
                    }

                    b.Bool(l.CellColors is not null);
                    foreach (RgbaF32 color in l.CellColors ?? [])
                    {
                        b.Color(color);
                    }

                    b.Rect(c.Destination);
                    b.Sampling(c.Sampling);
                    b.OptionalPaint(paint);
                    break;
                }

            case DrawAtlasCall c:
                {
                    int image = ImageIndex(c.Image);
                    Buffer? paint = c.Paint is null ? null : PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawAtlas);
                    b.U32((uint)image);
                    b.U32((uint)c.Transforms.Length);
                    foreach (SpriteTransform t in c.Transforms)
                    {
                        b.F32(t.ScaleCos);
                        b.F32(t.ScaleSin);
                        b.F32(t.Tx);
                        b.F32(t.Ty);
                    }

                    foreach (RectF s in c.Sources)
                    {
                        b.Rect(s);
                    }

                    b.Bool(c.Colors.Length > 0);
                    foreach (RgbaF32 color in c.Colors)
                    {
                        b.Color(color);
                    }

                    b.U8((byte)c.ColorBlend);
                    b.Sampling(c.Sampling);
                    b.OptionalPaint(paint);
                    break;
                }

            case DrawMeshCall c:
                {
                    Buffer paint = PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawMesh);
                    Mesh m = c.Mesh;
                    b.U8((byte)m.Mode);
                    b.U32((uint)m.Positions.Length);
                    foreach (Vector2 p in m.Positions.Span)
                    {
                        b.Point(p);
                    }

                    b.U8((byte)((m.Colors.IsEmpty ? 0 : 1) | (m.TexCoords.IsEmpty ? 0 : 2) | (m.Values.IsEmpty ? 0 : 4) | (m.Indices.IsEmpty ? 0 : 8)));
                    for (int v = 0; v < m.Colors.Length; v++)
                    {
                        b.Color(m.Colors.Span[v], m.Inks.IsEmpty ? null : m.Inks.Span[v]);
                    }

                    foreach (Vector2 t in m.TexCoords.Span)
                    {
                        b.Point(t);
                    }

                    foreach (float v in m.Values.Span)
                    {
                        b.F32(v);
                    }

                    if (!m.Indices.IsEmpty)
                    {
                        b.U32((uint)m.Indices.Length);
                        foreach (int i in m.Indices.Span)
                        {
                            b.I32(i);
                        }
                    }

                    b.Append(paint);
                    b.U8((byte)c.ColorBlend);
                    b.Stops(c.Stops);
                    break;
                }

            case DrawPatchCall c:
                {
                    Buffer paint = PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawPatch);
                    Patch p = c.Patch;
                    b.U8((byte)p.Kind);
                    foreach (Vector2 point in p.Points.Span)
                    {
                        b.Point(point);
                    }

                    b.U8((byte)((p.CornerColors.IsEmpty ? 0 : 1) | (p.CornerTexCoords.IsEmpty ? 0 : 2) | (p.CornerValues.IsEmpty ? 0 : 4)));
                    for (int v = 0; v < p.CornerColors.Length; v++)
                    {
                        b.Color(p.CornerColors.Span[v], p.CornerInks.IsEmpty ? null : p.CornerInks.Span[v]);
                    }

                    foreach (Vector2 t in p.CornerTexCoords.Span)
                    {
                        b.Point(t);
                    }

                    foreach (float v in p.CornerValues.Span)
                    {
                        b.F32(v);
                    }

                    b.Append(paint);
                    b.U8((byte)c.ColorBlend);
                    b.Stops(c.Stops);
                    break;
                }

            case DrawPictureCall c:
                {
                    int picture = DrawingIndex(c.Picture);
                    Buffer? paint = c.Paint is null ? null : PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawPicture);
                    b.U32((uint)picture);
                    b.Matrix(c.Matrix);
                    b.OptionalPaint(paint);
                    b.Bool(c.CacheAsImage);
                    break;
                }

            case DrawEncodedImageCall c:
                {
                    int image = EncodedIndex(c.Image);
                    Buffer? paint = c.Paint is null ? null : PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawEncodedImage);
                    b.U32((uint)image);
                    b.Rect(c.Source);
                    b.Rect(c.Destination);
                    b.Sampling(c.Sampling);
                    b.OptionalPaint(paint);
                    break;
                }

            case DrawImageMaskCall c:
                {
                    int image = ImageIndex(c.Mask);
                    Buffer paint = PaintBytes(c.Paint);
                    b.Op(Egdr.Op.DrawImageMask);
                    b.U32((uint)image);
                    b.Rect(c.Destination);
                    b.Sampling(c.Sampling);
                    b.Append(paint);
                    break;
                }

            case DrawAnnotationCall c:
                b.Op(Egdr.Op.DrawAnnotation);
                b.Rect(c.Area);
                b.U8((byte)c.Annotation.Kind);
                b.String(c.Annotation.Value);
                break;
            case BeginMarkedContentCall c:
                {
                    MarkedContent m = c.Content;
                    b.Op(Egdr.Op.BeginMarkedContent);
                    b.String(m.Tag);
                    b.U8((byte)((m.NodeId is null ? 0 : 1) | (m.ActualText is null ? 0 : 2) | (m.AlternateText is null ? 0 : 4)));
                    if (m.NodeId is int node)
                    {
                        b.I32(node);
                    }

                    if (m.ActualText is string actual)
                    {
                        b.String(actual);
                    }

                    if (m.AlternateText is string alt)
                    {
                        b.String(alt);
                    }

                    break;
                }

            case EndMarkedContentCall:
                b.Op(Egdr.Op.EndMarkedContent);
                break;
            default:
                throw new NotSupportedException($"{call.GetType().Name} has no .egdr form.");
        }
    }

    private void Shape(Buffer b, Egdr.Op op, Stroke? stroke, Paint paint, Action<Buffer> geometry)
    {
        Buffer p = PaintBytes(paint);
        b.Op(op);
        geometry(b);
        b.Bool(stroke is not null);
        if (stroke is not null)
        {
            b.Stroke(stroke);
        }

        b.Append(p);
    }

    /// <summary>A paint's bytes; images and pictures it uses are written as chunks first.</summary>
    private Buffer PaintBytes(Paint paint)
    {
        Buffer b = new();
        if (paint.Overprint != Overprint.Off)
        {
            b.Mark(Egdr.InkEntry.Overprint, [(byte)paint.Overprint]);
        }

        switch (paint)
        {
            case SolidPaint s:
                b.U8((byte)Egdr.PaintKind.Solid);
                Common(b, paint);
                b.Color(s.Color, s.Ink);
                break;
            case LinearGradient g:
                b.U8((byte)Egdr.PaintKind.Linear);
                Common(b, paint);
                b.Point(g.Start);
                b.Point(g.End);
                GradientCommon(b, g);
                break;
            case RadialGradient g:
                b.U8((byte)Egdr.PaintKind.Radial);
                Common(b, paint);
                b.Point(g.Center);
                b.F32(g.Radius);
                GradientCommon(b, g);
                break;
            case ConicalGradient g:
                b.U8((byte)Egdr.PaintKind.Conical);
                Common(b, paint);
                b.Point(g.Start);
                b.F32(g.StartRadius);
                b.Point(g.End);
                b.F32(g.EndRadius);
                GradientCommon(b, g);
                break;
            case SweepGradient g:
                b.U8((byte)Egdr.PaintKind.Sweep);
                Common(b, paint);
                b.Point(g.Center);
                b.F32(g.StartAngle);
                b.F32(g.EndAngle);
                GradientCommon(b, g);
                break;
            case ImagePaint i:
                {
                    int image = ImageIndex(i.Image);
                    b.U8((byte)Egdr.PaintKind.Image);
                    Common(b, paint);
                    b.U32((uint)image);
                    b.U8((byte)i.TileX);
                    b.U8((byte)i.TileY);
                    b.Sampling(i.Sampling);
                    break;
                }

            case NoisePaint n:
                b.U8((byte)Egdr.PaintKind.Noise);
                Common(b, paint);
                b.U8((byte)n.Kind);
                b.F32(n.BaseFrequencyX);
                b.F32(n.BaseFrequencyY);
                b.I32(n.Octaves);
                b.I32(n.Seed);
                b.Bool(n.TileSize is not null);
                if (n.TileSize is Size size)
                {
                    b.I32(size.Width);
                    b.I32(size.Height);
                }

                break;
            case BlendPaint bp:
                {
                    Buffer destination = PaintBytes(bp.Destination), source = PaintBytes(bp.Source);
                    b.U8((byte)Egdr.PaintKind.Blend);
                    Common(b, paint);
                    b.Append(destination);
                    b.Append(source);
                    b.U8((byte)bp.Mode);
                    break;
                }

            case ColorMatrixPaint cm:
                {
                    Buffer source = PaintBytes(cm.Source);
                    b.U8((byte)Egdr.PaintKind.ColorMatrix);
                    Common(b, paint);
                    b.Append(source);
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 5; c++)
                        {
                            b.F32(cm.Matrix[r, c]);
                        }
                    }

                    break;
                }

            case PicturePaint dp:
                {
                    int picture = DrawingIndex(dp.Picture);
                    b.U8((byte)Egdr.PaintKind.Picture);
                    Common(b, paint);
                    b.U32((uint)picture);
                    b.U8((byte)dp.TileX);
                    b.U8((byte)dp.TileY);
                    b.Rect(dp.Tile);
                    break;
                }

            default:
                throw new NotSupportedException($"a {paint.GetType().Name} runs C# code, which a file cannot hold.");
        }

        return b;
    }

    private static void Common(Buffer b, Paint paint)
    {
        b.U8((byte)paint.BlendMode);
        b.F32(paint.Opacity);
        b.U8((byte)((paint.Antialias ? 1 : 0) | (paint.Dither ? 2 : 0) | (paint.Blur is null ? 0 : 4)));
        b.Matrix(paint.LocalMatrix);
        if (paint.Blur is MaskBlur blur)
        {
            b.F32(blur.Sigma);
            b.U8((byte)blur.Style);
        }
    }

    private static void GradientCommon(Buffer b, Gradient g)
    {
        b.Stops([.. g.Stops]);
        b.U8((byte)g.Tile);
        b.U8((byte)((g.ExtendStart ? 1 : 0) | (g.ExtendEnd ? 2 : 0) | (g.Premultiplied ? 4 : 0)));
        b.U8((byte)g.Interpolation.Space);
        b.U8((byte)g.Interpolation.Hue);
    }

    /// <summary>A growing little-endian byte buffer.</summary>
    private sealed class Buffer
    {
        private byte[] _bytes = new byte[64];

        /// <summary>Gets the INKS entries for this buffer's bytes, by offset; <see langword="null"/> when there are none.</summary>
        internal List<(int Offset, Egdr.InkEntry Kind, byte[] Data)>? Marks { get; private set; }

        /// <summary>Notes an INKS entry for what is written next.</summary>
        internal void Mark(Egdr.InkEntry kind, byte[] data) => (Marks ??= []).Add((_length, kind, data));

        internal void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Take(data.Length));

        /// <summary>A colour; with <paramref name="ink"/>, an INKS entry saying which inks it is.</summary>
        internal void Color(RgbaF32 c, InkColor? ink)
        {
            if (ink is not null)
            {
                Mark(Egdr.InkEntry.Color, InkBytes(ink));
            }

            Color(c);
        }

        // An ink colour: a CMYK flag, then each ink's name, kind, appearance and tint.
        private static byte[] InkBytes(InkColor ink)
        {
            Buffer b = new();
            b.U8((byte)(ink.IsCmyk ? 1 : 0));
            b.U32((uint)ink.Inks.Count);
            foreach (InkTint t in ink.Inks)
            {
                b.String(t.Colorant.Name);
                b.U8((byte)t.Colorant.Kind);
                b.U8((byte)(t.Colorant.Appearance is InkAppearance a ? (int)a.Kind + 1 : 0));
                if (t.Colorant.Appearance is InkAppearance appearance)
                {
                    b.F32(appearance.Values.X);
                    b.F32(appearance.Values.Y);
                    b.F32(appearance.Values.Z);
                    b.F32(appearance.Values.W);
                }

                b.F32(t.Tint);
            }

            return b.Span.ToArray();
        }
        private int _length;

        internal ReadOnlySpan<byte> Span => _bytes.AsSpan(0, _length);

        internal void U8(byte v) => Take(1)[0] = v;

        internal void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

        internal void Op(Egdr.Op op) => U8((byte)op);

        internal void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);

        internal void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);

        internal void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);

        internal void Point(Vector2 p)
        {
            F32(p.X);
            F32(p.Y);
        }

        internal void Rect(RectF r)
        {
            F32(r.X);
            F32(r.Y);
            F32(r.Width);
            F32(r.Height);
        }

        internal void RoundRect(RoundRect r)
        {
            Rect(r.Bounds);
            Point(r.Radii.TopLeft);
            Point(r.Radii.TopRight);
            Point(r.Radii.BottomRight);
            Point(r.Radii.BottomLeft);
        }

        internal void Matrix(Matrix3x2 m)
        {
            F32(m.M11);
            F32(m.M12);
            F32(m.M21);
            F32(m.M22);
            F32(m.M31);
            F32(m.M32);
        }

        internal void Color(RgbaF32 c)
        {
            F32(c.R);
            F32(c.G);
            F32(c.B);
            F32(c.A);
        }

        internal void Stops(GradientStop[] stops)
        {
            U32((uint)stops.Length);
            foreach (GradientStop s in stops)
            {
                F32(s.Offset);
                Color(s.Color, s.Ink);
            }
        }

        internal void Stroke(Stroke s)
        {
            F32(s.Width);
            U8((byte)s.Cap);
            U8((byte)s.Join);
            F32(s.MiterLimit);
            Bool(s.Dash is not null);
            if (s.Dash is DashPattern dash)
            {
                U32((uint)dash.Intervals.Count);
                foreach (float i in dash.Intervals)
                {
                    F32(i);
                }

                F32(dash.Phase);
            }
        }

        internal void Sampling(SamplingOptions s)
        {
            U8((byte)s.Filter);
            U8((byte)s.ColorSpace);
            U8((byte)s.Edges);
            U8(s.Background.R);
            U8(s.Background.G);
            U8(s.Background.B);
            U8(s.Background.A);
        }

        internal void String(string s)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            U32((uint)bytes.Length);
            bytes.CopyTo(Take(bytes.Length));
        }

        internal void OptionalPaint(Buffer? paint)
        {
            Bool(paint is not null);
            if (paint is not null)
            {
                Append(paint);
            }
        }

        internal void Append(Buffer other)
        {
            if (other.Marks is not null)
            {
                foreach ((int offset, Egdr.InkEntry kind, byte[] data) in other.Marks)
                {
                    (Marks ??= []).Add((_length + offset, kind, data));
                }
            }

            other.Span.CopyTo(Take(other._length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<byte> Take(int n)
        {
            if (_length + n > _bytes.Length)
            {
                Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + n));
            }

            Span<byte> s = _bytes.AsSpan(_length, n);
            _length += n;
            return s;
        }
    }
}
