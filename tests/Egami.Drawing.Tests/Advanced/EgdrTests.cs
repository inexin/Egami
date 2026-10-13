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
        foreach (Picture picture in Corpus(image))
        {
            byte[] first = Bytes(picture);
            Picture loaded = Picture.Load(new MemoryStream(first));
            Assert.Equal(picture.Bounds, loaded.Bounds);
            Assert.Equal(picture.CallCount, loaded.CallCount);
            Assert.Equal(first, Bytes(loaded));

            using TraceCanvas a = new(128, 100), b = new(128, 100);
            picture.Playback(a);
            loaded.Playback(b);
            Assert.Equal(a.Calls, b.Calls);

            using Raster<Rgba8> p = Pixels.Draw(128, 100, picture.Playback), q = Pixels.Draw(128, 100, loaded.Playback);
            Pixels.Equal(p, q);
        }
    }

    [Fact]
    public void TextIsSavedAsOutlines()
    {
        Fonts.Typeface face = TextTests.Noto.Value;
        PictureRecorder recorder = new(new RectF(0, 0, 100, 30));
        recorder.DrawText("Egami", new Vector2(4, 22), new Fonts.Font(face, 18), Rgba8.Black);
        Picture picture = recorder.Finish();
        Picture loaded = Picture.Load(new MemoryStream(Bytes(picture)));
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
        Picture loaded = Picture.Load(new MemoryStream(grown));
        Assert.Equal(bytes, Bytes(loaded));
    }

    [Fact]
    public void CustomPaintsCannotBeSaved()
    {
        PictureRecorder recorder = new(new RectF(0, 0, 10, 10));
        recorder.FillRect(new RectF(0, 0, 5, 5), Rgba8.Red);
        recorder.FillRect(new RectF(0, 0, 5, 5), new Stripes());
        Picture picture = recorder.Finish();
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => picture.Save(new MemoryStream()));
        Assert.Contains("Call 1", e.Message, StringComparison.Ordinal);

        PictureRecorder layered = new(new RectF(0, 0, 10, 10));
        layered.SaveLayer(new LayerOptions { Filter = o => o.Invert() });
        Assert.Throws<NotSupportedException>(() => layered.Finish().Save(new MemoryStream()));
    }

    [Fact]
    public void DamageIsReported()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        byte[] bytes = Bytes(RecordingTests.Record(image));
        Assert.Throws<UnsupportedFormatException>(() => Picture.Load(new MemoryStream([.. bytes.AsSpan(1)])));
        byte[] major = [.. bytes];
        major[8] = 2;
        Assert.Throws<UnsupportedFormatException>(() => Picture.Load(new MemoryStream(major)));
        for (int cut = 12; cut < bytes.Length; cut += 97)
        {
            Assert.ThrowsAny<EgamiException>(() => Picture.Load(new MemoryStream(bytes[..cut])));
        }

        Random random = new(3);
        for (int i = 0; i < 300; i++)
        {
            byte[] damaged = [.. bytes];
            damaged[12 + random.Next(damaged.Length - 12)] ^= (byte)(1 << random.Next(8));
            try
            {
                Picture.Load(new MemoryStream(damaged));
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Fact]
    public void NestingAndExpansionAreBounded()
    {
        // Each picture draws the one before it twice: 70 levels would replay 2^70 calls.
        PictureRecorder first = new(new RectF(0, 0, 4, 4));
        first.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red);
        Picture picture = first.Finish();
        for (int i = 0; i < 70; i++)
        {
            PictureRecorder next = new(new RectF(0, 0, 4, 4));
            next.DrawPicture(picture);
            next.DrawPicture(picture);
            picture = next.Finish();
        }

        LimitExceededException e = Assert.Throws<LimitExceededException>(() => Picture.Load(new MemoryStream(Bytes(picture))));
        Assert.Equal("ExpandedDrawingCalls", e.LimitName);

        // Deep but cheap: one call per level.
        Picture deep = Chain(70);
        Assert.Equal("DrawingNesting", Assert.Throws<LimitExceededException>(() => Picture.Load(new MemoryStream(Bytes(deep)))).LimitName);
        Assert.Equal(1, Picture.Load(new MemoryStream(Bytes(Chain(40)))).CallCount);
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
            foreach (Picture d in Corpus(image).Append(Chain(3)))
            {
                File.WriteAllBytes(System.IO.Path.Combine(folder, $"seed{n++}.egdr"), Bytes(d));
            }
        }

        string[] seeds = Directory.GetFiles(folder);
        Assert.NotEmpty(seeds);
        foreach (string seed in seeds)
        {
            using FileStream file = File.OpenRead(seed);
            Assert.True(Picture.Load(file).CallCount > 0);
        }
    }

    private static Picture Chain(int levels)
    {
        PictureRecorder first = new(new RectF(0, 0, 4, 4));
        first.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red);
        Picture picture = first.Finish();
        for (int i = 0; i < levels; i++)
        {
            PictureRecorder next = new(new RectF(0, 0, 4, 4));
            next.DrawPicture(picture);
            picture = next.Finish();
        }

        return picture;
    }

    internal static IEnumerable<Picture> Corpus(Raster image)
    {
        yield return RecordingTests.Record(image);

        PictureRecorder patterns = new(new RectF(0, 0, 128, 100));
        PictureRecorder dot = new(new RectF(0, 0, 8, 8));
        dot.FillCircle(new Vector2(4, 4), 3, Rgba8.Blue);
        Picture tile = dot.Finish();
        patterns.FillRect(new RectF(0, 0, 128, 100), new PicturePaint(tile, TileMode.Repeat, TileMode.Mirror) { LocalMatrix = Matrix3x2.CreateRotation(0.3f) });
        patterns.FillOval(new RectF(10, 10, 60, 40), new BlendPaint(NoisePaint.Turbulence(0.05f, 0.08f, 3, 7, new Size(32, 32)), new ImagePaint(image, TileMode.Repeat, TileMode.Decal) { Opacity = 0.7f }, Operations.BlendMode.Screen));
        patterns.FillRect(new RectF(70, 50, 40, 40), new SweepGradient(new Vector2(90, 70), 20, 300, new GradientStop(0, Rgba8.Red), new GradientStop(1, Rgba8.Blue)) { Interpolation = GradientInterpolation.Oklch(HueMethod.Longer), ExtendStart = false });
        patterns.FillRect(new RectF(5, 60, 50, 30), new ConicalGradient(new Vector2(10, 70), 3, new Vector2(40, 75), 20, new GradientStop(0, Rgba8.Green), new GradientStop(1, Rgba8.Black)).WithColorMatrix(Operations.ColorMatrix.Identity) with { Dither = true });
        patterns.FillPath(Path.Circle(new Vector2(100, 20), 12), new SolidPaint(Rgba8.Red) { Blur = new MaskBlur(2, BlurStyle.Outer), Antialias = false });
        patterns.DrawPicture(tile, Matrix3x2.CreateScale(2), new SolidPaint(Rgba8.White) { Opacity = 0.5f }, cacheAsImage: true);
        patterns.ClipPath(Path.Circle(new Vector2(64, 50), 30), ClipOp.Difference, antialias: false);
        patterns.SaveLayer(new LayerOptions { Bounds = new RectF(0, 0, 50, 50), Isolated = false, BlendMode = Operations.BlendMode.Overlay, Opacity = 0.9f });
        patterns.DrawImageLattice(image, new Lattice([4, 12], [3, 9]) { Bounds = new Rect(1, 1, 14, 10), CellKinds = [.. Enumerable.Repeat(LatticeCellKind.Default, 8), LatticeCellKind.FixedColor], CellColors = [.. Enumerable.Repeat(new RgbaF32(0, 1, 1, 1), 9)] }, new RectF(0, 0, 40, 30));
        patterns.DrawMesh(new Mesh(MeshMode.Strip, [new(0, 0), new(0, 10), new(10, 0), new(10, 10)], texCoords: [new(0, 0), new(0, 12), new(16, 0), new(16, 12)], values: [0, 1, 0.5f, 0.2f]), new ImagePaint(image), Operations.BlendMode.Multiply, [new(0, Rgba8.Red), new(1, Rgba8.White)]);
        patterns.DrawPatch(Patch.Tensor([.. Enumerable.Range(0, 16).Select(i => new Vector2((i % 4) * 10, (i / 4) * 10))], cornerTexCoords: [new(0, 0), new(16, 0), new(16, 12), new(0, 12)]), new ImagePaint(image));
        yield return patterns.Finish();

        using MemoryStream jpeg = new();
        image.Save(jpeg, "jpeg");
        PictureRecorder document = new(new RectF(0, 0, 128, 100));
        document.BeginMarkedContent(new MarkedContent("Figure") { NodeId = 1, AlternateText = "A photo" });
        document.DrawImage(EncodedImage.FromBytes(jpeg.ToArray()), new RectF(4, 4, 64, 48));
        document.EndMarkedContent();
        document.DrawAnnotation(new RectF(4, 4, 64, 48), Annotation.Url("https://example.com/egami"));
        document.SaveLayer(new LayerOptions { Knockout = true, Opacity = 0.9f });
        document.FillRect(new RectF(70, 4, 40, 30), new RgbaF32(1, 0, 0, 0.5f));
        document.DrawImageMask(image, new RectF(80, 10, 40, 30), new RgbaF32(0, 0, 1, 0.5f));
        document.Restore();
        yield return document.Finish();
    }

    private static byte[] Bytes(Picture picture)
    {
        using MemoryStream stream = new();
        picture.Save(stream);
        return stream.ToArray();
    }

    private sealed record class Stripes : CustomPaint
    {
        public override void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse) => row.Fill(new PremulRgbaF32(0, 0, 0, 1));
    }
}
