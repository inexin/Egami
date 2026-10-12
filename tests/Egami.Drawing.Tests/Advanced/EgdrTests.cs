using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing.Tests.Canvas;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class EgdrTests
{
    [Fact]
    public void RoundTripsAreIdentical()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        foreach (Drawing drawing in Corpus(image))
        {
            byte[] first = Bytes(drawing);
            Drawing loaded = Drawing.Load(new MemoryStream(first));
            Assert.Equal(drawing.Bounds, loaded.Bounds);
            Assert.Equal(drawing.CallCount, loaded.CallCount);
            Assert.Equal(first, Bytes(loaded));

            using TraceCanvas a = new(128, 100), b = new(128, 100);
            drawing.Playback(a);
            loaded.Playback(b);
            Assert.Equal(a.Calls, b.Calls);

            using Raster<Rgba8> p = Pixels.Draw(128, 100, drawing.Playback), q = Pixels.Draw(128, 100, loaded.Playback);
            Pixels.Equal(p, q);
        }
    }

    [Fact]
    public void TextIsSavedAsOutlines()
    {
        Fonts.Typeface face = TextTests.Noto.Value;
        DrawingRecorder recorder = new(new RectF(0, 0, 100, 30));
        recorder.DrawText("Egami", new Vector2(4, 22), new Fonts.Font(face, 18), Rgba8.Black);
        Drawing drawing = recorder.Finish();
        Drawing loaded = Drawing.Load(new MemoryStream(Bytes(drawing)));
        using TraceCanvas trace = new(100, 30);
        loaded.Playback(trace);
        Assert.Contains(trace.Calls, c => c.StartsWith("DrawPath", StringComparison.Ordinal));
        // The raster backend draws glyph runs from its glyph cache, placed to a quarter pixel; the outlines draw as their path.
        using Raster<Rgba8> p = Pixels.Draw(100, 30, c => c.FillPath(new Fonts.Font(face, 18).GetTextPath("Egami", new Vector2(4, 22)), Rgba8.Black));
        using Raster<Rgba8> q = Pixels.Draw(100, 30, loaded.Playback);
        Pixels.Equal(p, q);
    }

    [Fact]
    public void UnknownChunksAreSkipped()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        byte[] bytes = Bytes(RecordingTests.Record(image));

        // A chunk of a later minor version, put right after the header.
        byte[] extra = [.. "XTRA"u8, 5, 0, 0, 0, 1, 2, 3, 4, 5];
        byte[] grown = [.. bytes.AsSpan(0, 12), .. extra, .. bytes.AsSpan(12)];
        BinaryPrimitives.WriteUInt16LittleEndian(grown.AsSpan(10), 7);
        Drawing loaded = Drawing.Load(new MemoryStream(grown));
        Assert.Equal(bytes, Bytes(loaded));
    }

    [Fact]
    public void CustomPaintsCannotBeSaved()
    {
        DrawingRecorder recorder = new(new RectF(0, 0, 10, 10));
        recorder.FillRect(new RectF(0, 0, 5, 5), Rgba8.Red);
        recorder.FillRect(new RectF(0, 0, 5, 5), new Stripes());
        Drawing drawing = recorder.Finish();
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => drawing.Save(new MemoryStream()));
        Assert.Contains("Call 1", e.Message, StringComparison.Ordinal);

        DrawingRecorder layered = new(new RectF(0, 0, 10, 10));
        layered.SaveLayer(new LayerOptions { Filter = o => o.Invert() });
        Assert.Throws<NotSupportedException>(() => layered.Finish().Save(new MemoryStream()));
    }

    [Fact]
    public void DamageIsReported()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        byte[] bytes = Bytes(RecordingTests.Record(image));
        Assert.Throws<UnsupportedFormatException>(() => Drawing.Load(new MemoryStream([.. bytes.AsSpan(1)])));
        byte[] major = [.. bytes];
        major[8] = 2;
        Assert.Throws<UnsupportedFormatException>(() => Drawing.Load(new MemoryStream(major)));
        for (int cut = 12; cut < bytes.Length; cut += 97)
        {
            Assert.ThrowsAny<EgamiException>(() => Drawing.Load(new MemoryStream(bytes[..cut])));
        }

        Random random = new(3);
        for (int i = 0; i < 300; i++)
        {
            byte[] damaged = [.. bytes];
            damaged[12 + random.Next(damaged.Length - 12)] ^= (byte)(1 << random.Next(8));
            try
            {
                Drawing.Load(new MemoryStream(damaged));
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Fact]
    public void NestingAndExpansionAreBounded()
    {
        // Each drawing draws the one before it twice: 70 levels would replay 2^70 calls.
        DrawingRecorder first = new(new RectF(0, 0, 4, 4));
        first.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red);
        Drawing drawing = first.Finish();
        for (int i = 0; i < 70; i++)
        {
            DrawingRecorder next = new(new RectF(0, 0, 4, 4));
            next.DrawDrawing(drawing);
            next.DrawDrawing(drawing);
            drawing = next.Finish();
        }

        LimitExceededException e = Assert.Throws<LimitExceededException>(() => Drawing.Load(new MemoryStream(Bytes(drawing))));
        Assert.Equal("ExpandedDrawingCalls", e.LimitName);

        // Deep but cheap: one call per level.
        Drawing deep = Chain(70);
        Assert.Equal("DrawingNesting", Assert.Throws<LimitExceededException>(() => Drawing.Load(new MemoryStream(Bytes(deep)))).LimitName);
        Assert.Equal(1, Drawing.Load(new MemoryStream(Bytes(Chain(40)))).CallCount);
    }

    [Fact]
    public void FuzzSeedsLoad()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "images", "fuzz", "egdr-seeds");
        if (Environment.GetEnvironmentVariable("EGAMI_WRITE_EGDR_SEEDS") == "1")
        {
            Directory.CreateDirectory(folder);
            using Raster<Rgba8> image = Pixels.Photo(16, 12);
            int n = 0;
            foreach (Drawing d in Corpus(image).Append(Chain(3)))
            {
                File.WriteAllBytes(System.IO.Path.Combine(folder, $"seed{n++}.egdr"), Bytes(d));
            }
        }

        string[] seeds = Directory.GetFiles(folder);
        Assert.NotEmpty(seeds);
        foreach (string seed in seeds)
        {
            using FileStream file = File.OpenRead(seed);
            Assert.True(Drawing.Load(file).CallCount > 0);
        }
    }

    private static Drawing Chain(int levels)
    {
        DrawingRecorder first = new(new RectF(0, 0, 4, 4));
        first.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red);
        Drawing drawing = first.Finish();
        for (int i = 0; i < levels; i++)
        {
            DrawingRecorder next = new(new RectF(0, 0, 4, 4));
            next.DrawDrawing(drawing);
            drawing = next.Finish();
        }

        return drawing;
    }

    internal static IEnumerable<Drawing> Corpus(Raster image)
    {
        yield return RecordingTests.Record(image);

        DrawingRecorder patterns = new(new RectF(0, 0, 128, 100));
        DrawingRecorder dot = new(new RectF(0, 0, 8, 8));
        dot.FillCircle(new Vector2(4, 4), 3, Rgba8.Blue);
        Drawing tile = dot.Finish();
        patterns.FillRect(new RectF(0, 0, 128, 100), new DrawingPaint(tile, TileMode.Repeat, TileMode.Mirror) { LocalMatrix = Matrix3x2.CreateRotation(0.3f) });
        patterns.FillOval(new RectF(10, 10, 60, 40), new BlendPaint(NoisePaint.Turbulence(0.05f, 0.08f, 3, 7, new Size(32, 32)), new ImagePaint(image, TileMode.Repeat, TileMode.Decal) { Opacity = 0.7f }, Operations.BlendMode.Screen));
        patterns.FillRect(new RectF(70, 50, 40, 40), new SweepGradient(new Vector2(90, 70), 20, 300, new GradientStop(0, Rgba8.Red), new GradientStop(1, Rgba8.Blue)) { Interpolation = GradientInterpolation.Oklch(HueMethod.Longer), ExtendStart = false });
        patterns.FillRect(new RectF(5, 60, 50, 30), new ConicalGradient(new Vector2(10, 70), 3, new Vector2(40, 75), 20, new GradientStop(0, Rgba8.Green), new GradientStop(1, Rgba8.Black)).WithColorMatrix(Operations.ColorMatrix.Identity) with { Dither = true });
        patterns.FillPath(Path.Circle(new Vector2(100, 20), 12), new SolidPaint(Rgba8.Red) { Blur = new MaskBlur(2, BlurStyle.Outer), Antialias = false });
        patterns.DrawDrawing(tile, Matrix3x2.CreateScale(2), new SolidPaint(Rgba8.White) { Opacity = 0.5f }, cacheAsImage: true);
        patterns.ClipPath(Path.Circle(new Vector2(64, 50), 30), ClipOp.Difference, antialias: false);
        patterns.SaveLayer(new LayerOptions { Bounds = new RectF(0, 0, 50, 50), Isolated = false, BlendMode = Operations.BlendMode.Overlay, Opacity = 0.9f });
        patterns.DrawImageLattice(image, new Lattice([4, 12], [3, 9]) { Bounds = new Rect(1, 1, 14, 10), CellKinds = [.. Enumerable.Repeat(LatticeCellKind.Default, 8), LatticeCellKind.FixedColor], CellColors = [.. Enumerable.Repeat(new RgbaF32(0, 1, 1, 1), 9)] }, new RectF(0, 0, 40, 30));
        patterns.DrawMesh(new Mesh(MeshMode.Strip, [new(0, 0), new(0, 10), new(10, 0), new(10, 10)], texCoords: [new(0, 0), new(0, 12), new(16, 0), new(16, 12)], values: [0, 1, 0.5f, 0.2f]), new ImagePaint(image), Operations.BlendMode.Multiply, [new(0, Rgba8.Red), new(1, Rgba8.White)]);
        patterns.DrawPatch(Patch.Tensor([.. Enumerable.Range(0, 16).Select(i => new Vector2((i % 4) * 10, (i / 4) * 10))], cornerTexCoords: [new(0, 0), new(16, 0), new(16, 12), new(0, 12)]), new ImagePaint(image));
        yield return patterns.Finish();
    }

    private static byte[] Bytes(Drawing drawing)
    {
        using MemoryStream stream = new();
        drawing.Save(stream);
        return stream.ToArray();
    }

    private sealed record class Stripes : CustomPaint
    {
        public override void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse) => row.Fill(new PremulRgbaF32(0, 0, 0, 1));
    }
}
