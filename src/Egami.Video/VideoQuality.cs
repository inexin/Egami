using System.Security.Cryptography;

using Egami.Pixels;
using Egami.Video.Codecs.Raw;

namespace Egami.Video;

/// <summary>
/// Measures how close decoded video is to a reference, frame by frame or over a whole clip: PSNR and SSIM per plane, and MD5
/// hashes of frames and packets in the layout FFmpeg's framemd5 uses (planes one after another, rows without padding,
/// little-endian 16-bit samples above 8 bits).
/// </summary>
/// <example>
/// <code language="csharp">
/// VideoQuality quality = new();
/// foreach ((YuvVideoFrame reference, YuvVideoFrame encoded) in pairs)
///     quality.Add(reference.Raster, encoded.Raster);
/// Console.WriteLine($"PSNR {quality.Psnr.Average:F2} dB, SSIM {quality.Ssim.Average:F4}");
/// </code>
/// </example>
public sealed class VideoQuality
{
    private readonly double[] _sse = new double[4];
    private readonly long[] _samples = new long[4];
    private readonly double[] _ssimSum = new double[4];
    private readonly long[] _ssimFrames = new long[4];
    private readonly long[] _planeSize = new long[4];
    private int _planes;
    private int _maxValue = 255;

    /// <summary>Gets how many frame pairs were added.</summary>
    public long Frames { get; private set; }

    /// <summary>
    /// Gets the PSNR in decibels over every frame added: per plane from the plane's total squared error, and the average from
    /// all planes' squared error together. Infinity when the frames are identical.
    /// </summary>
    public PlaneScores Psnr
    {
        get
        {
            double total = 0;
            long samples = 0;
            double[] perPlane = new double[4];
            for (int p = 0; p < _planes; p++)
            {
                perPlane[p] = ToDecibels(_sse[p], _samples[p]);
                total += _sse[p];
                samples += _samples[p];
            }

            return new PlaneScores(perPlane[0], perPlane[1], perPlane[2], _planes > 3 ? perPlane[3] : null, ToDecibels(total, samples));
        }
    }

    /// <summary>
    /// Gets the SSIM over every frame added (1 for identical frames): per plane the mean of the frames' SSIM, which is the
    /// mean over 8 by 8 windows at a 4-pixel step; the average weights the planes by their sample counts.
    /// </summary>
    public PlaneScores Ssim
    {
        get
        {
            double[] perPlane = new double[4];
            double weighted = 0;
            long weight = 0;
            for (int p = 0; p < _planes; p++)
            {
                perPlane[p] = _ssimFrames[p] == 0 ? 1 : _ssimSum[p] / _ssimFrames[p];
                weighted += perPlane[p] * _planeSize[p];
                weight += _planeSize[p];
            }

            return new PlaneScores(perPlane[0], perPlane[1], perPlane[2], _planes > 3 ? perPlane[3] : null, weight == 0 ? 1 : weighted / weight);
        }
    }

    /// <summary>Adds a frame pair. Both must have the same size, chroma format, bit depth and alpha.</summary>
    /// <param name="reference">The original.</param>
    /// <param name="distorted">The frame to measure.</param>
    /// <returns>This frame's scores, PSNR and SSIM.</returns>
    /// <exception cref="ArgumentException">The frames differ in layout.</exception>
    public (PlaneScores Psnr, PlaneScores Ssim) Add(YuvRaster reference, YuvRaster distorted)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(distorted);
        if (reference.Width != distorted.Width || reference.Height != distorted.Height || reference.ChromaFormat != distorted.ChromaFormat
            || reference.BitDepth != distorted.BitDepth || reference.HasAlpha != distorted.HasAlpha)
        {
            throw new ArgumentException("The frames differ in size, chroma format, bit depth or alpha.", nameof(distorted));
        }

        _maxValue = (1 << reference.BitDepth) - 1;
        double[] frameSse = new double[4];
        double[] frameSsim = new double[4];
        long[] frameSamples = new long[4];
        int planes = 0;
        for (int p = 0; p < 4; p++)
        {
            (double[] a, double[] b, int w, int h)? pair = Plane(reference, distorted, p);
            if (pair is null)
            {
                continue;
            }

            (double[] a, double[] b, int w, int h) = pair.Value;
            planes = p + 1;
            double sse = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sse += d * d;
            }

            frameSse[p] = sse;
            frameSamples[p] = a.Length;
            frameSsim[p] = PlaneSsim(a, b, w, h, _maxValue);
            _sse[p] += sse;
            _samples[p] += a.Length;
            _ssimSum[p] += frameSsim[p];
            _ssimFrames[p]++;
            _planeSize[p] = a.Length;
        }

        _planes = Math.Max(_planes, planes);
        Frames++;
        double totalSse = frameSse.Sum();
        long totalSamples = frameSamples.Sum();
        double ssimWeighted = 0;
        for (int p = 0; p < planes; p++)
        {
            ssimWeighted += frameSsim[p] * frameSamples[p];
        }

        return (
            new PlaneScores(ToDecibels(frameSse[0], frameSamples[0]), ToDecibels(frameSse[1], frameSamples[1]), ToDecibels(frameSse[2], frameSamples[2]),
                planes > 3 ? ToDecibels(frameSse[3], frameSamples[3]) : null, ToDecibels(totalSse, totalSamples)),
            new PlaneScores(frameSsim[0], frameSsim[1], frameSsim[2], planes > 3 ? frameSsim[3] : null, totalSamples == 0 ? 1 : ssimWeighted / totalSamples));
    }

    /// <summary>Returns the MD5 of a frame's samples as lowercase hex, in FFmpeg's framemd5 layout.</summary>
    /// <param name="planes">The frame.</param>
    /// <returns>The hash.</returns>
    public static string Md5(YuvRaster planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        RawLayout layout = RawLayout.Planar(planes.ChromaFormat, planes.BitDepth, planes.HasAlpha);
        byte[] bytes = new byte[layout.FrameSize(planes.Width, planes.Height)];
        layout.Write(planes, bytes);
        return Md5(bytes);
    }

    /// <summary>Returns the MD5 of bytes, such as a packet's, as lowercase hex.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The hash.</returns>
#pragma warning disable CA5351 // A content checksum to compare with FFmpeg's framemd5, not security.
    public static string Md5(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));
#pragma warning restore CA5351

    private double ToDecibels(double sse, long samples) =>
        samples == 0 ? double.NaN : sse == 0 ? double.PositiveInfinity : 10 * Math.Log10((double)_maxValue * _maxValue / (sse / samples));

    private static (double[] A, double[] B, int Width, int Height)? Plane(YuvRaster reference, YuvRaster distorted, int index)
    {
        if (reference is YuvRaster<byte> rb && distorted is YuvRaster<byte> db)
        {
            YuvPlane<byte>? a = Select(rb, index), b = Select(db, index);
            return a is null || b is null ? null : (ToDoubles(a), ToDoubles(b), a.Width, a.Height);
        }

        if (reference is YuvRaster<ushort> rw && distorted is YuvRaster<ushort> dw)
        {
            YuvPlane<ushort>? a = Select(rw, index), b = Select(dw, index);
            return a is null || b is null ? null : (ToDoubles(a), ToDoubles(b), a.Width, a.Height);
        }

        throw new ArgumentException("The frames have different sample types.", nameof(distorted));
    }

    private static YuvPlane<T>? Select<T>(YuvRaster<T> raster, int index)
        where T : unmanaged => index switch
        {
            0 => raster.Y,
            1 => raster.U,
            2 => raster.V,
            _ => raster.A,
        };

    private static double[] ToDoubles(YuvPlane<byte> plane)
    {
        double[] values = new double[plane.Width * plane.Height];
        for (int y = 0; y < plane.Height; y++)
        {
            Span<byte> row = plane.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                values[(y * plane.Width) + x] = row[x];
            }
        }

        return values;
    }

    private static double[] ToDoubles(YuvPlane<ushort> plane)
    {
        double[] values = new double[plane.Width * plane.Height];
        for (int y = 0; y < plane.Height; y++)
        {
            Span<ushort> row = plane.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                values[(y * plane.Width) + x] = row[x];
            }
        }

        return values;
    }

    /// <summary>SSIM (Wang, Bovik, Sheikh and Simoncelli, 2004) over 8 by 8 windows at a 4-pixel step, with unweighted windows.</summary>
    private static double PlaneSsim(double[] a, double[] b, int width, int height, int max)
    {
        double c1 = 0.01 * max * 0.01 * max;
        double c2 = 0.03 * max * 0.03 * max;
        double sum = 0;
        long windows = 0;
        for (int y = 0; y + 8 <= height; y += 4)
        {
            for (int x = 0; x + 8 <= width; x += 4)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    int row = ((y + j) * width) + x;
                    for (int i = 0; i < 8; i++)
                    {
                        double va = a[row + i], vb = b[row + i];
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                // Sample statistics (n - 1), as in the paper's definition of the variance and covariance.
                const double N = 64;
                double ma = sa / N, mb = sb / N;
                double va2 = (saa - (sa * ma)) / (N - 1), vb2 = (sbb - (sb * mb)) / (N - 1), cov = (sab - (sa * mb)) / (N - 1);
                sum += ((2 * ma * mb) + c1) * ((2 * cov) + c2) / (((ma * ma) + (mb * mb) + c1) * (va2 + vb2 + c2));
                windows++;
            }
        }

        return windows == 0 ? 1 : sum / windows;
    }
}

/// <summary>A score per plane (Y, U, V and alpha when present) and an average.</summary>
/// <param name="Y">The luma score.</param>
/// <param name="U">The first chroma plane's score (NaN for gray frames).</param>
/// <param name="V">The second chroma plane's score (NaN for gray frames).</param>
/// <param name="A">The alpha plane's score, or <see langword="null"/>.</param>
/// <param name="Average">The score over all planes.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-quality"/>
/// </example>
public sealed record PlaneScores(double Y, double U, double V, double? A, double Average);
