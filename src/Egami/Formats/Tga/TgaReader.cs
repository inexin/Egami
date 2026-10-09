using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Tga;

/// <summary>
/// Reads TGA, written from the Truevision TGA File Format Specification 2.0: color-mapped, true-color and gray images, raw and
/// run-length encoded, 8 to 32 bits, all four origins, and the 2.0 footer and extension area. Packets may cross row ends but
/// not the end of the image.
/// </summary>
internal sealed class TgaReader : IFormatReader
{
    internal static readonly TgaReader Instance = new();

    internal const int HeaderLength = 18;
    internal const int FooterLength = 26;
    internal const int ExtensionLength = 495;

    /// <summary>Gets the footer signature, "TRUEVISION-XFILE." and a zero byte.</summary>
    internal static ReadOnlySpan<byte> FooterSignature => "TRUEVISION-XFILE.\0"u8;

    /// <summary>
    /// Returns whether the 18 bytes are a consistent TGA header: a known image type with a color-map type that fits it, a color-map
    /// entry size that is 0 exactly when there is no map, a pixel depth that fits the type, a nonzero size and clear reserved bits.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
        {
            return false;
        }

        int mapType = header[1];
        int type = header[2];
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(header[5..]);
        int entry = header[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        int depth = header[16];
        int descriptor = header[17];
        bool mapped = type is 1 or 9 or 32 or 33;
        bool mapFits = mapped
            ? mapType == 1 && mapLength > 0 && entry is 15 or 16 or 24 or 32
            : mapType == 0 ? entry == 0 : mapType == 1 && entry is 15 or 16 or 24 or 32;
        bool depthFits = type switch
        {
            1 or 9 or 32 or 33 => depth is 8 or 16,
            2 or 10 => depth is 15 or 16 or 24 or 32,
            3 or 11 => depth is 8 or 16,
            _ => false,
        };
        return mapFits && depthFits && width > 0 && height > 0 && (descriptor & 0xC0) == 0;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, options, probe: true);
        PixelLayoutInfo layout = header.Natural;
        return new RasterInfo
        {
            Format = TgaFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = layout,
            FrameCount = 1,
            HasMetadata = header.ImageId.Length > 0 || header.Extension is not null,
            EstimatedBytes = (long)header.Width * header.Height * layout.BytesPerPixel,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Decode<TPixel>(input, options, ReadHeader(input, options, probe: false));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, options, probe: false);
        PixelLayoutInfo natural = header.Natural;
        return natural == Gray8.Layout ? Decode<Gray8>(input, options, header)
            : natural == GrayAlpha8.Layout ? Decode<GrayAlpha8>(input, options, header)
            : natural == Bgra8.Layout ? Decode<Bgra8>(input, options, header)
            : Decode<Bgr8>(input, options, header);
    }

    private static Header ReadHeader(EgamiInput input, ReaderOptions options, bool probe)
    {
        Span<byte> bytes = stackalloc byte[HeaderLength];
        input.ReadExactly(bytes);
        if (!Matches(bytes))
        {
            throw input.Corrupt(0, "The TGA header is not consistent.");
        }

        Header header = new()
        {
            Type = bytes[2],
            MapFirst = BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]),
            MapLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]),
            MapEntry = bytes[1] == 1 ? bytes[7] : 0,
            Width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
            Depth = bytes[16],
            AttributeBits = bytes[17] & 0x0F,
            RightToLeft = (bytes[17] & 0x10) != 0,
            TopDown = (bytes[17] & 0x20) != 0,
        };
        if (header.Type is 32 or 33)
        {
            throw new UnsupportedFormatException("Huffman and delta compressed TGA files (types 32 and 33) are not supported.");
        }

        header.ImageId = new byte[bytes[0]];
        input.ReadExactly(header.ImageId);
        if (header.MapEntry > 0)
        {
            int entryBytes = (header.MapEntry + 7) / 8;
            byte[] map = new byte[header.MapLength * entryBytes];
            input.ReadExactly(map);
            if (header.Mapped)
            {
                header.Palette = ReadPalette(map, header.MapEntry, header.AttributeBits);
            }
        }

        if (options.ReadMetadata || header.HasAlphaBits)
        {
            ReadFooter(input, header, probe);
        }

        return header;
    }

    private static Bgra8[] ReadPalette(ReadOnlySpan<byte> map, int entry, int attributeBits)
    {
        int entryBytes = (entry + 7) / 8;
        Bgra8[] palette = new Bgra8[map.Length / entryBytes];
        for (int i = 0; i < palette.Length; i++)
        {
            ReadOnlySpan<byte> e = map.Slice(i * entryBytes, entryBytes);
            palette[i] = entry switch
            {
                15 or 16 => Color16(BinaryPrimitives.ReadUInt16LittleEndian(e), entry == 16 && attributeBits == 1),
                24 => new Bgra8(e[0], e[1], e[2], 255),
                _ => new Bgra8(e[0], e[1], e[2], attributeBits > 0 ? e[3] : (byte)255),
            };
        }

        return palette;
    }

    /// <summary>Reads the TGA 2.0 footer and extension area from the end of a seekable stream; others get them after the pixels.</summary>
    private static void ReadFooter(EgamiInput input, Header header, bool probe)
    {
        if (input.Length is not long length || length < HeaderLength + FooterLength)
        {
            header.FooterAfterPixels = !probe;
            return;
        }

        Span<byte> footer = stackalloc byte[FooterLength];
        if (!input.TryReadAt(length - FooterLength, footer) || !footer[8..].SequenceEqual(FooterSignature))
        {
            return;
        }

        long offset = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        byte[] extension = new byte[ExtensionLength];
        if (offset >= HeaderLength && offset + ExtensionLength <= length - FooterLength && input.TryReadAt(offset, extension))
        {
            header.Extension = extension;
        }
    }

    private static Bgra8 Color16(ushort v, bool alpha)
    {
        ReadOnlySpan<byte> scale = Scale5;
        return new Bgra8(scale[v & 31], scale[(v >> 5) & 31], scale[(v >> 10) & 31], alpha && (v & 0x8000) == 0 ? (byte)0 : (byte)255);
    }

    /// <summary>Gets round(v × 255 / 31) for 5-bit values.</summary>
    private static ReadOnlySpan<byte> Scale5 =>
    [
        0, 8, 16, 25, 33, 41, 49, 58, 66, 74, 82, 90, 99, 107, 115, 123, 132, 140, 148, 156, 165, 173, 181, 189, 197, 206, 214, 222, 230, 239, 247, 255,
    ];

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReaderOptions options, Header header)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            bool strict = options.Recovery == RecoveryMode.Strict;
            Decoder<TPixel> decoder = new(input, header, raster, strict);
            decoder.Run();
            if (header.FooterAfterPixels)
            {
                ReadTrailingFooter(input, header);
                decoder.FixAlphaAfterwards();
            }

            ApplyMetadata(header, raster.Metadata, options);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Finds the footer in the bytes after the pixels of a stream that cannot seek, when they fit MaxMetadataBytes.</summary>
    private static void ReadTrailingFooter(EgamiInput input, Header header)
    {
        long start = input.Position;
        long cap = Math.Min(input.Limits.MaxMetadataBytes, 1 << 24);
        byte[] rest = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == rest.Length)
                {
                    if (length >= cap)
                    {
                        return;
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent(length * 2);
                    rest.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(rest);
                    rest = larger;
                }

                int n = input.Read(rest.AsSpan(length));
                if (n == 0)
                {
                    break;
                }

                length += n;
            }

            if (length < FooterLength || !rest.AsSpan(length - FooterLength + 8, 18).SequenceEqual(FooterSignature))
            {
                return;
            }

            long offset = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(length - FooterLength)) - start;
            if (offset >= 0 && offset + ExtensionLength <= length - FooterLength)
            {
                header.Extension = rest.AsSpan((int)offset, ExtensionLength).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rest);
        }
    }

    private static void ApplyMetadata(Header header, RasterMetadata metadata, ReaderOptions options)
    {
        TgaDetails details = new()
        {
            ImageType = header.Type,
            BitsPerPixel = header.Depth,
            AttributeBits = header.AttributeBits,
            Origin = (header.TopDown, header.RightToLeft) switch
            {
                (false, false) => TgaOrigin.BottomLeft,
                (false, true) => TgaOrigin.BottomRight,
                (true, false) => TgaOrigin.TopLeft,
                _ => TgaOrigin.TopRight,
            },
            ImageId = header.ImageId,
            AlphaType = header.AlphaType,
        };
        metadata.FormatDetails = details;
        foreach (string warning in header.Warnings)
        {
            metadata.Warnings.Add(warning);
        }

        if (header.Extension is not byte[] e || !options.ReadMetadata)
        {
            return;
        }

        string author = Field(e.AsSpan(2, 41));
        if (author.Length > 0)
        {
            metadata.Text.Add(new TextEntry("Author", author));
        }

        string comment = string.Join('\n', Enumerable.Range(0, 4).Select(i => Field(e.AsSpan(43 + (81 * i), 81)))).TrimEnd('\n');
        if (comment.Length > 0)
        {
            metadata.Text.Add(new TextEntry("Comment", comment));
        }

        ReadOnlySpan<byte> date = e.AsSpan(367, 12);
        int month = BinaryPrimitives.ReadUInt16LittleEndian(date), day = BinaryPrimitives.ReadUInt16LittleEndian(date[2..]);
        int year = BinaryPrimitives.ReadUInt16LittleEndian(date[4..]), hour = BinaryPrimitives.ReadUInt16LittleEndian(date[6..]);
        int minute = BinaryPrimitives.ReadUInt16LittleEndian(date[8..]), second = BinaryPrimitives.ReadUInt16LittleEndian(date[10..]);
        if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && hour < 24 && minute < 60 && second < 60)
        {
            details.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }

        details.JobName = Field(e.AsSpan(379, 41));
        details.Software = Field(e.AsSpan(426, 41));
        int aspectX = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(474)), aspectY = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(476));
        details.PixelAspectRatio = aspectX > 0 && aspectY > 0 ? (double)aspectX / aspectY : null;
        int gammaNumerator = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(478)), gammaDenominator = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(480));
        if (gammaNumerator > 0 && gammaDenominator > 0)
        {
            metadata.ColorSpace = ColorSpaceHint.Gamma;
            metadata.Gamma = (double)gammaNumerator / gammaDenominator;
        }
    }

    /// <summary>Reads a zero-terminated ASCII field, trimmed of trailing spaces.</summary>
    private static string Field(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).TrimEnd(' ');
    }

    /// <summary>What the header, color map and footer say.</summary>
    private sealed class Header
    {
        public int Type { get; init; }

        public int MapFirst { get; init; }

        public int MapLength { get; init; }

        public int MapEntry { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int Depth { get; init; }

        public int AttributeBits { get; init; }

        public bool RightToLeft { get; init; }

        public bool TopDown { get; init; }

        public byte[] ImageId { get; set; } = [];

        public Bgra8[] Palette { get; set; } = [];

        public byte[]? Extension { get; set; }

        /// <summary>Gets or sets whether the footer can only be looked for after the pixels (the stream cannot seek).</summary>
        public bool FooterAfterPixels { get; set; }

        public List<string> Warnings { get; } = [];

        public bool Mapped => Type is 1 or 9;

        public bool Gray => Type is 3 or 11;

        public bool Rle => Type >= 9;

        public int BytesPerPixel => (Depth + 7) / 8;

        /// <summary>Gets whether the pixels or color map have bits the descriptor or the footer may call alpha.</summary>
        public bool HasAlphaBits => Mapped
            ? MapEntry == 32 || (MapEntry == 16 && AttributeBits == 1)
            : Gray ? Depth == 16 : Depth == 32 || (Depth == 16 && AttributeBits == 1);

        public TgaAlphaType? AlphaType => Extension is { } e && BinaryPrimitives.ReadUInt16LittleEndian(e) >= ExtensionLength && e[494] <= 4 ? (TgaAlphaType)e[494] : null;

        /// <summary>Gets whether the alpha bits are alpha: the descriptor's attribute bits say so, unless the 2.0 alpha type says none.</summary>
        public bool UsesAlpha => HasAlphaBits && AttributeBits > 0 && AlphaType is not (TgaAlphaType.None or TgaAlphaType.UndefinedIgnore);

        public PixelLayoutInfo Natural => Gray
            ? (Depth == 16 ? GrayAlpha8.Layout : Gray8.Layout)
            : HasAlphaBits ? Bgra8.Layout : Bgr8.Layout;
    }

    /// <summary>Decodes raw or run-length encoded pixels row by row into the raster.</summary>
    private sealed class Decoder<TPixel>(EgamiInput input, Header header, Raster<TPixel> raster, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly ByteReader? _bytes = header.Rle ? new ByteReader(input) : null;
        private int _remaining;
        private bool _repeat;
        private bool _pastPalette;

        public void Run()
        {
            int width = header.Width;
            int bpp = header.BytesPerPixel;
            byte[] bytes = ArrayPool<byte>.Shared.Rent(width * bpp);
            Bgra8[] color = ArrayPool<Bgra8>.Shared.Rent(width);
            GrayAlpha8[] gray = ArrayPool<GrayAlpha8>.Shared.Rent(width);
            Span<byte> packet = stackalloc byte[4];
            bool direct = !header.Gray && !header.Mapped && !header.RightToLeft
                && ((header.Depth == 32 && typeof(TPixel) == typeof(Bgra8)) || (header.Depth == 24 && typeof(TPixel) == typeof(Bgr8)));
            int row = 0;
            try
            {
                for (; row < header.Height; row++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    if (direct)
                    {
                        // The stored layout is the target's: read straight into the row.
                        Span<TPixel> row0 = raster.Row(header.TopDown ? row : header.Height - 1 - row);
                        Span<byte> into = MemoryMarshal.AsBytes(row0);
                        if (header.Rle)
                        {
                            ReadPackets(into, packet[..bpp]);
                        }
                        else
                        {
                            input.ReadExactly(into);
                        }

                        if (bpp == 4)
                        {
                            Span<Bgra8> pixels = MemoryMarshal.Cast<byte, Bgra8>(into);
                            if (!header.UsesAlpha)
                            {
                                SetOpaque(MemoryMarshal.Cast<byte, uint>(into));
                            }
                            else if (header.AlphaType == TgaAlphaType.Premultiplied)
                            {
                                Unpremultiply(pixels);
                            }
                        }

                        continue;
                    }

                    Span<byte> stored = bytes.AsSpan(0, width * bpp);
                    if (header.Rle)
                    {
                        ReadPackets(stored, packet[..bpp]);
                    }
                    else
                    {
                        input.ReadExactly(stored);
                    }

                    Span<TPixel> target = raster.Row(header.TopDown ? row : header.Height - 1 - row);
                    if (header.Gray)
                    {
                        Span<GrayAlpha8> pixels = gray.AsSpan(0, width);
                        Gray(stored, pixels);
                        if (header.RightToLeft)
                        {
                            pixels.Reverse();
                        }

                        PixelConvert.Convert<GrayAlpha8, TPixel>(pixels, target);
                    }
                    else
                    {
                        Span<Bgra8> pixels = color.AsSpan(0, width);
                        Color(stored, pixels);
                        if (header.RightToLeft)
                        {
                            pixels.Reverse();
                        }

                        PixelConvert.Convert<Bgra8, TPixel>(pixels, target);
                    }
                }

                if (_remaining > 0)
                {
                    Problem("A run-length packet goes past the end of the image; the rest of it was ignored.");
                    if (!_repeat)
                    {
                        for (int i = 0; i < _remaining * bpp; i++)
                        {
                            _bytes!.Next();
                        }
                    }
                }

                if (_pastPalette)
                {
                    header.Warnings.Add("Some pixels use indices outside the color map; they are drawn black.");
                }
            }
            catch (CorruptDataException e) when (!strict)
            {
                for (int rest = row; rest < header.Height; rest++)
                {
                    raster.Row(header.TopDown ? rest : header.Height - 1 - rest).Clear();
                }

                header.Warnings.Add($"{e.Message} The {header.Height - row} rows not decoded are left black.");
                header.FooterAfterPixels = false;
            }
            finally
            {
                _bytes?.Finish();
                _bytes?.Release();
                ArrayPool<byte>.Shared.Return(bytes);
                ArrayPool<Bgra8>.Shared.Return(color);
                ArrayPool<GrayAlpha8>.Shared.Return(gray);
            }
        }

        /// <summary>
        /// Applies a footer found only after the pixels (a stream that cannot seek): an alpha type of none makes the pixels opaque,
        /// and premultiplied alpha is divided out, through 8-bit BGRA.
        /// </summary>
        public void FixAlphaAfterwards()
        {
            TgaAlphaType? type = header.AlphaType;
            bool opaque = header.HasAlphaBits && header.AttributeBits > 0 && type is TgaAlphaType.None or TgaAlphaType.UndefinedIgnore;
            bool premultiplied = header.UsesAlpha && type == TgaAlphaType.Premultiplied;
            if (!TPixel.Layout.HasAlpha || !(opaque || premultiplied))
            {
                return;
            }

            Bgra8[] buffer = ArrayPool<Bgra8>.Shared.Rent(raster.Width);
            try
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    Span<Bgra8> pixels = buffer.AsSpan(0, raster.Width);
                    PixelConvert.Convert<TPixel, Bgra8>(raster.Row(y), pixels);
                    if (opaque)
                    {
                        foreach (ref Bgra8 p in pixels)
                        {
                            p.A = 255;
                        }
                    }
                    else
                    {
                        Unpremultiply(pixels);
                    }

                    PixelConvert.Convert<Bgra8, TPixel>(pixels, raster.Row(y));
                }
            }
            finally
            {
                ArrayPool<Bgra8>.Shared.Return(buffer);
            }
        }

        private static void SetOpaque(Span<uint> pixels)
        {
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Span<Vector256<uint>> vectors = MemoryMarshal.Cast<uint, Vector256<uint>>(pixels);
                foreach (ref Vector256<uint> v in vectors)
                {
                    v |= Vector256.Create(0xFF00_0000u);
                }

                x = vectors.Length * Vector256<uint>.Count;
            }

            for (; x < pixels.Length; x++)
            {
                pixels[x] |= 0xFF00_0000;
            }
        }

        private static void Unpremultiply(Span<Bgra8> pixels)
        {
            foreach (ref Bgra8 p in pixels)
            {
                if (p.A == 0)
                {
                    p = default;
                }
                else if (p.A < 255)
                {
                    int a = p.A;
                    p.B = (byte)Math.Min(255, ((p.B * 510) + a) / (2 * a));
                    p.G = (byte)Math.Min(255, ((p.G * 510) + a) / (2 * a));
                    p.R = (byte)Math.Min(255, ((p.R * 510) + a) / (2 * a));
                }
            }
        }

        private void Problem(string problem)
        {
            if (strict)
            {
                throw new CorruptDataException(TgaFormat.Info.Name, input.Position, problem);
            }

            header.Warnings.Add(problem);
        }

        /// <summary>Fills one row from packets, which may continue from the row before.</summary>
        private void ReadPackets(Span<byte> stored, Span<byte> pixel)
        {
            int bpp = pixel.Length;
            int x = 0;
            int width = stored.Length / bpp;
            while (x < width)
            {
                if (_remaining == 0)
                {
                    byte b = _bytes!.Next();
                    _remaining = (b & 0x7F) + 1;
                    _repeat = (b & 0x80) != 0;
                    if (_repeat)
                    {
                        _bytes.Take(bpp).CopyTo(pixel);
                    }
                }

                int n = Math.Min(_remaining, width - x);
                Span<byte> target = stored.Slice(x * bpp, n * bpp);
                if (!_repeat)
                {
                    _bytes!.Take(n * bpp).CopyTo(target);
                }
                else if (bpp == 4)
                {
                    MemoryMarshal.Cast<byte, uint>(target).Fill(MemoryMarshal.Read<uint>(pixel));
                }
                else if (bpp == 1)
                {
                    target.Fill(pixel[0]);
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        pixel.CopyTo(target[(i * bpp)..]);
                    }
                }

                x += n;
                _remaining -= n;
            }
        }

        private void Gray(ReadOnlySpan<byte> stored, Span<GrayAlpha8> pixels)
        {
            bool alpha = header.UsesAlpha;
            if (header.Depth == 8)
            {
                for (int x = 0; x < pixels.Length; x++)
                {
                    pixels[x] = new GrayAlpha8(stored[x], 255);
                }

                return;
            }

            for (int x = 0; x < pixels.Length; x++)
            {
                byte a = alpha ? stored[(2 * x) + 1] : (byte)255;
                pixels[x] = new GrayAlpha8(stored[2 * x], a);
            }

            if (alpha && header.AlphaType == TgaAlphaType.Premultiplied)
            {
                foreach (ref GrayAlpha8 p in pixels)
                {
                    p.L = p.A == 0 ? (byte)0 : (byte)Math.Min(255, ((p.L * 510) + p.A) / (2 * p.A));
                }
            }
        }

        private void Color(ReadOnlySpan<byte> stored, Span<Bgra8> pixels)
        {
            bool alpha = header.UsesAlpha;
            switch (header.Mapped ? 0 : header.Depth)
            {
                case 0:
                    Bgra8[] palette = header.Palette;
                    bool wide = header.Depth == 16;
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        int index = (wide ? BinaryPrimitives.ReadUInt16LittleEndian(stored[(2 * x)..]) : stored[x]) - header.MapFirst;
                        if ((uint)index < (uint)palette.Length)
                        {
                            Bgra8 c = palette[index];
                            pixels[x] = alpha ? c : new Bgra8(c.B, c.G, c.R, 255);
                        }
                        else
                        {
                            pixels[x] = new Bgra8(0, 0, 0, 255);
                            _pastPalette = true;
                        }
                    }

                    break;
                case 15 or 16:
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        pixels[x] = Color16(BinaryPrimitives.ReadUInt16LittleEndian(stored[(2 * x)..]), alpha);
                    }

                    break;
                case 24:
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        pixels[x] = new Bgra8(stored[3 * x], stored[(3 * x) + 1], stored[(3 * x) + 2], 255);
                    }

                    break;
                default:
                    MemoryMarshal.Cast<byte, Bgra8>(stored).CopyTo(pixels);
                    if (!alpha)
                    {
                        foreach (ref Bgra8 p in pixels)
                        {
                            p.A = 255;
                        }
                    }

                    break;
            }

            if (alpha && header.AlphaType == TgaAlphaType.Premultiplied)
            {
                Unpremultiply(pixels);
            }
        }
    }
}
