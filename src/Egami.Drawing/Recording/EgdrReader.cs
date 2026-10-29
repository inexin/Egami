using System.Buffers.Binary;
using System.Numerics;

using Egami.Colors;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Recording;

/// <summary>
/// Reads .egdr, an untrusted format: every count is checked against the bytes left before anything is allocated, every number
/// and enum goes through the public constructors' checks, paths must be well formed, images go through the PNG reader with the
/// context's limits, and the nesting of pictures and the calls a picture would replay, nested pictures expanded, are bounded.
/// Every failure is an <see cref="EgamiException"/>.
/// </summary>
internal sealed class EgdrReader
{
    private const string Format = "EGDR";

    private readonly EgamiContext _context;
    private readonly List<Raster> _images = [];
    private readonly List<EncodedImage> _encoded = [];
    private readonly List<Path> _paths = [];
    private readonly List<(Picture Picture, int Depth, long Expanded)> _pictures = [];
    private long _budget;

    private EgdrReader(EgamiContext context)
    {
        _context = context;
        _budget = context.Limits.MaxTotalBytes;
    }

    internal static Picture Read(Stream stream, EgamiContext context)
    {
        byte[] data;
        using (MemoryStream copy = new())
        {
            stream.CopyTo(copy);
            data = copy.ToArray();
        }

        if (data.Length < 12 || !data.AsSpan(0, 8).SequenceEqual(Egdr.Signature))
        {
            throw new UnsupportedFormatException("The data is not an Egami picture (.egdr): the signature is missing.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        if (major != Egdr.Major)
        {
            throw new UnsupportedFormatException($"The picture is .egdr version {major}; this Egami.Drawing reads version {Egdr.Major}.");
        }

        EgdrReader reader = new(context);
        try
        {
            return reader.Chunks(data);
        }
        catch (ArgumentException e)
        {
            throw new CorruptDataException(Format, -1, e.Message, e);
        }
    }

    private Picture Chunks(byte[] data)
    {
        int at = 12;
        Dictionary<int, (Egdr.InkEntry Kind, byte[] Data)>? inks = null;
        while (true)
        {
            if (data.Length - at < 8)
            {
                throw Corrupt(at, "the file ends before its DEND chunk.");
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            at += 8;
            if (length > (uint)(data.Length - at))
            {
                throw Corrupt(at - 4, $"a chunk claims {length} bytes but {data.Length - at} are left.");
            }

            ReadOnlyMemory<byte> payload = data.AsMemory(at, (int)length);
            if (type == Egdr.End)
            {
                return _pictures.Count > 0 ? _pictures[^1].Picture : throw Corrupt(at, "the file holds no picture.");
            }

            if (type == Egdr.Image)
            {
                Raster image = Raster.Load(payload, PngFormat.Info, _context);
                _images.Add(image);
                Spend(length + (image.Width * (long)image.Height * 16));
            }
            else if (type == Egdr.EncodedImageChunk)
            {
                Spend(length);
                _encoded.Add(EncodedImage.Load(payload.ToArray(), _context));
            }
            else if (type == Egdr.PathChunk)
            {
                _paths.Add(new Cursor(payload, at, this).ReadPath());
            }
            else if (type == Egdr.InksChunk)
            {
                inks = new Cursor(payload, at, this).ReadInks();
            }
            else if (type == Egdr.DrawingChunk)
            {
                Cursor cursor = new(payload, at, this) { Inks = inks };
                _pictures.Add(cursor.ReadPicture());
                inks = null;
            }

            // Any other chunk type comes from a later minor version and is skipped.
            at += (int)length;
        }
    }

    /// <summary>Charges decoded memory against the context's MaxTotalBytes.</summary>
    private void Spend(long bytes)
    {
        _budget -= bytes;
        if (_budget < 0)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), _context.Limits.MaxTotalBytes, _context.Limits.MaxTotalBytes - _budget);
        }
    }

    private static CorruptDataException Corrupt(long offset, string problem) => new(Format, offset, problem);

    /// <summary>Reads one chunk's payload.</summary>
    private sealed class Cursor(ReadOnlyMemory<byte> payload, int offset, EgdrReader reader)
    {
        private int _at;
        private int _depth;

        /// <summary>Gets the entries of the INKS chunk before this DRAW chunk, by offset, or <see langword="null"/>.</summary>
        internal Dictionary<int, (Egdr.InkEntry Kind, byte[] Data)>? Inks { get; init; }

        /// <summary>Reads an INKS chunk: entries by offset in the DRAW chunk that follows.</summary>
        internal Dictionary<int, (Egdr.InkEntry Kind, byte[] Data)> ReadInks()
        {
            int count = Count(9);
            Dictionary<int, (Egdr.InkEntry, byte[])> entries = new(count);
            for (int i = 0; i < count; i++)
            {
                int offset = (int)Math.Min(U32(), int.MaxValue);
                Egdr.InkEntry kind = Enum<Egdr.InkEntry>();
                byte[] data = Take(Count(1)).ToArray();
                if (!entries.TryAdd(offset, (kind, data)))
                {
                    throw Problem($"two ink entries share offset {offset}.");
                }
            }

            End();
            return entries;
        }

        // The entry of a kind at an offset of this chunk, or null.
        private byte[]? Entry(int at, Egdr.InkEntry kind)
        {
            if (Inks is null || !Inks.TryGetValue(at, out var entry))
            {
                return null;
            }

            _inksUsed++;
            return entry.Kind == kind ? entry.Data : throw Problem($"the ink entry at {at} is not a {kind} entry.");
        }

        private int _inksUsed;

        // The colour at the cursor, and its inks when the INKS chunk names them.
        private (RgbaF32 Color, InkColor? Ink) ReadInkColor()
        {
            int at = _at;
            RgbaF32 color = ReadColor();
            return (color, Entry(at, Egdr.InkEntry.Color) is byte[] data ? new Cursor(data, offset + at, reader).ReadInk() : null);
        }

        private InkColor ReadInk()
        {
            bool cmyk = Bool();
            InkTint[] inks = new InkTint[Count(10)];
            for (int i = 0; i < inks.Length; i++)
            {
                string name = ReadString();
                ColorantKind kind = Enum<ColorantKind>();
                byte look = U8();
                InkAppearance? appearance = look switch
                {
                    0 => null,
                    1 => InkAppearance.Cmyk(F32(), F32(), F32(), F32()),
                    2 => Rgb(F32(), F32(), F32(), F32()),
                    3 => Lab(F32(), F32(), F32(), F32()),
                    _ => throw Problem($"{look} is not an ink appearance."),
                };
                Colorant colorant = kind switch
                {
                    ColorantKind.Process => Colorant.Process(name, appearance),
                    ColorantKind.Spot => Colorant.Spot(name, appearance),
                    _ => Colorant.Technical(name),
                };
                inks[i] = new InkTint(colorant, F32());
            }

            End();
            if (cmyk)
            {
                return inks.Length == 4 && inks.Select(t => t.Colorant).SequenceEqual([Colorant.Cyan, Colorant.Magenta, Colorant.Yellow, Colorant.Black])
                    ? InkColor.Cmyk(inks[0].Tint, inks[1].Tint, inks[2].Tint, inks[3].Tint)
                    : throw Problem("a CMYK ink colour does not name the four process inks.");
            }

            return InkColor.Mix(inks);

            static InkAppearance Rgb(float r, float g, float b, float unused) => InkAppearance.Rgb(r, g, b);

            static InkAppearance Lab(float l, float a, float b, float unused) => InkAppearance.Lab(new LabColor(l, a, b));
        }
        private long _expanded;

        internal Path ReadPath()
        {
            FillRule rule = Enum<FillRule>();
            int verbCount = Count(1);
            PathVerb[] verbs = new PathVerb[verbCount];
            int needed = 0;
            bool open = false;
            for (int i = 0; i < verbCount; i++)
            {
                PathVerb v = Enum<PathVerb>();
                if (v is PathVerb.Line or PathVerb.Quad or PathVerb.Cubic && !open)
                {
                    throw Problem("a path draws a segment before moving to its start.");
                }

                open = v != PathVerb.Close && (v == PathVerb.Move || open);
                needed += Path.PointCount(v);
                verbs[i] = v;
            }

            int pointCount = Count(8);
            if (pointCount != needed)
            {
                throw Problem($"a path's verbs need {needed} points but it has {pointCount}.");
            }

            Vector2[] points = new Vector2[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                points[i] = ReadPoint();
            }

            End();
            return new Path(points, verbs, rule);
        }

        internal (Picture Picture, int Depth, long Expanded) ReadPicture()
        {
            RectF bounds = ReadRect();
            int count = Count(1);
            DrawingCall[] calls = new DrawingCall[count];
            for (int i = 0; i < count; i++)
            {
                calls[i] = ReadCall();
                _expanded++;
            }

            End();
            if (Inks is not null && _inksUsed != Inks.Count)
            {
                throw Problem("an ink entry points at nothing in the picture.");
            }

            if (_marked != 0)
            {
                throw Problem("a picture's marked content does not end.");
            }

            if (_expanded > Egdr.MaxExpandedCalls)
            {
                throw new LimitExceededException("ExpandedDrawingCalls", Egdr.MaxExpandedCalls, _expanded);
            }

            if (!(bounds.IsFinite && !bounds.IsEmpty))
            {
                throw Problem("a picture's bounds must be finite and not empty.");
            }

            reader.Spend(count * 16L);
            return (new Picture(bounds, calls), _depth + 1, _expanded);
        }

        private DrawingCall ReadCall()
        {
            int opAt = _at;
            Egdr.Op op = Enum<Egdr.Op>();
            switch (op)
            {
                case Egdr.Op.Save:
                    return SaveCall.Instance;
                case Egdr.Op.Restore:
                    return RestoreCall.Instance;
                case Egdr.Op.SaveLayer:
                    {
                        float opacity = F32();
                        BlendMode mode = Enum<BlendMode>();
                        byte group = U8();
                        if (group > 3)
                        {
                            throw Problem("a layer has unknown flags.");
                        }

                        RectF? bounds = Bool() ? ReadRect() : null;
                        ColorProfile? space = Entry(opAt, Egdr.InkEntry.LayerSpace) is byte[] icc ? ColorProfile.FromIcc(icc) : null;
                        return new SaveLayerCall(new LayerOptions { Opacity = opacity, BlendMode = mode, Isolated = (group & 1) != 0, Knockout = (group & 2) != 0, Bounds = bounds, ColorSpace = space });
                    }

                case Egdr.Op.SetMatrix:
                    return new SetMatrixCall(ReadMatrix());
                case Egdr.Op.ClipRect:
                    return new ClipRectCall(ReadRect(), Enum<ClipOp>(), Bool());
                case Egdr.Op.ClipRoundRect:
                    return new ClipRoundRectCall(ReadRoundRect(), Enum<ClipOp>(), Bool());
                case Egdr.Op.ClipPath:
                    return new ClipPathCall(PathRef(), Enum<ClipOp>(), Bool());
                case Egdr.Op.DrawPaint:
                    return new DrawPaintCall(ReadPaint());
                case Egdr.Op.DrawRect:
                    {
                        RectF rect = ReadRect();
                        return new DrawRectCall(rect, ReadOptionalStroke(), ReadPaint());
                    }

                case Egdr.Op.DrawRoundRect:
                    {
                        RoundRect rect = ReadRoundRect();
                        return new DrawRoundRectCall(rect, ReadOptionalStroke(), ReadPaint());
                    }

                case Egdr.Op.DrawOval:
                    {
                        RectF rect = ReadRect();
                        return new DrawOvalCall(rect, ReadOptionalStroke(), ReadPaint());
                    }

                case Egdr.Op.DrawPath:
                    {
                        Path path = PathRef();
                        return new DrawPathCall(path, ReadOptionalStroke(), ReadPaint());
                    }

                case Egdr.Op.DrawPoints:
                    {
                        PointMode mode = Enum<PointMode>();
                        Vector2[] points = new Vector2[Count(8)];
                        for (int i = 0; i < points.Length; i++)
                        {
                            points[i] = ReadPoint();
                        }

                        return new DrawPointsCall(mode, points, ReadStroke(), ReadPaint());
                    }

                case Egdr.Op.DrawImage:
                    {
                        Raster image = ImageRef();
                        RectF source = ReadRect(), destination = ReadRect();
                        CheckSource(image, source);
                        return new DrawImageCall(image, source, destination, ReadSampling(), ReadOptionalPaint());
                    }

                case Egdr.Op.DrawLattice:
                    return ReadLattice();
                case Egdr.Op.DrawAtlas:
                    return ReadAtlas();
                case Egdr.Op.DrawMesh:
                    {
                        Mesh mesh = ReadMesh();
                        Paint paint = ReadPaint();
                        BlendMode blend = Enum<BlendMode>();
                        GradientStop[] stops = ReadStops();
                        if (!mesh.Values.IsEmpty && stops.Length == 0)
                        {
                            throw Problem("a mesh with values has no stops.");
                        }

                        return new DrawMeshCall(mesh, paint, blend, stops);
                    }

                case Egdr.Op.DrawPatch:
                    {
                        Patch patch = ReadPatch();
                        Paint paint = ReadPaint();
                        BlendMode blend = Enum<BlendMode>();
                        GradientStop[] stops = ReadStops();
                        if (!patch.CornerValues.IsEmpty && stops.Length == 0)
                        {
                            throw Problem("a patch with values has no stops.");
                        }

                        return new DrawPatchCall(patch, paint, blend, stops);
                    }

                case Egdr.Op.DrawPicture:
                    {
                        Picture picture = DrawingRef();
                        Matrix3x2 matrix = ReadMatrix();
                        return new DrawPictureCall(picture, matrix, ReadOptionalPaint(), Bool());
                    }

                case Egdr.Op.DrawEncodedImage:
                    {
                        uint index = U32();
                        EncodedImage image = index < (uint)reader._encoded.Count ? reader._encoded[(int)index] : throw Problem($"encoded image {index} is not defined before it is used.");
                        RectF source = ReadRect(), destination = ReadRect();
                        if (!source.IsFinite || source.X < 0 || source.Y < 0 || source.Right > image.Width || source.Bottom > image.Height)
                        {
                            throw Problem("an encoded image's source rectangle lies outside it.");
                        }

                        return new DrawEncodedImageCall(image, source, destination, ReadSampling(), ReadOptionalPaint());
                    }

                case Egdr.Op.DrawImageMask:
                    {
                        Raster mask = ImageRef();
                        RectF destination = ReadRect();
                        SamplingOptions sampling = ReadSampling();
                        return new DrawImageMaskCall(mask, destination, sampling, ReadPaint());
                    }

                case Egdr.Op.DrawAnnotation:
                    {
                        RectF area = ReadRect();
                        AnnotationKind kind = Enum<AnnotationKind>();
                        return new DrawAnnotationCall(area, new Annotation(kind, ReadString()));
                    }

                case Egdr.Op.BeginMarkedContent:
                    {
                        string tag = ReadString();
                        byte flags = U8();
                        if (flags > 7)
                        {
                            throw Problem("marked content has unknown flags.");
                        }

                        int? node = (flags & 1) != 0 ? I32() : null;
                        string? actual = (flags & 2) != 0 ? ReadString() : null;
                        string? alt = (flags & 4) != 0 ? ReadString() : null;
                        _marked++;
                        return new BeginMarkedContentCall(new MarkedContent(tag) { NodeId = node, ActualText = actual, AlternateText = alt });
                    }

                case Egdr.Op.EndMarkedContent:
                    if (--_marked < 0)
                    {
                        throw Problem("marked content ends that never began.");
                    }

                    return EndMarkedContentCall.Instance;
                default:
                    throw Problem($"unknown call {(byte)op}.");
            }
        }

        private DrawLatticeCall ReadLattice()
        {
            Raster image = ImageRef();
            int[] xDivs = new int[Count(4)];
            for (int i = 0; i < xDivs.Length; i++)
            {
                xDivs[i] = I32();
            }

            int[] yDivs = new int[Count(4)];
            for (int i = 0; i < yDivs.Length; i++)
            {
                yDivs[i] = I32();
            }

            Rect? bounds = Bool() ? new Rect(I32(), I32(), I32(), I32()) : null;
            long cells = (xDivs.Length + 1L) * (yDivs.Length + 1L);
            LatticeCellKind[]? kinds = null;
            if (Bool())
            {
                kinds = new LatticeCellKind[CheckCount(cells, 1)];
                for (int i = 0; i < kinds.Length; i++)
                {
                    kinds[i] = Enum<LatticeCellKind>();
                }
            }

            RgbaF32[]? colors = null;
            if (Bool())
            {
                colors = new RgbaF32[CheckCount(cells, 16)];
                for (int i = 0; i < colors.Length; i++)
                {
                    colors[i] = ReadColor();
                }
            }

            Lattice lattice = new(xDivs, yDivs) { Bounds = bounds, CellKinds = kinds, CellColors = colors };
            lattice.Check(image, "lattice");
            RectF destination = ReadRect();
            return new DrawLatticeCall(image, lattice, destination, ReadSampling(), ReadOptionalPaint());
        }

        private DrawAtlasCall ReadAtlas()
        {
            Raster image = ImageRef();
            int n = Count(32);
            SpriteTransform[] transforms = new SpriteTransform[n];
            for (int i = 0; i < n; i++)
            {
                transforms[i] = new SpriteTransform(F32(), F32(), F32(), F32());
                if (!transforms[i].IsFinite)
                {
                    throw Problem("a sprite transform is not finite.");
                }
            }

            RectF[] sources = new RectF[n];
            for (int i = 0; i < n; i++)
            {
                sources[i] = ReadRect();
            }

            RgbaF32[] colors = [];
            if (Bool())
            {
                colors = new RgbaF32[CheckCount(n, 16)];
                for (int i = 0; i < n; i++)
                {
                    colors[i] = ReadColor();
                }
            }

            BlendMode blend = Enum<BlendMode>();
            return new DrawAtlasCall(image, transforms, sources, colors, blend, ReadSampling(), ReadOptionalPaint());
        }

        private Mesh ReadMesh()
        {
            MeshMode mode = Enum<MeshMode>();
            int n = Count(8);
            Vector2[] positions = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                positions[i] = ReadPoint();
            }

            byte flags = U8();
            if (flags > 15)
            {
                throw Problem("a mesh has unknown flags.");
            }

            RgbaF32[] colors = (flags & 1) != 0 ? new RgbaF32[CheckCount(n, 16)] : [];
            InkColor?[] inks = new InkColor?[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                (colors[i], inks[i]) = ReadInkColor();
            }

            Vector2[] tex = (flags & 2) != 0 ? new Vector2[CheckCount(n, 8)] : [];
            for (int i = 0; i < tex.Length; i++)
            {
                tex[i] = ReadPoint();
            }

            float[] values = (flags & 4) != 0 ? new float[CheckCount(n, 4)] : [];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = F32();
            }

            int[] indices = (flags & 8) != 0 ? new int[Count(4)] : [];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = I32();
            }

            if ((flags & 8) != 0 && indices.Length == 0)
            {
                throw Problem("a mesh's index list is empty.");
            }

            reader.Spend(n * 40L);
            Mesh mesh = new(mode, positions, colors, tex, values, indices);
            return WithInks(inks) is InkColor[] all ? mesh.WithInks(all) : mesh;
        }

        private Patch ReadPatch()
        {
            PatchKind kind = Enum<PatchKind>();
            Vector2[] points = new Vector2[kind == PatchKind.Coons ? 12 : 16];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = ReadPoint();
            }

            byte flags = U8();
            if (flags > 7)
            {
                throw Problem("a patch has unknown flags.");
            }

            RgbaF32[] colors = (flags & 1) != 0 ? new RgbaF32[4] : [];
            InkColor?[] inks = new InkColor?[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                (colors[i], inks[i]) = ReadInkColor();
            }

            Vector2[] tex = (flags & 2) != 0 ? [ReadPoint(), ReadPoint(), ReadPoint(), ReadPoint()] : [];
            float[] values = (flags & 4) != 0 ? [F32(), F32(), F32(), F32()] : [];
            Patch patch = kind == PatchKind.Coons ? Egami.Drawing.Patch.Coons(points, colors, tex, values) : Egami.Drawing.Patch.Tensor(points, colors, tex, values);
            return WithInks(inks) is InkColor[] all ? patch.WithInks(all) : patch;
        }

        // Vertex inks: all or none.
        private InkColor[]? WithInks(InkColor?[] inks)
        {
            int named = inks.Count(i => i is not null);
            return named == 0 ? null : named == inks.Length ? [.. inks.Select(i => i!)] : throw Problem("only some vertices have inks.");
        }

        private Paint ReadPaint()
        {
            int at = _at;
            Egdr.PaintKind kind = Enum<Egdr.PaintKind>();
            BlendMode mode = Enum<BlendMode>();
            float opacity = F32();
            byte flags = U8();
            if (flags > 7)
            {
                throw Problem("a paint has unknown flags.");
            }

            Matrix3x2 local = ReadMatrix();
            MaskBlur? blur = (flags & 4) != 0 ? new MaskBlur(F32(), Enum<BlurStyle>()) : null;
            Paint paint;
            switch (kind)
            {
                case Egdr.PaintKind.Solid:
                    {
                        (RgbaF32 color, InkColor? ink) = ReadInkColor();
                        paint = ink is null ? new SolidPaint(color) : new SolidPaint(ink);
                    }

                    break;
                case Egdr.PaintKind.Linear:
                    {
                        Vector2 start = ReadPoint(), end = ReadPoint();
                        paint = ReadGradient(new LinearGradient(start, end, ReadStops(atLeastOne: true)));
                        break;
                    }

                case Egdr.PaintKind.Radial:
                    {
                        Vector2 center = ReadPoint();
                        float radius = F32();
                        paint = ReadGradient(new RadialGradient(center, radius, ReadStops(atLeastOne: true)));
                        break;
                    }

                case Egdr.PaintKind.Conical:
                    {
                        Vector2 start = ReadPoint();
                        float r0 = F32();
                        Vector2 end = ReadPoint();
                        float r1 = F32();
                        paint = ReadGradient(new ConicalGradient(start, r0, end, r1, ReadStops(atLeastOne: true)));
                        break;
                    }

                case Egdr.PaintKind.Sweep:
                    {
                        Vector2 center = ReadPoint();
                        float a0 = F32(), a1 = F32();
                        paint = ReadGradient(new SweepGradient(center, a0, a1, ReadStops(atLeastOne: true)));
                        break;
                    }

                case Egdr.PaintKind.Image:
                    {
                        Raster image = ImageRef();
                        TileMode x = Enum<TileMode>(), y = Enum<TileMode>();
                        paint = new ImagePaint(image, x, y, ReadSampling());
                        break;
                    }

                case Egdr.PaintKind.Noise:
                    {
                        NoisePaintKind type = Enum<NoisePaintKind>();
                        float fx = F32(), fy = F32();
                        int octaves = I32(), seed = I32();
                        Size? tile = Bool() ? new Size(I32(), I32()) : null;
                        if (tile is Size s && (s.Width <= 0 || s.Height <= 0))
                        {
                            throw Problem("a noise tile is empty.");
                        }

                        paint = type switch
                        {
                            NoisePaintKind.Fractal => NoisePaint.Fractal(fx, fy, octaves, seed, tile),
                            NoisePaintKind.Turbulence => NoisePaint.Turbulence(fx, fy, octaves, seed, tile),
                            _ => NoisePaint.Simplex(fx, octaves, seed, tile),
                        };
                        break;
                    }

                case Egdr.PaintKind.Blend:
                    {
                        Paint destination = ReadNested(), source = ReadNested();
                        paint = new BlendPaint(destination, source, Enum<BlendMode>());
                        break;
                    }

                case Egdr.PaintKind.ColorMatrix:
                    {
                        Paint source = ReadNested();
                        float[] m = new float[20];
                        for (int i = 0; i < 20; i++)
                        {
                            m[i] = F32();
                        }

                        paint = new ColorMatrixPaint(source, new ColorMatrix(m));
                        break;
                    }

                case Egdr.PaintKind.Picture:
                    {
                        Picture picture = DrawingRef();
                        TileMode x = Enum<TileMode>(), y = Enum<TileMode>();
                        paint = new PicturePaint(picture, x, y, ReadRect());
                        break;
                    }

                default:
                    throw Problem($"unknown paint kind {(byte)kind}.");
            }

            Overprint overprint = Entry(at, Egdr.InkEntry.Overprint) is [byte o] ? (Overprint)o : Overprint.Off;
            return paint with { BlendMode = mode, Opacity = opacity, Antialias = (flags & 1) != 0, Dither = (flags & 2) != 0, LocalMatrix = local, Blur = blur, Overprint = overprint };
        }

        /// <summary>A paint inside a paint; nesting counts toward the same depth limit as pictures.</summary>
        private Paint ReadNested()
        {
            if (++_paintDepth > Egdr.MaxNesting)
            {
                throw new LimitExceededException("DrawingNesting", Egdr.MaxNesting, _paintDepth);
            }

            Paint paint = ReadPaint();
            _paintDepth--;
            return paint;
        }

        private int _paintDepth;
        private int _marked;

        private string ReadString()
        {
            int length = Count(1);
            return new System.Text.UTF8Encoding(false, true).GetString(Take(length));
        }

        private Gradient ReadGradient(Gradient g)
        {
            TileMode tile = Enum<TileMode>();
            byte flags = U8();
            if (flags > 7)
            {
                throw Problem("a gradient has unknown flags.");
            }

            InterpolationSpace space = Enum<InterpolationSpace>();
            HueMethod hue = Enum<HueMethod>();
            GradientInterpolation interpolation = space switch
            {
                InterpolationSpace.LinearSrgb => GradientInterpolation.LinearSrgb,
                InterpolationSpace.Oklab => GradientInterpolation.Oklab,
                InterpolationSpace.Oklch => GradientInterpolation.Oklch(hue),
                InterpolationSpace.GdiPlusGamma => GradientInterpolation.GdiPlusGamma,
                _ => GradientInterpolation.Encoded,
            };
            return g with { Tile = tile, ExtendStart = (flags & 1) != 0, ExtendEnd = (flags & 2) != 0, Premultiplied = (flags & 4) != 0, Interpolation = interpolation };
        }

        private Stroke? ReadOptionalStroke() => Bool() ? ReadStroke() : null;

        private Stroke ReadStroke()
        {
            float width = F32();
            LineCap cap = Enum<LineCap>();
            LineJoin join = Enum<LineJoin>();
            float miter = F32();
            DashPattern? dash = null;
            if (Bool())
            {
                float[] intervals = new float[Count(4)];
                for (int i = 0; i < intervals.Length; i++)
                {
                    intervals[i] = F32();
                }

                dash = new DashPattern(intervals, F32());
            }

            return new Stroke(width) { Cap = cap, Join = join, MiterLimit = miter, Dash = dash };
        }

        private Paint? ReadOptionalPaint() => Bool() ? ReadPaint() : null;

        private SamplingOptions ReadSampling()
        {
            ResampleFilter filter = Enum<ResampleFilter>();
            ColorSpace space = Enum<ColorSpace>();
            EdgeMode edges = Enum<EdgeMode>();
            Rgba8 background = new(U8(), U8(), U8(), U8());
            return new SamplingOptions { Filter = filter, ColorSpace = space, Edges = edges, Background = background };
        }

        private GradientStop[] ReadStops(bool atLeastOne = false)
        {
            GradientStop[] stops = new GradientStop[Count(20)];
            if (atLeastOne && stops.Length == 0)
            {
                throw Problem("a gradient has no stops.");
            }

            for (int i = 0; i < stops.Length; i++)
            {
                float offset = F32();
                (RgbaF32 color, InkColor? ink) = ReadInkColor();
                stops[i] = ink is null ? new GradientStop(offset, color) : new GradientStop(offset, ink);
            }

            return stops;
        }

        private RoundRect ReadRoundRect()
        {
            RectF bounds = ReadRect();
            return new RoundRect(bounds, new CornerRadii(ReadPoint(), ReadPoint(), ReadPoint(), ReadPoint()));
        }

        private Path PathRef()
        {
            uint i = U32();
            return i < (uint)reader._paths.Count ? reader._paths[(int)i] : throw Problem($"path {i} is not defined before it is used.");
        }

        private Raster ImageRef()
        {
            uint i = U32();
            return i < (uint)reader._images.Count ? reader._images[(int)i] : throw Problem($"image {i} is not defined before it is used.");
        }

        private Picture DrawingRef()
        {
            uint i = U32();
            if (i >= (uint)reader._pictures.Count)
            {
                throw Problem($"picture {i} is not defined before it is used.");
            }

            (Picture picture, int depth, long expanded) = reader._pictures[(int)i];
            _depth = Math.Max(_depth, depth);
            if (_depth >= Egdr.MaxNesting)
            {
                throw new LimitExceededException("DrawingNesting", Egdr.MaxNesting, _depth + 1);
            }

            _expanded += expanded;
            if (_expanded > Egdr.MaxExpandedCalls)
            {
                throw new LimitExceededException("ExpandedDrawingCalls", Egdr.MaxExpandedCalls, _expanded);
            }

            return picture;
        }

        private static void CheckSource(Raster image, RectF source)
        {
            if (!source.IsFinite || source.X < 0 || source.Y < 0 || source.Right > image.Width || source.Bottom > image.Height)
            {
                throw new ArgumentException("An image's source rectangle lies outside it.");
            }
        }

        private Vector2 ReadPoint() => new(F32(), F32());

        private RectF ReadRect() => new(F32(), F32(), F32(), F32());

        private Matrix3x2 ReadMatrix()
        {
            Matrix3x2 m = new(F32(), F32(), F32(), F32(), F32(), F32());
            return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32)
                ? m
                : throw Problem("a matrix is not finite.");
        }

        private RgbaF32 ReadColor() => new(F32(), F32(), F32(), F32());

        private T Enum<T>()
            where T : struct, Enum
        {
            byte b = U8();
            T value = (T)System.Enum.ToObject(typeof(T), b);
            return System.Enum.IsDefined(value) ? value : throw Problem($"{b} is not a {typeof(T).Name}.");
        }

        private bool Bool() => U8() switch
        {
            0 => false,
            1 => true,
            byte b => throw Problem($"{b} is not 0 or 1."),
        };

        private byte U8() => Take(1)[0];

        private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        private int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        private float F32()
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            return float.IsFinite(v) ? v : throw Problem("a number is not finite.");
        }

        /// <summary>Reads a count of items of at least <paramref name="size"/> bytes each, which must fit in what is left.</summary>
        private int Count(int size) => CheckCount(U32(), size);

        private int CheckCount(long count, int size)
        {
            if (count > (payload.Length - _at) / size)
            {
                throw Problem($"a count of {count} does not fit in the {payload.Length - _at} bytes left.");
            }

            return (int)count;
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (payload.Length - _at < n)
            {
                throw Problem("a chunk ends in the middle of a value.");
            }

            ReadOnlySpan<byte> s = payload.Span.Slice(_at, n);
            _at += n;
            return s;
        }

        private void End()
        {
            if (_at != payload.Length)
            {
                throw Problem($"{payload.Length - _at} bytes are left over at the end of a chunk.");
            }
        }

        private CorruptDataException Problem(string problem) => Corrupt(offset + _at, problem);
    }
}
