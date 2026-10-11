using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>The pixel type a page loads as by default, from the design's natural-layout table.</summary>
internal enum TiffNatural
{
    Gray8,
    GrayAlpha8,
    Gray16,
    GrayAlpha16,
    GrayF32,
    Rgb8,
    Rgba8,
    Rgb16,
    Rgba16,
    RgbF32,
    RgbaF32,
    RgbaF16,
    Cmyk8,
    Cmyk16,
}

/// <summary>
/// One page (or SubIFD) of a TIFF file, read from its IFD: what its samples are, how they are compressed, and where its strips
/// or tiles lie. Building it checks every tag the pixels depend on; nothing is decoded.
/// </summary>
internal sealed class TiffPage
{
    private TiffPage(TiffFile file, TiffIfd ifd)
    {
        File = file;
        Ifd = ifd;
    }

    public TiffFile File { get; }

    public TiffIfd Ifd { get; }

    /// <summary>Gets the page's index in the main chain.</summary>
    public int Index { get; private init; }

    /// <summary>Gets the SubIFD's index under its page, or -1 for a page of the main chain.</summary>
    public int Sub { get; private init; } = -1;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Gets the samples a pixel has, extra samples included.</summary>
    public int Samples { get; private set; }

    /// <summary>Gets the samples that carry color: 1 for gray and palette, 3 for RGB, YCbCr and Lab, the inks used for CMYK.</summary>
    public int ColorSamples { get; private set; }

    public int Bits { get; private set; }

    /// <summary>Gets the sample format: 1 unsigned, 2 signed, 3 float.</summary>
    public int Format { get; private set; } = 1;

    public bool Float => Format == 3;

    public int Photometric { get; private set; }

    public int Compression { get; private set; } = TiffCodes.None;

    public int Predictor { get; private set; } = 1;

    public bool Planar { get; private set; }

    /// <summary>Gets whether each byte's bits are stored least significant first (FillOrder 2).</summary>
    public bool Reversed { get; private set; }

    public bool Tiled { get; private set; }

    /// <summary>Gets the width of a strip or tile: the image width for strips.</summary>
    public int UnitWidth { get; private set; }

    /// <summary>Gets the height of a strip or tile: RowsPerStrip, at most the image height, for strips.</summary>
    public int UnitHeight { get; private set; }

    /// <summary>Gets the strips or tiles across the image (1 for strips).</summary>
    public int Across { get; private set; }

    /// <summary>Gets the strips or tiles down the image.</summary>
    public int Down { get; private set; }

    /// <summary>Gets the planes: the samples when planar, else 1.</summary>
    public int Planes => Planar ? Samples : 1;

    /// <summary>Gets the samples in each plane's units.</summary>
    public int UnitSamples => Planar ? 1 : Samples;

    /// <summary>Gets the bytes of one decoded row of a unit.</summary>
    public long UnitRowBytes => (((long)UnitWidth * UnitSamples * Bits) + 7) / 8;

    public TiffArray Offsets { get; private set; } = null!;

    /// <summary>Gets the byte counts, or <see langword="null"/> when they must be worked out from the offsets.</summary>
    public TiffArray? Counts { get; private set; }

    /// <summary>Gets the units the offsets give, which may be fewer than the geometry needs in a damaged file.</summary>
    public long UnitCount { get; private set; }

    /// <summary>Gets what each extra sample is.</summary>
    public int[] Extra { get; private set; } = [];

    /// <summary>Gets the index of the alpha sample among the samples, or -1.</summary>
    public int AlphaIndex { get; private set; } = -1;

    /// <summary>Gets whether the alpha sample is associated (premultiplied).</summary>
    public bool AssociatedAlpha { get; private set; }

    /// <summary>Gets the ColorMap, reds then greens then blues, 16 bits each.</summary>
    public ushort[]? ColorMap { get; private set; }

    public int SubH { get; private set; } = 1;

    public int SubV { get; private set; } = 1;

    /// <summary>Gets whether the page is YCbCr in data units (subsampled, not JPEG).</summary>
    public bool Subsampled => Photometric == 6 && (SubH, SubV) != (1, 1) && Compression is not (TiffCodes.Jpeg or TiffCodes.OldJpeg);

    public double[] Coefficients { get; private set; } = [0.299, 0.587, 0.114];

    public double[] ReferenceBlackWhite { get; private set; } = [];

    /// <summary>Gets the white point of a Lab page, as (x, y) chromaticity; D50 when the file gives none, as Adobe's TIFF note and libtiff have it.</summary>
    public (double X, double Y) WhitePoint { get; private set; } = (0.3457, 0.3585);

    public int InkSet { get; private set; } = 1;

    public string[] InkNames { get; private set; } = [];

    /// <summary>Gets whether a separated page's first four inks are read as CMYK.</summary>
    public bool Cmyk { get; private set; }

    public uint FaxOptions { get; private set; }

    public byte[]? JpegTables { get; private set; }

    public uint NewSubfileType { get; private set; }

    public bool IsReduced => (NewSubfileType & 1) != 0 || Sub >= 0;

    public bool IsMask => (NewSubfileType & 4) != 0 || Photometric == 4;

    public TiffNatural Natural { get; private set; }

    /// <summary>Gets the warnings building the page gave, for the raster's metadata.</summary>
    public List<string> Warnings { get; } = [];

    public PixelLayoutInfo Layout => Natural switch
    {
        TiffNatural.Gray8 => Gray8.Layout,
        TiffNatural.GrayAlpha8 => GrayAlpha8.Layout,
        TiffNatural.Gray16 => Gray16.Layout,
        TiffNatural.GrayAlpha16 => GrayAlpha16.Layout,
        TiffNatural.GrayF32 => GrayF32.Layout,
        TiffNatural.Rgb8 => Rgb8.Layout,
        TiffNatural.Rgba8 => Rgba8.Layout,
        TiffNatural.Rgb16 => Rgb16.Layout,
        TiffNatural.Rgba16 => Rgba16.Layout,
        TiffNatural.RgbF32 => RgbF32.Layout,
        TiffNatural.RgbaF32 => RgbaF32.Layout,
        TiffNatural.RgbaF16 => RgbaF16.Layout,
        TiffNatural.Cmyk8 => Cmyk8.Layout,
        _ => Cmyk16.Layout,
    };

    /// <summary>Calls <paramref name="visitor"/> with the natural pixel type.</summary>
    public TResult WithNaturalType<TResult>(Streaming.INaturalTypeVisitor<TResult> visitor) => Natural switch
    {
        TiffNatural.Gray8 => visitor.Visit<Gray8>(),
        TiffNatural.GrayAlpha8 => visitor.Visit<GrayAlpha8>(),
        TiffNatural.Gray16 => visitor.Visit<Gray16>(),
        TiffNatural.GrayAlpha16 => visitor.Visit<GrayAlpha16>(),
        TiffNatural.GrayF32 => visitor.Visit<GrayF32>(),
        TiffNatural.Rgb8 => visitor.Visit<Rgb8>(),
        TiffNatural.Rgba8 => visitor.Visit<Rgba8>(),
        TiffNatural.Rgb16 => visitor.Visit<Rgb16>(),
        TiffNatural.Rgba16 => visitor.Visit<Rgba16>(),
        TiffNatural.RgbF32 => visitor.Visit<RgbF32>(),
        TiffNatural.RgbaF32 => visitor.Visit<RgbaF32>(),
        TiffNatural.RgbaF16 => visitor.Visit<RgbaF16>(),
        TiffNatural.Cmyk8 => visitor.Visit<Cmyk8>(),
        _ => visitor.Visit<Cmyk16>(),
    };

    /// <summary>Reads only what decides whether an IFD is a page: its NewSubfileType and photometric.</summary>
    public static (uint SubfileType, int Photometric) Kind(TiffFile file, TiffIfd ifd)
    {
        uint subfile = (uint)file.Integer(ifd, TiffTags.NewSubfileType, 0);
        if (!ifd.Has(TiffTags.NewSubfileType) && file.Integer(ifd, TiffTags.SubfileType, 1) == 2)
        {
            subfile = 1;
        }

        return (subfile, (int)file.Integer(ifd, TiffTags.Photometric, ulong.MaxValue));
    }

    /// <summary>Reads a page's description from its IFD.</summary>
    /// <exception cref="CorruptDataException">A tag the pixels need is missing or impossible.</exception>
    /// <exception cref="NotSupportedException">The page uses a compression or photometric interpretation Egami does not read.</exception>
    public static TiffPage Read(TiffFile file, TiffIfd ifd, int index, int sub, bool strict)
    {
        EgamiInput input = file.Input;
        TiffPage page = new(file, ifd) { Index = index, Sub = sub };
        (page.NewSubfileType, page.Photometric) = Kind(file, ifd);

        ulong width = Required(file, ifd, TiffTags.ImageWidth, "ImageWidth");
        ulong height = Required(file, ifd, TiffTags.ImageLength, "ImageLength");
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(ifd.Offset, $"The page is {width} × {height} pixels.");
        }

        (page.Width, page.Height) = ((int)width, (int)height);
        page.Compression = (int)file.Integer(ifd, TiffTags.Compression, TiffCodes.None);
        page.Samples = (int)file.Integer(ifd, TiffTags.SamplesPerPixel, 1);
        if (page.Samples is < 1 or > 32)
        {
            throw input.Corrupt(ifd.Offset, $"SamplesPerPixel is {page.Samples}.");
        }

        ulong[] bits = ifd.TryGet(TiffTags.BitsPerSample, out TiffEntry bitsEntry) ? file.Integers(bitsEntry, 64) : [1];
        page.Bits = (int)bits[0];
        page.Format = (int)file.Integer(ifd, TiffTags.SampleFormat, 1);
        if (!ifd.Has(TiffTags.Photometric))
        {
            // TIFF 6.0 makes it required; libtiff guesses from the samples, and so does Egami, with a warning.
            page.Photometric = page.Samples >= 3 ? 2 : 1;
            page.Warnings.Add($"The page has no PhotometricInterpretation; it was read as {(page.Samples >= 3 ? "RGB" : "gray")}.");
        }

        if (page.Compression is TiffCodes.SgiLog or TiffCodes.SgiLog24 || page.Photometric is 32844 or 32845)
        {
            page.ReadLog(strict);
            page.ReadUnits(strict);
            return page;
        }

        page.CheckCompression();
        if (page.Format is not (1 or 2 or 3))
        {
            throw new NotSupportedException($"TIFF: SampleFormat {page.Format} (complex or undefined samples) is not read.");
        }

        if (Array.Exists(bits, b => b != (ulong)page.Bits))
        {
            if (page.Photometric != 3 && bits.Length >= page.Samples && Array.TrueForAll(bits[..page.Samples], b => b == (ulong)page.Bits))
            {
                // Values past SamplesPerPixel are ignored.
            }
            else
            {
                throw new NotSupportedException($"TIFF: samples of different depths ({string.Join("/", bits)} bits) are not read.");
            }
        }

        bool depthRead = page.Float ? page.Bits is 16 or 24 or 32 or 64 : page.Bits is >= 1 and <= 32;
        if (!depthRead)
        {
            throw new NotSupportedException($"TIFF: {page.Bits}-bit {(page.Float ? "floating-point" : "integer")} samples are not read.");
        }

        if (page.Float && page.Bits == 24)
        {
            throw new NotSupportedException("TIFF: 24-bit floating-point samples are not read.");
        }

        page.ReadColor(strict);
        page.ReadExtra();
        page.Reversed = file.Integer(ifd, TiffTags.FillOrder, 1) == 2;
        page.Planar = file.Integer(ifd, TiffTags.PlanarConfiguration, 1) == 2 && page.Samples > 1;
        page.FaxOptions = (uint)file.Integer(ifd, TiffTags.T4Options, 0);
        page.Predictor = (int)file.Integer(ifd, TiffTags.Predictor, 1);
        if (page.Compression is not (TiffCodes.Lzw or TiffCodes.Deflate or TiffCodes.AdobeDeflate or TiffCodes.Zstd or TiffCodes.Webp))
        {
            // The predictor only means something to the codecs that use it; libtiff ignores it elsewhere too.
            page.Predictor = page.Predictor == 1 ? 1 : page.IgnorePredictor();
        }

        bool predictorRead = page.Predictor switch
        {
            1 => true,
            2 => page.Bits is 8 or 16 or 32 or 64,
            3 => page.Float,
            _ => false,
        };
        if (!predictorRead)
        {
            throw new NotSupportedException($"TIFF: predictor {page.Predictor} with {page.Bits}-bit {(page.Float ? "floating-point" : "integer")} samples is not read.");
        }

        if (page.Compression is TiffCodes.CcittRle or TiffCodes.CcittGroup3 or TiffCodes.CcittGroup4 or TiffCodes.CcittRleWord
            && (page.Bits != 1 || page.Samples != 1))
        {
            throw new NotSupportedException($"TIFF: CCITT compression of {page.Samples} samples of {page.Bits} bits is not read (only 1-bit).");
        }

        if (page.Compression == TiffCodes.ThunderScan && (page.Bits != 4 || page.Samples != 1))
        {
            throw new NotSupportedException($"TIFF: ThunderScan compression of {page.Samples} samples of {page.Bits} bits is not read (only 4-bit gray).");
        }

        if (page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg)
        {
            if (page.Bits != 8 || page.Float)
            {
                throw new NotSupportedException($"TIFF: JPEG compression of {page.Bits}-bit samples is not read.");
            }

            if (ifd.TryGet(TiffTags.JpegTables, out TiffEntry tables) && tables.ValueAt >= 0)
            {
                input.CheckMetadataBytes(tables.ByteCount);
                page.JpegTables = file.Bytes(tables);
            }
        }

        if (page.Compression == TiffCodes.Webp && (page.Bits != 8 || page.Samples is not (3 or 4) || page.Photometric != 2))
        {
            throw new NotSupportedException($"TIFF: WebP compression of {page.Samples} samples of {page.Bits} bits is not read (only 8-bit RGB and RGBA).");
        }

        if (page.Subsampled && (page.Bits != 8 || page.Planar || page.Samples != 3))
        {
            throw new NotSupportedException("TIFF: subsampled YCbCr other than 8-bit, chunky and with three samples is not read.");
        }

        page.ChooseNatural();
        page.ReadUnits(strict);
        return page;
    }

    private int IgnorePredictor()
    {
        Warnings.Add($"A predictor with {TiffCodes.Name(Compression)} compression means nothing; it was ignored.");
        return 1;
    }

    private void CheckCompression()
    {
        if (Compression is TiffCodes.None or TiffCodes.CcittRle or TiffCodes.CcittGroup3 or TiffCodes.CcittGroup4 or TiffCodes.Lzw
            or TiffCodes.OldJpeg or TiffCodes.Jpeg or TiffCodes.Deflate or TiffCodes.AdobeDeflate or TiffCodes.CcittRleWord
            or TiffCodes.PackBits or TiffCodes.ThunderScan or TiffCodes.Webp)
        {
            return;
        }

        throw new NotSupportedException(Compression is TiffCodes.Zstd or TiffCodes.Lzma or TiffCodes.Lerc or TiffCodes.JpegXl
            ? $"TIFF: compression {Compression} ({TiffCodes.Name(Compression)}) is not read yet; it is added on real demand."
            : $"TIFF: compression {Compression} ({TiffCodes.Name(Compression)}) is not read.");
    }

    private void ReadLog(bool strict)
    {
        if (Compression is not (TiffCodes.SgiLog or TiffCodes.SgiLog24))
        {
            throw new NotSupportedException($"TIFF: a LogL or LogLuv page compressed with {TiffCodes.Name(Compression)} is not read (only SGILog).");
        }

        if (Photometric is not (32844 or 32845))
        {
            throw new NotSupportedException($"TIFF: SGILog compression of photometric interpretation {Photometric} is not read.");
        }

        if (Compression == TiffCodes.SgiLog24 && Photometric != 32845)
        {
            throw new NotSupportedException("TIFF: SGILog24 compression of a LogL page is not read (it holds LogLuv24).");
        }

        // The stored samples are SGILog's own; the decoder writes 32-bit float XYZ (or Y).
        Planar = false;
        Samples = Photometric == 32844 ? 1 : 3;
        ColorSamples = Samples;
        Bits = 32;
        Format = 3;
        Natural = Photometric == 32844 ? TiffNatural.GrayF32 : TiffNatural.RgbF32;
        _ = strict;
    }

    private void ReadColor(bool strict)
    {
        EgamiInput input = File.Input;
        switch (Photometric)
        {
            case 0 or 1:
                ColorSamples = 1;
                break;
            case 2:
                ColorSamples = 3;
                break;
            case 3:
                ColorSamples = 1;
                if (Bits > 16 || Float)
                {
                    throw new NotSupportedException($"TIFF: a palette of {Bits}-bit {(Float ? "floating-point " : string.Empty)}indexes is not read.");
                }

                int entries = 1 << Bits;
                if (!Ifd.TryGet(TiffTags.ColorMap, out TiffEntry map) || map.Count < 3L * entries || map.ValueAt < 0)
                {
                    throw input.Corrupt(Ifd.Offset, $"A palette page needs a ColorMap of {3 * entries} values.");
                }

                ulong[] values = File.Integers(map with { Count = 3L * entries }, 3L * 65536);
                ColorMap = Array.ConvertAll(values, v => (ushort)v);
                break;
            case 4:
                ColorSamples = 1;
                break;
            case 5:
                InkSet = (int)File.Integer(Ifd, TiffTags.InkSet, 1);
                if (Ifd.TryGet(TiffTags.InkNames, out TiffEntry names) && names.ValueAt >= 0 && names.Count <= 64 * 1024)
                {
                    InkNames = System.Text.Encoding.Latin1.GetString(File.Bytes(names)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                }

                int inks = (int)File.Integer(Ifd, TiffTags.NumberOfInks, (ulong)Samples);
                inks = Math.Clamp(inks, 1, Samples);
                Cmyk = InkSet == 1 ? Samples >= 4 : inks >= 4 && InkNames.Length >= 4 && IsCmykNames(InkNames);
                if (InkSet == 1 && Samples < 4)
                {
                    throw input.Corrupt(Ifd.Offset, $"A CMYK page has {Samples} samples.");
                }

                ColorSamples = Cmyk ? 4 : inks;
                if (!Cmyk)
                {
                    Warnings.Add($"The page has {inks} inks that are not CMYK; only the first was read, as gray.");
                }

                break;
            case 6:
                ColorSamples = 3;
                ulong[] sub = Ifd.TryGet(TiffTags.YCbCrSubSampling, out TiffEntry subEntry) ? File.Integers(subEntry, 16) : [2, 2];
                if (sub.Length >= 2)
                {
                    (SubH, SubV) = ((int)sub[0], (int)sub[1]);
                }

                if (SubH is not (1 or 2 or 4) || SubV is not (1 or 2 or 4) || SubV > SubH)
                {
                    throw input.Corrupt(Ifd.Offset, $"YCbCr subsampling is {SubH} × {SubV}.");
                }

                if (Ifd.TryGet(TiffTags.YCbCrCoefficients, out TiffEntry luma) && luma.Count >= 3)
                {
                    double[] c = File.Numbers(luma, 16);
                    if (c[1] > 0 && c[0] >= 0 && c[2] >= 0)
                    {
                        Coefficients = c[..3];
                    }
                }

                ReadReferenceBlackWhite([0, 255, 128, 255, 128, 255]);
                break;
            case 8 or 9 or 10:
                ColorSamples = 3;
                if (Float)
                {
                    throw new NotSupportedException("TIFF: floating-point Lab is not read.");
                }

                if (Ifd.TryGet(TiffTags.WhitePoint, out TiffEntry white) && white.Count >= 2)
                {
                    double[] w = File.Numbers(white, 16);
                    if (w[0] > 0 && w[1] > 0 && w[0] + w[1] < 1)
                    {
                        WhitePoint = (w[0], w[1]);
                    }
                }

                break;
            case 32803 or 34892:
                throw new NotSupportedException("TIFF: CFA and LinearRaw pages are camera RAW (DNG), which Egami reads only on real demand.");
            default:
                throw new NotSupportedException($"TIFF: photometric interpretation {Photometric} is not read.");
        }

        if (Samples < ColorSamples)
        {
            throw input.Corrupt(Ifd.Offset, $"The page has {Samples} samples, fewer than its {ColorSamples} colors.");
        }

        if (Photometric is 6 or 8 or 9 or 10 or 3 && Format == 2)
        {
            Format = 1;
        }

        if (Format == 2)
        {
            Warnings.Add("The page has signed samples; they were offset so 0 is the middle value.");
        }

        _ = strict;
    }

    private void ReadReferenceBlackWhite(double[] fallback)
    {
        ReferenceBlackWhite = fallback;
        if (Ifd.TryGet(TiffTags.ReferenceBlackWhite, out TiffEntry reference) && reference.Count >= 6)
        {
            double[] r = File.Numbers(reference, 16);
            if (r[1] > r[0] && r[3] > r[2] && r[5] > r[4])
            {
                ReferenceBlackWhite = r[..6];
            }
        }
    }

    private static bool IsCmykNames(string[] names)
    {
        return names[0].Equals("cyan", StringComparison.OrdinalIgnoreCase) && names[1].Equals("magenta", StringComparison.OrdinalIgnoreCase)
            && names[2].Equals("yellow", StringComparison.OrdinalIgnoreCase) && names[3].Equals("black", StringComparison.OrdinalIgnoreCase);
    }

    private void ReadExtra()
    {
        int extras = Samples - ColorSamples;
        if (extras == 0)
        {
            return;
        }

        ulong[] kinds = Ifd.TryGet(TiffTags.ExtraSamples, out TiffEntry entry) && entry.ValueAt >= 0 ? File.Integers(entry, 64) : [];
        Extra = new int[extras];
        for (int i = 0; i < extras; i++)
        {
            Extra[i] = i < kinds.Length && kinds[i] <= 2 ? (int)kinds[i] : 0;
        }

        // Inks past the first of a page read as gray are extra samples too, but not alpha.
        int alpha = Photometric == 5 && !Cmyk ? -1 : Array.FindIndex(Extra, k => k is 1 or 2);
        if (alpha >= 0)
        {
            AlphaIndex = ColorSamples + alpha;
            AssociatedAlpha = Extra[alpha] == 1;
        }

        int dropped = Extra.Length - (alpha >= 0 ? 1 : 0);
        if (dropped > 0 && Photometric != 5 || (Photometric == 5 && Cmyk && dropped > 0))
        {
            Warnings.Add($"The page has {dropped} extra sample{(dropped == 1 ? string.Empty : "s")} that {(dropped == 1 ? "is" : "are")} not alpha; dropped.");
        }
    }

    private void ChooseNatural()
    {
        bool alpha = AlphaIndex >= 0;
        bool wide = Float || Bits > 16;
        Natural = Photometric switch
        {
            0 or 1 or 4 => wide ? (alpha ? TiffNatural.RgbaF32 : TiffNatural.GrayF32)
                : Bits > 8 ? (alpha ? TiffNatural.GrayAlpha16 : TiffNatural.Gray16)
                : alpha ? TiffNatural.GrayAlpha8 : TiffNatural.Gray8,
            2 => Float && Bits == 16 ? TiffNatural.RgbaF16
                : wide ? (alpha ? TiffNatural.RgbaF32 : TiffNatural.RgbF32)
                : Bits > 8 ? (alpha ? TiffNatural.Rgba16 : TiffNatural.Rgb16)
                : alpha ? TiffNatural.Rgba8 : TiffNatural.Rgb8,
            3 => ColorMap!.Any(v => v % 257 != 0) ? (alpha ? TiffNatural.Rgba16 : TiffNatural.Rgb16) : alpha ? TiffNatural.Rgba8 : TiffNatural.Rgb8,
            5 when !Cmyk => wide ? TiffNatural.GrayF32 : Bits > 8 ? TiffNatural.Gray16 : TiffNatural.Gray8,
            5 => wide ? TiffNatural.RgbaF32 : alpha ? (Bits > 8 ? TiffNatural.Rgba16 : TiffNatural.Rgba8) : Bits > 8 ? TiffNatural.Cmyk16 : TiffNatural.Cmyk8,
            6 => Bits > 8 ? TiffNatural.Rgb16 : TiffNatural.Rgb8,
            _ => wide ? TiffNatural.RgbF32 : Bits > 8 ? TiffNatural.Rgb16 : TiffNatural.Rgb8,
        };
        if (Photometric is 6 or 8 or 9 or 10 && alpha)
        {
            Natural = Natural switch
            {
                TiffNatural.Rgb8 => TiffNatural.Rgba8,
                TiffNatural.Rgb16 => TiffNatural.Rgba16,
                _ => TiffNatural.RgbaF32,
            };
        }

        if (Bits == 64 && Float)
        {
            Warnings.Add("The page has 64-bit floating-point samples; they were read as 32-bit.");
        }
    }

    private void ReadUnits(bool strict)
    {
        EgamiInput input = File.Input;
        if (Ifd.Has(TiffTags.TileWidth) || Ifd.Has(TiffTags.TileOffsets))
        {
            Tiled = true;
            ulong tw = Required(File, Ifd, TiffTags.TileWidth, "TileWidth");
            ulong th = Required(File, Ifd, TiffTags.TileLength, "TileLength");
            if (tw is 0 or > 65536 || th is 0 or > 65536)
            {
                throw input.Corrupt(Ifd.Offset, $"Tiles of {tw} × {th} pixels.");
            }

            if (tw % 16 != 0 || th % 16 != 0)
            {
                Warnings.Add($"Tiles of {tw} × {th} are not multiples of 16, as TIFF 6.0 requires; they were read as they are.");
            }

            (UnitWidth, UnitHeight) = ((int)tw, (int)th);
            Across = (int)(((ulong)Width + tw - 1) / tw);
            Down = (int)(((ulong)Height + th - 1) / th);
        }
        else
        {
            ulong rows = File.Integer(Ifd, TiffTags.RowsPerStrip, uint.MaxValue);
            if (rows == 0)
            {
                if (strict)
                {
                    throw input.Corrupt(Ifd.Offset, "RowsPerStrip is 0.");
                }

                rows = (ulong)Height;
                Warnings.Add("RowsPerStrip is 0; the page was read as one strip.");
            }

            UnitWidth = Width;
            UnitHeight = (int)Math.Min(rows, (ulong)Height);
            if (Subsampled && UnitHeight % SubV != 0 && UnitHeight != Height)
            {
                throw input.Corrupt(Ifd.Offset, $"RowsPerStrip {UnitHeight} is not a multiple of the vertical subsampling {SubV}.");
            }

            Across = 1;
            Down = (Height + UnitHeight - 1) / UnitHeight;
        }

        long needed = (long)Across * Down * Planes;
        int offsetsTag = Tiled && Ifd.Has(TiffTags.TileOffsets) ? TiffTags.TileOffsets : TiffTags.StripOffsets;
        int countsTag = Tiled && Ifd.Has(TiffTags.TileByteCounts) ? TiffTags.TileByteCounts : TiffTags.StripByteCounts;
        if (!Ifd.TryGet(offsetsTag, out TiffEntry offsets) || offsets.ValueAt < 0)
        {
            if (Compression == TiffCodes.OldJpeg && Ifd.Has(TiffTags.JpegInterchangeFormat))
            {
                Offsets = new TiffArray([]);
                UnitCount = 0;
                return;
            }

            throw input.Corrupt(Ifd.Offset, Tiled ? "The page has no TileOffsets." : "The page has no StripOffsets.");
        }

        Offsets = new TiffArray(File, offsets);
        UnitCount = Math.Min(offsets.Count, needed);
        if (offsets.Count < needed)
        {
            if (strict)
            {
                throw input.Corrupt(Ifd.Offset, $"The page needs {needed} {(Tiled ? "tiles" : "strips")}; its offsets give {offsets.Count}.");
            }

            Warnings.Add($"The page needs {needed} {(Tiled ? "tiles" : "strips")}; its offsets give {offsets.Count}. The rest is left black.");
        }

        if (Ifd.TryGet(countsTag, out TiffEntry counts) && counts.ValueAt >= 0 && counts.Count >= UnitCount)
        {
            Counts = new TiffArray(File, counts);
            if (Compression == TiffCodes.None && UnitCount == 1 && Counts[0] == 0)
            {
                Counts = null;
            }
        }

        if (Counts is null && UnitCount > 0)
        {
            Warnings.Add(Compression == TiffCodes.None
                ? "The page has no usable byte counts; they were worked out from its size."
                : "The page has no usable byte counts; each strip or tile was read up to the next.");
        }
    }

    /// <summary>Returns where unit <paramref name="unit"/> lies: its offset and byte count, cut to the file.</summary>
    public (long Offset, long Count) Unit(long unit)
    {
        if (unit >= UnitCount)
        {
            return (0, 0);
        }

        long offset = (long)Math.Min(Offsets[unit], (ulong)File.Source.Length);
        long count;
        if (Counts is not null)
        {
            count = (long)Math.Min(Counts[unit], (ulong)long.MaxValue);
        }
        else if (Compression == TiffCodes.None)
        {
            count = UnitRowBytes * UnitHeight;
        }
        else
        {
            // Up to the nearest later unit, or the end of the file.
            long end = File.Source.Length;
            for (long i = 0; i < UnitCount && i < 1 << 20; i++)
            {
                long other = (long)Math.Min(Offsets[i], (ulong)long.MaxValue);
                if (other > offset && other < end)
                {
                    end = other;
                }
            }

            count = end - offset;
        }

        return (offset, Math.Max(0, Math.Min(count, File.Source.Length - offset)));
    }

    /// <summary>Gets the bytes a unit decodes to.</summary>
    public long UnitBytes
    {
        get
        {
            if (Subsampled)
            {
                long blocksAcross = (UnitWidth + SubH - 1) / SubH;
                long blocksDown = (UnitHeight + SubV - 1) / SubV;
                return blocksAcross * blocksDown * ((SubH * SubV) + 2);
            }

            return UnitRowBytes * UnitHeight;
        }
    }

    private static ulong Required(TiffFile file, TiffIfd ifd, int tag, string name)
    {
        if (!ifd.TryGet(tag, out TiffEntry entry) || entry.ValueAt < 0)
        {
            throw file.Input.Corrupt(ifd.Offset, $"The page has no {name} (tag {tag}).");
        }

        return file.Integer(entry, 0);
    }
}
