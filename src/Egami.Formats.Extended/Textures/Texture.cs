using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Ktx;
using Egami.Operations;

namespace Egami.Formats.Extended.Textures;

/// <summary>
/// A whole GPU texture: every array layer, cube face and depth slice at every mip level, loaded from DDS, KTX or KTX 2, edited
/// image by image, and saved to DDS or KTX 2 with exactly the mips it holds. Each image is a <see cref="Raster"/> in the pixel
/// type the reader gives the format; the texture owns them and disposes them with itself.
/// </summary>
/// <remarks>
/// A 2D texture, texture array or cube map has <see cref="Layers"/> × <see cref="Faces"/> surfaces, each with a chain of
/// <see cref="MipLevels"/> images halving down. A volume texture (<see cref="IsVolume"/>) has <see cref="Depth"/> slices at mip 0,
/// and half as many (at least one) at each mip after it, as both file formats store it.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="texture"/>
/// </example>
public sealed class Texture : IDisposable
{
    private List<Raster>[] _mips;

    private Texture(int width, int height, int depth, int layers, bool cube, List<Raster>[] levels)
    {
        Width = width;
        Height = height;
        Depth = depth;
        Layers = layers;
        IsCube = cube;
        _mips = levels;
    }

    /// <summary>Gets the width of mip 0.</summary>
    public int Width { get; }

    /// <summary>Gets the height of mip 0.</summary>
    public int Height { get; }

    /// <summary>Gets the number of depth slices of mip 0: more than 1 for a volume texture, else 1.</summary>
    public int Depth { get; }

    /// <summary>Gets the number of array layers (cubes, for a cube map), at least 1.</summary>
    public int Layers { get; }

    /// <summary>Gets whether every layer is a cube map of six faces in the order +X, −X, +Y, −Y, +Z, −Z.</summary>
    public bool IsCube { get; }

    /// <summary>Gets whether the texture is a volume: a stack of <see cref="Depth"/> slices that shrinks with each mip.</summary>
    public bool IsVolume => Depth > 1;

    /// <summary>Gets the number of faces of each layer: 6 for a cube map, else 1.</summary>
    public int Faces => IsCube ? 6 : 1;

    /// <summary>Gets the number of mip levels, at least 1.</summary>
    public int MipLevels => _mips.Length;

    /// <summary>
    /// Creates a 2D texture, texture array or cube map from the mip 0 images of its surfaces, layer by layer and, for cube maps,
    /// face by face. The texture takes ownership of the rasters. <see cref="GenerateMips"/> adds the mips.
    /// </summary>
    /// <param name="surfaces">The surfaces, all the same size.</param>
    /// <param name="cube">Whether the surfaces are cube faces, six a layer.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">There are no surfaces, they differ in size, or a cube map's faces are not square or not six a layer.</exception>
    public static Texture FromSurfaces(IReadOnlyList<Raster> surfaces, bool cube = false)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        Check(surfaces);
        if (cube && (surfaces.Count % 6 != 0 || surfaces[0].Width != surfaces[0].Height))
        {
            throw new ArgumentException($"A cube map needs square faces, six a cube; {surfaces.Count} rasters of {surfaces[0].Width}×{surfaces[0].Height} were given.", nameof(surfaces));
        }

        return new Texture(surfaces[0].Width, surfaces[0].Height, 1, cube ? surfaces.Count / 6 : surfaces.Count, cube, [[.. surfaces]]);
    }

    /// <summary>
    /// Creates a volume texture from the depth slices of its mip 0, front to back. The texture takes ownership of the rasters.
    /// </summary>
    /// <param name="slices">The slices, all the same size; at least two.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">There are fewer than two slices, or they differ in size.</exception>
    public static Texture FromSlices(IReadOnlyList<Raster> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        Check(slices);
        if (slices.Count < 2)
        {
            throw new ArgumentException("A volume texture needs at least two slices; use FromSurfaces for one image.", nameof(slices));
        }

        return new Texture(slices[0].Width, slices[0].Height, slices.Count, 1, false, [[.. slices]]);
    }

    /// <summary>Loads every image of a DDS, KTX or KTX 2 file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The context, or <see langword="null"/> for the default; the texture formats need not be registered in it.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not DDS, KTX or KTX 2.</exception>
    public static Texture Load(string path, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), context);
    }

    /// <summary>Loads every image of a DDS, KTX or KTX 2 file from a stream, read to its end.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="context">The context, or <see langword="null"/> for the default; the texture formats need not be registered in it.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not DDS, KTX or KTX 2.</exception>
    public static Texture Load(Stream stream, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream data = new();
        stream.CopyTo(data);
        return Load(data.GetBuffer().AsMemory(0, (int)data.Length), context);
    }

    /// <summary>Loads every image of a DDS, KTX or KTX 2 file in memory.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="context">The context, or <see langword="null"/> for the default; the texture formats need not be registered in it.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not DDS, KTX or KTX 2.</exception>
    public static Texture Load(ReadOnlyMemory<byte> data, EgamiContext? context = null)
    {
        ReadOnlySpan<byte> head = data.Span[..Math.Min(data.Length, 16)];
        if (DdsFormat.Info.Matches(head))
        {
            using RasterList all = Raster.LoadAll(data, DdsFormat.Info, context, new DdsReaderOptions { IncludeMipLevels = true });
            DdsDetails details = (DdsDetails)all[0].Metadata.FormatDetails!;
            bool volume = details.Dimension == DdsDimension.Texture3D;
            int faces = details.IsCube ? 6 : 1;
            return Gather(all, volume ? details.Depth : 1, volume ? 1 : details.ArraySize, details.IsCube, details.MipLevels,
                r => ((DdsDetails)r.Metadata.FormatDetails!).Surface, r => ((DdsDetails)r.Metadata.FormatDetails!).MipLevel);
        }

        foreach (FormatInfo format in (FormatInfo[])[KtxFormat.Info, Ktx2Format.Info])
        {
            if (format.Matches(head))
            {
                using RasterList all = Raster.LoadAll(data, format, context, new KtxReaderOptions { IncludeMipLevels = true });
                KtxDetails details = (KtxDetails)all[0].Metadata.FormatDetails!;
                return Gather(all, details.Depth, details.Layers, details.IsCube, details.MipLevels,
                    r => ((KtxDetails)r.Metadata.FormatDetails!).Surface, r => ((KtxDetails)r.Metadata.FormatDetails!).MipLevel);
            }
        }

        throw new UnsupportedFormatException("A texture loads from DDS, KTX or KTX 2; the data is none of them.");
    }

    /// <summary>Returns the width of a mip level.</summary>
    /// <param name="mip">The mip level.</param>
    /// <returns>The width, at least 1.</returns>
    public int MipWidth(int mip) => Math.Max(1, Width >> mip);

    /// <summary>Returns the height of a mip level.</summary>
    /// <param name="mip">The mip level.</param>
    /// <returns>The height, at least 1.</returns>
    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    /// <summary>Returns the number of depth slices of a mip level: 1 unless the texture is a volume.</summary>
    /// <param name="mip">The mip level.</param>
    /// <returns>The number of slices, at least 1.</returns>
    public int MipDepth(int mip) => Math.Max(1, Depth >> mip);

    /// <summary>Gets one image. The texture keeps owning it; edit it in place or replace it with <see cref="SetImage"/>.</summary>
    /// <param name="mip">The mip level.</param>
    /// <param name="layer">The array layer (the cube, for a cube map).</param>
    /// <param name="face">The cube face, 0 to 5, or 0.</param>
    /// <param name="slice">The depth slice of a volume at that mip, or 0.</param>
    /// <returns>The image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public Raster GetImage(int mip = 0, int layer = 0, int face = 0, int slice = 0)
    {
        int index = Index(mip, layer, face, slice);
        return _mips[mip][index];
    }

    /// <summary>
    /// Replaces one image with a raster of the mip level's size, which the texture then owns; it disposes the image it replaces.
    /// The new image may be of another pixel type; saving converts it to the file's format.
    /// </summary>
    /// <param name="image">The new image.</param>
    /// <param name="mip">The mip level.</param>
    /// <param name="layer">The array layer (the cube, for a cube map).</param>
    /// <param name="face">The cube face, 0 to 5, or 0.</param>
    /// <param name="slice">The depth slice of a volume at that mip, or 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    /// <exception cref="ArgumentException">The image is not the mip level's size.</exception>
    public void SetImage(Raster image, int mip = 0, int layer = 0, int face = 0, int slice = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        int index = Index(mip, layer, face, slice);
        if (image.Width != MipWidth(mip) || image.Height != MipHeight(mip))
        {
            throw new ArgumentException($"Mip {mip} is {MipWidth(mip)}×{MipHeight(mip)}; the image is {image.Width}×{image.Height}.", nameof(image));
        }

        Raster old = _mips[mip][index];
        _mips[mip][index] = image;
        if (!ReferenceEquals(old, image))
        {
            old.Dispose();
        }
    }

    /// <summary>
    /// Replaces the mips after mip 0 with new ones made from it with <see cref="MipChain"/>: each the average of 2 × 2 blocks of
    /// premultiplied pixels of the level before, down to 1 × 1 or to <paramref name="mipLevels"/> levels.
    /// </summary>
    /// <param name="mipLevels">How many levels to keep in all, mip 0 included: 0 (the default) for the full chain.</param>
    /// <param name="colorSpace">
    /// Where to average: <see cref="ColorSpace.Encoded"/> (the default) as the values are stored, or <see cref="ColorSpace.Linear"/>
    /// in linear light, right for sRGB colour.
    /// </param>
    /// <exception cref="NotSupportedException">The texture is a volume, whose mips would average slices too.</exception>
    public void GenerateMips(int mipLevels = 0, ColorSpace colorSpace = ColorSpace.Encoded)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevels);
        if (IsVolume)
        {
            throw new NotSupportedException("GenerateMips makes the mips of 2D textures and cube maps; a volume keeps the mips it has.");
        }

        int full = 1 + (int)Math.Log2(Math.Max(Width, Height));
        int count = mipLevels == 0 ? full : Math.Min(mipLevels, full);
        List<Raster>[] mips = new List<Raster>[count];
        mips[0] = _mips[0];
        for (int m = 1; m < count; m++)
        {
            mips[m] = [];
        }

        foreach (Raster surface in _mips[0])
        {
            using MipChain chain = MipChain.Build(surface, colorSpace);
            for (int m = 1; m < count; m++)
            {
                // A texture's mip rounds its size down; the chain's halving rounds up, so an odd size takes a box resize from it.
                mips[m].Add(chain.Resize(MipWidth(m), MipHeight(m), ResampleFilter.Box));
            }
        }

        DisposeMips(1);
        _mips = mips;
    }

    /// <summary>Keeps the first <paramref name="mipLevels"/> mip levels and disposes the rest.</summary>
    /// <param name="mipLevels">How many levels to keep, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mipLevels"/> is less than 1.</exception>
    public void TrimMips(int mipLevels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mipLevels, 1);
        if (mipLevels < _mips.Length)
        {
            DisposeMips(mipLevels);
            _mips = _mips[..mipLevels];
        }
    }

    /// <summary>
    /// Saves the texture as DDS or KTX 2, chosen by the file's extension (<c>.dds</c> or <c>.ktx2</c>), with every image and
    /// mip it holds.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options"><see cref="DdsWriterOptions"/> or <see cref="Ktx2WriterOptions"/>, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="NotSupportedException">The extension is neither <c>.dds</c> nor <c>.ktx2</c>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="texture"/>
    /// </example>
    public void Save(string path, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FormatInfo format = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".DDS" => DdsFormat.Info,
            ".KTX2" => Ktx2Format.Info,
            _ => throw new NotSupportedException($"A texture saves as .dds or .ktx2; {Path.GetFileName(path)} is neither."),
        };
        using FileStream file = File.Create(path);
        Save(file, format, options);
    }

    /// <summary>
    /// Saves the texture with every image and mip it holds, in <see cref="DdsFormat.Info"/> or <see cref="Ktx2Format.Info"/>. The
    /// options choose the format, effort and so on as for a raster; their mip count, when not 0, keeps only that many of the
    /// texture's mips, and their cube flag is ignored: the texture's own layout is written.
    /// </summary>
    /// <param name="output">The stream.</param>
    /// <param name="format"><see cref="DdsFormat.Info"/> or <see cref="Ktx2Format.Info"/>.</param>
    /// <param name="options"><see cref="DdsWriterOptions"/> or <see cref="Ktx2WriterOptions"/>, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="NotSupportedException">The format is not DDS or KTX 2, or DDS is asked to hold an array of volumes.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="texture"/>
    /// </example>
    public void Save(Stream output, FormatInfo format, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(format);
        if (format.Writer is not ITextureWriter writer)
        {
            throw new NotSupportedException($"A texture saves as DDS or KTX 2, not {format.Name}.");
        }

        writer.WriteTexture(this, output, options ?? WriterOptions.Default);
    }

    /// <summary>Disposes every image.</summary>
    public void Dispose()
    {
        foreach (List<Raster> level in _mips)
        {
            foreach (Raster raster in level)
            {
                raster.Dispose();
            }
        }

        _mips = [];
    }

    /// <summary>Gets a mip level's images in KTX 2's order: every layer, every face of it, every depth slice of that.</summary>
    internal IReadOnlyList<Raster> Level(int mip) => _mips[mip];

    private static void Check(IReadOnlyList<Raster> rasters)
    {
        if (rasters.Count == 0)
        {
            throw new ArgumentException("A texture needs at least one image.", nameof(rasters));
        }

        foreach (Raster raster in rasters)
        {
            ArgumentNullException.ThrowIfNull(raster, nameof(rasters));
            if (raster.Width != rasters[0].Width || raster.Height != rasters[0].Height)
            {
                throw new ArgumentException($"Every image of a texture's mip 0 has the same size; {raster.Width}×{raster.Height} differs from {rasters[0].Width}×{rasters[0].Height}.", nameof(rasters));
            }
        }
    }

    /// <summary>
    /// Sorts what LoadAll gave (every surface at mip 0, each followed by its mips; for a volume, slice s of mip 0 followed by
    /// the mips whose slice s >> m it starts) into levels, taking the rasters out of the list.
    /// </summary>
    private static Texture Gather(RasterList all, int depth, int layers, bool cube, int mips, Func<Raster, int> surfaceOf, Func<Raster, int> mipOf)
    {
        int faces = cube ? 6 : 1;
        List<Raster>[] levels = new List<Raster>[mips];
        Raster?[][] slots = new Raster?[mips][];
        for (int m = 0; m < mips; m++)
        {
            slots[m] = new Raster?[layers * faces * Math.Max(1, depth >> m)];
        }

        for (int i = 0; i < all.Count; i++)
        {
            Raster raster = all[i];
            int surface = surfaceOf(raster);
            int mip = mipOf(raster);
            int index = depth > 1 ? ((surface / depth) * Math.Max(1, depth >> mip)) + ((surface % depth) >> mip) : surface;
            slots[mip][index] = all.Detach(i);
        }

        for (int m = 0; m < mips; m++)
        {
            levels[m] = [.. slots[m].Select(r => r ?? throw new CorruptDataException("Texture", 0, $"mip {m} is missing an image"))];
        }

        return new Texture(levels[0][0].Width, levels[0][0].Height, depth, layers, cube, levels);
    }

    private int Index(int mip, int layer, int face, int slice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mip, MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, Layers);
        ArgumentOutOfRangeException.ThrowIfNegative(face);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(face, Faces);
        ArgumentOutOfRangeException.ThrowIfNegative(slice);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slice, MipDepth(mip));
        return (((layer * Faces) + face) * MipDepth(mip)) + slice;
    }

    private void DisposeMips(int from)
    {
        for (int m = from; m < _mips.Length; m++)
        {
            foreach (Raster raster in _mips[m])
            {
                raster.Dispose();
            }
        }
    }
}

/// <summary>A writer that writes a whole <see cref="Texture"/> with the mips it holds.</summary>
internal interface ITextureWriter
{
    void WriteTexture(Texture texture, Stream output, WriterOptions options);
}
