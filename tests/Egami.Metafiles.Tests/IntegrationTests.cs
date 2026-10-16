using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles.Tests;

/// <summary>Stage D: what a vector canvas such as Egap's PDF page receives, and pictures inside metafiles.</summary>
public class IntegrationTests
{
    /// <summary>Counts the calls a document backend gets.</summary>
    private sealed class RecordingCanvas() : Canvas(400, 300)
    {
        public int EncodedImages { get; private set; }

        public int Rasters { get; private set; }

        public List<string?> Texts { get; } = [];

        public int Paths { get; private set; }

        protected override void OnClipPath(Egami.Drawing.Path path, ClipOp op, bool antialias)
        {
        }

        protected override void OnDrawPath(Egami.Drawing.Path path, Stroke? stroke, Paint paint) => Paths++;

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint) => Rasters++;

        protected override void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint) => EncodedImages++;

        protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint) => Texts.Add(run.Text);
    }

    [Fact]
    public void JpegAndPngReachTheCanvasEncoded()
    {
        // The images scenario draws a bitmap (GDI+ records it as a PNG too), a PNG and a JPEG: all go through unchanged (question 12).
        Metafile m = Metafile.Load(TestFiles.SyntheticPath("plus-images.plus.emf"));
        RecordingCanvas canvas = new();
        m.Draw(canvas, new RectF(0, 0, 400, 300));
        Assert.True(canvas.EncodedImages >= 2, $"{canvas.EncodedImages} encoded images");
    }

    [Fact]
    public void TextReachesTheCanvasAsGlyphRunsWithItsCharacters()
    {
        foreach (string name in (string[])["gdi-text-textout.emf", "gdi-text-textout.wmf", "plus-text-drawstring.plus.emf", "plus-text-drawdriverstring.plus.emf"])
        {
            RecordingCanvas canvas = new();
            Metafile.Load(TestFiles.SyntheticPath(name)).Draw(canvas, new RectF(0, 0, 400, 300));
            Assert.NotEmpty(canvas.Texts);
            Assert.All(canvas.Texts, t => Assert.False(string.IsNullOrEmpty(t)));
        }

        RecordingCanvas paths = new();
        Metafile.Load(TestFiles.SyntheticPath("gdi-text-textout.emf")).Draw(paths, new RectF(0, 0, 400, 300), new MetafileRenderOptions { TextAsPaths = true });
        Assert.Empty(paths.Texts);
        Assert.True(paths.Paths > 0);
    }

    [Fact]
    public void MultiformatsCommentPlaysItsEmf()
    {
        // An EMF whose only drawing is a GDICOMMENT_MULTIFORMATS carrying another EMF, drawn into the comment's rectangle.
        byte[] inner = File.ReadAllBytes(TestFiles.SyntheticPath("gdi-shapes-basic.emf"));
        byte[] outer = WrapInMultiformats(inner);
        Metafile wrapped = Metafile.Load(new MemoryStream(outer));
        Metafile plain = Metafile.Load(new MemoryStream(inner));
        using Raster<Rgba8> a = wrapped.Rasterize<Rgba8>(new MetafileRenderOptions { Width = 182, Height = 137 });
        using Raster<Rgba8> b = plain.Rasterize<Rgba8>(new MetafileRenderOptions { Width = 182, Height = 137 });
        if (Environment.GetEnvironmentVariable("EGAMI_TEST_OUT") is string dir)
        {
            a.Save(System.IO.Path.Combine(dir, "multiformats-a.png"));
            b.Save(System.IO.Path.Combine(dir, "multiformats-b.png"));
        }

        int same = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                same += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A) < 32 ? 1 : 0;
            }
        }

        Assert.True(same > a.Width * a.Height * 0.97, $"{same} of {a.Width * a.Height} pixels agree.");
    }

    /// <summary>A copy of the inner EMF's header, one EMR_GDICOMMENT (GDIC, MULTIFORMATS) holding the whole inner file, and EMR_EOF.</summary>
    private static byte[] WrapInMultiformats(byte[] inner)
    {
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(inner.AsSpan(4));
        byte[] header = inner[..headerSize];
        Span<byte> h = header;

        // The comment's output rectangle is the inner picture's bounds, in the outer file's logical units (MM_TEXT, reference pixels).
        int l = BinaryPrimitives.ReadInt32LittleEndian(h[8..]), t = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
        int r = BinaryPrimitives.ReadInt32LittleEndian(h[16..]), b = BinaryPrimitives.ReadInt32LittleEndian(h[20..]);
        int frameR = BinaryPrimitives.ReadInt32LittleEndian(h[32..]), frameB = BinaryPrimitives.ReadInt32LittleEndian(h[36..]);
        int devX = BinaryPrimitives.ReadInt32LittleEndian(h[72..]), mmX = BinaryPrimitives.ReadInt32LittleEndian(h[80..]);
        int devY = BinaryPrimitives.ReadInt32LittleEndian(h[76..]), mmY = BinaryPrimitives.ReadInt32LittleEndian(h[84..]);
        int outR = (int)Math.Round(frameR / 100.0 * devX / mmX), outB = (int)Math.Round(frameB / 100.0 * devY / mmY);
        _ = (l, t, r, b);

        int dataStart = 4 + 4 + 16 + 4 + 16; // identifier, comment type, output rect, count, one format
        int dataSize = dataStart + inner.Length;
        int commentSize = 12 + ((dataSize + 3) & ~3);
        byte[] comment = new byte[commentSize];
        Span<byte> c = comment;
        BinaryPrimitives.WriteInt32LittleEndian(c, 70);
        BinaryPrimitives.WriteInt32LittleEndian(c[4..], commentSize);
        BinaryPrimitives.WriteInt32LittleEndian(c[8..], dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(c[12..], 0x43494447);
        BinaryPrimitives.WriteUInt32LittleEndian(c[16..], 0x40000004);
        BinaryPrimitives.WriteInt32LittleEndian(c[28..], outR);
        BinaryPrimitives.WriteInt32LittleEndian(c[32..], outB);
        BinaryPrimitives.WriteInt32LittleEndian(c[36..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c[40..], 0x464D4520);
        BinaryPrimitives.WriteInt32LittleEndian(c[44..], 0x10000);
        BinaryPrimitives.WriteInt32LittleEndian(c[48..], inner.Length);
        BinaryPrimitives.WriteInt32LittleEndian(c[52..], 12 + dataStart); // from the start of the record
        inner.CopyTo(comment, 12 + dataStart);

        byte[] eof = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(eof, 14);
        BinaryPrimitives.WriteInt32LittleEndian(eof.AsSpan(4), 20);
        byte[] file = [.. header, .. comment, .. eof];
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(48), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(52), 3);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(56), 1);
        return file;
    }
}
