using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Formats.Heif;

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
        foreach (ReadOnlyMemory<byte> unit in HevcNal.SplitLengthPrefixed(data, config.LengthSize))
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

        // The planes inside the conformance window, as HM writes them.
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
}
