using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// An image as a paint: shapes are filled with the image's pixels, pixel (0, 0) covering the paint-space square from 0 to 1, under
/// <see cref="Paint.LocalMatrix"/> and the canvas transform. <see cref="TileX"/> and <see cref="TileY"/> say what lies past its
/// edges, so it tiles as a pattern, and <see cref="TileMode.Decal"/> leaves the outside transparent, as image masks and stencils
/// need. When the paint shrinks the image, sampling reads the nearest smaller level of a <see cref="MipChain"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="image-paint"/>
/// </example>
public sealed record class ImagePaint : Paint
{
    private static readonly SamplingOptions DefaultSampling = new();

    private readonly Raster _image;
    private readonly TileMode _tileX;
    private readonly TileMode _tileY;
    private readonly SamplingOptions _sampling = DefaultSampling;

    /// <summary>Initializes a new instance of the <see cref="ImagePaint"/> class.</summary>
    /// <param name="image">The image; its first frame is used, read when a call draws, so later changes to it show.</param>
    /// <param name="tileX">What lies left and right of the image.</param>
    /// <param name="tileY">What lies above and below it.</param>
    /// <param name="sampling">The filter and color space; Egami's default sampling when <see langword="null"/>. Its edge mode and background are not used: the tile modes decide the edges.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tile mode is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="image-paint"/>
    /// </example>
    public ImagePaint(Raster image, TileMode tileX = TileMode.Clamp, TileMode tileY = TileMode.Clamp, SamplingOptions? sampling = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        TileX = tileX;
        TileY = tileY;
        Sampling = sampling ?? DefaultSampling;
    }

    /// <summary>Gets the image.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Raster Image
    {
        get => _image;
        init => _image = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets what lies left and right of the image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TileMode TileX
    {
        get => _tileX;
        init => _tileX = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets what lies above and below the image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TileMode TileY
    {
        get => _tileY;
        init => _tileY = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets the filter and color space the image is sampled with.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public SamplingOptions Sampling
    {
        get => _sampling;
        init => _sampling = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
        => TryInverse(toDevice, out Matrix3x2 inverse) ? ImageShader.Create(this, inverse) : null;

    /// <inheritdoc/>
    internal override PixelShader<TPixel>? CreatePixelShader<TPixel>(Matrix3x2 toDevice, ColorSpace colorSpace)
        => CreatePixelShader<TPixel>(toDevice, colorSpace, null);

    /// <summary>
    /// Returns the shader for a fill into <paramref name="target"/>: when the image shares no memory with the target, an unturned
    /// mapping reads the image's rows as it needs them instead of converting the whole image first.
    /// </summary>
    internal PixelShader<TPixel>? CreatePixelShader<TPixel>(Matrix3x2 toDevice, ColorSpace colorSpace, RasterTarget? target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (!TryInverse(toDevice, out Matrix3x2 inverse))
        {
            return null;
        }

        // A whole-pixel shift with a filter that passes pixels through copies rows, in the target's own pixels.
        if (ImageShader.IsPixelCopy(inverse, _sampling) && !(Dither && TPixel.Layout.ChannelType == PixelChannelType.Unorm8))
        {
            return new ImageCopyShader<TPixel>(this, inverse);
        }

        return new ConvertingShader<TPixel>(ImageShader.Create(this, inverse, target), Dither);
    }
}
