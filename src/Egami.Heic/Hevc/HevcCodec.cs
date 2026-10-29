using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Formats.Heif;
using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// HEIC's codec behind the HEIF image layer: an hvc1 item's parameter sets (from hvcC) and data decoded by <see cref="HevcDecoder"/>,
/// cropped to the SPS's conformance window, as planes. It keeps no state between items, so a grid's cells decode in parallel.
/// </summary>
internal sealed class HevcCodec : IHeifCodec, IHevcSampleCodec
{
    private readonly Lock _lock = new();
    private ColorCodePoints _codePoints = new(2, 2, 2, false);

    /// <summary>Gets the color the SPS's VUI of the last item decoded gives (unspecified, limited range, when it has none).</summary>
    public ColorCodePoints CodePoints
    {
        get
        {
            lock (_lock)
            {
                return _codePoints;
            }
        }
    }

    public bool Concurrent => true;

    public HeifPlanes Decode(HeifImage file, HeifItem item, EgamiInput input, int parallelism)
    {
        HeifFile heif = file.Heif;
        if (heif.Property(item, HeicFile.HvcC) is not HeifProperty property)
        {
            throw input.Corrupt(-1, $"Item {item.Id} has no hvcC property.");
        }

        return Decode(HevcConfig.Read(heif, property), heif.ItemData(item, input.Limits), input, parallelism);
    }

    /// <summary>Decodes a sample or item's data (length-prefixed NAL units) after the configuration's parameter sets.</summary>
    public HeifPlanes Decode(HevcConfig config, ReadOnlyMemory<byte> data, EgamiInput input, int parallelism)
    {
        List<HevcNal> nals = [.. config.NalUnits.Select(n => HevcNal.Parse(n.Span))];
        foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitLengthPrefixed(data, config.LengthSize))
        {
            nals.Add(HevcNal.Parse(unit.Span));
        }

        HevcPicture picture = HevcDecoder.Decode(nals, input.Limits, out _, parallelism, input.CancellationToken);
        HevcSps sps = picture.Sps;
        if (sps.ChromaArrayType != 0 && sps.BitDepthLuma != sps.BitDepthChroma)
        {
            throw HevcErrors.Unsupported($"The item codes luma at {sps.BitDepthLuma} bits and chroma at {sps.BitDepthChroma}.");
        }

        if (sps.Vui is HevcVui vui)
        {
            lock (_lock)
            {
                _codePoints = new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange);
            }
        }

        return Planes(picture);
    }

    public IHevcTrackDecoder OpenTrack(HevcConfig config, EgamiInput input, int parallelism) => new TrackDecoder(this, config, input, parallelism);

    /// <summary>Keeps the color the SPS's VUI gives, when it has one.</summary>
    private void TakeColor(HevcSps sps)
    {
        if (sps.Vui is HevcVui vui)
        {
            lock (_lock)
            {
                _codePoints = new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange);
            }
        }
    }

    /// <summary>The planes inside the conformance window, as HM writes them.</summary>
    private static HeifPlanes Planes(HevcPicture picture)
    {
        HevcSps sps = picture.Sps;
        int planes = sps.ChromaArrayType == 0 ? 1 : 3;
        ushort[][] samples = new ushort[planes][];
        int[] strides = new int[planes];
        for (int c = 0; c < planes; c++)
        {
            int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
            int left = sps.Conformance.Left / sx, top = sps.Conformance.Top / sy;
            int width = sps.OutputWidth / sx, height = sps.OutputHeight / sy;
            ushort[] source = picture.Planes[c];
            int stride = picture.Widths[c];
            if (left == 0 && top == 0 && width == stride && height == picture.Heights[c])
            {
                samples[c] = source;
            }
            else
            {
                samples[c] = new ushort[width * height];
                for (int y = 0; y < height; y++)
                {
                    source.AsSpan(((top + y) * stride) + left, width).CopyTo(samples[c].AsSpan(y * width));
                }
            }

            strides[c] = width;
        }

        return new HeifPlanes(sps.OutputWidth, sps.OutputHeight, sps.BitDepthLuma, sps.SubWidthC - 1, sps.SubHeightC - 1, planes == 1, samples, strides);
    }

    /// <summary>A track's samples through <see cref="HevcSequenceDecoder"/>, after its configuration's parameter sets.</summary>
    private sealed class TrackDecoder : IHevcTrackDecoder
    {
        private readonly HevcSequenceDecoder _decoder;
        private readonly HevcConfig _config;
        private readonly EgamiInput _input;
        private readonly List<(HevcDpbPicture Picture, int Poc)> _output = [];
        private readonly Queue<HevcPicture> _pictures = new();

        private readonly HevcCodec _codec;

        internal TrackDecoder(HevcCodec codec, HevcConfig config, EgamiInput input, int parallelism)
        {
            _codec = codec;
            _config = config;
            _input = input;
            _decoder = new HevcSequenceDecoder(input.Limits, parallelism);
            foreach (ReadOnlyMemory<byte> unit in config.NalUnits)
            {
                _decoder.Send(HevcNal.Parse(unit.Span), _output, input.CancellationToken);
            }
        }

        public void Send(ReadOnlyMemory<byte> sample)
        {
            foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitLengthPrefixed(sample, _config.LengthSize))
            {
                _decoder.Send(HevcNal.Parse(unit.Span), _output, _input.CancellationToken);
            }

            Collect();
        }

        public void Flush()
        {
            _decoder.Flush(_output, _input.CancellationToken);
            Collect();
        }

        public bool TryTake(out HeifPlanes planes)
        {
            if (_pictures.TryDequeue(out HevcPicture? picture))
            {
                _codec.TakeColor(picture.Sps);
                planes = Planes(picture);
                return true;
            }

            planes = null!;
            return false;
        }

        private void Collect()
        {
            foreach ((HevcDpbPicture picture, _) in _output)
            {
                _pictures.Enqueue(picture.Picture);
            }

            _output.Clear();
        }
    }
}
