using System.Buffers.Binary;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Rotates, flips, crops and drops the color of JPEG files without decoding them to pixels, so nothing is lost: the quantized DCT
/// coefficients are moved and sign-flipped block by block, as <c>jpegtran</c> does, and coded again. Quantization tables and
/// chroma subsampling stay as they were. Every mode the reader takes works except lossless JPEG, which has no coefficients.
/// </summary>
/// <remarks>
/// The whole image's coefficients are held while it runs (about 2 bytes per sample, 3 bytes per pixel for a 4:2:0 photo), counted
/// against <see cref="SafetyLimits.MaxTotalBytes"/>. The result is the same for any <see cref="EgamiContext.MaxParallelism"/>.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
/// </example>
public static class JpegTransform
{
    /// <summary>Transforms the JPEG in <paramref name="source"/> and writes the result to <paramref name="destination"/>.</summary>
    /// <param name="source">The JPEG, read from its current position. It is not disposed.</param>
    /// <param name="destination">Where the result goes. It is not disposed.</param>
    /// <param name="options">What to do.</param>
    /// <param name="context">The context whose limits and diagnostics apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The size of the result and the warnings.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="JpegTransformOptions.Crop"/> is empty or leaves the image.</exception>
    /// <exception cref="InvalidOperationException"><see cref="JpegTransformEdges.Strict"/> and a partial MCU would move.</exception>
    /// <exception cref="NotSupportedException"><see cref="JpegTransformOptions.Grayscale"/> on an RGB, CMYK or YCCK file.</exception>
    /// <exception cref="UnsupportedFormatException">The file is lossless or hierarchical JPEG.</exception>
    /// <exception cref="CorruptDataException">The file breaks the format's rules beyond repair.</exception>
    /// <exception cref="LimitExceededException">The image is larger than the limits allow, or its coefficients need more memory.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
    /// </example>
    public static JpegTransformResult Apply(Stream source, Stream destination, JpegTransformOptions options, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        context ??= EgamiContext.Default;
        JpegTransformResult result = JpegTransformer.Run(source, destination, options, context);
        using DiagnosticReporter diagnostics = DiagnosticReporter.Start(context);
        foreach (EgamiDiagnostic warning in result.Warnings)
        {
            diagnostics.Report(warning);
        }

        return result;
    }

    /// <summary>
    /// Transforms a JPEG file into another, or into itself: the result goes to a temporary file beside
    /// <paramref name="destinationPath"/> that replaces it once complete, so a failure leaves the destination as it was.
    /// </summary>
    /// <param name="sourcePath">The JPEG file.</param>
    /// <param name="destinationPath">The file to write; it may be <paramref name="sourcePath"/>.</param>
    /// <param name="options">What to do.</param>
    /// <param name="context">The context whose limits and diagnostics apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The size of the result and the warnings.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="JpegTransformOptions.Crop"/> is empty or leaves the image.</exception>
    /// <exception cref="InvalidOperationException"><see cref="JpegTransformEdges.Strict"/> and a partial MCU would move.</exception>
    /// <exception cref="NotSupportedException"><see cref="JpegTransformOptions.Grayscale"/> on an RGB, CMYK or YCCK file.</exception>
    /// <exception cref="UnsupportedFormatException">The file is lossless or hierarchical JPEG.</exception>
    /// <exception cref="CorruptDataException">The file breaks the format's rules beyond repair.</exception>
    /// <exception cref="LimitExceededException">The image is larger than the limits allow, or its coefficients need more memory.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
    /// </example>
    public static JpegTransformResult Apply(string sourcePath, string destinationPath, JpegTransformOptions options, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(options);
        string full = Path.GetFullPath(destinationPath);
        string temporary = Path.Combine(Path.GetDirectoryName(full) ?? ".", "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            JpegTransformResult result;
            using (FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                result = Apply(input, output, options, context);
            }

            File.Move(temporary, full, overwrite: true);
            return result;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

/// <summary>The transform itself: reading the coefficients, mapping each output block to its source block, and writing the file.</summary>
internal static class JpegTransformer
{
    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> AdobeId => "Adobe"u8;

    private static ReadOnlySpan<byte> MpfId => "MPF\0"u8;

    internal static JpegTransformResult Run(Stream source, Stream destination, JpegTransformOptions options, EgamiContext context)
    {
        EgamiInput input = new(source, context, CancellationToken.None);
        try
        {
            input.SetFormat(JpegFormat.Info);
            JpegHeader header = JpegSegmentReader.ReadHeader(input, ReaderOptions.Default, new JpegHeader { Segments = [] });
            if (header.Lossless)
            {
                throw new UnsupportedFormatException("Lossless JPEG (SOF3 or SOF11) has no DCT coefficients to transform losslessly.");
            }

            input.Limits.CheckFrameSize(header.Width, header.Height);
            WarningList warnings = new();
            warnings.AddRange(header.Metadata.WarningList);
            using JpegDecoder<Gray8> decoder = new(header, ReaderOptions.Default, warnings, fastUpsampling: false, scale: 1);
            Plan plan = new(header, decoder, options);
            decoder.DecodeCoefficients(input);
            using JpegBitWriter writer = new(destination);
            writer.Marker(JpegMarker.Soi);
            WriteSegments(writer, header, plan, options, context, warnings);
            plan.Write(writer, input, options);
            writer.Marker(JpegMarker.Eoi);
            writer.Flush();
            return new JpegTransformResult(plan.Width, plan.Height, [.. warnings]);
        }
        finally
        {
            input.Dispose();
        }
    }

    /// <summary>Returns the EXIF orientation's lossless way upright, as <see cref="Operations.RasterOps.AutoOrient"/> turns it.</summary>
    internal static JpegTransformOperation Upright(ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.FlipHorizontal => JpegTransformOperation.FlipHorizontal,
        ImageOrientation.Rotate180 => JpegTransformOperation.Rotate180,
        ImageOrientation.FlipVertical => JpegTransformOperation.FlipVertical,
        ImageOrientation.Transpose => JpegTransformOperation.Transpose,
        ImageOrientation.Rotate90 => JpegTransformOperation.Rotate90,
        ImageOrientation.Transverse => JpegTransformOperation.Transverse,
        ImageOrientation.Rotate270 => JpegTransformOperation.Rotate270,
        _ => JpegTransformOperation.None,
    };

    /// <summary>
    /// Writes the APPn and COM segments in the source's order. The JFIF and Adobe segments, which say how to read the colors, are
    /// always kept (Adobe's is dropped for a gray result); with <see cref="JpegTransformOptions.CopyMetadata"/> the rest follow,
    /// EXIF updated, except an MPF index, whose offsets point at images after the end of the source that are not copied.
    /// </summary>
    private static void WriteSegments(JpegBitWriter writer, JpegHeader header, Plan plan, JpegTransformOptions options, EgamiContext context, WarningList warnings)
    {
        bool exifDone = false;
        foreach ((byte marker, byte[] payload) in header.Segments!)
        {
            bool jfif = marker == JpegMarker.App0 && payload.AsSpan().StartsWith("JFIF\0"u8);
            bool adobe = marker == JpegMarker.App14 && payload.AsSpan().StartsWith(AdobeId);
            if (adobe && plan.Gray)
            {
                continue;
            }

            if (!jfif && !adobe && !options.CopyMetadata)
            {
                continue;
            }

            if (marker == JpegMarker.App2 && payload.AsSpan().StartsWith(MpfId))
            {
                continue;
            }

            if (marker == JpegMarker.App1 && payload.AsSpan().StartsWith(ExifId) && !exifDone)
            {
                exifDone = true;
                if (header.Metadata.Exif is ExifData exif && UpdateExif(exif, plan, options, context, warnings) is byte[] updated)
                {
                    JpegWriter.Segment(writer, marker, updated);
                }

                continue;
            }

            JpegWriter.Segment(writer, marker, payload);
        }
    }

    /// <summary>
    /// Returns the EXIF segment's payload for the result: pixel dimensions set to its size, the orientation reset after
    /// <see cref="JpegTransformOperation.AutoOrient"/>, and the thumbnail transformed the same way (dropped after a crop, or when it
    /// cannot be transformed). Returns null if the block no longer fits a segment.
    /// </summary>
    private static byte[]? UpdateExif(ExifData source, Plan plan, JpegTransformOptions options, EgamiContext context, WarningList warnings)
    {
        ExifData exif = source.Copy();
        if (plan.AutoOriented && exif.TryGet(ExifKey.Orientation, out _))
        {
            exif.Orientation = ImageOrientation.Normal;
        }

        Dimension(exif, ExifKey.PixelXDimension, plan.Width);
        Dimension(exif, ExifKey.PixelYDimension, plan.Height);
        if (!exif.Thumbnail.IsEmpty && (plan.Moved || plan.Gray || options.Crop is not null))
        {
            TransformThumbnail(exif, plan, options, context, warnings);
        }

        ReadOnlyMemory<byte> bytes = exif.ToBytes();
        int max = ushort.MaxValue - 2 - ExifId.Length;
        if (bytes.Length > max && exif.RemoveThumbnail())
        {
            warnings.Add("Save.MetadataDropped", "The EXIF block is over the 64 KB a JPEG segment holds; its thumbnail was left out.");
            bytes = exif.ToBytes();
        }

        if (bytes.Length > max)
        {
            warnings.Add("Save.MetadataDropped", "The EXIF block is over the 64 KB a JPEG segment holds, even without its thumbnail; it was not written.");
            return null;
        }

        return [.. ExifId, .. bytes.Span];
    }

    /// <summary>Sets a pixel dimension tag the block has, keeping its type (SHORT or LONG).</summary>
    private static void Dimension(ExifData exif, ExifKey key, int value)
    {
        if (!exif.TryGet(key, out ExifEntry? entry))
        {
            return;
        }

        if (entry.Type == ExifType.Short)
        {
            exif.Set(key, (ushort)value);
        }
        else
        {
            exif.Set(key, (uint)value);
        }
    }

    /// <summary>Gives the thumbnail the result's rotation, flip and gray conversion, or drops it after a crop or when that fails.</summary>
    private static void TransformThumbnail(ExifData exif, Plan plan, JpegTransformOptions options, EgamiContext context, WarningList warnings)
    {
        if (options.Crop is not null)
        {
            exif.RemoveThumbnail();
            return;
        }

        ReadOnlySpan<byte> thumbnail = exif.Thumbnail.Span;
        if (thumbnail.Length < 4 || thumbnail[0] != 0xFF || thumbnail[1] != JpegMarker.Soi)
        {
            // An uncompressed thumbnail would need its own layout rules; it is dropped rather than left unturned.
            exif.RemoveThumbnail();
            warnings.Add("Jpeg.ThumbnailDropped", "The EXIF thumbnail is not a JPEG, so it could not be turned with the image; it was left out.");
            return;
        }

        try
        {
            using MemoryStream input = new(exif.Thumbnail.ToArray(), writable: false);
            using MemoryStream output = new();
            JpegTransformOptions turn = new()
            {
                Operation = plan.Operation,
                Grayscale = plan.Gray,
                Edges = options.Edges == JpegTransformEdges.Keep ? JpegTransformEdges.Keep : JpegTransformEdges.Trim,
                CopyMetadata = false,
            };
            _ = Run(input, output, turn, context);
            exif.SetThumbnail(output.ToArray());
        }
        catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException or LimitExceededException or NotSupportedException)
        {
            exif.RemoveThumbnail();
            warnings.Add("Jpeg.ThumbnailDropped", $"The EXIF thumbnail could not be transformed ({e.Message}); it was left out.");
        }
    }

    /// <summary>
    /// The geometry of one transform: the operation as a transpose followed by flips, the components written and their sampling,
    /// the size of the result, and where its blocks come from.
    /// </summary>
    private sealed class Plan
    {
        /// <summary>The zigzag position of each natural-order coefficient: the inverse of <see cref="JpegTables.ZigZag"/>.</summary>
        private static readonly byte[] s_zigzagOf = ZigZagOf();

        private static readonly short[] s_signs = Signs();

        private readonly JpegHeader _header;
        private readonly JpegDecoder<Gray8> _decoder;
        private readonly bool _transpose;
        private readonly bool _flipX;
        private readonly bool _flipY;
        private readonly int[] _source;
        private readonly int[] _h;
        private readonly int[] _v;
        private readonly int _maxH;
        private readonly int _maxV;
        private readonly int _cropX;
        private readonly int _cropY;
        private readonly int _fullColumns;
        private readonly int _fullRows;
        private readonly int _limit;
        private readonly byte[] _from;

        internal Plan(JpegHeader header, JpegDecoder<Gray8> decoder, JpegTransformOptions options)
        {
            _header = header;
            _decoder = decoder;
            if (!Enum.IsDefined(options.Operation) || !Enum.IsDefined(options.Edges))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "The operation or edge mode is not one of the defined values.");
            }

            AutoOriented = options.Operation == JpegTransformOperation.AutoOrient;
            Operation = AutoOriented ? Upright(header.Metadata.Exif?.Orientation ?? ImageOrientation.Normal) : options.Operation;
            (_transpose, _flipX, _flipY) = Operation switch
            {
                JpegTransformOperation.FlipHorizontal => (false, true, false),
                JpegTransformOperation.FlipVertical => (false, false, true),
                JpegTransformOperation.Rotate90 => (true, true, false),
                JpegTransformOperation.Rotate180 => (false, true, true),
                JpegTransformOperation.Rotate270 => (true, false, true),
                JpegTransformOperation.Transpose => (true, false, false),
                JpegTransformOperation.Transverse => (true, true, true),
                _ => (false, false, false),
            };

            int count = header.Components.Length;
            if (options.Grayscale && count > 1)
            {
                if (header.ColorSpace != JpegColorSpace.YCbCr)
                {
                    throw new NotSupportedException($"Only YCbCr JPEG can drop its color losslessly; this file is {header.ColorSpace}.");
                }

                Gray = true;
            }

            _source = Gray ? [0] : [.. Enumerable.Range(0, count)];
            _h = new int[_source.Length];
            _v = new int[_source.Length];
            for (int i = 0; i < _source.Length; i++)
            {
                (int h, int v) = decoder.Sampling(_source[i]);
                (_h[i], _v[i]) = Gray ? (1, 1) : _transpose ? (v, h) : (h, v);
            }

            _maxH = _h.Max();
            _maxV = _v.Max();
            int unitWidth = 8 * _maxH;
            int unitHeight = 8 * _maxV;
            int fullWidth = _transpose ? header.Height : header.Width;
            int fullHeight = _transpose ? header.Width : header.Height;
            _fullColumns = fullWidth / unitWidth;
            _fullRows = fullHeight / unitHeight;

            // The crop, in the transformed image, with its top-left corner moved to the MCU grid (jpegtran -crop).
            (int x, int y, int width, int height) = (0, 0, fullWidth, fullHeight);
            if (options.Crop is Rect crop)
            {
                if (crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0 || crop.X > fullWidth - crop.Width || crop.Y > fullHeight - crop.Height)
                {
                    throw new ArgumentOutOfRangeException(nameof(options), $"The crop {crop} is empty or leaves the {fullWidth}×{fullHeight} image.");
                }

                (x, y, width, height) = (crop.X / unitWidth, crop.Y / unitHeight, crop.Width + (crop.X % unitWidth), crop.Height + (crop.Y % unitHeight));
            }

            _cropX = x;
            _cropY = y;
            Width = Edge(_flipX, width, x, _fullColumns, unitWidth, options.Edges, "right");
            Height = Edge(_flipY, height, y, _fullRows, unitHeight, options.Edges, "bottom");
            McusX = (Width + unitWidth - 1) / unitWidth;
            McusY = (Height + unitHeight - 1) / unitHeight;

            // Values a corrupt source could hold beyond what the precision allows are clamped, so every one has a Huffman code.
            _limit = (1 << (header.Precision + 2)) - 1;
            _from = From(_transpose);
        }

        /// <summary>Gets the operation applied, with <see cref="JpegTransformOperation.AutoOrient"/> resolved.</summary>
        internal JpegTransformOperation Operation { get; }

        /// <summary>Gets whether the operation came from the EXIF orientation, which is then reset.</summary>
        internal bool AutoOriented { get; }

        /// <summary>Gets whether blocks move at all.</summary>
        internal bool Moved => Operation != JpegTransformOperation.None;

        /// <summary>Gets whether only luma is written.</summary>
        internal bool Gray { get; }

        internal int Width { get; }

        internal int Height { get; }

        private int McusX { get; }

        private int McusY { get; }

        private int BlocksPerMcu => _h.Select((h, i) => h * _v[i]).Sum();

        private bool Single => _source.Length == 1;

        /// <summary>
        /// Returns the result's size along one axis. A mirrored axis with a partial MCU inside the area is trimmed back to whole
        /// MCUs (jpegtran's trim_right_edge rule, which also leaves an area inside the partial MCU alone), or refused when strict.
        /// </summary>
        private static int Edge(bool mirrored, int size, int offset, int fullUnits, int unit, JpegTransformEdges edges, string side)
        {
            int units = size / unit;
            bool partial = mirrored && offset + units >= fullUnits && size > (fullUnits - offset) * unit;
            if (!partial || edges == JpegTransformEdges.Keep)
            {
                return size;
            }

            if (edges == JpegTransformEdges.Strict)
            {
                throw new InvalidOperationException($"The {side} edge is not a whole number of {unit}-pixel MCUs, so it cannot be mirrored losslessly. Use JpegTransformEdges.Trim or Keep.");
            }

            return units > 0 && offset + units == fullUnits ? units * unit : size;
        }

        /// <summary>Writes the quantization tables, the frame header and the scans.</summary>
        internal void Write(JpegBitWriter writer, EgamiInput input, JpegTransformOptions options)
        {
            bool sixteen = false;
            List<int> slots = [];
            foreach (int s in _source)
            {
                int slot = _header.Components[s].QuantTable;
                if (!slots.Contains(slot))
                {
                    slots.Add(slot);
                }
            }

            foreach (int slot in slots.Order())
            {
                ushort[] table = _transpose ? Transposed(_header.QuantTables[slot]!) : _header.QuantTables[slot]!;
                bool wide = table.Any(q => q > byte.MaxValue);
                sixteen |= wide;
                byte[] dqt = new byte[wide ? 129 : 65];
                dqt[0] = (byte)((wide ? 0x10 : 0) | slot);
                for (int k = 0; k < 64; k++)
                {
                    if (wide)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(dqt.AsSpan(1 + (2 * k)), table[k]);
                    }
                    else
                    {
                        dqt[1 + k] = (byte)table[k];
                    }
                }

                JpegWriter.Segment(writer, JpegMarker.Dqt, dqt);
            }

            bool deep = _header.Precision > 8;
            byte[] sof = new byte[6 + (3 * _source.Length)];
            sof[0] = (byte)_header.Precision;
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)Height);
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)Width);
            sof[5] = (byte)_source.Length;
            for (int i = 0; i < _source.Length; i++)
            {
                JpegFrameComponent c = _header.Components[_source[i]];
                sof[6 + (3 * i)] = (byte)c.Id;
                sof[7 + (3 * i)] = (byte)((_h[i] << 4) | _v[i]);
                sof[8 + (3 * i)] = (byte)c.QuantTable;
            }

            byte marker = options.Arithmetic
                ? options.Progressive ? JpegMarker.Sof10 : JpegMarker.Sof9
                : options.Progressive ? JpegMarker.Sof2 : deep || sixteen ? JpegMarker.Sof1 : JpegMarker.Sof0;
            JpegWriter.Segment(writer, marker, sof);
            if (options.Progressive)
            {
                WriteProgressive(writer, input, options.Arithmetic);
            }
            else if (options.Arithmetic)
            {
                WriteArithmetic(writer);
            }
            else
            {
                WriteSequential(writer, options.OptimizeHuffman || deep);
            }
        }

        /// <summary>Writes one interleaved scan, with optimized or standard Huffman tables; MCU rows are made as they are coded.</summary>
        private void WriteSequential(JpegBitWriter writer, bool optimize)
        {
            int tables = Single ? 1 : 2;
            JpegHuffmanCodes[] dc = Single ? [JpegHuffmanCodes.DcLuma] : [JpegHuffmanCodes.DcLuma, JpegHuffmanCodes.DcChroma];
            JpegHuffmanCodes[] ac = Single ? [JpegHuffmanCodes.AcLuma] : [JpegHuffmanCodes.AcLuma, JpegHuffmanCodes.AcChroma];
            short[] row = new short[McusX * BlocksPerMcu * 64];
            int[] predictors = new int[_source.Length];
            if (optimize)
            {
                long[] counts = new long[tables * 512];
                for (int my = 0; my < McusY; my++)
                {
                    FillRow(my, row);
                    int b = 0;
                    for (int mx = 0; mx < McusX; mx++)
                    {
                        for (int i = 0; i < _source.Length; i++)
                        {
                            int t = Table(i);
                            for (int n = 0; n < _h[i] * _v[i]; n++, b++)
                            {
                                JpegBlockCoder.Count(row.AsSpan(b * 64, 64), ref predictors[i], counts.AsSpan(t * 512, 256), counts.AsSpan((t * 512) + 256, 256));
                            }
                        }
                    }
                }

                for (int t = 0; t < tables; t++)
                {
                    dc[t] = JpegHuffmanCodes.Optimal(counts.AsSpan(t * 512, 256));
                    ac[t] = JpegHuffmanCodes.Optimal(counts.AsSpan((t * 512) + 256, 256));
                }

                Array.Clear(predictors);
            }

            for (int t = 0; t < tables; t++)
            {
                Huffman(writer, 0, t, dc[t]);
                Huffman(writer, 1, t, ac[t]);
            }

            byte[] sos = new byte[1 + (2 * _source.Length) + 3];
            sos[0] = (byte)_source.Length;
            for (int i = 0; i < _source.Length; i++)
            {
                sos[1 + (2 * i)] = (byte)_header.Components[_source[i]].Id;
                sos[2 + (2 * i)] = (byte)((Table(i) << 4) | Table(i));
            }

            sos[^2] = 63;
            JpegWriter.Segment(writer, JpegMarker.Sos, sos);
            for (int my = 0; my < McusY; my++)
            {
                FillRow(my, row);
                int b = 0;
                for (int mx = 0; mx < McusX; mx++)
                {
                    for (int i = 0; i < _source.Length; i++)
                    {
                        int t = Table(i);
                        for (int n = 0; n < _h[i] * _v[i]; n++, b++)
                        {
                            JpegBlockCoder.Encode(writer, row.AsSpan(b * 64, 64), ref predictors[i], dc[t], ac[t]);
                        }
                    }
                }
            }

            writer.Align();
        }

        /// <summary>Writes one interleaved arithmetic-coded scan; MCU rows are made as they are coded.</summary>
        private void WriteArithmetic(JpegBitWriter writer)
        {
            byte[] sos = new byte[1 + (2 * _source.Length) + 3];
            sos[0] = (byte)_source.Length;
            for (int i = 0; i < _source.Length; i++)
            {
                sos[1 + (2 * i)] = (byte)_header.Components[_source[i]].Id;
                sos[2 + (2 * i)] = (byte)((Table(i) << 4) | Table(i));
            }

            sos[^2] = 63;
            JpegWriter.Segment(writer, JpegMarker.Sos, sos);
            short[] row = new short[McusX * BlocksPerMcu * 64];
            int[] predictors = new int[_source.Length];
            JpegArithmeticEncoder coder = new(writer);
            for (int my = 0; my < McusY; my++)
            {
                FillRow(my, row);
                int b = 0;
                for (int mx = 0; mx < McusX; mx++)
                {
                    for (int i = 0; i < _source.Length; i++)
                    {
                        int t = Table(i);
                        for (int n = 0; n < _h[i] * _v[i]; n++, b++)
                        {
                            coder.EncodeBlock(row.AsSpan(b * 64, 64), i, ref predictors[i], t, t);
                        }
                    }
                }
            }

            coder.Finish();
        }

        /// <summary>Writes the progressive scans from every MCU row, made first and held, counted against the memory limit.</summary>
        private void WriteProgressive(JpegBitWriter writer, EgamiInput input, bool arithmetic)
        {
            int length = McusX * BlocksPerMcu * 64;
            input.CheckMemory((long)length * sizeof(short) * McusY);
            short[][] rows = new short[McusY][];
            for (int my = 0; my < McusY; my++)
            {
                rows[my] = new short[length];
                FillRow(my, rows[my]);
            }

            JpegProgressiveEncoder.Component[] components = new JpegProgressiveEncoder.Component[_source.Length];
            int offset = 0;
            for (int i = 0; i < _source.Length; i++)
            {
                int samplesX = (int)((((long)Width * _h[i]) + _maxH - 1) / _maxH);
                int samplesY = (int)((((long)Height * _v[i]) + _maxV - 1) / _maxV);
                components[i] = new(_header.Components[_source[i]].Id, _h[i], _v[i], Table(i), (samplesX + 7) / 8, (samplesY + 7) / 8, offset);
                offset += _h[i] * _v[i];
            }

            JpegProgressiveEncoder encoder = new(components, McusX, McusY, 0, my => rows[my]);
            bool ycbcr = !Gray && _header.ColorSpace == JpegColorSpace.YCbCr;
            encoder.Write(writer, JpegProgressiveEncoder.Script(components.Length, ycbcr), JpegWriter.Segment, arithmetic);
        }

        private static int Table(int i) => i == 0 ? 0 : 1;

        /// <summary>
        /// Returns a quantization table (in zigzag order) for transposed blocks: each step moves with the coefficient it divides,
        /// as jpegtran's transpose_critical_parameters does.
        /// </summary>
        private static ushort[] Transposed(ushort[] zigzag)
        {
            ushort[] result = new ushort[64];
            for (int k = 0; k < 64; k++)
            {
                int n = JpegTables.ZigZag[k];
                result[s_zigzagOf[((n & 7) * 8) + (n >> 3)]] = zigzag[k];
            }

            return result;
        }

        private static byte[] ZigZagOf()
        {
            byte[] inverse = new byte[64];
            for (int k = 0; k < 64; k++)
            {
                inverse[JpegTables.ZigZag[k]] = (byte)k;
            }

            return inverse;
        }

        private static void Huffman(JpegBitWriter writer, int tableClass, int slot, JpegHuffmanCodes codes)
        {
            byte[] dht = new byte[17 + codes.Symbols.Length];
            dht[0] = (byte)((tableClass << 4) | slot);
            codes.Counts.CopyTo(dht, 1);
            codes.Symbols.CopyTo(dht, 17);
            JpegWriter.Segment(writer, JpegMarker.Dht, dht);
        }

        /// <summary>Fills MCU row <paramref name="my"/> of the result: each MCU's blocks, component by component, row by row.</summary>
        private void FillRow(int my, short[] row)
        {
            int b = 0;
            for (int mx = 0; mx < McusX; mx++)
            {
                for (int i = 0; i < _source.Length; i++)
                {
                    for (int v = 0; v < _v[i]; v++)
                    {
                        for (int h = 0; h < _h[i]; h++, b++)
                        {
                            Map(i, (mx * _h[i]) + h, (my * _v[i]) + v, row.AsSpan(b * 64, 64));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fills block (<paramref name="x"/>, <paramref name="y"/>) of output component <paramref name="i"/>: the source block it
        /// comes from, transposed and with alternate rows or columns negated as the operation says. Blocks of a partial MCU at a
        /// mirrored edge (kept with <see cref="JpegTransformEdges.Keep"/>) stay on their side of that axis, unmirrored.
        /// </summary>
        private void Map(int i, int x, int y, Span<short> target)
        {
            int gx = x + (_cropX * _h[i]);
            int gy = y + (_cropY * _v[i]);
            int width = _fullColumns * _h[i];
            int height = _fullRows * _v[i];
            bool flipX = _flipX && gx < width;
            bool flipY = _flipY && gy < height;
            int mx = flipX ? width - 1 - gx : gx;
            int my = flipY ? height - 1 - gy : gy;
            (int sx, int sy) = _transpose ? (my, mx) : (mx, my);
            (int sh, int sv) = _decoder.Sampling(_source[i]);
            if (sx >= _decoder.McusX * sh || sy >= _decoder.McusY * sv)
            {
                // A gray result's luma grid can reach a block column past a 4:2:0 frame's padded MCUs only by rounding; none is coded there.
                target.Clear();
                return;
            }

            // The decoder holds blocks in zigzag order; the coders take them in natural order.
            ReadOnlySpan<short> block = _decoder.CoefficientBlock(_source[i], sx, sy);
            ReadOnlySpan<byte> from = _from;
            ReadOnlySpan<short> sign = s_signs.AsSpan((flipX ? 64 : 0) + (flipY ? 128 : 0), 64);
            int limit = _limit;
            for (int k = 0; k < 64; k++)
            {
                target[k] = (short)Math.Clamp(block[from[k]] * sign[k], -limit, limit);
            }
        }

        /// <summary>
        /// Returns, for each natural-order position of a result block, the zigzag position in the source block it comes from: the
        /// same position, or the transposed one.
        /// </summary>
        private static byte[] From(bool transpose)
        {
            byte[] from = new byte[64];
            for (int k = 0; k < 64; k++)
            {
                from[k] = s_zigzagOf[transpose ? ((k & 7) * 8) + (k >> 3) : k];
            }

            return from;
        }

        /// <summary>
        /// Returns the signs of a block's coefficients for each mirror, 64 at a time: none, horizontal (odd columns negated),
        /// vertical (odd rows), both.
        /// </summary>
        private static short[] Signs()
        {
            short[] signs = new short[256];
            for (int mirror = 0; mirror < 4; mirror++)
            {
                for (int k = 0; k < 64; k++)
                {
                    bool negate = ((mirror & 1) != 0 && (k & 1) != 0) ^ ((mirror & 2) != 0 && ((k >> 3) & 1) != 0);
                    signs[(mirror * 64) + k] = (short)(negate ? -1 : 1);
                }
            }

            return signs;
        }
    }
}
