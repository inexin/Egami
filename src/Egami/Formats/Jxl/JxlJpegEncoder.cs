using System.IO.Compression;

using Egami.Metadata;

namespace Egami.Formats.Jxl;

/// <summary>
/// Recompresses a JPEG into JPEG XL without loss (ISO/IEC 18181-1 VarDCT, 18181-2 jbrd): the JPEG's quantized coefficients
/// become a VarDCT frame of 8×8 DCTs with its quantization tables, and the jbrd box keeps what rebuilding the file byte for byte
/// needs. The choices (chroma from luma, DC contexts, coefficient orders, entropy coding) follow libjxl 0.12.0's cjxl at its
/// default effort: enc_frame.cc ComputeJPEGTranscodingData, enc_coeff_order.cc, enc_entropy_coder.cc and
/// jpeg/enc_jpeg_data.cc (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlJpegEncoder
{
    private const int GroupDim = 256;
    private const int CflBits = 11;
    private const int CflRatioMax = (256 << CflBits) - 1;
    private const int ColorFactor = 84;
    private const int NumOrders = 13;
    private const int NonZeroBuckets = 37;
    private const int ZeroDensityContexts = 458;

    private static readonly byte[] IccTag = "ICC_PROFILE\0"u8.ToArray();
    private static readonly byte[] ExifTag = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpTag = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly int[] ModeH = [0, 1, 1, 0];
    private static readonly int[] ModeV = [0, 1, 0, 1];

    private static readonly int[] CoeffFreqContext =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23, 23, 23,
        24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    private static readonly int[] CoeffNumNonzeroContext =
    [
        0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    private readonly JpegData _jpg;
    private readonly ParallelOptions _parallel;
    private readonly bool _gray;
    private readonly bool _ycbcr;
    private readonly int[] _map;
    private readonly int[] _modes = new int[3];
    private readonly int[] _hs = new int[3];
    private readonly int[] _vs = new int[3];
    private readonly int _width;
    private readonly int _height;
    private readonly int _blocksX;
    private readonly int _blocksY;
    private readonly int _groupsX;
    private readonly int _groups;
    private readonly int _dcGroupsX;
    private readonly int _dcGroups;
    private readonly int _tilesX;
    private readonly int _tilesY;

    // JPEG XL channel order X, Y, B (Cb, Y, Cr for YCbCr JPEGs).
    private readonly int[] _qt = new int[64 * 3];
    private readonly int[] _scaled = new int[64 * 3];
    private readonly float[] _dcQuant = new float[3];
    private readonly int[][] _dc = new int[3][];
    private readonly int[][] _coeffs = new int[3][];
    private readonly sbyte[] _yToX;
    private readonly sbyte[] _yToB;
    private int[] _dcThresholds = [];
    private int[] _ctxMap = [];
    private int _numCtxs;
    private int _usedOrders;
    private int[][] _orders = [JxlHfPass.Natural(0), JxlHfPass.Natural(0), JxlHfPass.Natural(0)];

    private JxlJpegEncoder(JpegData jpg, int parallelism)
    {
        _jpg = jpg;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) };
        int nb = jpg.Components.Count;
        if (nb is not (1 or 3))
        {
            throw new UnsupportedFormatException($"The JPEG has {nb} components; JPEG XL recompression takes gray and three-component JPEGs.");
        }

        _gray = nb == 1;
        _ycbcr = _gray || !IsRgb(jpg);
        _map = _gray ? [0, 0, 0] : _ycbcr ? [1, 0, 2] : [0, 1, 2];
        for (int c = 0; c < 3; c++)
        {
            JpegComponentData comp = jpg.Components[_gray ? 0 : c < 2 ? c ^ 1 : c];
            int mode = Array.FindIndex([0, 1, 2, 3], i => 1 << ModeH[i] == comp.HSamp && 1 << ModeV[i] == comp.VSamp);
            if (mode < 0)
            {
                throw new UnsupportedFormatException($"The JPEG's {comp.HSamp}×{comp.VSamp} sampling has no JPEG XL equivalent.");
            }

            _modes[c] = mode;
        }

        int maxH = _modes.Max(m => ModeH[m]);
        int maxV = _modes.Max(m => ModeV[m]);
        for (int c = 0; c < 3; c++)
        {
            _hs[c] = maxH - ModeH[_modes[c]];
            _vs[c] = maxV - ModeV[_modes[c]];
        }

        if (!_ycbcr && !Is444)
        {
            throw new UnsupportedFormatException("The RGB JPEG has subsampled channels, which JPEG XL takes only for YCbCr.");
        }

        _width = jpg.Width;
        _height = jpg.Height;
        _blocksX = Ceil(_width, 8 << maxH) << maxH;
        _blocksY = Ceil(_height, 8 << maxV) << maxV;
        _groupsX = Ceil(_width, GroupDim);
        _groups = _groupsX * Ceil(_height, GroupDim);
        _dcGroupsX = Ceil(_blocksX, GroupDim);
        _dcGroups = _dcGroupsX * Ceil(_blocksY, GroupDim);
        _tilesX = Ceil(_blocksX, 8);
        _tilesY = Ceil(_blocksY, 8);
        _yToX = new sbyte[_tilesX * _tilesY];
        _yToB = new sbyte[_tilesX * _tilesY];
        for (int c = 0; c < 3; c++)
        {
            JpegComponentData comp = jpg.Components[_map[c]];
            if (comp.WidthInBlocks < PlaneBlocksX(c) || comp.HeightInBlocks < PlaneBlocksY(c))
            {
                throw new UnsupportedFormatException("The JPEG's component sizes do not fit its sampling.");
            }
        }
    }

    private bool Is444 => _hs.All(s => s == 0) && _vs.All(s => s == 0);

    /// <summary>Recompresses a JPEG file into a JPEG XL file with a jbrd box.</summary>
    internal static byte[] Recompress(byte[] jpeg, int parallelism, SafetyLimits limits)
    {
        JpegData jpg = JxlJpegReader.Read(jpeg);
        if (jpg.Width > limits.MaxWidth || jpg.Height > limits.MaxHeight || (long)jpg.Width * jpg.Height > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, (long)jpg.Width * jpg.Height);
        }

        JxlJpegEncoder e = new(jpg, parallelism);
        DetectMarkerTypes(jpg);
        byte[]? icc = Chunked(jpg);
        (byte[]? exif, byte[]? xmp) = Blobs(jpg);
        int orientation = 1;
        if (exif is not null)
        {
            try
            {
                orientation = Math.Clamp((int)ExifData.Parse(exif, limits.MaxMetadataBytes).Orientation, 1, 8);
            }
            catch (CorruptDataException)
            {
            }
        }

        byte[] jbrd = WriteJbrd(jpg);
        byte[] codestream = e.Codestream(icc, orientation);
        using MemoryStream output = new();
        JxlWriter.WriteFile(output, codestream, exif, xmp, jbrd, compressBoxes: true);
        return output.ToArray();
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    private static int CeilLog2(long x) => x <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(x - 1));

    private int PlaneBlocksX(int c) => _blocksX >> _hs[c];

    private int PlaneBlocksY(int c) => _blocksY >> _vs[c];

    /// <summary>Gets whether the JPEG holds RGB rather than YCbCr: an Adobe marker with transform 0 or R, G, B ids (no JFIF marker).</summary>
    private static bool IsRgb(JpegData jpg)
    {
        if (jpg.MarkerOrder.Contains(0xE0))
        {
            return false;
        }

        int app = 0;
        foreach (byte m in jpg.MarkerOrder)
        {
            if ((m & 0xF0) != 0xE0)
            {
                continue;
            }

            if (m == 0xEE)
            {
                byte[] d = jpg.AppData[app];
                if (d.Length == 15 && d.AsSpan(3, 5).SequenceEqual("Adobe"u8))
                {
                    return d[14] == 0;
                }
            }

            app++;
        }

        return jpg.Components.Count == 3 && jpg.Components[0].Id == 'R' && jpg.Components[1].Id == 'G' && jpg.Components[2].Id == 'B';
    }

    /// <summary>Marks the ICC chunks and the first Exif and XMP segments, which the codestream and boxes carry instead (DetectIccProfile, DetectBlobs).</summary>
    private static void DetectMarkerTypes(JpegData jpg)
    {
        jpg.AppTypes.Clear();
        jpg.AppTypes.AddRange(Enumerable.Repeat(0u, jpg.AppData.Count));
        int numIcc = 0, numIccJpeg = 0;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] app = jpg.AppData[i];
            if (app[0] != 0xE2)
            {
                continue;
            }

            int tagLength = (app[1] << 8) | app[2];
            if (tagLength < 2 + IccTag.Length + 2 || !app.AsSpan(3).StartsWith(IccTag))
            {
                continue;
            }

            int chunk = app[3 + IccTag.Length];
            int chunks = app[4 + IccTag.Length];
            if (chunk != numIcc + 1)
            {
                continue;
            }

            if (numIccJpeg == 0)
            {
                numIccJpeg = chunks;
            }

            if (numIccJpeg != chunks)
            {
                continue;
            }

            numIcc++;
            jpg.AppTypes[i] = 1;
        }

        if (numIcc != numIccJpeg)
        {
            throw new UnsupportedFormatException("The JPEG's ICC profile chunks are incomplete.");
        }

        bool exif = false, xmp = false;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] app = jpg.AppData[i];
            if (app[0] != 0xE1 || ((app[1] << 8) | app[2]) != app.Length - 1)
            {
                continue;
            }

            ReadOnlySpan<byte> payload = app.AsSpan(3);
            if (!exif && payload.Length > ExifTag.Length && payload.StartsWith(ExifTag))
            {
                jpg.AppTypes[i] = 2;
                exif = true;
            }

            if (!xmp && payload.Length >= XmpTag.Length && payload.StartsWith(XmpTag))
            {
                jpg.AppTypes[i] = 3;
                xmp = true;
            }
        }
    }

    /// <summary>Joins the ICC profile's chunks, or gives <see langword="null"/> when there are none.</summary>
    private static byte[]? Chunked(JpegData jpg)
    {
        List<byte> icc = [];
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            if (jpg.AppTypes[i] == 1)
            {
                icc.AddRange(jpg.AppData[i].AsSpan(5 + IccTag.Length).ToArray());
            }
        }

        return icc.Count == 0 ? null : [.. icc];
    }

    private static (byte[]? Exif, byte[]? Xmp) Blobs(JpegData jpg)
    {
        byte[]? exif = null, xmp = null;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            if (jpg.AppTypes[i] == 2)
            {
                exif = jpg.AppData[i][(3 + ExifTag.Length)..];
            }
            else if (jpg.AppTypes[i] == 3)
            {
                xmp = jpg.AppData[i][(3 + XmpTag.Length)..];
            }
        }

        return (exif, xmp);
    }

    /// <summary>Writes the jbrd box: the JPEGData fields (VisitFields), then the unknown segments and tail, Brotli compressed.</summary>
    private static byte[] WriteJbrd(JpegData jpg)
    {
        JxlBitWriter w = new();
        w.WriteBool(jpg.Components.Count == 1);
        if (jpg.MarkerOrder.Count > 16384)
        {
            throw new UnsupportedFormatException("The JPEG has too many markers for JPEG XL recompression.");
        }

        foreach (byte m in jpg.MarkerOrder)
        {
            w.Write((ulong)(m - 0xC0), 6);
        }

        JxlU32 count4 = new(1, 0, 2, 0, 3, 0, 4, 0);
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 1, 4, 2), jpg.AppTypes[i]);
            w.Write((ulong)(jpg.AppData[i].Length - 1), 16);
        }

        foreach (byte[] com in jpg.ComData)
        {
            w.Write((ulong)(com.Length - 1), 16);
        }

        if (jpg.Quant.Count == 4)
        {
            throw new UnsupportedFormatException("The JPEG has four quantization tables; JPEG XL recompression takes up to three.");
        }

        w.WriteU32(count4, (uint)jpg.Quant.Count);
        for (int i = 0; i < jpg.Quant.Count; i++)
        {
            w.Write((ulong)jpg.Quant[i].Precision, 1);
            w.Write((ulong)jpg.Quant[i].Index, 2);
            w.WriteBool(jpg.Quant[i].IsLast);
        }

        List<JpegComponentData> comps = jpg.Components;
        int type = comps.Count == 1 && comps[0].Id == 1 ? 0
            : comps.Count == 3 && comps[0].Id == 1 && comps[1].Id == 2 && comps[2].Id == 3 ? 1
            : comps.Count == 3 && comps[0].Id == 'R' && comps[1].Id == 'G' && comps[2].Id == 'B' ? 2
            : 3;
        w.Write((ulong)type, 2);
        if (type == 3)
        {
            w.WriteU32(count4, (uint)comps.Count);
            foreach (JpegComponentData c in comps)
            {
                w.Write((ulong)c.Id, 8);
            }
        }

        int used = 0;
        foreach (JpegComponentData c in comps)
        {
            w.Write((ulong)c.QuantIndex, 2);
            used |= 1 << c.QuantIndex;
        }

        for (int i = 0; i < jpg.Quant.Count; i++)
        {
            if ((used & (1 << i)) == 0 && (i == 0 || !jpg.Quant[i].Values.AsSpan().SequenceEqual(jpg.Quant[i - 1].Values)))
            {
                throw new UnsupportedFormatException("The JPEG has an unused quantization table JPEG XL cannot keep.");
            }
        }

        w.WriteU32(new JxlU32(4, 0, 2, 3, 10, 4, 26, 6), (uint)jpg.Huffman.Count);
        foreach (JpegHuffmanCode h in jpg.Huffman)
        {
            w.WriteBool((h.SlotId >> 4) != 0);
            w.Write((ulong)(h.SlotId & 15), 2);
            w.WriteBool(h.IsLast);
            foreach (int count in h.Counts)
            {
                w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 3, 0, 8), (uint)count);
            }

            foreach (int v in h.Values)
            {
                w.WriteU32(new JxlU32(0, 2, 4, 2, 8, 4, 1, 8), (uint)v);
            }
        }

        foreach (JpegScanData s in jpg.Scans)
        {
            if (s.Components.Count >= 4)
            {
                throw new UnsupportedFormatException("A JPEG scan has four components; JPEG XL recompression takes up to three.");
            }

            w.WriteU32(count4, (uint)s.Components.Count);
            w.Write((ulong)s.Ss, 6);
            w.Write((ulong)s.Se, 6);
            w.Write((ulong)s.Al, 4);
            w.Write((ulong)s.Ah, 4);
            foreach ((int comp, int dc, int ac) in s.Components)
            {
                w.Write((ulong)comp, 2);
                w.Write((ulong)ac, 2);
                w.Write((ulong)dc, 2);
            }

            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 3), 0); // last needed pass
        }

        if (jpg.MarkerOrder.Contains(0xDD))
        {
            w.Write((ulong)jpg.RestartInterval, 16);
        }

        JxlU32 counts = new(0, 0, 1, 2, 4, 4, 20, 16);
        JxlU32 blocks = new(0, 0, 1, 3, 9, 5, 41, 28);
        foreach (JpegScanData s in jpg.Scans)
        {
            w.WriteU32(counts, (uint)s.ResetPoints.Count);
            int last = -1;
            foreach (int b in s.ResetPoints)
            {
                if (b >= 3 << 26)
                {
                    throw new UnsupportedFormatException("The JPEG is too large for JPEG XL recompression.");
                }

                w.WriteU32(blocks, (uint)(b - (last + 1)));
                last = b;
            }

            w.WriteU32(counts, (uint)s.ExtraZeroRuns.Count);
            last = -1;
            foreach ((int b, int runs) in s.ExtraZeroRuns)
            {
                if (runs > 4 || b > 3 << 26)
                {
                    throw new UnsupportedFormatException("A JPEG block has more zero runs than JPEG XL recompression keeps.");
                }

                w.WriteU32(new JxlU32(1, 0, 2, 2, 5, 4, 20, 8), (uint)runs);
                w.WriteU32(blocks, (uint)(b - (last + 1)));
                last = b;
            }
        }

        foreach (byte[] d in jpg.InterMarkerData)
        {
            if (d.Length > 0xFFFF)
            {
                throw new UnsupportedFormatException("The JPEG has too much data between markers for JPEG XL recompression.");
            }

            w.Write((ulong)d.Length, 16);
        }

        if (jpg.Tail.Length > 4260096)
        {
            throw new UnsupportedFormatException("The JPEG has too much data after its end for JPEG XL recompression.");
        }

        w.WriteU32(new JxlU32(0, 0, 1, 8, 257, 16, 65793, 22), (uint)jpg.Tail.Length);
        w.WriteBool(jpg.HasZeroPaddingBit);
        if (jpg.HasZeroPaddingBit)
        {
            w.Write((ulong)jpg.PaddingBits.Count, 24);
            foreach (bool bit in jpg.PaddingBits)
            {
                w.WriteBool(bit);
            }
        }

        w.ZeroPadToByte();

        // The data section: unknown APP segments, COM segments, the bytes between markers and the tail, at Brotli quality 8.
        using MemoryStream raw = new();
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            if (jpg.AppTypes[i] == 0)
            {
                raw.Write(jpg.AppData[i]);
            }
        }

        foreach (byte[] com in jpg.ComData)
        {
            raw.Write(com);
        }

        foreach (byte[] d in jpg.InterMarkerData)
        {
            raw.Write(d);
        }

        raw.Write(jpg.Tail);
        byte[] input = raw.ToArray();
        byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(input.Length)];
        if (!BrotliEncoder.TryCompress(input, compressed, out int written, 8, 22))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        w.Append(compressed.AsSpan(0, written).ToArray());
        return w.ToArray();
    }

    /// <summary>Writes the codestream: image header and ICC, the VarDCT frame header, the TOC and the sections.</summary>
    private byte[] Codestream(byte[]? icc, int orientation)
    {
        Prepare();
        JxlBitWriter w = new();
        JxlEncodeInput input = new()
        {
            Width = _width,
            Height = _height,
            Color = _gray ? [[]] : [[], [], []],
            Icc = icc,
            Orientation = orientation,
        };
        JxlLosslessEncoder.WriteImageHeader(w, input);
        if (icc is not null)
        {
            JxlIccEncoder.Write(w, icc);
        }

        w.ZeroPadToByte();
        List<byte[]> sections = Sections();
        WriteFrameHeader(w);
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 size = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] s in sections)
        {
            w.WriteU32(size, (uint)s.Length);
        }

        w.ZeroPadToByte();
        foreach (byte[] s in sections)
        {
            w.Append(s);
        }

        return w.ToArray();
    }

    /// <summary>Writes the frame header (C.2): a last regular VarDCT frame, YCbCr with the JPEG's sampling or RGB, adaptive LF smoothing and the restoration filters off.</summary>
    private void WriteFrameHeader(JxlBitWriter w)
    {
        w.WriteBool(false);
        w.Write(0, 2); // regular frame
        w.Write(0, 1); // VarDCT
        w.WriteU64(128); // skip adaptive LF smoothing
        w.WriteBool(_ycbcr);
        if (_ycbcr)
        {
            foreach (int m in _modes)
            {
                w.Write((ulong)m, 2);
            }
        }

        w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1); // upsampling
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3), 1); // one pass
        w.WriteBool(false); // no crop
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 2), 0); // replace
        w.WriteBool(true); // last frame
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name
        w.WriteBool(false);
        w.WriteBool(false); // no Gaborish
        w.Write(0, 2); // no EPF
        w.WriteNoExtensions();
        w.WriteNoExtensions();
    }

    /// <summary>Takes the quantization tables, chroma from luma, DC and coefficients from the JPEG, and the DC contexts and orders from them.</summary>
    private void Prepare()
    {
        for (int c = 0; c < 3; c++)
        {
            int[] quant = _jpg.Quant[_jpg.Components[_map[c]].QuantIndex].Values;
            _dcQuant[c] = 255 * 8f / quant[0];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    // JPEG XL transposes the DCT, JPEG does not.
                    _qt[(64 * c) + (8 * x) + y] = quant[(8 * y) + x];
                }
            }
        }

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int pos = (y * 8) + x;
                    int ratio = (1 << CflBits) * _qt[64 + pos] / _qt[(64 * c) + pos];
                    if (ratio > CflRatioMax)
                    {
                        throw new UnsupportedFormatException("Two entries of the JPEG's quantization tables are too far apart for JPEG XL recompression.");
                    }

                    _scaled[(64 * c) + (8 * x) + y] = ratio;
                }
            }
        }

        if (Is444 && !_gray)
        {
            FindCfl(0, _yToX);
            FindCfl(2, _yToB);
        }

        List<int> dcCounts = [.. new int[2048]];
        long totalY = 0;
        foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
        {
            int pbx = PlaneBlocksX(c);
            int pby = PlaneBlocksY(c);
            _dc[c] = new int[pbx * pby];
            _coeffs[c] = new int[pbx * pby * 64];
            if (_gray && c != 1)
            {
                continue;
            }

            Coefficients(c, c == 1 ? dcCounts : null, ref totalY);
        }

        // DC contexts: up to eight luma DC buckets of equal counts, more for larger and finer images.
        int thresholds = CeilLog2(totalY) - CeilLog2(_qt[1] + _qt[2] + _qt[3] + _qt[4] + _qt[5]) - 7;
        thresholds = Math.Clamp(thresholds, 1, 7);
        List<int> dct = [];
        long cumsum = 0;
        long cut = totalY / (thresholds + 1);
        for (int j = 0; j < 2048; j++)
        {
            cumsum += dcCounts[j];
            if (cumsum > cut)
            {
                dct.Add(j - 1025);
                cut = totalY * (dct.Count + 1) / (thresholds + 1);
            }
        }

        _dcThresholds = [.. dct];
        int numDc = dct.Count + 1;
        _ctxMap = new int[3 * NumOrders * numDc];
        for (int i = 0; i < numDc; i++)
        {
            _ctxMap[i] = i;
            if (_gray)
            {
                _ctxMap[(NumOrders * numDc) + i] = _ctxMap[(2 * NumOrders * numDc) + i] = numDc;
            }
            else
            {
                _ctxMap[(NumOrders * numDc) + i] = numDc + (i / 2);
                _ctxMap[(2 * NumOrders * numDc) + i] = numDc + ((numDc - 1) / 2) + 1 + (i / 2);
            }
        }

        _numCtxs = _ctxMap.Max() + 1;
        ComputeOrders();
    }

    /// <summary>Finds each 64×64 tile's chroma-from-luma factor: the one that zeroes the most chroma coefficients (libjxl's FindCorrelation).</summary>
    private void FindCfl(int c, sbyte[] map)
    {
        const float scale = ColorFactor;
        const int offset = 127;
        const float zeroThresh = scale * 0.5f * 0.9999f;
        JpegComponentData ym = _jpg.Components[_map[1]];
        JpegComponentData sm = _jpg.Components[_map[c]];
        Parallel.For(0, _tilesY, _parallel, ty =>
        {
            int[] dNumZeros = new int[257];
            for (int tx = 0; tx < _tilesX; tx++)
            {
                Array.Clear(dNumZeros);
                int y0 = ty * 8, x0 = tx * 8;
                int y1 = Math.Min(_blocksY, y0 + 8), x1 = Math.Min(_blocksX, x0 + 8);
                for (int y = y0; y < y1; y++)
                {
                    int rowM = y * ym.WidthInBlocks * 64;
                    int rowS = y * sm.WidthInBlocks * 64;
                    for (int x = x0; x < x1; x++)
                    {
                        for (int pos = 1; pos < 64; pos++)
                        {
                            float scaledM = ym.Coeffs[rowM + (x * 64) + pos] * (1.0f / (1 << CflBits)) * _scaled[(64 * c) + pos];
                            float scaledS = (scale * sm.Coeffs[rowS + (x * 64) + pos]) + (offset * scaledM);
                            if (MathF.Abs(scaledM) > 1e-8f)
                            {
                                float from, to;
                                if (scaledM > 0)
                                {
                                    from = (scaledS - zeroThresh) / scaledM;
                                    to = (scaledS + zeroThresh) / scaledM;
                                }
                                else
                                {
                                    from = (scaledS + zeroThresh) / scaledM;
                                    to = (scaledS - zeroThresh) / scaledM;
                                }

                                from = Math.Max(from, 0f);
                                to = Math.Min(to, 255f);
                                if (from <= to)
                                {
                                    dNumZeros[(int)MathF.Ceiling(from)]++;
                                    dNumZeros[(int)MathF.Floor(to + 1)]--;
                                }
                            }
                        }
                    }
                }

                // The middle of the widest run of the maximum count (FindAvgIndexOfSumMaximum).
                int maxVal = 0, val = 0, begin = 0, end = 0;
                for (int i = 0; i < 256; i++)
                {
                    val += dNumZeros[i];
                    if (val > maxVal)
                    {
                        maxVal = val;
                        begin = i;
                    }

                    if (val == maxVal)
                    {
                        end = i;
                    }
                }

                int best = (begin + end + 1) >> 1;
                int offsetSum = 0;
                for (int i = 0; i <= offset; i++)
                {
                    offsetSum += dNumZeros[i];
                }

                map[(ty * _tilesX) + tx] = maxVal > offsetSum + 1 ? (sbyte)(best - offset) : (sbyte)0;
            }
        });
    }

    /// <summary>Takes a channel's DC and AC coefficients from its JPEG component, with integer chroma from luma taken out of chroma.</summary>
    private void Coefficients(int c, List<int>? dcCounts, ref long total)
    {
        JpegComponentData comp = _jpg.Components[_map[c]];
        JpegComponentData yComp = _jpg.Components[_map[1]];
        bool cfl = c != 1 && Is444 && !_gray;
        int qtDc = _qt[64 * c];
        int pbx = PlaneBlocksX(c);
        int pby = PlaneBlocksY(c);
        int[] dc = _dc[c];
        int[] coeffs = _coeffs[c];
        sbyte[] cm = c == 0 ? _yToX : _yToB;
        for (int by = 0; by < pby; by++)
        {
            int row = by * comp.WidthInBlocks * 64;
            int rowY = by * yComp.WidthInBlocks * 64;
            for (int bx = 0; bx < pbx; bx++)
            {
                int b = row + (bx * 64);
                int idc = _ycbcr ? comp.Coeffs[b] : comp.Coeffs[b] + (1024 / qtDc);
                if (dcCounts is not null)
                {
                    dcCounts[(int)Math.Min((uint)(idc + 1024), 2047u)]++;
                }

                total += c == 1 ? 1 : 0;
                dc[(by * pbx) + bx] = idc;
                Span<int> block = coeffs.AsSpan(((by * pbx) + bx) * 64, 64);
                if (!cfl)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            block[(x * 8) + y] = comp.Coeffs[b + (y * 8) + x];
                        }
                    }

                    continue;
                }

                int scale = cm[((by / 8) * _tilesX) + (bx / 8)] * (1 << CflBits) / ColorFactor;
                int by0 = rowY + (bx * 64);
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int pos = (y * 8) + x;
                        int coeffScale = ((scale * _scaled[(64 * c) + pos]) + (1 << (CflBits - 1))) >> CflBits;
                        int cflFactor = ((yComp.Coeffs[by0 + pos] * coeffScale) + (1 << (CflBits - 1))) >> CflBits;
                        block[(x * 8) + y] = comp.Coeffs[b + pos] - cflFactor;
                    }
                }
            }
        }
    }

    /// <summary>Chooses the DCT8 coefficient order of each channel from sampled zero counts (ComputeUsedOrders and ComputeCoeffOrder at effort 7).</summary>
    private void ComputeOrders()
    {
        if (_blocksX < 5 && _blocksY < 5)
        {
            _usedOrders = 0;
            return;
        }

        // Count zeros in half the blocks, as libjxl: its xorshift picks the blocks, groups in order and blocks row by row in
        // each. For subsampled chroma libjxl reads the group's blocks at luma offsets from the packed chroma blocks.
        long[][] numZeros = [new long[64], new long[64], new long[64]];
        ulong s0 = 0x94D049BB133111EB, s1 = 0xBF58476D1CE4E5B9;
        const ulong threshold = 2147483647;
        int[][] packed = new int[3][];
        for (int g = 0; g < _groups; g++)
        {
            (int gx0, int gy0, int gw, int gh) = GroupRect(g);
            for (int c = 0; c < 3; c++)
            {
                packed[c] = GroupCoefficients(c, gx0, gy0, gw, gh);
            }

            int offset = 0;
            for (int by = 0; by < gh; by++)
            {
                for (int bx = 0; bx < gw; bx++)
                {
                    ulong a = s0;
                    ulong b = s1;
                    ulong bits = a + b;
                    s0 = b;
                    a ^= a << 23;
                    a ^= b ^ (a >> 18) ^ (b >> 5);
                    s1 = a;
                    if ((bits >> 32) > threshold)
                    {
                        // libjxl's offset advances only over sampled blocks.
                        continue;
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        int[] p = packed[c];
                        for (int k = 0; k < 64; k++)
                        {
                            int at = offset + k;
                            if (at >= p.Length || p[at] == 0)
                            {
                                numZeros[c][k]++;
                            }
                        }

                        numZeros[c][0] = -1;
                    }

                    offset += 64;
                }
            }
        }

        int[] natural = JxlHfPass.Natural(0);
        bool nondefault = false;
        for (int c = 0; c < 3; c++)
        {
            (ulong Key, int Pos)[] items = new (ulong, int)[64];
            for (int i = 0; i < 64; i++)
            {
                int pos = natural[i];
                float count = (numZeros[c][pos] * 0.125f) + 0.1f;
                ulong q = count <= 0 ? 0 : (ulong)count;
                items[i] = ((q << 16) | (uint)i, pos);
            }

            Array.Sort(items, (x, y) => x.Key.CompareTo(y.Key));
            int[] order = new int[64];
            for (int i = 0; i < 64; i++)
            {
                order[i] = items[i].Pos;
                nondefault |= order[i] != natural[i];
            }

            _orders[c] = order;
        }

        _usedOrders = nondefault ? 1 : 0;
        if (!nondefault)
        {
            _orders = [natural, natural, natural];
        }
    }

    /// <summary>Gets a pass group's place and size in luma blocks.</summary>
    private (int X0, int Y0, int W, int H) GroupRect(int g)
    {
        int x0 = g % _groupsX * 32;
        int y0 = g / _groupsX * 32;
        return (x0, y0, Math.Min(32, _blocksX - x0), Math.Min(32, _blocksY - y0));
    }

    /// <summary>Gets a channel's coefficients in a group, block after block, only the blocks the channel has (as libjxl stores them).</summary>
    private int[] GroupCoefficients(int c, int x0, int y0, int w, int h)
    {
        List<int> list = [];
        int pbx = PlaneBlocksX(c);
        for (int by = 0; by < h; by++)
        {
            for (int bx = 0; bx < w; bx++)
            {
                int ax = x0 + bx, ay = y0 + by;
                if ((ax >> _hs[c]) << _hs[c] != ax || (ay >> _vs[c]) << _vs[c] != ay)
                {
                    continue;
                }

                list.AddRange(_coeffs[c].AsSpan((((ay >> _vs[c]) * pbx) + (ax >> _hs[c])) * 64, 64).ToArray());
            }
        }

        return [.. list];
    }

    /// <summary>Gets a luma block's DC context: how many of the luma thresholds its DC exceeds.</summary>
    private int DcContext(int bx, int by)
    {
        int q = _dc[1][((by >> _vs[1]) * PlaneBlocksX(1)) + (bx >> _hs[1])];
        int idx = 0;
        foreach (int t in _dcThresholds)
        {
            if (q > t)
            {
                idx++;
            }
        }

        return idx;
    }

    /// <summary>Tokenizes a pass group's coefficients (TokenizeCoefficients for DCT8 blocks).</summary>
    private List<JxlToken> TokenizeGroup(int g)
    {
        (int x0, int y0, int w, int h) = GroupRect(g);
        List<JxlToken> tokens = new(w * h * 3 * 8);
        int numDc = _dcThresholds.Length + 1;
        int[][] rows = [new int[w], new int[w], new int[w]];
        ReadOnlySpan<int> channels = [1, 0, 2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dcIdx = DcContext(x0 + x, y0 + y);
                for (int k = 0; k < 3; k++)
                {
                    int c = channels[k];
                    int sx = x >> _hs[c], sy = y >> _vs[c];
                    if (sx << _hs[c] != x || sy << _vs[c] != y)
                    {
                        continue;
                    }

                    int blockCtx = _ctxMap[(k * NumOrders * numDc) + dcIdx];
                    int pbx = PlaneBlocksX(c);
                    ReadOnlySpan<int> block = _coeffs[c].AsSpan(((((y0 >> _vs[c]) + sy) * pbx) + (x0 >> _hs[c]) + sx) * 64, 64);
                    int nz = 0;
                    for (int i = 1; i < 64; i++)
                    {
                        nz += block[i] != 0 ? 1 : 0;
                    }

                    int[] row = rows[c];
                    int predicted = sy == 0 ? (sx == 0 ? 32 : row[sx - 1]) : sx == 0 ? row[sx] : (row[sx] + row[sx - 1] + 1) >> 1;
                    row[sx] = nz;
                    int nzIndex = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    tokens.Add(new JxlToken(blockCtx + (nzIndex * _numCtxs), (uint)nz));
                    int baseCtx = (blockCtx * ZeroDensityContexts) + (NonZeroBuckets * _numCtxs);
                    int prev = nz > 4 ? 0 : 1;
                    int[] order = _orders[c];
                    for (int i = 1; i < 64 && nz != 0; i++)
                    {
                        int coeff = block[order[i]];
                        int ctx = baseCtx + ((CoeffNumNonzeroContext[nz - 1] + CoeffFreqContext[i - 1]) * 2) + prev;
                        tokens.Add(new JxlToken(ctx, JxlTreeLearner.PackSigned(coeff)));
                        prev = coeff != 0 ? 1 : 0;
                        nz -= prev;
                    }
                }
            }
        }

        return tokens;
    }

    /// <summary>Encodes the sections in TOC order: LF global, the LF groups, HF global and the pass groups (all in one for a single group).</summary>
    private List<byte[]> Sections()
    {
        // The Modular streams: each LF group's DC and HF metadata, and the quantization table, under one tree.
        int dcStream0 = 1;
        int acmStream0 = 1 + (2 * _dcGroups);
        int qtStream = 1 + (3 * _dcGroups);
        List<(int Id, List<JxlChannel> Channels)> streams = [];
        long dcPixels = 0;
        int[] metaCounts = new int[_dcGroups];
        for (int g = 0; g < _dcGroups; g++)
        {
            List<JxlChannel> dc = DcChannels(g);
            dcPixels += dc.Sum(ch => (long)ch.Width * ch.Height);
            streams.Add((dcStream0 + g, dc));
        }

        for (int g = 0; g < _dcGroups; g++)
        {
            List<JxlChannel> meta = MetaChannels(g);
            metaCounts[g] = meta[2].Width;
            streams.Add((acmStream0 + g, meta));
        }

        List<JxlChannel> qt = [];
        for (int c = 0; c < 3; c++)
        {
            JxlChannel ch = new(8, 8, 0, 0);
            _qt.AsSpan(64 * c, 64).CopyTo(ch.Data);
            qt.Add(ch);
        }

        streams.Add((qtStream, qt));

        // libjxl's fixed WP tree for DC, a zero predictor for the HF metadata, the gradient for the table; joined on the stream id.
        List<JxlTreeNode> dcTree = JxlModularEncoder.FixedTree(15, 6, Math.Max(dcPixels, 1), 8);
        List<JxlTreeNode> tree = [];
        tree.Add(new JxlTreeNode { Property = 1, SplitValue = acmStream0 - 1, Left = 1, Right = 0 });
        tree.Add(new JxlTreeNode { Property = 1, SplitValue = qtStream - 1, Left = 2, Right = 3 });
        tree.Add(JxlTreeNode.Leaf(5));
        tree.Add(JxlTreeNode.Leaf(0));
        int dcRoot = tree.Count;
        foreach (JxlTreeNode n in dcTree)
        {
            tree.Add(n.Property < 0 ? n : n with { Left = n.Left + dcRoot, Right = n.Right + dcRoot });
        }

        tree[0] = tree[0] with { Right = dcRoot };
        List<List<JxlToken>> treeTokens = [[]];
        (int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context) = JxlModularEncoder.TokenizeTree(tree, treeTokens[0]);
        bool usesWp = property.Any(p => p == 15) || property.Select((p, i) => p < 0 && predictor[i] == 6).Any(b => b);
        List<JxlToken>[] streamTokens = new List<JxlToken>[streams.Count];
        Parallel.For(0, streams.Count, _parallel, s =>
        {
            List<JxlToken> t = [];
            (int id, List<JxlChannel> chs) = streams[s];
            for (int c = 0; c < chs.Count; c++)
            {
                if (chs[c].Width > 0 && chs[c].Height > 0)
                {
                    t.AddRange(JxlModularEncoder.Tokenize(chs[c], c, id, property, value, left, right, predictor, context, usesWp));
                }
            }

            streamTokens[s] = t;
        });

        List<List<JxlToken>> modularTokens = [.. streamTokens];
        int[] widths = [.. streams.Select(s => s.Channels.Max(ch => ch.Width))];

        // The AC tokens of every pass group.
        List<JxlToken>[] acTokens = new List<JxlToken>[_groups];
        Parallel.For(0, _groups, _parallel, g => acTokens[g] = TokenizeGroup(g));

        // LF global.
        JxlBitWriter global = new();
        global.WriteBool(false); // LF dequantization from the JPEG tables
        foreach (float q in _dcQuant)
        {
            global.WriteF16(1.0f / q * 128.0f);
        }

        global.WriteU32(new JxlU32(1, 11, 2049, 11, 4097, 12, 8193, 16), 65536); // global scale
        global.WriteU32(new JxlU32(16, 0, 1, 5, 1, 8, 1, 16), 1); // quant LF
        global.WriteBool(false); // block context map
        for (int c = 0; c < 3; c++)
        {
            int[] t = c == 1 ? _dcThresholds : [];
            global.Write((ulong)t.Length, 4);
            foreach (int v in t)
            {
                global.WriteU32(new JxlU32(0, 4, 16, 8, 272, 16, 65808, 32), JxlTreeLearner.PackSigned(v));
            }
        }

        global.Write(0, 4); // no HF multiplier thresholds
        JxlEntropyCode.WriteContextMap(global, [.. _ctxMap.Select(v => (byte)v)], _numCtxs);
        global.WriteBool(false); // chroma from luma: JPEG's factor, no base correlation
        global.WriteU32(new JxlU32(84, 0, 256, 0, 2, 8, 258, 16), ColorFactor);
        global.WriteF16(0);
        global.WriteF16(0);
        global.Write(128, 8);
        global.Write(128, 8);
        global.WriteBool(true); // global tree
        JxlHistogramParams mp = JxlHistogramParams.ForModular(7);
        JxlEntropyCode treeCode = JxlEntropyCode.Write(global, mp, 6, treeTokens);
        treeCode.WriteTokens(global, treeTokens[0]);
        JxlEntropyCode code = JxlEntropyCode.Write(global, mp with { ImageWidths = widths }, context.Max() + 1, modularTokens);

        // LF groups.
        JxlBitWriter[] lfGroups = new JxlBitWriter[_dcGroups];
        Parallel.For(0, _dcGroups, _parallel, g =>
        {
            JxlBitWriter w = new();
            w.Write(0, 2); // no extra DC precision
            WriteStream(w, code, modularTokens[g]);
            (_, _, int bw, int bh) = DcGroupRect(g);
            int bits = CeilLog2((long)bw * bh);
            w.Write((ulong)(metaCounts[g] - 1), bits);
            WriteStream(w, code, modularTokens[_dcGroups + g]);
            lfGroups[g] = w;
        });

        // HF global: the quantization tables (DCT8 from the JPEG, the rest the defaults), then the order and AC distributions.
        JxlBitWriter hf = new();
        hf.WriteBool(false);
        hf.Write(7, 3); // RAW
        hf.WriteF16(1f / (8 * 255));
        WriteStream(hf, code, modularTokens[^1]);
        for (int i = 1; i < 17; i++)
        {
            hf.Write(0, 3);
        }

        hf.Write(0, CeilLog2(_groups)); // one HF preset
        hf.WriteU32(new JxlU32(0x5F, 0, 0x13, 0, 0, 0, 0, 13), (uint)_usedOrders);
        if (_usedOrders != 0)
        {
            WriteOrders(hf);
        }

        JxlHistogramParams acp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            UintMethod = JxlHistogramParams.UintKind.None,
            AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
        };
        List<List<JxlToken>> ac = [.. acTokens];
        JxlEntropyCode acCode = JxlEntropyCode.Write(hf, acp, (NonZeroBuckets + ZeroDensityContexts) * _numCtxs, ac);
        JxlBitWriter[] passGroups = new JxlBitWriter[_groups];
        Parallel.For(0, _groups, _parallel, g =>
        {
            JxlBitWriter w = new();
            acCode.WriteTokens(w, ac[g]);
            passGroups[g] = w;
        });

        if (_groups == 1)
        {
            JxlBitWriter all = global;
            all.AppendBits(lfGroups[0]);
            all.AppendBits(hf);
            all.AppendBits(passGroups[0]);
            all.ZeroPadToByte();
            return [all.ToArray()];
        }

        List<byte[]> sections = [Padded(global)];
        sections.AddRange(lfGroups.Select(Padded));
        sections.Add(Padded(hf));
        sections.AddRange(passGroups.Select(Padded));
        return sections;
    }

    private static byte[] Padded(JxlBitWriter w)
    {
        w.ZeroPadToByte();
        return w.ToArray();
    }

    /// <summary>Writes a Modular stream that uses the global tree: its header (default WP, no transforms) and tokens.</summary>
    private static void WriteStream(JxlBitWriter w, JxlEntropyCode code, List<JxlToken> tokens)
    {
        w.WriteBool(true);
        w.WriteBool(true);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 8), 0);
        code.WriteTokens(w, tokens);
    }

    /// <summary>Gets an LF group's place and size in luma blocks.</summary>
    private (int X0, int Y0, int W, int H) DcGroupRect(int g)
    {
        int x0 = g % _dcGroupsX * GroupDim;
        int y0 = g / _dcGroupsX * GroupDim;
        return (x0, y0, Math.Min(GroupDim, _blocksX - x0), Math.Min(GroupDim, _blocksY - y0));
    }

    /// <summary>Gets an LF group's DC channels (Y, X, B), each at its own sampling.</summary>
    private List<JxlChannel> DcChannels(int g)
    {
        (int x0, int y0, int w, int h) = DcGroupRect(g);
        List<JxlChannel> channels = [];
        foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
        {
            int cw = w >> _hs[c], chh = h >> _vs[c];
            int cx = x0 >> _hs[c], cy = y0 >> _vs[c];
            int pbx = PlaneBlocksX(c);
            JxlChannel ch = new(cw, chh, 0, 0);
            for (int y = 0; y < chh; y++)
            {
                _dc[c].AsSpan(((cy + y) * pbx) + cx, cw).CopyTo(ch.Data.AsSpan(y * cw, cw));
            }

            channels.Add(ch);
        }

        return channels;
    }

    /// <summary>Gets an LF group's HF metadata: the chroma-from-luma factors, the transforms and multipliers (all DCT8 at 1) and the EPF sharpness.</summary>
    private List<JxlChannel> MetaChannels(int g)
    {
        (int x0, int y0, int w, int h) = DcGroupRect(g);
        int tx0 = x0 / 8, ty0 = y0 / 8;
        int tw = Ceil(w, 8), th = Ceil(h, 8);
        JxlChannel xfy = new(tw, th, 0, 0);
        JxlChannel bfy = new(tw, th, 0, 0);
        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                xfy.Data[(y * tw) + x] = _yToX[((ty0 + y) * _tilesX) + tx0 + x];
                bfy.Data[(y * tw) + x] = _yToB[((ty0 + y) * _tilesX) + tx0 + x];
            }
        }

        return [xfy, bfy, new JxlChannel(w * h, 2, 0, 0), new JxlChannel(w, h, 0, 0)];
    }

    /// <summary>Writes the coefficient orders as Lehmer codes against the natural order (EncodeCoeffOrders).</summary>
    private void WriteOrders(JxlBitWriter w)
    {
        int[] natural = JxlHfPass.Natural(0);
        int[] lut = new int[64];
        for (int i = 0; i < 64; i++)
        {
            lut[natural[i]] = i;
        }

        List<JxlToken> tokens = [];
        for (int c = 0; c < 3; c++)
        {
            int[] perm = new int[64];
            for (int i = 0; i < 64; i++)
            {
                perm[i] = lut[_orders[c][i]];
            }

            int[] lehmer = new int[64];
            bool[] taken = new bool[64];
            for (int i = 0; i < 64; i++)
            {
                int smaller = 0;
                for (int v = 0; v < perm[i]; v++)
                {
                    smaller += taken[v] ? 0 : 1;
                }

                lehmer[i] = smaller;
                taken[perm[i]] = true;
            }

            int end = 64;
            while (end > 1 && lehmer[end - 1] == 0)
            {
                end--;
            }

            tokens.Add(new JxlToken(OrderContext(64), (uint)(end - 1)));
            uint last = 0;
            for (int i = 1; i < end; i++)
            {
                tokens.Add(new JxlToken(OrderContext(last), (uint)lehmer[i]));
                last = (uint)lehmer[i];
            }
        }

        List<List<JxlToken>> streams = [tokens];
        JxlHistogramParams p = new() { Rle = true };
        JxlEntropyCode code = JxlEntropyCode.Write(w, p, 8, streams);
        code.WriteTokens(w, streams[0]);
    }

    private static int OrderContext(uint x) => Math.Min(JxlEntropyDecoder.CeilLog2Plus1(x), 7);
}
