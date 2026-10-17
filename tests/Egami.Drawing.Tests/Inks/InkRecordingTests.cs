using System.Buffers.Binary;
using System.Numerics;

using Egami.Colors;
using Egami.Drawing.Tests.Canvas;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 13: pictures keep ink paints, overprint and layer colour spaces, in memory and in .egdr, whose INKS chunk older
/// readers skip to draw the appearances.
/// </summary>
public class InkRecordingTests
{
    private static readonly InkColor Red186 = InkColor.Spot("PANTONE 186 C", 0.8f, InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
    private static readonly InkColor Gold = InkColor.Mix(new InkTint(Colorant.Spot("Gold", InkAppearance.Lab(new LabColor(70, 5, 60))), 0.6f), new InkTint(Colorant.Black, 0.2f));
    private static readonly InkColor Varnish = InkColor.Spot(Colorant.Technical("Varnish"), 1);
    private static readonly InkSet Set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", null).WithSpot("Gold", null).WithSpot("Varnish", null, ColorantKind.Technical);

    [Fact]
    public void APicturePlaysBackItsInks()
    {
        Picture picture = Record(Scene);
        Assert.Equal(Plates(Scene), Plates(picture.Playback));
    }

    [Fact]
    public void EgdrKeepsTheInks()
    {
        Picture picture = Record(Scene);
        byte[] bytes = Bytes(picture);
        Picture loaded = Picture.Load(new MemoryStream(bytes));

        Assert.Equal(bytes, Bytes(loaded));
        Assert.Equal(Plates(Scene), Plates(loaded.Playback));
        using TraceCanvas a = new(64, 48), b = new(64, 48);
        picture.Playback(a);
        loaded.Playback(b);
        Assert.Equal(a.Calls, b.Calls);
        Assert.Contains(b.Calls, c => c.Contains("inks PANTONE 186 C 0.8", StringComparison.Ordinal) && c.Contains("overprint On", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadersWithoutTheInksChunkDrawTheAppearances()
    {
        Picture picture = Record(Scene);
        Picture older = Picture.Load(new MemoryStream(WithoutInks(Bytes(picture))));

        // RGB is unchanged; on plates the colours come through their appearances, so the spot plates stay empty.
        using Raster<Rgba8> rgb = Advanced.Pixels.Draw(64, 48, picture.Playback), olderRgb = Advanced.Pixels.Draw(64, 48, older.Playback);
        Advanced.Pixels.Equal(rgb, olderRgb);
        byte[][] plates = Plates(older.Playback);
        Assert.All(plates[Set.IndexOf("PANTONE 186 C")], v => Assert.Equal(0, v));
        Assert.All(plates[Set.IndexOf("Varnish")], v => Assert.Equal(0, v));
        using TraceCanvas trace = new(64, 48);
        older.Playback(trace);
        Assert.DoesNotContain(trace.Calls, c => c.Contains("inks", StringComparison.Ordinal) || c.Contains("overprint", StringComparison.Ordinal));
    }

    [Fact]
    public void BrokenInkEntriesAreReported()
    {
        byte[] bytes = Bytes(Record(c => c.FillRect(new RectF(0, 0, 8, 8), new SolidPaint(Red186))));
        int inks = Find(bytes, "INKS");
        int at = inks + 8 + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), 3);   // the colour's offset moved off any colour
        Assert.Throws<CorruptDataException>(() => Picture.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void TheFuzzSeedHasInks()
    {
        string seed = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "images", "fuzz", "egdr-seeds", "inks.egdr");
        if (Environment.GetEnvironmentVariable("EGAMI_WRITE_EGDR_SEEDS") == "1")
        {
            File.WriteAllBytes(seed, Bytes(Record(Scene)));
        }

        Assert.Equal(Plates(Scene), Plates(Picture.Load(new MemoryStream(File.ReadAllBytes(seed))).Playback));
    }

    private static void Scene(Egami.Drawing.Canvas canvas)
    {
        canvas.FillRect(new RectF(0, 0, 64, 48), new SolidPaint(InkColor.Cmyk(0.3f, 0.1f, 0, 0)));
        canvas.FillRect(new RectF(4, 4, 24, 16), new SolidPaint(Red186) { Overprint = Overprint.On });
        canvas.FillRect(new RectF(30, 4, 30, 16), new LinearGradient(new Vector2(30, 0), new Vector2(60, 0), [new GradientStop(0, Gold), new GradientStop(1, InkColor.Cmyk(0, 0, 1, 0))]) { Overprint = Overprint.NonZero });
        canvas.DrawMesh(new Mesh(MeshMode.Triangles, [new(4, 24), new(28, 24), new(16, 44)]).WithInks([Red186, Gold, InkColor.Cmyk(1, 0, 0, 0)]));
        Vector2[] net = [new(32, 24), new(40, 23), new(50, 25), new(60, 24), new(61, 30), new(60, 37), new(60, 44), new(50, 45), new(40, 43), new(32, 44), new(33, 37), new(31, 30)];
        canvas.DrawPatch(Patch.Coons(net).WithInks([Gold, Red186, InkColor.Cmyk(0, 0.5f, 0, 0), InkColor.None]));
        canvas.SaveLayer(new LayerOptions { ColorSpace = ColorProfile.Srgb, Opacity = 0.7f });
        canvas.FillRect(new RectF(10, 10, 40, 6), new RgbaF32(0.2f, 0.6f, 0.3f, 1));
        canvas.FillRect(new RectF(12, 30, 8, 8), new SolidPaint(Varnish));
        canvas.Restore();
    }

    private static Picture Record(Action<Egami.Drawing.Canvas> draw)
    {
        PictureRecorder recorder = new(new RectF(0, 0, 64, 48));
        draw(recorder);
        return recorder.Finish();
    }

    private static byte[][] Plates(Action<Egami.Drawing.Canvas> draw)
    {
        using InkRaster plates = new(Set, 64, 48);
        using (InkCanvas canvas = new(plates))
        {
            draw(canvas);
        }

        return [.. plates.Plates.Select(p => Enumerable.Range(0, 48).SelectMany(y => p.Row8(y).ToArray()).ToArray()), Enumerable.Range(0, 48).SelectMany(y => plates.Alpha.Row8(y).ToArray()).ToArray()];
    }

    private static byte[] Bytes(Picture picture)
    {
        using MemoryStream stream = new();
        picture.Save(stream);
        return stream.ToArray();
    }

    // The file as a reader of minor version 0 sees it: the INKS chunks gone.
    private static byte[] WithoutInks(byte[] bytes)
    {
        List<byte> kept = [.. bytes.AsSpan(0, 12)];
        for (int at = 12; at < bytes.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (!bytes.AsSpan(at, 4).SequenceEqual("INKS"u8))
            {
                kept.AddRange(bytes.AsSpan(at, 8 + length));
            }

            at += 8 + length;
        }

        Assert.True(kept.Count < bytes.Length);
        return [.. kept];
    }

    private static int Find(byte[] bytes, string tag) => bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(tag));
}
