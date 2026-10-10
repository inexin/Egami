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
    public static ColorTransform Create(ColorProfile source, ColorProfile target, ColorTransformOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        options ??= ColorTransformOptions.Default;
        CompiledTransform compiled = TransformCache.Get(context ?? EgamiContext.Default, source, target, options.Intent, options.BlackPointCompensation);
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

        if (Bytes<TFrom>(_compiled.InputChannels) is ByteLayout inLayout && Bytes<TTo>(_compiled.OutputChannels) is ByteLayout outLayout)
        {
            ApplyBytes(source, inLayout, destination[..source.Length], outLayout);
            return;
        }

        ApplyFloats(source, destination[..source.Length]);
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

        // Alpha first, from the source bytes, in case the spans are the same memory with the color written over them next.
        if (alphaOut && !(alphaIn && inLayout.Stride == outLayout.Stride && Unsafe.AreSame(ref MemoryMarshal.GetReference(src), ref MemoryMarshal.GetReference(dst))))
        {
            Span<byte> alpha = count <= Chunk ? stackalloc byte[count] : new byte[count];
            for (int i = 0; i < count; i++)
            {
                alpha[i] = alphaIn ? src[(i * inLayout.Stride) + inAlpha] : (byte)255;
            }

            _compiled.Apply(src, inLayout, dst, outLayout, count);
            for (int i = 0; i < count; i++)
            {
                dst[(i * outLayout.Stride) + outAlpha] = alpha[i];
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
