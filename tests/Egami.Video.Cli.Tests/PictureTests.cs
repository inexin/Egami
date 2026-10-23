using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Cli.Commands;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>thumbnail</c>, <c>scenes</c>, <c>sheet</c> and <c>animate</c> (Build Plan tasks 13 to 15): a typical frame is chosen, cuts
/// are found within a frame of a labelled set of clips, contact sheets match their references, and animations have the frames
/// and delays asked for.
/// </summary>
public sealed class PictureTests
{
    /// <summary>
    /// The labelled set: clips made of shots (solid colors, gradients, grain, moving shapes, color bars still and panning) joined
    /// by cuts, fades through black and dissolves. Only cuts are labelled; fades and dissolves must not be taken for cuts.
    /// </summary>
    private static readonly string[] LabelledClips =
    [
        "solid:red 30 | cut | solid:blue 30 | cut | solid:yellow 30",
        "hgrad:black,white 35 | cut | vgrad:black,white 35",
        "grain:1,gray 40 | cut | grain:2,orange 40 | cut | grain:3,teal 40",
        "moving:navy,white 40 | cut | moving:olive,pink 40",
        "bars:0 40 | cut | bars:30 40",
        "solid:red 30 | fade:10 | solid:blue 30",
        "hgrad:red,yellow 35 | dissolve:15 | vgrad:blue,cyan 35",
        "grain:4,gray 35 | fade:8 | moving:navy,white 35 | cut | bars:0 35",
        "bars:0 30 | cut | bars:12 30 | cut | bars:0 30",
        "moving:black,white 45 | cut | moving:white,black 45",
        "vgrad:navy,white 30 | cut | hgrad:navy,white 30 | dissolve:12 | solid:gray 30",
        "grain:5,brown 40 | cut | grain:6,brown 40",
        "solid:white 30 | fade:12 | grain:7,gray 35 | cut | solid:purple 30",
        "moving:teal,yellow 35 | cut | bars:20 35 | fade:6 | hgrad:black,orange 35",
        "hgrad:black,white 30 | cut | hgrad:white,black 30",
        "pan:0 50 | cut | solid:green 30",
        "grain:8,gray 30 | dissolve:20 | grain:9,gray 30 | cut | moving:navy,white 30",
        "solid:black 30 | cut | solid:gray 30 | cut | solid:white 30",
        "moving:olive,pink 30 | cut | moving:orange,pink 30",
        "vgrad:red,blue 30 | cut | grain:10,navy 30 | cut | bars:40 30 | cut | solid:orange 30",
    ];

    private static readonly Dictionary<string, Rgba8> Colors = new(StringComparer.Ordinal)
    {
        ["black"] = new(0, 0, 0, 255),
        ["white"] = new(255, 255, 255, 255),
        ["gray"] = new(128, 128, 128, 255),
        ["red"] = new(220, 30, 30, 255),
        ["green"] = new(40, 200, 60, 255),
        ["blue"] = new(30, 40, 220, 255),
        ["yellow"] = new(240, 230, 40, 255),
        ["cyan"] = new(40, 220, 230, 255),
        ["navy"] = new(20, 30, 100, 255),
        ["orange"] = new(250, 140, 20, 255),
        ["teal"] = new(20, 130, 130, 255),
        ["olive"] = new(120, 120, 30, 255),
        ["pink"] = new(250, 150, 200, 255),
        ["brown"] = new(120, 70, 30, 255),
        ["purple"] = new(130, 40, 160, 255),
    };

    private static readonly Rgba8[] Bars = [new(235, 235, 235, 255), new(235, 235, 20, 255), new(20, 235, 235, 255), new(20, 235, 20, 255), new(235, 20, 235, 255), new(235, 20, 20, 255)];

    [Fact]
    public void ScenesMatchTheLabelledSet()
    {
        using Tool tool = new();
        List<string> misses = [];
        for (int clip = 0; clip < LabelledClips.Length; clip++)
        {
            string name = $"clip{clip:00}.mkv";
            List<int> cuts = MakeClip(tool.PathOf(name), LabelledClips[clip]);
            ToolRun run = tool.Run("scenes", name, "--json");
            Assert.True(run.Code == 0, run.Error);
            using JsonDocument json = JsonDocument.Parse(run.Text);
            List<int> found = [.. json.RootElement.GetProperty("files")[0].GetProperty("scenes").EnumerateArray().Skip(1).Select(s => s.GetProperty("frame").GetInt32())];
            if (found.Count != cuts.Count || found.Zip(cuts).Any(p => Math.Abs(p.First - p.Second) > 1))
            {
                misses.Add($"{LabelledClips[clip]}: labelled [{string.Join(", ", cuts)}], found [{string.Join(", ", found)}]");
            }
        }

        Assert.True(misses.Count == 0, string.Join("\n", misses));
    }

    [Fact]
    public void ScenesTextCsvAndFrames()
    {
        using Tool tool = new();
        MakeClip(tool.PathOf("a.mkv"), "solid:red 30 | cut | grain:1,gray 30 | cut | solid:blue 30");
        ToolRun text = tool.Run("scenes", "a.mkv", "-o", "shots/{name}-{scene:000}.png");
        Assert.True(text.Code == 0, text.Error);
        Assert.Equal(["scene   frame  time         score", "1", "2", "3"], text.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((l, i) => i == 0 ? l : l.Trim().Split(' ')[0]));
        Assert.Contains("0:00:01.200", text.Text, StringComparison.Ordinal);
        Assert.Equal(["a-001.png", "a-002.png", "a-003.png"], Directory.GetFiles(tool.PathOf("shots")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using (Raster second = Raster.Load(tool.PathOf("shots/a-003.png")))
        {
            Rgba8 pixel = second.As<Rgba8>().Row(10)[10];
            Assert.True(pixel.B > 180 && pixel.R < 80, $"the third scene starts blue, not {pixel}");
        }

        ToolRun csv = tool.Run("scenes", "a.mkv", "--csv", "--threshold", "0.3", "--min-gap", "500ms");
        Assert.True(csv.Code == 0, csv.Error);
        string[] rows = csv.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("file,scene,frame,time,seconds,score", rows[0]);
        Assert.Equal(4, rows.Length);
        Assert.StartsWith("a.mkv,2,30,0:00:01.200,1.2,", rows[2], StringComparison.Ordinal);

        // A minimum gap longer than the scenes keeps only the first cut after it.
        ToolRun gap = tool.Run("scenes", "a.mkv", "--json", "--min-gap", "2s");
        using JsonDocument json = JsonDocument.Parse(gap.Text);
        Assert.Equal(2, json.RootElement.GetProperty("files")[0].GetProperty("scenes").GetArrayLength());
    }

    [Theory]
    [InlineData("--json and --csv", "not both", "scenes", "a.mkv", "--json", "--csv")]
    [InlineData("threshold", "--threshold must be from 0 to 1", "scenes", "a.mkv", "--threshold", "1.5")]
    [InlineData("gap", "--min-gap must be", "scenes", "a.mkv", "--min-gap", "#3")]
    [InlineData("one file", "would write every scene to one file", "scenes", "a.mkv", "-o", "shot.png")]
    [InlineData("thumbnail output", "No output", "thumbnail", "a.mkv")]
    [InlineData("past the end", "past the end", "thumbnail", "a.mkv", "-o", "t.jpg", "--at", "10")]
    [InlineData("past the last frame", "past the last frame", "thumbnail", "a.mkv", "-o", "t.jpg", "--at", "#500")]
    [InlineData("size", "give a width, a height or both", "thumbnail", "a.mkv", "-o", "t.jpg", "--size", "x")]
    [InlineData("format", "not an image format", "thumbnail", "a.mkv", "-o", "t.xyz")]
    [InlineData("track", "no video track", "thumbnail", "a.mkv", "-o", "t.jpg", "--track", "a")]
    [InlineData("grid", "not a grid", "sheet", "a.mkv", "-o", "s.png", "--grid", "0x3")]
    [InlineData("width", "--width must be", "sheet", "a.mkv", "-o", "s.png", "--width", "10")]
    [InlineData("narrow", "under 16 pixels wide", "sheet", "a.mkv", "-o", "s.png", "--width", "200", "--grid", "20x1")]
    [InlineData("animate format", "animate writes GIF, WebP or PNG", "animate", "a.mkv", "-o", "a.jpg")]
    [InlineData("rate", "--rate must be above 0", "animate", "a.mkv", "-o", "a.gif", "--rate", "0")]
    [InlineData("order", "--to must come after --from", "animate", "a.mkv", "-o", "a.gif", "--from", "1", "--to", "0.5")]
    [InlineData("loop", "--loop must be", "animate", "a.mkv", "-o", "a.gif", "--loop", "-1")]
    [InlineData("several inputs", "would all be written", "animate", "a.mkv", "b.mkv", "-o", "a.gif")]
    [InlineData("replace", "would replace the input", "thumbnail", "a.mkv", "-o", "a.mkv", "--format", "png")]
    public void UsageErrors(string what, string message, params string[] args)
    {
        _ = what;
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "a.mkv");
        tool.Copy("mkv-mjpeg-pcm.mkv", "b.mkv");
        ToolRun run = tool.Run(args);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(tool.Folder, "*.*"), f => Path.GetFileName(f) is not ("a.mkv" or "b.mkv"));
    }

    [Fact]
    public void ThumbnailChoosesATypicalFrame()
    {
        using Tool tool = new();

        // Black frames first, a few frames of another shot among the main one: the typical frame is the gradient's.
        MakeClip(tool.PathOf("clip.mkv"), "solid:black 20 | cut | hgrad:red,yellow 40 | cut | grain:3,blue 6 | cut | hgrad:red,yellow 40");
        ToolRun run = tool.Run("thumbnail", "clip.mkv", "-o", "thumb.png");
        Assert.True(run.Code == 0, run.Error);
        using Raster<Rgba8> thumb = Raster.Load(tool.PathOf("thumb.png")).As<Rgba8>();
        Assert.Equal((96, 54), (thumb.Width, thumb.Height));
        Rgba8 left = thumb.Row(27)[2], right = thumb.Row(27)[93];
        Assert.True(left.R > 180 && left.G < 90, $"left {left}");
        Assert.True(right.R > 180 && right.G > 180, $"right {right}");

        // The same from standard input, which is read once.
        ToolRun piped = tool.Run(["thumbnail", "-", "-o", "piped.png"], File.ReadAllBytes(tool.PathOf("clip.mkv")));
        Assert.True(piped.Code == 0, piped.Error);
        Assert.Equal(File.ReadAllBytes(tool.PathOf("thumb.png")), File.ReadAllBytes(tool.PathOf("piped.png")));
    }

    [Fact]
    public void ChoosesAwayFromBlackAndFlatFrames()
    {
        FrameSummary Frame(double mean, double spread, int bin) => new([], [.. Enumerable.Range(0, 64).Select(b => b == bin ? 1.0 : 0)], mean, spread);
        Assert.Equal(2, PictureCommands.ChooseRepresentative([Frame(0.01, 0.0, 0), Frame(0.5, 0.0, 5), Frame(0.5, 0.2, 9), Frame(0.5, 0.2, 9), Frame(0.5, 0.2, 7)]));
        Assert.Equal(0, PictureCommands.ChooseRepresentative([Frame(0.01, 0.0, 0), Frame(0.01, 0.0, 0)]));
    }

    [Fact]
    public void ThumbnailAtATimeSizedAndBatched()
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "in/a.mkv");
        tool.Copy("webm-vp8-opus.webm", "in/b.webm");
        ToolRun run = tool.Run("thumbnail", "in/*", "-o", "thumbs/", "--size", "80x", "--at", "1.0");
        Assert.True(run.Code == 0, run.Error);
        foreach (string name in (string[])["a", "b"])
        {
            using Raster thumb = Raster.Load(tool.PathOf($"thumbs/{name}.jpg"));
            Assert.Equal((80, 60), (thumb.Width, thumb.Height));
        }

        Assert.Equal(0, tool.Run("thumbnail", "in/a.mkv", "-o", "frame3.png", "--at", "#3").Code);
        Assert.Equal(0, tool.Run("thumbnail", "in/a.mkv", "-o", "time.png", "--at", "0.12").Code);
        Assert.Equal(File.ReadAllBytes(tool.PathOf("frame3.png")), File.ReadAllBytes(tool.PathOf("time.png")));
        ToolRun stdout = tool.Run("thumbnail", "in/a.mkv", "-o", "-", "--format", "png", "--at", "#3");
        Assert.Equal(File.ReadAllBytes(tool.PathOf("frame3.png")), stdout.Bytes);
    }

    [Theory]
    [InlineData("sheet-4x4", "webm-vp8-opus.webm", "--width", "480")]
    [InlineData("sheet-3x2-timestamps-title", "avi-mjpeg-420-pcm.avi", "--grid", "3x2", "--width", "720", "--timestamps", "--title")]
    [InlineData("sheet-5x1-timestamps", "y4m-420jpeg.y4m", "--grid", "5x1", "--width", "640", "--timestamps")]
    public void SheetMatchesItsReference(string reference, string clip, params string[] options)
    {
        // References come from tools/make-video-cli-references.py, which runs the same arguments.
        using Tool tool = new();
        tool.Copy(clip);
        ToolRun run = tool.Run(["sheet", clip, "-o", "sheet.png", .. options]);
        Assert.True(run.Code == 0, run.Error);
        AssertClose(Path.Combine(ReferenceFolder, reference + ".png"), tool.PathOf("sheet.png"));
    }

    [Fact]
    public void ThumbnailMatchesItsReference()
    {
        using Tool tool = new();
        tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("thumbnail", "webm-vp8-opus.webm", "-o", "thumb.png", "--size", "120x");
        Assert.True(run.Code == 0, run.Error);
        AssertClose(Path.Combine(ReferenceFolder, "thumbnail-120.png"), tool.PathOf("thumb.png"));
    }

    [Fact]
    public void SheetFromStandardInput()
    {
        using Tool tool = new();
        ToolRun run = tool.Run(["sheet", "-", "-o", "sheet.png", "--grid", "2x2", "--width", "400"], File.ReadAllBytes(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv")));
        Assert.True(run.Code == 0, run.Error);
        using Raster sheet = Raster.Load(tool.PathOf("sheet.png"));
        Assert.Equal(400, sheet.Width);
    }

    [Theory]
    [InlineData("out.gif", "10", null, null, 20, 100)]
    [InlineData("out.webp", "5", null, null, 10, 200)]
    [InlineData("out.png", "10", "0.5", "1.5", 10, 100)]
    [InlineData("out.gif", "4", "#10", null, 7, 250)]
    public void AnimationsHaveTheFramesAndDelays(string output, string rate, string? from, string? to, int frames, int delay)
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "clip.mkv");
        List<string> args = ["animate", "clip.mkv", "-o", output, "--rate", rate, "--size", "80x", "--loop", "3"];
        if (from is not null)
        {
            args.AddRange(["--from", from]);
        }

        if (to is not null)
        {
            args.AddRange(["--to", to]);
        }

        ToolRun run = tool.Run([.. args]);
        Assert.True(run.Code == 0, run.Error);
        using Raster<Rgba8> animation = Raster.Load(tool.PathOf(output)).As<Rgba8>();
        Assert.Equal(frames, animation.FrameCount);
        Assert.Equal((80, 60), (animation.Width, animation.Height));
        Assert.All(Enumerable.Range(0, frames - 1), i => Assert.Equal(delay, animation.Frames[i].Metadata.Delay.TotalMilliseconds));
        Assert.Equal(3, animation.Metadata.LoopCount);
        // ffmpeg's WebP decoder reads no animations, so only GIF and APNG are counted by ffprobe too.
        if (!output.EndsWith(".webp", StringComparison.Ordinal) && Ffprobe(tool.PathOf(output)) is { } counted)
        {
            Assert.Equal(frames, counted);
        }
    }

    [Fact]
    public void AnimationHoldsFramesWhenTheRateIsHigher()
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "clip.mkv");
        Assert.Equal(0, tool.Run("animate", "clip.mkv", "-o", "fast.gif", "--rate", "50", "--to", "1").Code);
        using Raster<Rgba8> animation = Raster.Load(tool.PathOf("fast.gif")).As<Rgba8>();
        Assert.Equal(25, animation.FrameCount);
        Assert.All(Enumerable.Range(0, 25), i => Assert.Equal(40, animation.Frames[i].Metadata.Delay.TotalMilliseconds));
    }

    [Theory]
    [InlineData("thumbnail", "-o", "out.jpg")]
    [InlineData("scenes", "-o", "shots/")]
    [InlineData("sheet", "-o", "out.png")]
    [InlineData("animate", "-o", "out.gif")]
    public void CancelledJobLeavesNoFile(string command, params string[] options)
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        ToolRun run = tool.Run([command, Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), .. options]);
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>The folder of this tool's reference images, tests/reference/video-cli.</summary>
    private static string ReferenceFolder => Path.Combine(Path.GetDirectoryName(Tool.Images)!, "reference", "video-cli");

    /// <summary>Asserts that two images have the same size and differ by at most a few levels anywhere, as fonts and resampling may.</summary>
    private static void AssertClose(string expectedPath, string actualPath)
    {
        using Raster<Rgba8> expected = Raster.Load(expectedPath).As<Rgba8>();
        using Raster<Rgba8> actual = Raster.Load(actualPath).As<Rgba8>();
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int worst = 0;
        long total = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgba8> a = expected.Row(y), b = actual.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                int d = Math.Max(Math.Max(Math.Abs(a[x].R - b[x].R), Math.Abs(a[x].G - b[x].G)), Math.Abs(a[x].B - b[x].B));
                worst = Math.Max(worst, d);
                total += d;
            }
        }

        double mean = (double)total / (expected.Width * expected.Height);
        Assert.True(worst <= 8 && mean <= 0.25, $"{Path.GetFileName(expectedPath)}: differs by up to {worst} levels, {mean:0.000} on average");
    }

    /// <summary>The frames ffprobe counts in a file, or <see langword="null"/> when ffprobe is not on the PATH.</summary>
    private static int? Ffprobe(string path)
    {
        try
        {
            ProcessStartInfo start = new("ffprobe", ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && int.TryParse(output.Trim().TrimEnd(','), NumberStyles.None, CultureInfo.InvariantCulture, out int frames) ? frames : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Writes a clip of shots and transitions as Motion JPEG at 25 frames per second; returns the first frame of each cut's new shot.</summary>
    private static List<int> MakeClip(string path, string spec)
    {
        const int width = 96, height = 54;
        string[] parts = [.. spec.Split('|').Select(p => p.Trim())];
        List<(string Kind, string[] Args, int Length)> shots = [];
        List<string> joins = [];
        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                joins.Add(parts[i]);
                continue;
            }

            string[] words = parts[i].Split(' ');
            int colon = words[0].IndexOf(':', StringComparison.Ordinal);
            shots.Add((words[0][..colon], words[0][(colon + 1)..].Split(','), int.Parse(words[1], CultureInfo.InvariantCulture)));
        }

        List<int> cuts = [];
        int frame = 0;
        using VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = MotionJpegCodec.Instance, FrameRate = new Rational(25, 1), Quality = 92 });
        void Write(Raster<Rgba8> image)
        {
            writer.WriteFrame(image);
            image.Dispose();
            frame++;
        }

        for (int s = 0; s < shots.Count; s++)
        {
            string join = s == 0 ? "cut" : joins[s - 1];
            int length = shots[s].Length;
            int start = 0;
            if (join.StartsWith("fade:", StringComparison.Ordinal))
            {
                int n = int.Parse(join[5..], CultureInfo.InvariantCulture);
                for (int k = 1; k <= n; k++)
                {
                    Write(Mix(Paint(shots[s - 1], shots[s - 1].Length + k - 1, width, height), null, 1 - (k / (double)(n + 1))));
                }

                for (int k = 1; k <= n; k++)
                {
                    Write(Mix(Paint(shots[s], k - 1, width, height), null, k / (double)(n + 1)));
                }

                start = n;
            }
            else if (join.StartsWith("dissolve:", StringComparison.Ordinal))
            {
                int n = int.Parse(join[9..], CultureInfo.InvariantCulture);
                for (int k = 1; k <= n; k++)
                {
                    using Raster<Rgba8> next = Paint(shots[s], k - 1, width, height);
                    Write(Mix(Paint(shots[s - 1], shots[s - 1].Length + k - 1, width, height), next, 1 - (k / (double)(n + 1))));
                }

                start = n;
            }
            else if (s > 0)
            {
                cuts.Add(frame);
            }

            for (int t = start; t < start + length; t++)
            {
                Write(Paint(shots[s], t, width, height));
            }
        }

        return cuts;
    }

    /// <summary>Blends an image toward another (or black) by weight, the image's share; disposes the other.</summary>
    private static Raster<Rgba8> Mix(Raster<Rgba8> image, Raster<Rgba8>? other, double weight)
    {
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgba8> row = image.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 o = other?.Row(y)[x] ?? new Rgba8(0, 0, 0, 255);
                row[x] = new Rgba8(Lerp(row[x].R, o.R), Lerp(row[x].G, o.G), Lerp(row[x].B, o.B), 255);
            }
        }

        return image;
        byte Lerp(byte a, byte b) => (byte)Math.Round((a * weight) + (b * (1 - weight)));
    }

    /// <summary>Frame <paramref name="t"/> of a shot.</summary>
    private static Raster<Rgba8> Paint((string Kind, string[] Args, int Length) shot, int t, int width, int height)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(width, height);
        string[] a = shot.Args;
        Random? noise = shot.Kind == "grain" ? new Random((int.Parse(a[0], CultureInfo.InvariantCulture) * 1000) + t) : null;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = shot.Kind switch
                {
                    "solid" => Colors[a[0]],
                    "hgrad" => Blend(Colors[a[0]], Colors[a[1]], x / (double)(width - 1)),
                    "vgrad" => Blend(Colors[a[0]], Colors[a[1]], y / (double)(height - 1)),
                    "grain" => Grain(Colors[a[1]], int.Parse(a[0], CultureInfo.InvariantCulture), x, y, noise!),
                    "moving" => Moving(Colors[a[0]], Colors[a[1]], x, y, t, width, height),
                    "bars" => Bars[((x + int.Parse(a[0], CultureInfo.InvariantCulture)) / 16) % Bars.Length],
                    "pan" => Bars[((x + int.Parse(a[0], CultureInfo.InvariantCulture) + t) / 16) % Bars.Length],
                    _ => throw new ArgumentException(shot.Kind),
                };
            }
        }

        return image;
    }

    private static Rgba8 Blend(Rgba8 a, Rgba8 b, double f) =>
        new((byte)Math.Round(a.R + ((b.R - a.R) * f)), (byte)Math.Round(a.G + ((b.G - a.G) * f)), (byte)Math.Round(a.B + ((b.B - a.B) * f)), 255);

    /// <summary>A still texture of 6-pixel blocks around a color, seeded, with a little noise that changes every frame.</summary>
    private static Rgba8 Grain(Rgba8 color, int seed, int x, int y, Random noise)
    {
        uint hash = (uint)((seed * 73856093) ^ ((x / 6) * 19349663) ^ ((y / 6) * 83492791));
        hash ^= hash >> 13;
        hash *= 0x5bd1e995;
        hash ^= hash >> 15;
        int block = (int)(hash % 121) - 60;
        int n = noise.Next(-10, 11);
        return new Rgba8(Clamp(color.R + block + n), Clamp(color.G + block + n), Clamp(color.B + block + n), 255);
        static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    /// <summary>A rectangle moving across a background, bouncing up and down.</summary>
    private static Rgba8 Moving(Rgba8 background, Rgba8 shape, int x, int y, int t, int width, int height)
    {
        int left = (t * 2) % (width + 20) - 20;
        int top = Math.Abs(((t * 1) % (2 * (height - 14))) - (height - 14));
        return x >= left && x < left + 20 && y >= top && y < top + 14 ? shape : background;
    }
}
