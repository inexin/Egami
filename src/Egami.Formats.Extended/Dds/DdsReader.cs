using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Textures;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dds;

/// <summary>
/// Reads DDS: one surface at one mip level a load, chosen by <see cref="DdsReaderOptions"/> or
/// <see cref="ReaderOptions.MinimumSize"/>. The surfaces lie back to back after the header, so the reader skips to the one it
/// wants and decodes its rows in order, a row of 4 × 4 blocks at a time for the block formats. Planar video surfaces store
/// chroma after luma and are read whole.
/// </summary>
internal sealed class DdsReader : ExtendedReader, IMultiImageReader
{
    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        DdsHeader header = DdsHeader.Read(input, options.Recovery == RecoveryMode.Strict);
        (int surface, int mip) = Choose(header, options);
        int width = header.MipWidth(mip);
        int height = header.MipHeight(mip);
        PixelLayoutInfo layout = Layout(header.Format.Natural);
        return Describe(DdsFormat.Info, width, height, layout, header.Format.IsPlanar ? RasterAccess.Buffered : RasterAccess.Streamed, WorkingBytes(header, width, height)) with
        {
            ImageCount = Surfaces(header, options).Count,
            HasMetadata = true,
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        DdsHeader header = DdsHeader.Read(input, options.Recovery == RecoveryMode.Strict);
        (int surface, int mip) = Choose(header, options);
        int width = header.MipWidth(mip);
        int height = header.MipHeight(mip);
        input.CheckFrameSize(width, height);
        long offset;
        try
        {
            offset = header.Offset(surface, mip);
        }
        catch (OverflowException)
        {
            throw input.Corrupt(DdsHeader.Length, "The surfaces the header describes do not fit in a file.");
        }

        input.Skip(offset - input.Position);
        bool z = options is DdsReaderOptions { ReconstructNormalZ: true } && header.Format.IsTwoChannel;
        Source source = new(header, surface, mip, z);
        return header.Format.Natural switch
        {
            TextureNatural.Gray8 => new Rows<Gray8>(input, options, source),
            TextureNatural.Gray16 => new Rows<Gray16>(input, options, source),
            TextureNatural.GrayF32 => new Rows<GrayF32>(input, options, source),
            TextureNatural.GrayAlpha8 => new Rows<GrayAlpha8>(input, options, source),
            TextureNatural.GrayAlpha16 => new Rows<GrayAlpha16>(input, options, source),
            TextureNatural.Alpha8 => new Rows<Alpha8>(input, options, source),
            TextureNatural.Rgb8 => new Rows<Rgb8>(input, options, source),
            TextureNatural.Rgb16 => new Rows<Rgb16>(input, options, source),
            TextureNatural.RgbF32 => new Rows<RgbF32>(input, options, source),
            TextureNatural.Rgba8 => new Rows<Rgba8>(input, options, source),
            TextureNatural.Rgba16 => new Rows<Rgba16>(input, options, source),
            TextureNatural.RgbaF16 => new Rows<RgbaF16>(input, options, source),
            TextureNatural.RgbaF32 => new Rows<RgbaF32>(input, options, source),
            TextureNatural.Rgb10A2 => new Rows<Rgb10A2>(input, options, source),
            TextureNatural.Bgr565 => new Rows<Bgr565>(input, options, source),
            TextureNatural.Bgr555 => new Rows<Bgr555>(input, options, source),
            TextureNatural.Bgra5551 => new Rows<Bgra5551>(input, options, source),
            _ => new Rows<Bgra4444>(input, options, source),
        };
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        DdsHeader header = DdsHeader.Read(input, options.Recovery == RecoveryMode.Strict);
        DdsReaderOptions dds = options as DdsReaderOptions ?? new DdsReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
        return [.. Surfaces(header, options).Select(s => dds with { Surface = s.Surface, MipLevel = s.Mip })];
    }

    /// <summary>Returns the surfaces LoadAll gives: every surface at mip 0, each followed by its mips when they are asked for.</summary>
    private static List<(int Surface, int Mip)> Surfaces(DdsHeader header, ReaderOptions options)
    {
        bool mips = options is DdsReaderOptions { IncludeMipLevels: true };
        List<(int, int)> list = [];
        for (int s = 0; s < header.Surfaces; s++)
        {
            list.Add((s, 0));
            for (int m = 1; mips && m < header.MipLevels; m++)
            {
                // A volume's mip m has half as many depth slices: slice s of mip 0 carries the mips its slice s >> m starts.
                if (header.Dimension != DdsDimension.Texture3D || (s % (1 << Math.Min(m, 30)) == 0 && (s >> m) < header.MipDepth(m)))
                {
                    list.Add((s, m));
                }
            }
        }

        return list;
    }

    /// <summary>Returns the surface and mip a load reads, and for a volume the depth slice of that mip.</summary>
    private static (int Surface, int Mip) Choose(DdsHeader header, ReaderOptions options)
    {
        DdsReaderOptions? dds = options as DdsReaderOptions;
        int surface = dds?.Surface ?? 0;
        if (surface >= header.Surfaces)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Surface {surface} was asked for; the texture has {header.Surfaces}.");
        }

        int mip = 0;
        if (dds?.MipLevel is int asked)
        {
            if (asked >= header.MipLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"Mip level {asked} was asked for; the texture has {header.MipLevels}.");
            }

            mip = asked;
        }
        else if (options.MinimumSize is Size minimum && options.Region is null)
        {
            while (mip + 1 < header.MipLevels && header.MipWidth(mip + 1) >= minimum.Width && header.MipHeight(mip + 1) >= minimum.Height)
            {
                mip++;
            }
        }

        return (header.Dimension == DdsDimension.Texture3D ? Math.Min(surface >> mip, header.MipDepth(mip) - 1) : surface, mip);
    }

    private static long WorkingBytes(DdsHeader header, int width, int height)
    {
        TextureFormat f = header.Format;
        if (f.IsPlanar)
        {
            return f.SurfaceBytes(width, height);
        }

        long row = (long)width * TextureDecode.NaturalBytes(f.Natural);
        return f.RowBytes(width) + (f.IsBlock ? row * 4 : row);
    }

    private static PixelLayoutInfo Layout(TextureNatural natural) => natural switch
    {
        TextureNatural.Gray8 => Gray8.Layout,
        TextureNatural.Gray16 => Gray16.Layout,
        TextureNatural.GrayF32 => GrayF32.Layout,
        TextureNatural.GrayAlpha8 => GrayAlpha8.Layout,
        TextureNatural.GrayAlpha16 => GrayAlpha16.Layout,
        TextureNatural.Alpha8 => Alpha8.Layout,
        TextureNatural.Rgb8 => Rgb8.Layout,
        TextureNatural.Rgb16 => Rgb16.Layout,
        TextureNatural.RgbF32 => RgbF32.Layout,
        TextureNatural.Rgba8 => Rgba8.Layout,
        TextureNatural.Rgba16 => Rgba16.Layout,
        TextureNatural.RgbaF16 => RgbaF16.Layout,
        TextureNatural.RgbaF32 => RgbaF32.Layout,
        TextureNatural.Rgb10A2 => Rgb10A2.Layout,
        TextureNatural.Bgr565 => Bgr565.Layout,
        TextureNatural.Bgr555 => Bgr555.Layout,
        TextureNatural.Bgra5551 => Bgra5551.Layout,
        _ => Bgra4444.Layout,
    };

    /// <summary>The surface a load reads.</summary>
    private sealed record Source(DdsHeader Header, int Surface, int Mip, bool ReconstructZ);

    /// <summary>The rows of one surface at one mip level.</summary>
    private sealed class Rows<TNatural> : ExtendedRows<TNatural, TNatural>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly DdsHeader _header;
        private readonly TextureFormat _format;
        private readonly bool _reconstructZ;
        private readonly bool _unpremultiply;
        private readonly StoredMips? _mips;
        private readonly IMemoryOwner<byte> _stored;
        private readonly IMemoryOwner<byte>? _strip;
        private readonly int _pixelBytes;
        private byte[]? _surface;
        private int _surfaceLength;

        public Rows(EgamiInput input, ReaderOptions options, Source source)
            : base(input, options, DdsFormat.Info, "Dds", source.Header.MipWidth(source.Mip), source.Header.MipHeight(source.Mip))
        {
            _header = source.Header;
            _format = source.Header.Format;
            _reconstructZ = source.ReconstructZ;
            _unpremultiply = _header.AlphaMode == DdsAlphaMode.Premultiplied;
            _pixelBytes = TextureDecode.NaturalBytes(_format.Natural);
            _stored = input.RentScratch(checked((int)Math.Max(_format.RowBytes(Width), 16)), ClearMode.Zero);
            if (_format.IsBlock)
            {
                _strip = input.RentScratch(checked(Width * _pixelBytes * 4), ClearMode.Zero);
            }

            if (_format.Codec == TextureCodec.Palette8 && Array.TrueForAll(_header.Palette, e => e >> 24 == 0))
            {
                // A P8 palette whose entries all have flags 0 has no alpha: read it opaque.
                for (int i = 0; i < 256; i++)
                {
                    _header.Palette[i] |= 0xFF000000;
                }
            }

            Metadata.FormatDetails = new DdsDetails
            {
                DxgiFormat = _format.Dxgi,
                FormatName = _format.Name,
                FourCC = _header.FourCC,
                Dx10Header = _header.Dx10,
                Dimension = _header.Dimension,
                IsCube = _header.IsCube,
                ArraySize = _header.ArraySize,
                Depth = _header.Depth,
                MipLevels = _header.MipLevels,
                AlphaMode = _header.AlphaMode,
                IsSrgb = _format.Srgb,
                Surface = source.Surface,
                MipLevel = source.Mip,
                Mips = _mips = KeepsMips(_header, source, options) ? new StoredMips { Dxgi = _format.Dxgi, Size = new Size(Width, Height), Count = _header.MipLevels - 1 } : null,
            };
            if (_format.Srgb)
            {
                Metadata.ColorSpace = ColorSpaceHint.Srgb;
            }

            foreach ((string id, string message) in _header.Repairs)
            {
                Warn(id, message);
            }
        }

        public override RasterAccess Access => _format.IsPlanar ? RasterAccess.Buffered : RasterAccess.Streamed;

        public override long WorkingBytes => base.WorkingBytes + _stored.Memory.Length + (_strip?.Memory.Length ?? 0) + (_surface?.Length ?? 0);

        protected override bool DecodeRow(int y, Span<TNatural> row)
        {
            Span<byte> pixels = MemoryMarshal.AsBytes(row);
            Span<byte> stored = _stored.Memory.Span[..(int)_format.RowBytes(Width)];
            if (_format.IsPlanar)
            {
                if (_surface is null)
                {
                    long bytes = _format.SurfaceBytes(Width, Height);
                    Input.CheckMemory(bytes);
                    _surface = new byte[bytes];
                    _surfaceLength = ReadFully(_surface);
                    if (_surfaceLength < bytes)
                    {
                        Damaged("DataMissing", "The file ends inside the surface; what is missing reads as black.");
                    }
                }

                TextureDecode.Planar(_format, _surface.AsSpan(0, _surfaceLength), y, pixels, Width, Height);
            }
            else if (_format.IsBlock)
            {
                Span<byte> strip = _strip!.Memory.Span;
                int stride = Width * _pixelBytes;
                if ((y & 3) == 0)
                {
                    if (ReadFully(stored) < stored.Length)
                    {
                        pixels.Clear();
                        return false;
                    }

                    TextureDecode.Blocks(_format, stored, strip, Width, Math.Min(4, Height - y), stride);
                }

                strip.Slice((y & 3) * stride, stride).CopyTo(pixels);
            }
            else
            {
                if (ReadFully(stored) < stored.Length)
                {
                    pixels.Clear();
                    return false;
                }

                TextureDecode.Row(_format, stored, pixels, Width, _header.Palette);
            }

            if (_unpremultiply)
            {
                TextureDecode.Unpremultiply(_format.Natural, pixels);
            }

            if (_reconstructZ)
            {
                TextureDecode.ReconstructZ(_format.Natural, pixels);
            }

            return true;
        }

        /// <summary>Reads the mips after mip 0, so an unchanged raster saves with the file's own mips.</summary>
        protected override void Finish()
        {
            if (_mips is null)
            {
                return;
            }

            long bytes = 0;
            for (int m = 1; m < _header.MipLevels; m++)
            {
                bytes += _header.MipBytes(m);
            }

            if (bytes > Array.MaxLength)
            {
                return;
            }

            Input.CheckMemory(bytes);
            byte[] mips = new byte[bytes];
            if (ReadFully(mips) == mips.Length)
            {
                _mips.Bytes = mips;
            }
        }

        /// <summary>Gets whether a load keeps the stored mips: mip 0 of a 2D surface whose bytes are its DXGI format's own.</summary>
        private static bool KeepsMips(DdsHeader header, Source source, ReaderOptions options)
        {
            TextureFormat f = header.Format;
            return options.ReadMetadata && source.Mip == 0 && header.MipLevels > 1 && header.Dimension != DdsDimension.Texture3D
                && header.AlphaMode != DdsAlphaMode.Premultiplied && f.Dxgi != 0 && TextureFormat.FromDxgi(f.Dxgi)?.Name == f.Name;
        }

        protected override void Dispose(bool disposing)
        {
            _stored.Dispose();
            _strip?.Dispose();
            base.Dispose(disposing);
        }

        private int ReadFully(Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = Input.Read(destination[total..]);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
    }
}
