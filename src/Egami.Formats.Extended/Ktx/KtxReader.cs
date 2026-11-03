using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Compression;
using Egami.Formats.Extended.Textures;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Ktx;

/// <summary>
/// Reads KTX 1.1 and KTX 2.0: one surface at one mip level a load, chosen by <see cref="KtxReaderOptions"/> or
/// <see cref="ReaderOptions.MinimumSize"/>. A texture stored top-down and not supercompressed streams: the reader skips to the
/// image it wants and decodes its rows in order, a row of 4 × 4 blocks at a time for the block formats. A texture stored
/// bottom-up is read whole first, and a supercompressed KTX 2 level is inflated whole into a pooled buffer, one level a load.
/// </summary>
internal sealed class KtxReader(KtxContainer container) : ExtendedReader, IMultiImageReader
{
    private FormatInfo Info => container == KtxContainer.Ktx1 ? KtxFormat.Info : Ktx2Format.Info;

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        KtxTexture texture = Header(input, options);
        (int surface, int mip) = Choose(texture, options);
        int width = texture.MipWidth(mip);
        int height = texture.MipHeight(mip);
        Plan plan = Plan.For(texture, surface, mip, options);
        return Describe(Info, width, height, Layout(plan.Work), plan.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed, WorkingBytes(texture, plan, width)) with
        {
            ImageCount = Surfaces(texture, options).Count,
            HasMetadata = true,
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        KtxTexture texture = Header(input, options);
        (int surface, int mip) = Choose(texture, options);
        input.CheckFrameSize(texture.MipWidth(mip), texture.MipHeight(mip));
        Plan plan = Plan.For(texture, surface, mip, options);
        return plan.Work switch
        {
            TextureNatural.Gray8 => new Rows<Gray8>(input, options, Info, plan),
            TextureNatural.Gray16 => new Rows<Gray16>(input, options, Info, plan),
            TextureNatural.GrayF32 => new Rows<GrayF32>(input, options, Info, plan),
            TextureNatural.GrayAlpha8 => new Rows<GrayAlpha8>(input, options, Info, plan),
            TextureNatural.GrayAlpha16 => new Rows<GrayAlpha16>(input, options, Info, plan),
            TextureNatural.Alpha8 => new Rows<Alpha8>(input, options, Info, plan),
            TextureNatural.Rgb8 => new Rows<Rgb8>(input, options, Info, plan),
            TextureNatural.Rgb16 => new Rows<Rgb16>(input, options, Info, plan),
            TextureNatural.RgbF32 => new Rows<RgbF32>(input, options, Info, plan),
            TextureNatural.Rgba8 => new Rows<Rgba8>(input, options, Info, plan),
            TextureNatural.Rgba16 => new Rows<Rgba16>(input, options, Info, plan),
            TextureNatural.RgbaF16 => new Rows<RgbaF16>(input, options, Info, plan),
            TextureNatural.RgbaF32 => new Rows<RgbaF32>(input, options, Info, plan),
            TextureNatural.Rgb10A2 => new Rows<Rgb10A2>(input, options, Info, plan),
            TextureNatural.Bgr565 => new Rows<Bgr565>(input, options, Info, plan),
            TextureNatural.Bgr555 => new Rows<Bgr555>(input, options, Info, plan),
            TextureNatural.Bgra5551 => new Rows<Bgra5551>(input, options, Info, plan),
            _ => new Rows<Bgra4444>(input, options, Info, plan),
        };
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        KtxTexture texture = Header(input, options);
        KtxReaderOptions ktx = options as KtxReaderOptions ?? new KtxReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
        return [.. Surfaces(texture, options).Select(s => ktx with { Surface = s.Surface, MipLevel = s.Mip })];
    }

    private KtxTexture Header(EgamiInput input, ReaderOptions options)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        return container == KtxContainer.Ktx1 ? Ktx1Header.Read(input, strict) : Ktx2Header.Read(input, strict);
    }

    /// <summary>Returns the surfaces LoadAll gives: every surface at mip 0, each followed by its mips when they are asked for.</summary>
    private static List<(int Surface, int Mip)> Surfaces(KtxTexture texture, ReaderOptions options)
    {
        bool mips = options is KtxReaderOptions { IncludeMipLevels: true };
        List<(int, int)> list = [];
        for (int s = 0; s < texture.Surfaces; s++)
        {
            list.Add((s, 0));
            int slice = texture.Split(s).Slice;
            for (int m = 1; mips && m < texture.MipLevels; m++)
            {
                // A volume's mip m has half as many depth slices: slice s of mip 0 carries the mips its slice s >> m starts.
                if (texture.Dimension != KtxDimension.Texture3D || (slice % (1 << Math.Min(m, 30)) == 0 && (slice >> m) < texture.MipDepth(m)))
                {
                    list.Add((s, m));
                }
            }
        }

        return list;
    }

    /// <summary>Returns the surface and mip a load reads.</summary>
    private static (int Surface, int Mip) Choose(KtxTexture texture, ReaderOptions options)
    {
        KtxReaderOptions? ktx = options as KtxReaderOptions;
        int surface = ktx?.Surface ?? 0;
        if (surface >= texture.Surfaces)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Surface {surface} was asked for; the texture has {texture.Surfaces}.");
        }

        int mip = 0;
        if (ktx?.MipLevel is int asked)
        {
            if (asked >= texture.MipLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"Mip level {asked} was asked for; the texture has {texture.MipLevels}.");
            }

            mip = asked;
        }
        else if (options.MinimumSize is Size minimum && options.Region is null)
        {
            while (mip + 1 < texture.MipLevels && texture.MipWidth(mip + 1) >= minimum.Width && texture.MipHeight(mip + 1) >= minimum.Height)
            {
                mip++;
            }
        }

        return (surface, mip);
    }

    private static long WorkingBytes(KtxTexture texture, Plan plan, int width)
    {
        long natural = (long)width * TextureDecode.NaturalBytes(texture.Format.Natural);
        long work = (long)width * TextureDecode.NaturalBytes(plan.Work);
        long rows = texture.Format.IsBlock ? natural * 4 : natural;
        long whole = plan.Supercompressed ? plan.LevelLength + plan.LevelUncompressed : plan.Buffered ? plan.ImageBytes : 0;
        return texture.RowPitch(plan.Mip) + rows + work + whole;
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

    /// <summary>What a load reads: the surface and mip, where the image lies, and how its rows turn into the work pixel type.</summary>
    private sealed class Plan
    {
        public required KtxTexture Texture { get; init; }

        public int Surface { get; init; }

        public int Mip { get; init; }

        /// <summary>Gets the pixel type rows come out as: the format's natural one, or a four-channel one a swizzle needs.</summary>
        public TextureNatural Work { get; init; }

        /// <summary>Gets the swizzle as channel indices (0 to 3, 4 for zero, 5 for one), or <see langword="null"/> for none.</summary>
        public byte[]? Swizzle { get; init; }

        public bool FlipX { get; init; }

        public bool FlipY { get; init; }

        public bool ReconstructZ { get; init; }

        /// <summary>Gets where the image starts in the file, or in the inflated level of a supercompressed one.</summary>
        public long ImageOffset { get; init; }

        /// <summary>Gets the stored bytes of the image.</summary>
        public long ImageBytes { get; init; }

        /// <summary>Gets the bytes of the image the file holds, fewer than <see cref="ImageBytes"/> when a level is short.</summary>
        public long Available { get; init; }

        /// <summary>Gets where a KTX 1 level's imageSize word is, or -1.</summary>
        public long ImageSizeAt { get; init; } = -1;

        public bool Supercompressed => Texture.Supercompression != KtxSupercompression.None;

        public bool Buffered => FlipY || Supercompressed;

        public long LevelOffset { get; init; }

        public long LevelLength { get; init; }

        public long LevelUncompressed { get; init; }

        public static Plan For(KtxTexture texture, int surface, int mip, ReaderOptions options)
        {
            bool keep = options is KtxReaderOptions { KeepStoredLayout: true };
            (int index, _) = texture.Image(surface, mip, !keep && texture.FlipZ);
            long imageBytes;
            long imageOffset;
            long available;
            long levelOffset = 0;
            long levelLength = 0;
            long levelUncompressed = 0;
            long imageSizeAt = -1;
            try
            {
                imageBytes = texture.ImageBytes(mip);
                if (texture.Container == KtxContainer.Ktx1)
                {
                    imageSizeAt = texture.Ktx1LevelStart(mip);
                    imageOffset = checked(imageSizeAt + 4 + texture.Ktx1ImageOffset(mip, index));
                    available = imageBytes;
                }
                else
                {
                    KtxLevel level = texture.Levels[mip];
                    long inLevel = checked(index * imageBytes);
                    levelOffset = level.Offset;
                    levelLength = level.Length;
                    levelUncompressed = texture.Supercompression == KtxSupercompression.None ? level.Length : level.UncompressedLength;
                    imageOffset = texture.Supercompression == KtxSupercompression.None ? checked(level.Offset + inLevel) : inLevel;
                    available = Math.Clamp(levelUncompressed - inLevel, 0, imageBytes);
                }
            }
            catch (OverflowException)
            {
                throw new CorruptDataException(texture.Container == KtxContainer.Ktx1 ? "KTX" : "KTX2", -1, "The images the header describes do not fit in a file.");
            }

            byte[]? swizzle = keep ? null : SwizzleOf(texture);
            return new Plan
            {
                Texture = texture,
                Surface = surface,
                Mip = mip,
                Work = swizzle is null ? texture.Format.Natural : Widened(texture.Format.Natural),
                Swizzle = swizzle,
                FlipX = !keep && texture.FlipX,
                FlipY = !keep && texture.FlipY && texture.MipHeight(mip) > 1,
                ReconstructZ = options is KtxReaderOptions { ReconstructNormalZ: true } && texture.Format.IsTwoChannel,
                ImageOffset = imageOffset,
                ImageBytes = imageBytes,
                Available = available,
                ImageSizeAt = imageSizeAt,
                LevelOffset = levelOffset,
                LevelLength = levelLength,
                LevelUncompressed = levelUncompressed,
            };
        }

        /// <summary>Returns the swizzle as indices, or <see langword="null"/> when it changes nothing for this format.</summary>
        private static byte[]? SwizzleOf(KtxTexture texture)
        {
            string swizzle = texture.Swizzle;
            bool gray = texture.Format.Natural is TextureNatural.Gray8 or TextureNatural.Gray16 or TextureNatural.GrayF32;
            if (swizzle == "rgba" || (gray && swizzle == "rrr1"))
            {
                return null;
            }

            return [.. swizzle.Select(c => (byte)"rgba01".IndexOf(c, StringComparison.Ordinal))];
        }

        /// <summary>Returns the four-channel pixel type a swizzled format loads as, at its depth.</summary>
        private static TextureNatural Widened(TextureNatural natural) => natural switch
        {
            TextureNatural.Gray16 or TextureNatural.GrayAlpha16 or TextureNatural.Rgb16 or TextureNatural.Rgba16 or TextureNatural.Rgb10A2 => TextureNatural.Rgba16,
            TextureNatural.RgbaF16 => TextureNatural.RgbaF16,
            TextureNatural.GrayF32 or TextureNatural.RgbF32 or TextureNatural.RgbaF32 => TextureNatural.RgbaF32,
            _ => TextureNatural.Rgba8,
        };
    }

    /// <summary>The rows of one surface at one mip level.</summary>
    private sealed class Rows<TWork> : ExtendedRows<TWork, TWork>
        where TWork : unmanaged, IPixelType<TWork>
    {
        private readonly Plan _plan;
        private readonly KtxTexture _texture;
        private readonly TextureFormat _format;
        private readonly bool _convert;
        private readonly bool _unpremultiply;
        private readonly int _rowBytes;
        private readonly int _pitch;
        private readonly IMemoryOwner<byte> _stored;
        private readonly IMemoryOwner<byte>? _natural;
        private readonly IMemoryOwner<byte>? _strip;
        private readonly int _naturalBytes;
        private IMemoryOwner<byte>? _whole;
        private int _wholeStart;
        private int _wholeLength;
        private long _streamed;
        private int _stripRow = -1;

        public Rows(EgamiInput input, ReaderOptions options, FormatInfo info, Plan plan)
            : base(input, options, info, info == KtxFormat.Info ? "Ktx" : "Ktx2", plan.Texture.MipWidth(plan.Mip), plan.Texture.MipHeight(plan.Mip))
        {
            _plan = plan;
            _texture = plan.Texture;
            _format = _texture.Format;
            _convert = plan.Work != _format.Natural;
            _unpremultiply = _texture.AlphaMode == KtxAlphaMode.Premultiplied;
            _rowBytes = checked((int)_format.RowBytes(Width));
            _pitch = checked((int)_texture.RowPitch(plan.Mip));
            _naturalBytes = TextureDecode.NaturalBytes(_format.Natural);
            _stored = input.RentScratch(Math.Max(_pitch, 16), ClearMode.Zero);
            if (_convert)
            {
                _natural = input.RentScratch(checked(Width * _naturalBytes), ClearMode.Zero);
            }

            if (_format.IsBlock)
            {
                _strip = input.RentScratch(checked(Width * _naturalBytes * 4), ClearMode.Zero);
            }

            Metadata.FormatDetails = new KtxDetails
            {
                VkFormat = _texture.VkFormat,
                GlInternalFormat = _texture.GlInternalFormat,
                FormatName = _format.Name,
                Supercompression = _texture.Supercompression,
                Dimension = _texture.Dimension,
                IsCube = _texture.IsCube,
                Layers = _texture.Layers,
                Faces = _texture.Faces,
                Depth = _texture.Depth,
                MipLevels = _texture.MipLevels,
                IsSrgb = _texture.IsSrgb,
                AlphaMode = _texture.AlphaMode,
                Orientation = _texture.Orientation,
                Swizzle = _texture.Swizzle,
                Surface = plan.Surface,
                MipLevel = plan.Mip,
                KeyValues = _texture.KeyValues,
            };
            Tag(Metadata, _texture);
            foreach ((string id, string message) in _texture.Repairs)
            {
                Warn(id, message);
            }

            if (_plan.ImageSizeAt >= 0)
            {
                CheckImageSize();
            }

            if (!_plan.Buffered)
            {
                Input.Skip(_plan.ImageOffset - Input.Position);
            }

            if (_plan.Available < _plan.ImageBytes)
            {
                Damaged("LevelShort", "The mip level is shorter than its images; what is missing reads as black.");
            }
        }

        public override RasterAccess Access => _plan.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed;

        public override long WorkingBytes => base.WorkingBytes + _stored.Memory.Length + (_natural?.Memory.Length ?? 0) + (_strip?.Memory.Length ?? 0)
            + (_whole?.Memory.Length ?? 0);

        protected override bool DecodeRow(int y, Span<TWork> row)
        {
            if (_plan.Buffered && _whole is null)
            {
                ReadWhole();
            }

            int sy = _plan.FlipY ? Height - 1 - y : y;
            Span<byte> natural = _convert ? _natural!.Memory.Span[..(Width * _naturalBytes)] : MemoryMarshal.AsBytes(row);
            Span<byte> stored = _stored.Memory.Span[.._pitch];
            if (_format.IsBlock)
            {
                Span<byte> strip = _strip!.Memory.Span;
                int stride = Width * _naturalBytes;
                if (sy >> 2 != _stripRow)
                {
                    _stripRow = sy >> 2;
                    if (!Fetch(_stripRow, stored))
                    {
                        row.Clear();
                        return false;
                    }

                    TextureDecode.Blocks(_format, stored, strip, Width, Math.Min(4, Height - (_stripRow * 4)), stride);
                }

                strip.Slice((sy & 3) * stride, stride).CopyTo(natural);
            }
            else
            {
                if (!Fetch(sy, stored))
                {
                    row.Clear();
                    return false;
                }

                TextureDecode.Row(_format, stored, natural, Width, []);
            }

            if (_unpremultiply)
            {
                TextureDecode.Unpremultiply(_format.Natural, natural);
            }

            if (_plan.ReconstructZ)
            {
                TextureDecode.ReconstructZ(_format.Natural, natural);
            }

            if (_convert)
            {
                Convert(_format.Natural, natural, row);
                Swizzle(MemoryMarshal.AsBytes(row), _plan.Swizzle!);
            }

            if (_plan.FlipX)
            {
                row.Reverse();
            }

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            _stored.Dispose();
            _natural?.Dispose();
            _strip?.Dispose();
            _whole?.Dispose();
            _whole = null;
            base.Dispose(disposing);
        }

        /// <summary>Tags the colour space: the KTX 2 descriptor's primaries and transfer function, or an <c>_SRGB</c> format.</summary>
        private static void Tag(RasterMetadata metadata, KtxTexture texture)
        {
            int? primaries = texture.Primaries switch
            {
                1 => 1, // BT709
                2 => 5, // BT601_EBU
                3 => 6, // BT601_SMPTE
                4 => 9, // BT2020
                8 => 4, // NTSC1953
                10 => 12, // DISPLAYP3
                _ => null,
            };
            int? transfer = texture.Transfer switch
            {
                1 => 8, // LINEAR
                2 => 13, // SRGB
                3 => 1, // ITU
                4 => 6, // NTSC
                8 or 9 => 18, // HLG
                10 or 11 => 16, // PQ
                12 => 17, // DCIP3
                15 => 7, // ST240
                _ => null,
            };
            bool bt709 = texture.Primaries is 0 or 1;
            if (texture.IsSrgb && bt709)
            {
                metadata.ColorSpace = ColorSpaceHint.Srgb;
            }
            else if (texture.Transfer == 1 && bt709)
            {
                metadata.ColorSpace = ColorSpaceHint.Linear;
            }
            else if (primaries is int p && transfer is int t)
            {
                metadata.CodePoints = new ColorCodePoints((byte)p, (byte)t, 0, true);
            }
            else if (texture.IsSrgb)
            {
                metadata.ColorSpace = ColorSpaceHint.Srgb;
            }
        }

        /// <summary>Checks a KTX 1 level's imageSize word against the size its images have.</summary>
        private void CheckImageSize()
        {
            long expected = _texture.Ktx1ImageSize(_plan.Mip);
            Input.Skip(_plan.ImageSizeAt - Input.Position);
            Span<byte> word = stackalloc byte[4];
            if (ReadFully(word) < 4)
            {
                return;
            }

            long stated = _texture.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(word) : BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (stated != expected)
            {
                Damaged("ImageSizeWrong", $"The level states {stated} bytes of images; its size and format give {expected}. The format's size is used.", _plan.ImageSizeAt);
            }
        }

        /// <summary>Reads the whole image, or inflates the whole supercompressed level, before the first row.</summary>
        private void ReadWhole()
        {
            if (_plan.Supercompressed)
            {
                Inflate();
                return;
            }

            long bytes = _plan.Available;
            if (bytes > Array.MaxLength)
            {
                throw new LimitExceededException("MaxMemoryBytes", Array.MaxLength, bytes);
            }

            Input.CheckMemory(bytes);
            Input.Skip(_plan.ImageOffset - Input.Position);
            _whole = Input.RentScratch((int)bytes, ClearMode.Zero);
            _wholeStart = 0;
            _wholeLength = ReadFully(_whole.Memory.Span[..(int)bytes]);
            if (_wholeLength < bytes)
            {
                Damaged("DataMissing", "The file ends inside the image; what is missing reads as black.");
            }
        }

        /// <summary>Inflates the level that holds the image with Zstandard or zlib into a pooled buffer.</summary>
        private void Inflate()
        {
            long packed = _plan.LevelLength;
            long size = _plan.LevelUncompressed;
            if (packed > Array.MaxLength || size > Array.MaxLength)
            {
                throw new LimitExceededException("MaxMemoryBytes", Array.MaxLength, Math.Max(packed, size));
            }

            Input.CheckMemory(packed + size);
            if (_plan.LevelOffset < Input.Position)
            {
                throw Input.Corrupt(_plan.LevelOffset, "The mip level lies before the data read so far.");
            }

            Input.Skip(_plan.LevelOffset - Input.Position);
            using IMemoryOwner<byte> source = Input.RentScratch((int)packed);
            Span<byte> compressed = source.Memory.Span[..(int)packed];
            int read = ReadFully(compressed);
            _whole = Input.RentScratch((int)size, ClearMode.Zero);
            Span<byte> level = _whole.Memory.Span[..(int)size];
            int inflated;
            try
            {
                if (read < packed)
                {
                    throw Input.Corrupt(Input.Position, "The file ends inside the supercompressed mip level.");
                }

                inflated = _texture.Supercompression == KtxSupercompression.Zstandard
                    ? Zstandard.Decompress(compressed, level)
                    : Zlib(source.Memory[..(int)packed], level);
            }
            catch (CorruptDataException e)
            {
                if (Strict)
                {
                    throw;
                }

                Warn("LevelDamaged", $"The supercompressed mip level does not inflate ({e.Message}); the image reads as black.", _plan.LevelOffset);
                inflated = 0;
            }

            if (inflated < size && inflated > 0)
            {
                Damaged("LevelShort", $"The mip level inflates to {inflated} of the {size} bytes it states; what is missing reads as black.");
            }

            _wholeStart = (int)Math.Min(_plan.ImageOffset, size);
            _wholeLength = (int)Math.Clamp(inflated - _plan.ImageOffset, 0, _plan.ImageBytes);
        }

        private int Zlib(Memory<byte> compressed, Span<byte> level)
        {
            MemoryStream packed = MemoryMarshal.TryGetArray(compressed, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(compressed.ToArray(), writable: false);
            try
            {
                using ZLibStream zlib = new(packed, CompressionMode.Decompress);
                int total = 0;
                while (total < level.Length)
                {
                    int n = zlib.Read(level[total..]);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                return total;
            }
            catch (InvalidDataException e)
            {
                throw Input.Corrupt(_plan.LevelOffset, $"The zlib data is damaged: {e.Message}");
            }
        }

        /// <summary>Gets stored row (or row of blocks) <paramref name="index"/>, byte-swapped as the file needs.</summary>
        private bool Fetch(int index, Span<byte> stored)
        {
            Span<byte> row = stored[.._rowBytes];
            if (_whole is not null)
            {
                long at = (long)index * _pitch;
                int have = (int)Math.Clamp(_wholeLength - at, 0, _rowBytes);
                _whole.Memory.Span.Slice(_wholeStart + (int)Math.Min(at, _wholeLength), have).CopyTo(row);
                row[have..].Clear();
            }
            else
            {
                // Streamed rows come in order; a short level ends where its images end.
                long left = _plan.Available - _streamed;
                if (left < _rowBytes || ReadFully(row) < _rowBytes)
                {
                    return false;
                }

                int padding = (int)Math.Min(_pitch - _rowBytes, left - _rowBytes);
                if (padding > 0)
                {
                    Span<byte> skip = stackalloc byte[padding];
                    ReadFully(skip);
                }

                _streamed += _rowBytes + padding;
            }

            if (_texture.SwapUnit == 2)
            {
                Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(row[..(_rowBytes & ~1)]);
                BinaryPrimitives.ReverseEndianness(words, words);
            }
            else if (_texture.SwapUnit == 4)
            {
                Span<uint> words = MemoryMarshal.Cast<byte, uint>(row[..(_rowBytes & ~3)]);
                BinaryPrimitives.ReverseEndianness(words, words);
            }

            return true;
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

        /// <summary>Converts a row of the format's natural pixels to the four-channel work type a swizzle needs.</summary>
        private static void Convert(TextureNatural natural, ReadOnlySpan<byte> source, Span<TWork> destination)
        {
            switch (natural)
            {
                case TextureNatural.Gray8:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray8>(source), destination);
                    break;
                case TextureNatural.Gray16:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray16>(source), destination);
                    break;
                case TextureNatural.GrayF32:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayF32>(source), destination);
                    break;
                case TextureNatural.GrayAlpha8:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha8>(source), destination);
                    break;
                case TextureNatural.GrayAlpha16:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha16>(source), destination);
                    break;
                case TextureNatural.Alpha8:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Alpha8>(source), destination);
                    break;
                case TextureNatural.Rgb8:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(source), destination);
                    break;
                case TextureNatural.Rgb16:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb16>(source), destination);
                    break;
                case TextureNatural.RgbF32:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, RgbF32>(source), destination);
                    break;
                case TextureNatural.Rgba8:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba8>(source), destination);
                    break;
                case TextureNatural.Rgba16:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba16>(source), destination);
                    break;
                case TextureNatural.RgbaF16:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, RgbaF16>(source), destination);
                    break;
                case TextureNatural.RgbaF32:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, RgbaF32>(source), destination);
                    break;
                case TextureNatural.Rgb10A2:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb10A2>(source), destination);
                    break;
                case TextureNatural.Bgr565:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Bgr565>(source), destination);
                    break;
                case TextureNatural.Bgr555:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Bgr555>(source), destination);
                    break;
                case TextureNatural.Bgra5551:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Bgra5551>(source), destination);
                    break;
                default:
                    PixelConvert.Convert(MemoryMarshal.Cast<byte, Bgra4444>(source), destination);
                    break;
            }
        }

        /// <summary>Applies a swizzle to four-channel pixels of 8, 16 or 32 bits a channel (16-bit ones half floats for RgbaF16).</summary>
        private static void Swizzle(Span<byte> pixels, byte[] swizzle)
        {
            int channel = Unsafe.SizeOf<TWork>() / 4;
            uint one = channel switch
            {
                1 => 0xFF,
                2 => typeof(TWork) == typeof(RgbaF16) ? 0x3C00u : 0xFFFF,
                _ => 0x3F800000, // 1.0f
            };
            Span<uint> values = stackalloc uint[6];
            values[4] = 0;
            values[5] = one;
            for (int at = 0; at < pixels.Length; at += channel * 4)
            {
                Span<byte> pixel = pixels.Slice(at, channel * 4);
                for (int c = 0; c < 4; c++)
                {
                    values[c] = channel switch
                    {
                        1 => pixel[c],
                        2 => MemoryMarshal.Read<ushort>(pixel[(c * 2)..]),
                        _ => MemoryMarshal.Read<uint>(pixel[(c * 4)..]),
                    };
                }

                for (int c = 0; c < 4; c++)
                {
                    uint v = values[swizzle[c]];
                    switch (channel)
                    {
                        case 1:
                            pixel[c] = (byte)v;
                            break;
                        case 2:
                            MemoryMarshal.Write(pixel[(c * 2)..], (ushort)v);
                            break;
                        default:
                            MemoryMarshal.Write(pixel[(c * 4)..], v);
                            break;
                    }
                }
            }
        }
    }
}
