using System.Numerics;

namespace Egami.Video.Editing;

/// <summary>
/// Pass 1 of two-pass stabilization: measures the camera's motion over a clip (<see cref="CameraMotion"/>). Pass 2 is
/// <see cref="Clip.Stabilize"/>, which smooths the camera path with a Gaussian and warps each frame onto it.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public static class Stabilizer
{
    /// <summary>Measures the camera's motion in a file's first video track.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The measuring settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="progress">Receives progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <returns>The motion.</returns>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
    /// </example>
    public static CameraMotion Analyze(string path, StabilizerOptions? options = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { AutoOrient = true });
        return Analyze(reader, options, progress, cancellationToken);
    }

    /// <summary>Measures the camera's motion in a stream's first video track.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">The measuring settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="progress">Receives progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <returns>The motion.</returns>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
    /// </example>
    public static CameraMotion Analyze(Stream stream, StabilizerOptions? options = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using VideoReader reader = VideoReader.Open(stream, new VideoReaderOptions { AutoOrient = true }, leaveOpen: true);
        return Analyze(reader, options, progress, cancellationToken);
    }

    private static CameraMotion Analyze(VideoReader reader, StabilizerOptions? options, IProgress<MediaProgress>? progress, CancellationToken cancellationToken)
    {
        options ??= StabilizerOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.AnalysisWidth, 64, nameof(options));
        MotionEstimator estimator = new();
        List<FrameMotion> frames = [];
        int width = reader.Track.Width, height = reader.Track.Height;
        MediaTime? duration = reader.Info.Duration;
        long count = 0;
        while (reader.ReadFrame() is { } frame)
        {
            using (frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                width = frame.Width;
                height = frame.Height;
                Gray luma = Gray.FromLuma(frame.Raster, options.AnalysisWidth, out int factor);
                Similarity s = estimator.Add(luma);

                // About the frame's centre, in full-size pixels: where the centre goes, and the turn and zoom around it.
                double cx = luma.Width / 2.0, cy = luma.Height / 2.0;
                (double mx, double my) = s.Apply(cx, cy);
                frames.Add(new FrameMotion(frame.Timestamp, (mx - cx) * factor, (my - cy) * factor, s.Angle, s.Scale));
                count++;
                if (progress is not null && count % 25 == 0)
                {
                    progress.Report(new MediaProgress { Position = frame.Timestamp, Duration = duration, Frames = count, Fraction = duration is { } d && d.Seconds > 0 ? Math.Min(1, frame.Timestamp.Seconds / d.Seconds) : null });
                }
            }
        }

        return new CameraMotion(Math.Max(1, width), Math.Max(1, height), frames);
    }
}

/// <summary>
/// Pass 2's numbers for one clip: the camera path smoothed with a Gaussian, the correction that puts each frame on it, and the
/// zoom that hides the edges.
/// </summary>
internal sealed class StabilizationPath
{
    private readonly MediaTime[] _times;
    private readonly Matrix3x2[] _corrections;

    public StabilizationPath(CameraMotion motion, StabilizeOptions options)
    {
        int n = motion.Frames.Count;
        _times = [.. motion.Frames.Select(f => f.Time)];
        double[] x = new double[n], y = new double[n], a = new double[n], s = new double[n];
        for (int i = 0; i < n; i++)
        {
            FrameMotion f = motion.Frames[i];
            double px = i == 0 ? 0 : x[i - 1], py = i == 0 ? 0 : y[i - 1], pa = i == 0 ? 0 : a[i - 1], ps = i == 0 ? 0 : s[i - 1];
            x[i] = px + (i == 0 ? 0 : f.Dx);
            y[i] = py + (i == 0 ? 0 : f.Dy);
            a[i] = pa + (i == 0 ? 0 : f.Angle);
            s[i] = ps + (i == 0 ? 0 : Math.Log(Math.Max(1e-6, f.Scale)));
        }

        double[] sx = Smooth(x, options.Smoothing), sy = Smooth(y, options.Smoothing), sa = Smooth(a, options.Smoothing), ss = Smooth(s, options.Smoothing);
        Vector2 centre = new(motion.Width / 2f, motion.Height / 2f);
        _corrections = new Matrix3x2[n];
        for (int i = 0; i < n; i++)
        {
            // Undo the shake: move the frame by the smoothed path minus the measured one, about its centre.
            _corrections[i] = Matrix3x2.CreateTranslation(-centre)
                * Matrix3x2.CreateRotation((float)(sa[i] - a[i]))
                * Matrix3x2.CreateScale((float)Math.Exp(ss[i] - s[i]))
                * Matrix3x2.CreateTranslation(centre + new Vector2((float)(sx[i] - x[i]), (float)(sy[i] - y[i])));
        }

        Zoom = options.Edges == StabilizeEdges.Zoom ? EdgeZoom(motion.Width, motion.Height) : 1;
        if (Zoom != 1)
        {
            Matrix3x2 zoom = Matrix3x2.CreateScale((float)Zoom, centre);
            for (int i = 0; i < n; i++)
            {
                _corrections[i] *= zoom;
            }
        }
    }

    /// <summary>Gets the zoom applied to hide the edges, 1 for none.</summary>
    public double Zoom { get; }

    /// <summary>Returns the correction for the frame at a time: the measured frame nearest it.</summary>
    public Matrix3x2 CorrectionAt(MediaTime time)
    {
        if (_times.Length == 0)
        {
            return Matrix3x2.Identity;
        }

        int i = Array.BinarySearch(_times, time);
        if (i < 0)
        {
            i = ~i;
            if (i >= _times.Length || (i > 0 && Times.Sub(time, _times[i - 1]) < Times.Sub(_times[i], time)))
            {
                i--;
            }
        }

        return _corrections[Math.Clamp(i, 0, _corrections.Length - 1)];
    }

    /// <summary>A Gaussian over the given frames either way (sigma a third of that), the ends mirrored.</summary>
    private static double[] Smooth(double[] values, int radius)
    {
        int n = values.Length;
        if (radius <= 0 || n == 0)
        {
            return [.. values];
        }

        double sigma = Math.Max(1, radius / 3.0);
        double[] kernel = new double[(2 * radius) + 1];
        for (int k = -radius; k <= radius; k++)
        {
            kernel[k + radius] = Math.Exp(-(k * k) / (2 * sigma * sigma));
        }

        double[] result = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0, weight = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int j = i + k;
                j = j < 0 ? -j : j >= n ? (2 * (n - 1)) - j : j;
                j = Math.Clamp(j, 0, n - 1);
                sum += values[j] * kernel[k + radius];
                weight += kernel[k + radius];
            }

            result[i] = sum / weight;
        }

        return result;
    }

    /// <summary>The least zoom about the centre for which every corrected frame covers the whole picture, at most 1.5.</summary>
    private double EdgeZoom(int width, int height)
    {
        double worst = 1;
        Vector2[] corners = [new(0, 0), new(width, 0), new(0, height), new(width, height)];
        Vector2 centre = new(width / 2f, height / 2f);
        foreach (Matrix3x2 correction in _corrections)
        {
            double low = 1, high = 1.5;
            if (Covers(correction, 1))
            {
                continue;
            }

            for (int step = 0; step < 24; step++)
            {
                double mid = (low + high) / 2;
                if (Covers(correction, mid))
                {
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }

            worst = Math.Max(worst, high);
        }

        return worst;

        bool Covers(Matrix3x2 correction, double zoom)
        {
            // Each output corner, mapped back through the zoomed correction, must land inside the frame.
            if (!Matrix3x2.Invert(correction * Matrix3x2.CreateScale((float)zoom, centre), out Matrix3x2 inverse))
            {
                return false;
            }

            foreach (Vector2 corner in corners)
            {
                Vector2 p = Vector2.Transform(corner, inverse);
                if (p.X < -0.01f || p.Y < -0.01f || p.X > width + 0.01f || p.Y > height + 0.01f)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
