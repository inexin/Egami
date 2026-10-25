using System.IO.Compression;
using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Editing;

namespace Egami.Video.Tests.Editing;

/// <summary>
/// The V9 stage A gates against FFmpeg (tools/video/make-edit-references.py): join frame counts (task 4), fades (task 5),
/// xfade fades and wipes (task 6), overlay (task 7) and stacks (task 9), compared on the same raw clips.
/// </summary>
public class EditReferenceTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "corpus", "editing");

    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json"))).RootElement;

    private static readonly EditCanvas Canvas = new(96, 64, new Rational(10, 1));

    public static TheoryData<int> Joins => [.. Enumerable.Range(0, 10)];

    public static TheoryData<string> Pictures => ["fade", "xfade-fade", "xfade-wipeleft", "xfade-wiperight", "xfade-wipeup", "xfade-wipedown", "overlay", "hstack", "vstack", "xstack"];

    [Theory]
    [MemberData(nameof(Joins))]
    public void JoinFrameCountsMatchFfmpeg(int index)
    {
        JsonElement join = Manifest.GetProperty("joins")[index];
        VideoEdit edit = new(Canvas);
        foreach (JsonElement clip in join.GetProperty("clips").EnumerateArray())
        {
            edit.Track.Add(Clip.Open(Path.Combine(Folder, clip.GetString() + ".avi")));
        }

        Assert.Equal(join.GetProperty("frames").GetInt32(), edit.Plan().FrameCount);
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void PicturesMatchFfmpeg(string name)
    {
        JsonElement reference = Manifest.GetProperty("pictures").GetProperty(name);
        int width = reference.GetProperty("width").GetInt32(), height = reference.GetProperty("height").GetInt32();
        Clip e1 = Open("e1"), e2 = Open("e2");
        VideoEdit edit = new(name.EndsWith("stack", StringComparison.Ordinal) ? null : Canvas);
        MediaTime half = MediaTime.FromSeconds(0.5);
        switch (name)
        {
            case "fade":
                edit.Track.Add(e1.FadeIn(half).FadeOut(half));
                break;
            case "xfade-fade":
                edit.Track.Add(e1).Add(e2, Transition.Fade(half));
                break;
            case "overlay":
                edit.Track.Add(e1);
                edit.Layers.Add(Layer.Of(Open("logo")).Place(new Rect(10, 5, 32, 24)));
                break;
            case "hstack":
                edit.Track.Add(Stack.Horizontal(e1, e2));
                break;
            case "vstack":
                edit.Track.Add(Stack.Vertical(e1, e2));
                break;
            case "xstack":
                edit.Track.Add(Stack.Grid(2, 2, [e1, e2, e2, e1]));
                break;
            default:
                TransitionDirection direction = Enum.Parse<TransitionDirection>(name["xfade-wipe".Length..], ignoreCase: true);
                edit.Track.Add(e1).Add(e2, Transition.Wipe(direction, half));
                break;
        }

        byte[] expected;
        using (GZipStream gzip = new(File.OpenRead(Path.Combine(Folder, name + ".rgb.gz")), CompressionMode.Decompress))
        using (MemoryStream all = new())
        {
            gzip.CopyTo(all);
            expected = all.ToArray();
        }

        int frameBytes = width * height * 3, frames = 0;
        bool wipe = name.StartsWith("xfade-wipe", StringComparison.Ordinal);
        bool vertical = name is "xfade-wipeup" or "xfade-wipedown";
        double worst = double.PositiveInfinity;
        int widest = 0;
        foreach (Raster<Rgba8> frame in edit.RenderFrames())
        {
            Assert.Equal(width, frame.Width);
            Assert.Equal(height, frame.Height);
            ReadOnlySpan<byte> rgb = expected.AsSpan(frames * frameBytes, frameBytes);
            worst = Math.Min(worst, Psnr(frame, rgb));
            if (wipe)
            {
                widest = Math.Max(widest, EdgeError(frame, rgb, vertical));
            }

            frames++;
        }

        Assert.Equal(reference.GetProperty("frames").GetInt32(), frames);
        if (wipe)
        {
            // The geometry gate: the edge lands within 1 px of FFmpeg's on every line of every frame.
            Assert.True(widest <= 1, $"{name}: the edge is {widest} px off on some line");
        }
        else
        {
            Assert.True(worst >= 40, $"{name}: worst frame PSNR {worst:F1} dB (gate 40)");
        }
    }

    private static Clip Open(string name) => Clip.Open(Path.Combine(Folder, name + ".avi"));

    /// <summary>The most pixels on one line across the wipe that differ clearly from FFmpeg's.</summary>
    private static int EdgeError(Raster<Rgba8> frame, ReadOnlySpan<byte> rgb, bool vertical)
    {
        int worst = 0;
        int lines = vertical ? frame.Width : frame.Height, length = vertical ? frame.Height : frame.Width;
        for (int line = 0; line < lines; line++)
        {
            int count = 0;
            for (int i = 0; i < length; i++)
            {
                int x = vertical ? line : i, y = vertical ? i : line;
                Rgba8 p = frame.Row(y)[x];
                int o = ((y * frame.Width) + x) * 3;
                if (Math.Abs(p.R - rgb[o]) + Math.Abs(p.G - rgb[o + 1]) + Math.Abs(p.B - rgb[o + 2]) > 24)
                {
                    count++;
                }
            }

            worst = Math.Max(worst, count);
        }

        return worst;
    }

    private static double Psnr(Raster<Rgba8> frame, ReadOnlySpan<byte> rgb)
    {
        double sum = 0;
        int i = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            foreach (Rgba8 p in frame.Row(y))
            {
                double dr = p.R - rgb[i], dg = p.G - rgb[i + 1], db = p.B - rgb[i + 2];
                sum += (dr * dr) + (dg * dg) + (db * db);
                i += 3;
            }
        }

        double mse = sum / (frame.Width * frame.Height * 3);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }
}
