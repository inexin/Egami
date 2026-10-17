using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// A conversion from one color encoding to another, compiled once and then applied to spans of pixels of any type. Reusable and
/// safe to share between threads. Transforms are cached per <see cref="EgamiContext"/>, so creating the same one again is cheap.
/// </summary>
/// <remarks>
/// <para>
/// Pixels are read in the source encoding's model and written in the target's: RGB pixel types give and take red, green and
/// blue; gray types their gray value; CMYK types (<see cref="Cmyk8"/>, <see cref="Cmyk16"/>) their inks. A pixel type of another
/// model is converted to the encoding's model numerically first, as <see cref="PixelConvert"/> would (an RGB pixel's gray is its
/// luma), and results land in it the same way. Alpha is carried over unchanged, and is opaque when the source has none.
/// </para>
/// <para>
/// 8-bit RGB, BGR, RGBA, BGRA, gray, gray-alpha and CMYK pixels take a fast path; other types go through floats, so float and
/// half types keep values outside 0..1 (as wide-gamut and HDR conversions produce) and integer types are clamped.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="transform"/>
/// </example>
public sealed class ColorTransform
{
    private const int Chunk = 256;

    private readonly CompiledTransform _compiled;

    private ColorTransform(ColorProfile source, ColorProfile target, ColorTransformOptions options, CompiledTransform compiled)
    {
        (Source, Target, Options, _compiled) = (source, target, options, compiled);
    }

    /// <summary>Gets the encoding converted from.</summary>
    public ColorProfile Source { get; }

    /// <summary>Gets the encoding converted to.</summary>
    public ColorProfile Target { get; }

    /// <summary>Gets the intent and black point compensation.</summary>
    public ColorTransformOptions Options { get; }

    /// <summary>Gets a value indicating whether the two encodings mean the same colors, so applying the transform changes nothing.</summary>
    public bool IsIdentity => _compiled is IdentityTransform;

    /// <summary>Gets the channels of each color converted from: the source's <see cref="ColorProfile.ChannelCount"/>.</summary>
    public int InputChannels => _compiled.InputChannels;

    /// <summary>Gets the channels of each color converted to: the target's <see cref="ColorProfile.ChannelCount"/>.</summary>
    public int OutputChannels => _compiled.OutputChannels;

    /// <summary>Compiles the conversion from <paramref name="source"/> to <paramref name="target"/>, or returns it from the cache.</summary>
    /// <param name="source">The encoding of the pixels given.</param>
    /// <param name="target">The encoding to convert to.</param>
    /// <param name="options">The intent and black point compensation, or <see langword="null"/> for <see cref="ColorTransformOptions.Default"/>.</param>
    /// <param name="context">The context whose cache to use, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The transform.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnsupportedFormatException">An ICC profile has no tables for the direction it is used in, such as an input-only profile as the target.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="transform"/>
    /// </example>
    public static ColorTransform Create(ColorProfile source, ColorProfile target, ColorTransformOptions? options = null, EgamiContext? context = null) =>
        CreateFor(source, target, options, null, context);

    /// <summary>Compiles the conversion with the source peak taken from <paramref name="hdr"/> when the options give none.</summary>
    internal static ColorTransform CreateFor(ColorProfile source, ColorProfile target, ColorTransformOptions? options, HdrMetadata? hdr, EgamiContext? context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        options ??= ColorTransformOptions.Default;
        CompiledTransform compiled = TransformCache.Get(context ?? EgamiContext.Default, source, target, options, options.PeakFor(hdr));
        return new ColorTransform(source, target, options, compiled);
    }

    /// <summary>Converts pixels in place.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="pixels">The pixels.</param>
    public void Apply<TPixel>(Span<TPixel> pixels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Apply<TPixel, TPixel>(pixels, pixels);
    }

    /// <summary>Converts pixels from one type to another; the spans may be the same memory when the types are the same.</summary>
    /// <typeparam name="TFrom">The source pixel type.</typeparam>
    /// <typeparam name="TTo">The destination pixel type.</typeparam>
    /// <param name="source">The pixels to convert.</param>
    /// <param name="destination">Where to write them, at least as long as <paramref name="source"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="source"/>.</exception>
    public void Apply<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        if (InputChannels is not (1 or 3 or 4) || OutputChannels is not (1 or 3 or 4))
        {
            throw new InvalidOperationException($"No pixel type holds the {Math.Max(InputChannels, OutputChannels)} inks of an n-colour profile; convert with Apply(ReadOnlySpan<float>, Span<float>).");
        }

        if (Bytes<TFrom>(_compiled.InputChannels) is ByteLayout inLayout && Bytes<TTo>(_compiled.OutputChannels) is ByteLayout outLayout)
        {
            ApplyBytes(source, inLayout, destination[..source.Length], outLayout);
            return;
        }

        ApplyFloats(source, destination[..source.Length]);
    }

    /// <summary>
    /// Converts colors stored as floats, <see cref="InputChannels"/> per color in and <see cref="OutputChannels"/> out, each 0 to 1
    /// (inks as tints): the way to convert from or to an n-colour profile, which no pixel type holds, and for any other values
    /// that are not pixels. Inputs are clamped to 0 to 1; outputs keep any value the conversion gives.
    /// </summary>
    /// <param name="source">The colors, a whole number of <see cref="InputChannels"/> values each.</param>
    /// <param name="destination">Where to write them: <see cref="OutputChannels"/> values for each color in <paramref name="source"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> is not a whole number of colors, or <paramref name="destination"/> is too short.
    /// </exception>
    public void Apply(ReadOnlySpan<float> source, Span<float> destination)
    {
        int ins = InputChannels, outs = OutputChannels;
        if (source.Length % ins != 0)
        {
            throw new ArgumentException($"The source holds {source.Length} values, not a whole number of {ins}-channel colors.", nameof(source));
        }

        int count = source.Length / ins;
        if (destination.Length < (long)count * outs)
        {
            throw new ArgumentException($"The destination needs {(long)count * outs} values for {count} colors.", nameof(destination));
        }

        // Clamped in chunks, so out-of-range inputs mean the same on every path.
        Span<float> chunk = stackalloc float[Chunk * 15];
        for (int start = 0; start < count; start += Chunk)
        {
            int n = Math.Min(Chunk, count - start);
            ReadOnlySpan<float> from = source.Slice(start * ins, n * ins);
            for (int i = 0; i < from.Length; i++)
            {
                float v = from[i];
                chunk[i] = v > 0f ? v < 1f ? v : 1f : 0f;
            }

            _compiled.Apply(chunk[..(n * ins)], destination.Slice(start * outs, n * outs), n);
        }
    }

    /// <summary>The model a pixel type's channels are in: RGB, gray, CMYK, or none for an alpha mask.</summary>
    internal static ColorModel? ModelOf<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel> => TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => ColorModel.Gray,
            PixelChannelOrder.Cmyk => ColorModel.Cmyk,
            PixelChannelOrder.Alpha => null,
            _ => ColorModel.Rgb,
        };

    // An 8-bit type whose color channels sit at the start of the pixel in the transform's model, with alpha (if any) after them.
    private static ByteLayout? Bytes<TPixel>(int channels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = TPixel.Layout;
        if (layout.ChannelType != PixelChannelType.Unorm8 || layout.Alpha == AlphaKind.Premultiplied || Unsafe.SizeOf<TPixel>() != layout.ChannelCount)
        {
            return null;
        }

        return (layout.ChannelOrder, channels) switch
        {
            (PixelChannelOrder.Rgba or PixelChannelOrder.Rgb, 3) => new ByteLayout(layout.ChannelCount, false),
            (PixelChannelOrder.Bgra or PixelChannelOrder.Bgr, 3) => new ByteLayout(layout.ChannelCount, true),
            (PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha, 1) => new ByteLayout(layout.ChannelCount, false),
            (PixelChannelOrder.Cmyk, 4) => new ByteLayout(4, false),
            _ => null,
        };
    }

    private void ApplyBytes<TFrom, TTo>(ReadOnlySpan<TFrom> source, ByteLayout inLayout, Span<TTo> destination, ByteLayout outLayout)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(source);
        Span<byte> dst = MemoryMarshal.AsBytes(destination);
        int count = source.Length;
        bool alphaIn = TFrom.Layout.HasAlpha, alphaOut = TTo.Layout.HasAlpha;
        int inAlpha = inLayout.Stride - 1, outAlpha = outLayout.Stride - 1;

        // Alpha first, from the source bytes, in case the spans are the same memory with the color written over them next; a
        // chunk at a time, so nothing is allocated.
        if (alphaOut && !(alphaIn && inLayout.Stride == outLayout.Stride && Unsafe.AreSame(ref MemoryMarshal.GetReference(src), ref MemoryMarshal.GetReference(dst))))
        {
            Span<byte> alpha = stackalloc byte[Chunk];
            for (int start = 0; start < count; start += Chunk)
            {
                int n = Math.Min(Chunk, count - start);
                for (int i = 0; i < n; i++)
                {
                    alpha[i] = alphaIn ? src[((start + i) * inLayout.Stride) + inAlpha] : (byte)255;
                }

                _compiled.Apply(src[(start * inLayout.Stride)..], inLayout, dst[(start * outLayout.Stride)..], outLayout, n);
                for (int i = 0; i < n; i++)
                {
                    dst[((start + i) * outLayout.Stride) + outAlpha] = alpha[i];
                }
            }

            return;
        }

        _compiled.Apply(src, inLayout, dst, outLayout, count);
    }

    private void ApplyFloats<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        int ins = _compiled.InputChannels, outs = _compiled.OutputChannels;
        Span<float> a = stackalloc float[Chunk * 4];
        Span<float> b = stackalloc float[Chunk * 4];
        Span<float> alpha = stackalloc float[Chunk];
        bool inksIn = ins == 4 && CmykInk.Is<TFrom>(), inksOut = outs == 4 && CmykInk.Is<TTo>();
        bool grayIn = ModelOf<TFrom>() == ColorModel.Gray;
        for (int start = 0; start < source.Length; start += Chunk)
        {
            int n = Math.Min(Chunk, source.Length - start);
            ReadOnlySpan<TFrom> from = source.Slice(start, n);
            if (inksIn)
            {
                CmykInk.Load(from, a);
                alpha[..n].Fill(1f);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    Vector4 v = from[i].ToScaled();
                    alpha[i] = v.W;
                    switch (ins)
                    {
                        case 1:
                            a[i] = grayIn ? v.X : GrayF32.FromScaled(v).L;
                            break;
                        case 3:
                            (a[i * 3], a[(i * 3) + 1], a[(i * 3) + 2]) = (v.X, v.Y, v.Z);
                            break;
                        default:
                            Vector4 inks = CmykInk.FromRgb(v);
                            (a[i * 4], a[(i * 4) + 1], a[(i * 4) + 2], a[(i * 4) + 3]) = (inks.X, inks.Y, inks.Z, inks.W);
                            break;
                    }
                }
            }

            _compiled.Apply(a, b, n);
            Span<TTo> to = destination.Slice(start, n);
            if (inksOut)
            {
                CmykInk.Store(b[..(n * 4)], to);
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                to[i] = TTo.FromScaled(outs switch
                {
                    1 => new Vector4(b[i], b[i], b[i], alpha[i]),
                    3 => new Vector4(b[i * 3], b[(i * 3) + 1], b[(i * 3) + 2], alpha[i]),
                    _ => Plain(b.Slice(i * 4, 4), alpha[i]),
                });
            }
        }

        // Inks into a non-CMYK type: the plain formula, R = (1 − C)(1 − K).
        static Vector4 Plain(ReadOnlySpan<float> inks, float alpha)
        {
            float k = 1f - inks[3];
            return new Vector4((1f - inks[0]) * k, (1f - inks[1]) * k, (1f - inks[2]) * k, alpha);
        }
    }
}
