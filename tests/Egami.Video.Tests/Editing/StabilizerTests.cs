using System.Numerics;

using Egami.Operations;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Editing;

namespace Egami.Video.Tests.Editing;

/// <summary>V9 stage C: motion estimation (task 20) and stabilization (task 21) on synthetic shakes of known size.</summary>
public class StabilizerTests
{
    private const int Width = 320, Height = 240, Frames = 40;

    /// <summary>A textured scene larger than the frame: smooth random blobs, so corners and gradients are everywhere.</summary>
    private static Raster<Rgba8> Scene()
    {
        Random random = new(5);
        Raster<Rgba8> scene = Raster.Create(Width * 2, Height * 2, new Rgba8(90, 90, 90, 255));
        for (int i = 0; i < 400; i++)
        {
            int cx = random.Next(scene.Width), cy = random.Next(scene.Height), r = 4 + random.Next(14);
            byte level = (byte)random.Next(256);
            for (int y = Math.Max(0, cy - r); y < Math.Min(scene.Height, cy + r); y++)
            {
                Span<Rgba8> row = scene.Row(y);
                for (int x = Math.Max(0, cx - r); x < Math.Min(scene.Width, cx + r); x++)
                {
                    if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < r * r)
                    {
                        row[x] = new Rgba8(level, (byte)(255 - level), (byte)(level / 2), 255);
                    }
                }
            }
        }

        scene.Apply(ops => ops.Blur(1.2f));
        return scene;
    }

    /// <summary>Writes a clip of the scene seen through a camera at the given offsets and angles; returns its path.</summary>
    private static string Shaky(string name, Func<int, (double X, double Y, double Angle)> camera)
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-stab-{name}-{Environment.ProcessId}.mkv");
        using Raster<Rgba8> scene = Scene();
        using VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = RawVideoCodec.Instance, FrameRate = new Rational(25, 1), ChromaFormat = ChromaFormat.Yuv444 });
        for (int i = 0; i < Frames; i++)
        {
            (double x, double y, double angle) = camera(i);

            // The camera's view: the scene moved by (x, y) and turned about the frame's centre.
            Vector2 centre = new(Width / 2f, Height / 2f);
            Matrix3x2 view = Matrix3x2.CreateTranslation(-Width / 2f, -Height / 2f) * Matrix3x2.CreateTranslation(-centre)
                * Matrix3x2.CreateRotation((float)angle) * Matrix3x2.CreateTranslation(centre + new Vector2((float)x, (float)y));
            using Raster<Rgba8> frame = scene.Derive(ops => ops.Transform(view, new TransformOptions { Viewport = new Rect(0, 0, Width, Height) }));
            writer.WriteFrame(frame, new MediaTime(i, new Rational(1, 25)));
        }

        writer.Finish();
        return path;
    }

    [Fact]
    public void RecoversKnownShakesWithinAQuarterPixel()
    {
        // The task 20 gate: random sub-pixel shakes up to 6 px and 1 degree, measured within 0.25 px at the frame's corners.
        Random random = new(11);
        (double X, double Y, double Angle)[] path = [.. Enumerable.Range(0, Frames).Select(_ => ((random.NextDouble() * 12) - 6, (random.NextDouble() * 12) - 6, ((random.NextDouble() * 2) - 1) * Math.PI / 180))];
        string clip = Shaky("known", i => path[i]);
        try
        {
            CameraMotion motion = Stabilizer.Analyze(clip, new MotionAnalysisOptions { AnalysisWidth = Width }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Frames, motion.Frames.Count);
            double worst = 0;
            for (int i = 1; i < Frames; i++)
            {
                FrameMotion m = motion.Frames[i];

                // The true motion from frame i-1 to i: the change of the camera's offset and angle, about the centre.
                double dx = path[i].X - path[i - 1].X, dy = path[i].Y - path[i - 1].Y, da = path[i].Angle - path[i - 1].Angle;
                Assert.InRange(m.Scale, 0.995, 1.005);

                // Compare where the estimate and the truth put the frame's corners.
                foreach ((double cx, double cy) in new[] { (-Width / 2.0, -Height / 2.0), (Width / 2.0, Height / 2.0), (Width / 2.0, -Height / 2.0) })
                {
                    double ex = (Math.Cos(m.Angle) * cx) - (Math.Sin(m.Angle) * cy) + m.Dx, ey = (Math.Sin(m.Angle) * cx) + (Math.Cos(m.Angle) * cy) + m.Dy;
                    double tx = (Math.Cos(da) * cx) - (Math.Sin(da) * cy) + dx, ty = (Math.Sin(da) * cx) + (Math.Cos(da) * cy) + dy;
                    worst = Math.Max(worst, Math.Sqrt(((ex - tx) * (ex - tx)) + ((ey - ty) * (ey - ty))));
                }
            }

            Assert.True(worst <= 0.25, $"worst corner error {worst:F3} px");
            Assert.Equal(motion.ToJson(), CameraMotion.FromJson(motion.ToJson()).ToJson());
        }
        finally
        {
            File.Delete(clip);
        }
    }

    [Theory]
    [InlineData(StabilizeEdges.Zoom)]
    [InlineData(StabilizeEdges.Black)]
    [InlineData(StabilizeEdges.Blur)]
    public void StabilizingTakesTheShakeOut(StabilizeEdges edges)
    {
        // A slow pan with a fast jitter on top: after stabilizing, the frame-to-frame motion left is mostly the pan.
        Random random = new(3);
        double[] jitter = [.. Enumerable.Range(0, Frames).Select(_ => (random.NextDouble() * 8) - 4)];
        string clip = Shaky("pan", i => ((i * 1.5) + jitter[i], jitter[(i + 7) % Frames] * 0.5, 0));
        string output = Path.Combine(Path.GetTempPath(), $"egami-stab-out-{edges}-{Environment.ProcessId}.mkv");
        try
        {
            CameraMotion motion = Stabilizer.Analyze(clip, cancellationToken: TestContext.Current.CancellationToken);
            VideoEdit edit = new();
            edit.Track.Add(Clip.Open(clip).Stabilize(motion, new StabilizeOptions { Smoothing = 8, Edges = edges }));
            edit.Render(output, new EditRenderOptions { Writer = new VideoWriterOptions { Codec = RawVideoCodec.Instance, ChromaFormat = ChromaFormat.Yuv444 } }, null, TestContext.Current.CancellationToken);
            CameraMotion after = Stabilizer.Analyze(output, cancellationToken: TestContext.Current.CancellationToken);
            double before = Jitter(motion), left = Jitter(after);
            Assert.True(left < before * 0.35, $"jitter {before:F2} px before, {left:F2} px after");
        }
        finally
        {
            File.Delete(clip);
            File.Delete(output);
        }

        // The high-frequency part of the motion: each step's distance from the mean step.
        static double Jitter(CameraMotion m)
        {
            List<FrameMotion> steps = [.. m.Frames.Skip(1)];
            double mx = steps.Average(f => f.Dx), my = steps.Average(f => f.Dy);
            return Math.Sqrt(steps.Average(f => ((f.Dx - mx) * (f.Dx - mx)) + ((f.Dy - my) * (f.Dy - my))));
        }
    }

    [Fact]
    public void StabilizedClipsSaveAsJson()
    {
        CameraMotion motion = new(64, 48, [new FrameMotion(MediaTime.Zero, 0, 0, 0, 1), new FrameMotion(MediaTime.FromSeconds(0.04), 1.5, -0.25, 0.001, 1.002)]);
        VideoEdit edit = new();
        edit.Track.Add(Clip.Open("a.mkv").Stabilize(motion, new StabilizeOptions { Smoothing = 30, Edges = StabilizeEdges.Blur, MaxZoom = 1.2 }));
        string json = edit.ToJson();
        VideoEdit back = VideoEdit.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(StabilizeEdges.Blur, back.Track[0].Clip.Stabilization!.Options.Edges);
        Assert.Equal(1.2, back.Track[0].Clip.Stabilization!.Options.MaxZoom);
    }
}
