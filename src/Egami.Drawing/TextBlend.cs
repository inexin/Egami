using System.Collections.Concurrent;

using Egami.Operations;

namespace Egami.Drawing;

/// <summary>
/// <see cref="CanvasOptions.TextGamma"/> and <see cref="CanvasOptions.TextContrast"/> as tables, made once per value pair: the
/// contrast boost of each coverage, and each stored value raised to the gamma with the inverse, so the power-law blend costs table
/// reads and no <see cref="MathF.Pow"/> per pixel.
/// </summary>
internal sealed class TextBlend
{
    // The inverse's first guess comes from this many even steps of the raised value; a step or two up or down then lands on the
    // stored value whose raised midpoints bracket it, which is the correctly rounded inverse.
    private const int EncodeSteps = 4096;

    // Value pairs in use; cleared if a caller cycles through many, since each pair is a few kilobytes.
    private static readonly ConcurrentDictionary<(float Gamma, float Contrast), TextBlend> Cache = new();

    private readonly byte[]? _boost;
    private readonly float[]? _decode;
    private readonly float[]? _midpoints;
    private readonly byte[]? _encode;

    private TextBlend(float gamma, float contrast)
    {
        if (contrast > 0)
        {
            _boost = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double a = i / 255.0;
                _boost[i] = (byte)Math.Round((a + ((1 - a) * contrast * a)) * 255);
            }
        }

        if (gamma != 1)
        {
            _decode = new float[256];
            _midpoints = new float[255];
            _encode = new byte[EncodeSteps + 1];
            for (int i = 0; i < 256; i++)
            {
                _decode[i] = (float)Math.Pow(i / 255.0, gamma);
            }

            for (int i = 0; i < 255; i++)
            {
                _midpoints[i] = (float)Math.Pow((i + 0.5) / 255.0, gamma);
            }

            for (int i = 0; i <= EncodeSteps; i++)
            {
                _encode[i] = (byte)Math.Round(Math.Pow(i / (double)EncodeSteps, 1 / gamma) * 255);
            }
        }
    }

    /// <summary>Gets a value indicating whether the blend has a gamma other than 1, so it needs the destination.</summary>
    public bool HasGamma => _decode is not null;

    /// <summary>Gets a value indicating whether the blend boosts coverage.</summary>
    public bool HasBoost => _boost is not null;

    /// <summary>The tables for a canvas's options, or null for the defaults (and for gamma alone in linear blending), which draw as before.</summary>
    public static TextBlend? For(CanvasOptions options)
    {
        float gamma = options.ColorSpace == ColorSpace.Linear ? 1 : options.TextGamma;
        float contrast = options.TextContrast;
        if (gamma == 1 && contrast == 0)
        {
            return null;
        }

        if (Cache.Count > 64)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd((gamma, contrast), static key => new TextBlend(key.Gamma, key.Contrast));
    }

    /// <summary>A coverage after the contrast boost.</summary>
    public byte Boost(byte coverage) => _boost is null ? coverage : _boost[coverage];

    /// <summary>Boosts a row of coverage in place.</summary>
    public void Boost(Span<byte> coverage)
    {
        if (_boost is null)
        {
            return;
        }

        for (int i = 0; i < coverage.Length; i++)
        {
            coverage[i] = _boost[coverage[i]];
        }
    }

    /// <summary>One channel of the power-law blend: <paramref name="src"/> over <paramref name="dst"/> by the weight <paramref name="k"/>.</summary>
    public byte Mix(byte dst, byte src, float k)
    {
        if (k <= 0)
        {
            return dst;
        }

        if (k >= 1)
        {
            return src;
        }

        float d = _decode![dst];
        return Encode(d + ((_decode[src] - d) * k));
    }

    // The stored value whose raised value is nearest in its own scale: the inverse rounded, from a table guess and a few steps.
    private byte Encode(float v)
    {
        if (v <= 0)
        {
            return 0;
        }

        int e = _encode![Math.Min((int)(v * EncodeSteps), EncodeSteps)];
        float[] mid = _midpoints!;
        while (e < 255 && mid[e] < v)
        {
            e++;
        }

        while (e > 0 && mid[e - 1] >= v)
        {
            e--;
        }

        return (byte)e;
    }
}
