using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// Reads JPEG-LS, written from ITU-T T.87 and the LOCO-I paper (Weinberger, Seroussi and Sapiro, 2000). The file is read whole;
/// when one scan holds every component at full size its rows are decoded as they are asked for, otherwise every scan is decoded
/// into planes first. Gray loads as Gray8 or Gray16, two components as gray and alpha, three as RGB and four as RGBA; samples
/// of other depths are scaled to the pixel's range. A palettized file (one component and a mapping table) loads as its table's
/// colours.
/// </summary>
internal sealed class JpegLsFormatReader : ExtendedReader
{
    /// <summary>The most header bytes read before the first scan, mapping tables included.</summary>
    private const int MaxHeader = 16 << 20;

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        byte[] prefix = ReadPrefix(input, out int length);
        JpegLsDecoder decoder = Open(input, prefix, length, static (_, _) => { });
        Plan plan = Plan.For(decoder.Header, decoder.FirstScan, static (_, _) => { });
        return Describe(JpegLsFormat.Info, decoder.Header.Width, decoder.Header.Height, plan.Layout,
            decoder.Streams ? RasterAccess.Streamed : RasterAccess.Buffered, WorkingBytes(decoder.Header, decoder.Streams));
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        byte[] prefix = ReadPrefix(input, out int prefixLength);
        JpegLsDecoder probe = Open(input, prefix, prefixLength, static (_, _) => { });
        JpegLsHeader header = probe.Header;
        input.CheckFrameSize(header.Width, header.Height);

        // The coded data is at most about LIMIT bits a sample; anything far beyond is not image data.
        long samples = header.Components.Sum(c => (long)c.Width * c.Height);
        long max = prefixLength + (samples * 8) + (1 << 20);
        byte[] rest = InputData.ReadToEnd(input, max - prefixLength, out int restLength);
        input.CheckMemory(prefixLength + (long)restLength);
        byte[] data = new byte[prefixLength + restLength];
        prefix.AsSpan(0, prefixLength).CopyTo(data);
        rest.AsSpan(0, restLength).CopyTo(data.AsSpan(prefixLength));
        List<(string Name, string Message)> warnings = [];
        void Warn(string name, string message) => warnings.Add((name, message));
        JpegLsDecoder decoder = Open(input, data, data.Length, Warn);
        Plan plan = Plan.For(decoder.Header, decoder.FirstScan, Warn);
        RowReader rows = plan.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray when plan.Wide => new Rows<Gray16>(input, options, decoder, plan, warnings),
            PixelChannelOrder.Gray => new Rows<Gray8>(input, options, decoder, plan, warnings),
            PixelChannelOrder.GrayAlpha when plan.Wide => new Rows<GrayAlpha16>(input, options, decoder, plan, warnings),
            PixelChannelOrder.GrayAlpha => new Rows<GrayAlpha8>(input, options, decoder, plan, warnings),
            PixelChannelOrder.Rgb when plan.Wide => new Rows<Rgb16>(input, options, decoder, plan, warnings),
            PixelChannelOrder.Rgb => new Rows<Rgb8>(input, options, decoder, plan, warnings),
            _ when plan.Wide => new Rows<Rgba16>(input, options, decoder, plan, warnings),
            _ => new Rows<Rgba8>(input, options, decoder, plan, warnings),
        };
        return rows;
    }

    private static JpegLsDecoder Open(EgamiInput input, byte[] data, int length, Action<string, string> warn)
    {
        try
        {
            return new JpegLsDecoder(data, length, warn);
        }
        catch (JpegLsDataException e)
        {
            throw input.Corrupt(0, e.Message);
        }
    }

    private static long WorkingBytes(JpegLsHeader header, bool streams)
    {
        long lines = header.Components.Sum(c => (c.Width + 2L) * 4 * 2);
        return streams ? lines : lines + header.Components.Sum(c => (long)c.Width * c.Height * 2);
    }

    /// <summary>Reads the stream up to and including the first SOS segment.</summary>
    private static byte[] ReadPrefix(EgamiInput input, out int length)
    {
        byte[] data = new byte[4096];
        length = 0;
        Append(ref data, ref length, input, 2);
        if (data[0] != 0xFF || data[1] != JpegLsMarker.Soi)
        {
            throw input.Corrupt(0, "The data does not start with SOI.");
        }

        while (true)
        {
            Append(ref data, ref length, input, 1);
            if (data[length - 1] != 0xFF)
            {
                continue;
            }

            Append(ref data, ref length, input, 1);
            while (data[length - 1] == 0xFF)
            {
                Append(ref data, ref length, input, 1);
            }

            byte marker = data[length - 1];
            if (marker is JpegLsMarker.Soi or (>= JpegLsMarker.Rst0 and <= JpegLsMarker.Rst0 + 7))
            {
                continue;
            }

            if (marker == JpegLsMarker.Eoi)
            {
                throw input.Corrupt(input.Position, "The stream ends before its first scan.");
            }

            Append(ref data, ref length, input, 2);
            int size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(length - 2));
            if (size < 2)
            {
                throw input.Corrupt(input.Position - 2, $"The segment of marker 0x{marker:X2} has length {size}.");
            }

            Append(ref data, ref length, input, size - 2);
            if (marker == JpegLsMarker.Sos)
            {
                return data;
            }
        }

        static void Append(ref byte[] data, ref int length, EgamiInput input, int count)
        {
            if (length + count > data.Length)
            {
                if (length + count > MaxHeader)
                {
                    throw input.Corrupt(input.Position, "The segments before the first scan are larger than 16 MB.");
                }

                int grown = Math.Max(data.Length * 2, length + count);
                input.CheckMemory(grown);
                Array.Resize(ref data, grown);
            }

            input.ReadExactly(data.AsSpan(length, count));
            length += count;
        }
    }

    /// <summary>What a loaded pixel is made of: the layout, and how the components and any mapping table fill it.</summary>
    private sealed class Plan
    {
        public PixelLayoutInfo Layout { get; private init; }

        public bool Wide { get; private init; }

        /// <summary>Gets the frame components that give the channels, in channel order; for a palette, the one index component.</summary>
        public int Channels { get; private init; }

        /// <summary>Gets the palette, when one component indexes a mapping table of whole colours.</summary>
        public JpegLsMappingTable? Palette { get; private init; }

        public static Plan For(JpegLsHeader header, JpegLsScanDecoder scan, Action<string, string> warn)
        {
            int count = header.Components.Length;
            if (count == 1 && header.Components[0].Table != 0)
            {
                if (header.Tables.TryGetValue(header.Components[0].Table, out JpegLsMappingTable? table))
                {
                    PixelLayoutInfo? layout = table.EntryBytes switch
                    {
                        1 => Gray8.Layout,
                        2 => Gray16.Layout,
                        3 => Rgb8.Layout,
                        4 => Rgba8.Layout,
                        6 => Rgb16.Layout,
                        8 => Rgba16.Layout,
                        _ => null,
                    };
                    if (layout is { } entry)
                    {
                        return new Plan { Layout = entry, Wide = entry.BitsPerChannel > 8, Channels = 1, Palette = table };
                    }

                    warn("TableIgnored", $"Mapping table {table.Id} has {table.EntryBytes}-byte entries, which are not a gray or colour value; the indices are shown as gray.");
                }
                else
                {
                    warn("TableMissing", $"Mapping table {header.Components[0].Table} is not in the file; the indices are shown as gray.");
                }
            }
            else if (header.Components.Any(c => c.Table != 0))
            {
                warn("TableIgnored", "Mapping tables of images with more than one component are not applied; the samples are shown as stored.");
            }

            if (count > 4)
            {
                warn("ComponentsSkipped", $"The image has {count} components; the first 4 were read.");
            }

            bool wide = header.Bits > 8;
            PixelLayoutInfo plain = Math.Min(count, 4) switch
            {
                1 => wide ? Gray16.Layout : Gray8.Layout,
                2 => wide ? GrayAlpha16.Layout : GrayAlpha8.Layout,
                3 => wide ? Rgb16.Layout : Rgb8.Layout,
                _ => wide ? Rgba16.Layout : Rgba8.Layout,
            };
            return new Plan { Layout = plain, Wide = wide, Channels = Math.Min(count, 4) };
        }
    }

    /// <summary>
    /// Keeps the lines the decoder gives: the latest line of each component as it came (<see cref="Latest"/>), or whole planes.
    /// </summary>
    private sealed class Store : JpegLsLineSink
    {
        private readonly JpegLsHeader _header;
        private readonly ushort[][]? _planes;
        private readonly int[][]? _latest;

        public Store(JpegLsHeader header, bool planes)
        {
            _header = header;
            if (planes)
            {
                _planes = [.. header.Components.Select(c => new ushort[(long)c.Width * c.Height])];
            }
            else
            {
                _latest = [.. header.Components.Select(c => new int[c.Width])];
            }
        }

        public override void Line(int component, int y, ReadOnlySpan<int> samples)
        {
            if (_latest is not null)
            {
                samples.CopyTo(_latest[component]);
                return;
            }

            int width = _header.Components[component].Width;
            Span<ushort> target = _planes![component].AsSpan(y * width, width);
            for (int x = 0; x < samples.Length; x++)
            {
                target[x] = (ushort)samples[x];
            }
        }

        /// <summary>Returns the latest line of a component, when lines are not kept as planes.</summary>
        public ReadOnlySpan<int> Latest(int component) => _latest![component];

        /// <summary>Returns line <paramref name="y"/> of a component's plane.</summary>
        public ReadOnlySpan<ushort> Plane(int component, int y)
        {
            JpegLsComponent c = _header.Components[component];
            return _planes![component].AsSpan(y * c.Width, c.Width);
        }

        /// <summary>Copies line <paramref name="y"/> of a component (planes), or its latest line, into <paramref name="target"/>.</summary>
        public void Row(int component, int y, Span<ushort> target)
        {
            if (_latest is not null)
            {
                ReadOnlySpan<int> line = _latest[component];
                for (int x = 0; x < line.Length; x++)
                {
                    target[x] = (ushort)line[x];
                }

                return;
            }

            JpegLsComponent c = _header.Components[component];
            _planes![component].AsSpan(y * c.Width, c.Width).CopyTo(target);
        }
    }

    private sealed class Rows<TNatural> : ExtendedRows<TNatural, TNatural>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly JpegLsDecoder _decoder;
        private readonly JpegLsHeader _header;
        private readonly Plan _plan;
        private readonly Store _store;
        private readonly ushort[] _scale;
        private readonly int[][] _columns;
        private readonly int _shift;
        private readonly ushort[] _gather;
        private readonly ushort[] _line;
        private readonly bool _direct;
        private bool _decoded;
        private bool _failed;
        private bool _indexMissing;

        public Rows(EgamiInput input, ReaderOptions options, JpegLsDecoder decoder, Plan plan, List<(string Name, string Message)> warnings)
            : base(input, options, JpegLsFormat.Info, "JpegLs", decoder.Header.Width, decoder.Header.Height)
        {
            _decoder = decoder;
            _header = decoder.Header;
            _plan = plan;
            _shift = decoder.FirstScan.PointTransform;
            if (!decoder.Streams)
            {
                input.CheckMemory(_header.Components.Sum(c => (long)c.Width * c.Height * 2));
            }

            _store = new Store(_header, !decoder.Streams);
            int max = (1 << _header.Bits) - 1;
            int target = plan.Wide ? 65535 : 255;
            _scale = new ushort[max + 1];
            for (int v = 0; v <= max; v++)
            {
                _scale[v] = (ushort)((((long)v * target) + (max >> 1)) / max);
            }

            _columns = new int[_header.Components.Length][];
            for (int c = 0; c < _columns.Length; c++)
            {
                JpegLsComponent component = _header.Components[c];
                if (component.H != _header.HMax)
                {
                    _columns[c] = [.. Enumerable.Range(0, Width).Select(x => (int)((long)x * component.H / _header.HMax))];
                }
            }

            _gather = new ushort[Width * Math.Max(3, plan.Channels)];
            _line = new ushort[_header.Components.Max(c => c.Width)];

            // Whole rows at the pixel's own depth, untransformed: the decoded lines go straight into the pixels.
            _direct = !_header.Subsampled && plan.Palette is null && _header.Transform == JpegLsColorTransform.None && _shift == 0
                && _header.Bits == (plan.Wide ? 16 : 8);
            Metadata.FormatDetails = new JpegLsDetails
            {
                Near = decoder.FirstScan.Near,
                Interleave = (JpegLsInterleave)decoder.FirstScan.Interleave,
                ColorTransform = _header.Transform,
                BitsPerSample = _header.Bits,
                Components = _header.Components.Length,
                Palettized = plan.Palette is not null,
            };
            foreach ((string name, string message) in warnings)
            {
                Warn(name, message);
            }
        }

        public override RasterAccess Access => _decoder.Streams ? RasterAccess.Streamed : RasterAccess.Buffered;

        public override long WorkingBytes => base.WorkingBytes + JpegLsFormatReader.WorkingBytes(_header, _decoder.Streams);

        protected override bool DecodeRow(int y, Span<TNatural> row)
        {
            if (_failed)
            {
                return false;
            }

            try
            {
                if (_decoder.Streams)
                {
                    _decoder.NextRow(_store, Strict);
                }
                else if (!_decoded)
                {
                    _decoded = true;
                    _decoder.DecodeAll(_store, Strict);
                }
            }
            catch (JpegLsDataException e)
            {
                if (Strict)
                {
                    throw Input.Corrupt(Input.Position, e.Message);
                }

                _failed = true;
                Warn("Damaged", $"{e.Message} The rows from {y} on are left transparent black.");
                if (_decoder.Streams)
                {
                    row.Clear();
                    return false;
                }
            }

            Emit(y, row);
            return true;
        }

        private void Emit(int y, Span<TNatural> row)
        {
            int width = Width;
            if (_plan.Palette is { } palette)
            {
                EmitPalette(palette, Component(0, y), row);
                return;
            }

            int channels = _plan.Channels;
            if (_direct)
            {
                EmitDirect(y, row, channels);
                return;
            }

            Span<ushort> gather = _gather.AsSpan(0, width * Math.Max(channels, 3));
            for (int c = 0; c < channels; c++)
            {
                ReadOnlySpan<ushort> source = Component(c, y);
                int[]? columns = _columns[c];
                for (int x = 0, at = c; x < width; x++, at += channels)
                {
                    gather[at] = source[columns is null ? x : columns[x]];
                }
            }

            if (_header.Transform != JpegLsColorTransform.None && channels >= 3)
            {
                JpegLsColorTransforms.Inverse(_header.Transform, gather, channels, width, _header.Bits - _shift);
            }

            // Scans of one file may differ in their point transforms; a sample beyond the range is clamped to it.
            ushort[] scale = _scale;
            int shift = _shift;
            int top = scale.Length - 1;
            int samples = width * channels;
            if (_plan.Wide)
            {
                Span<ushort> target = MemoryMarshal.Cast<TNatural, ushort>(row);
                for (int i = 0; i < samples; i++)
                {
                    target[i] = scale[Math.Min(gather[i] << shift, top)];
                }
            }
            else
            {
                Span<byte> target = MemoryMarshal.AsBytes(row);
                for (int i = 0; i < samples; i++)
                {
                    target[i] = (byte)scale[Math.Min(gather[i] << shift, top)];
                }
            }
        }

        private void EmitDirect(int y, Span<TNatural> row, int channels)
        {
            int width = Width;
            if (!_decoder.Streams)
            {
                // Whole planes, decoded first.
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<ushort> source = _store.Plane(c, y);
                    if (_plan.Wide)
                    {
                        Span<ushort> target = MemoryMarshal.Cast<TNatural, ushort>(row);
                        for (int x = 0, at = c; x < source.Length; x++, at += channels)
                        {
                            target[at] = source[x];
                        }
                    }
                    else
                    {
                        Span<byte> target = MemoryMarshal.AsBytes(row);
                        for (int x = 0, at = c; x < source.Length; x++, at += channels)
                        {
                            target[at] = (byte)source[x];
                        }
                    }
                }

                return;
            }

            if (_plan.Wide)
            {
                Span<ushort> target = MemoryMarshal.Cast<TNatural, ushort>(row);
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<int> source = _store.Latest(c)[..width];
                    for (int x = 0, at = c; x < source.Length; x++, at += channels)
                    {
                        target[at] = (ushort)source[x];
                    }
                }
            }
            else
            {
                Span<byte> target = MemoryMarshal.AsBytes(row);
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<int> source = _store.Latest(c)[..width];
                    for (int x = 0, at = c; x < source.Length; x++, at += channels)
                    {
                        target[at] = (byte)source[x];
                    }
                }
            }
        }

        private ReadOnlySpan<ushort> Component(int c, int y)
        {
            JpegLsComponent component = _header.Components[c];
            int line = _decoder.Streams ? y : (int)((long)y * component.V / _header.VMax);
            Span<ushort> target = _line.AsSpan(0, component.Width);
            _store.Row(c, line, target);
            return target;
        }

        private void EmitPalette(JpegLsMappingTable palette, ReadOnlySpan<ushort> indices, Span<TNatural> row)
        {
            Span<byte> target = MemoryMarshal.AsBytes(row);
            int size = palette.EntryBytes;
            int count = palette.Count;
            bool wide = _plan.Wide;
            bool missing = false;
            for (int x = 0; x < Width; x++)
            {
                int index = indices[x] << _shift;
                if (index >= count)
                {
                    missing = true;
                    target.Slice(x * size, size).Clear();
                    continue;
                }

                ReadOnlySpan<byte> entry = palette.Entries.AsSpan(index * size, size);
                if (!wide)
                {
                    entry.CopyTo(target[(x * size)..]);
                }
                else
                {
                    // Big-endian 16-bit values, as the table stores them, into native ushorts.
                    Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(target.Slice(x * size, size));
                    for (int i = 0; i < words.Length; i++)
                    {
                        words[i] = BinaryPrimitives.ReadUInt16BigEndian(entry[(2 * i)..]);
                    }
                }
            }

            if (missing && !_indexMissing)
            {
                _indexMissing = true;
                Damaged("IndexMissing", $"Some samples index past the {count} entries of mapping table {palette.Id}; they are left black.");
            }
        }
    }
}
