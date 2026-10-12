using Egami.Metadata;

namespace Egami.Formats.Jpeg;

/// <summary>JPEG marker codes (ITU-T T.81 table B.1), the byte after 0xFF.</summary>
internal static class JpegMarker
{
    internal const byte Sof0 = 0xC0;
    internal const byte Sof1 = 0xC1;
    internal const byte Sof2 = 0xC2;
    internal const byte Sof3 = 0xC3;
    internal const byte Dht = 0xC4;
    internal const byte Sof9 = 0xC9;
    internal const byte Sof10 = 0xCA;
    internal const byte Sof11 = 0xCB;
    internal const byte Sof15 = 0xCF;
    internal const byte Dac = 0xCC;
    internal const byte Rst0 = 0xD0;
    internal const byte Rst7 = 0xD7;
    internal const byte Soi = 0xD8;
    internal const byte Eoi = 0xD9;
    internal const byte Sos = 0xDA;
    internal const byte Dqt = 0xDB;
    internal const byte Dnl = 0xDC;
    internal const byte Dri = 0xDD;
    internal const byte Dhp = 0xDE;
    internal const byte Exp = 0xDF;
    internal const byte App0 = 0xE0;
    internal const byte App1 = 0xE1;
    internal const byte App2 = 0xE2;
    internal const byte App13 = 0xED;
    internal const byte App14 = 0xEE;
    internal const byte App15 = 0xEF;
    internal const byte Com = 0xFE;
    internal const byte Tem = 0x01;

    /// <summary>Returns whether a marker stands alone, with no length field after it.</summary>
    internal static bool IsStandalone(byte marker)
    {
        return marker is Soi or Eoi or Tem or (>= Rst0 and <= Rst7);
    }
}

/// <summary>One component of the frame, with its sampling factors and quantization table.</summary>
internal sealed record JpegFrameComponent(int Id, int H, int V, int QuantTable);

/// <summary>A Huffman table as stored: the count of codes of each length 1 to 16, and the symbols in code order.</summary>
internal sealed record JpegHuffmanSpec(byte[] Counts, byte[] Symbols);

/// <summary>One component of a scan and the Huffman tables it uses.</summary>
internal sealed record JpegScanComponent(int ComponentIndex, int DcTable, int AcTable);

/// <summary>A scan header: its components and the spectral selection and successive approximation bits.</summary>
internal sealed record JpegScan(JpegScanComponent[] Components, int SpectralStart, int SpectralEnd, int ApproxHigh, int ApproxLow);

/// <summary>Everything read from the segments before the first scan.</summary>
internal sealed class JpegHeader
{
    /// <summary>Gets or sets the SOF marker that started the frame.</summary>
    internal byte FrameMarker { get; set; }

    internal int Width { get; set; }

    internal int Height { get; set; }

    internal JpegFrameComponent[] Components { get; set; } = [];

    /// <summary>Gets whether the frame is progressive (SOF2, or SOF10 with arithmetic coding).</summary>
    internal bool Progressive => FrameMarker is JpegMarker.Sof2 or JpegMarker.Sof10;

    /// <summary>Gets whether the scans are arithmetic-coded (SOF9 to SOF11) rather than Huffman-coded.</summary>
    internal bool Arithmetic => FrameMarker is JpegMarker.Sof9 or JpegMarker.Sof10 or JpegMarker.Sof11;

    /// <summary>Gets whether the frame is lossless (SOF3, or SOF11 with arithmetic coding): predicted samples, no DCT.</summary>
    internal bool Lossless => FrameMarker is JpegMarker.Sof3 or JpegMarker.Sof11;

    /// <summary>Gets or sets the sample precision in bits: 8 or 12 for DCT frames, 2 to 16 for lossless ones.</summary>
    internal int Precision { get; set; } = 8;

    /// <summary>Gets whether the samples are deeper than 8 bits, so the frame decodes through the 16-bit path.</summary>
    internal bool Wide => Precision > 8;

    internal int MaxH => Components.Max(c => c.H);

    internal int MaxV => Components.Max(c => c.V);

    /// <summary>Gets the quantization tables 0 to 3, in zigzag order as stored, or null where none was defined.</summary>
    internal ushort[]?[] QuantTables { get; } = new ushort[]?[4];

    internal JpegHuffmanSpec?[] DcTables { get; } = new JpegHuffmanSpec?[4];

    internal JpegHuffmanSpec?[] AcTables { get; } = new JpegHuffmanSpec?[4];

    internal int RestartInterval { get; set; }

    /// <summary>
    /// Gets the arithmetic conditioning of DC (and lossless) tables 0 to 3 (T.81 F.1.4.4.1.4): the lower bound L of each, 0 by
    /// default; a DAC segment may change it.
    /// </summary>
    internal byte[] ArithmeticLower { get; } = [0, 0, 0, 0];

    /// <summary>Gets the upper bound U of each DC conditioning table, 1 by default.</summary>
    internal byte[] ArithmeticUpper { get; } = [1, 1, 1, 1];

    /// <summary>Gets Kx of each AC conditioning table (T.81 F.1.4.4.2.1), 5 by default.</summary>
    internal byte[] ArithmeticKx { get; } = [5, 5, 5, 5];

    /// <summary>Gets or sets the Adobe APP14 transform flag (0, 1 or 2), or -1 with no Adobe segment.</summary>
    internal int AdobeTransform { get; set; } = -1;

    /// <summary>The Photoshop resource block's parts, from APP13 segments in file order.</summary>
    internal MemoryStream? PhotoshopBlock { get; set; }

    internal bool HasJfif { get; set; }

    /// <summary>
    /// Gets or sets the list that collects every APPn and COM segment as read, marker and payload, in file order; null (the
    /// default) keeps none. A lossless transform copies them.
    /// </summary>
    internal List<(byte Marker, byte[] Payload)>? Segments { get; set; }

    /// <summary>
    /// Gets or sets whether the stream is embedded in another format whose samples are decoded raw (TIFF compression 7, DNG), so
    /// the two-component frames such files use are accepted.
    /// </summary>
    internal bool Raw { get; set; }

    /// <summary>Gets or sets the first scan's header, read when parsing stopped at it.</summary>
    internal JpegScan? FirstScan { get; set; }

    internal RasterMetadata Metadata { get; } = new() { FormatName = JpegFormat.Info.Name };

    internal JpegDetails Details { get; } = new();

    /// <summary>Gets or sets the ISO 21496-1 gain map metadata (APP2 after its identifier), or null.</summary>
    internal byte[]? IsoGainMap { get; set; }

    internal bool HasMetadata { get; set; }

    /// <summary>Gets the color model, from the component count and the JFIF and Adobe segments, as libjpeg decides it.</summary>
    /// <remarks>
    /// A lossless frame of three components is RGB unless a JFIF segment or an Adobe transform other than 0 says YCbCr, since
    /// lossless files are usually stored without a color transform.
    /// </remarks>
    internal JpegColorSpace ColorSpace => Components.Length switch
    {
        1 => JpegColorSpace.Gray,
        3 when Lossless => AdobeTransform == 0 || (AdobeTransform < 0 && !HasJfif) ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr,
        3 => AdobeTransform == 0 || (AdobeTransform < 0 && !HasJfif && IsRgbIds()) ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr,
        _ => AdobeTransform == 2 ? JpegColorSpace.Ycck : JpegColorSpace.Cmyk,
    };

    /// <summary>Returns whether three components are labelled 'R', 'G', 'B', the convention for RGB files without an Adobe segment.</summary>
    private bool IsRgbIds()
    {
        return Components[0].Id == 'R' && Components[1].Id == 'G' && Components[2].Id == 'B';
    }
}
