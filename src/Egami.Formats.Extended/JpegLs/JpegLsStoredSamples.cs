namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// Decodes a JPEG-LS codestream to its stored samples, for a container that applies its own pixel rules (DICOM): every
/// component at full size, interleaved, at the file's precision, with any colour transform undone and the point transform
/// shifted back, but no scaling and no mapping table.
/// </summary>
internal static class JpegLsStoredSamples
{
    /// <summary>Decodes <paramref name="data"/>; a lenient read keeps what decoded before damage and warns.</summary>
    /// <returns>The size, the component count and the samples.</returns>
    public static (int Width, int Height, int Components, int[] Samples) Decode(ReadOnlyMemory<byte> data, EgamiInput input, bool strict, Action<string, string> warn)
    {
        byte[] bytes = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Offset == 0
            ? segment.Array!
            : data.ToArray();
        int length = data.Length;
        JpegLsDecoder decoder;
        try
        {
            decoder = new JpegLsDecoder(bytes, length, warn);
        }
        catch (JpegLsDataException e)
        {
            throw input.Corrupt(0, e.Message);
        }

        JpegLsHeader header = decoder.Header;
        if (header.Subsampled)
        {
            throw new NotSupportedException("This JPEG-LS frame has subsampled components, which DICOM does not use.");
        }

        input.CheckFrameSize(header.Width, header.Height);
        int count = header.Components.Length;
        long total = (long)header.Width * header.Height * count;
        input.CheckMemory(total * sizeof(int));
        int[] samples = new int[total];
        int shift = decoder.FirstScan.PointTransform;
        Sink sink = new(samples, header.Width, count);
        try
        {
            decoder.DecodeAll(sink, strict);
        }
        catch (JpegLsDataException e)
        {
            if (strict)
            {
                throw input.Corrupt(0, e.Message);
            }

            warn("JpegLsDamaged", $"{e.Message} What decoded before was kept.");
        }

        if (header.Transform != JpegLsColorTransform.None && count >= 3)
        {
            ushort[] line = new ushort[header.Width * count];
            for (int y = 0; y < header.Height; y++)
            {
                Span<int> row = samples.AsSpan(y * header.Width * count, header.Width * count);
                for (int i = 0; i < row.Length; i++)
                {
                    line[i] = (ushort)row[i];
                }

                JpegLsColorTransforms.Inverse(header.Transform, line, count, header.Width, header.Bits - shift);
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = line[i];
                }
            }
        }

        if (shift > 0)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] <<= shift;
            }
        }

        return (header.Width, header.Height, count, samples);
    }

    /// <summary>Puts each decoded line into its component's place in the interleaved samples.</summary>
    private sealed class Sink(int[] samples, int width, int components) : JpegLsLineSink
    {
        public override void Line(int component, int y, ReadOnlySpan<int> line)
        {
            Span<int> target = samples.AsSpan(y * width * components);
            int n = Math.Min(line.Length, width);
            if (components == 1)
            {
                line[..n].CopyTo(target);
                return;
            }

            for (int x = 0, at = component; x < n; x++, at += components)
            {
                target[at] = line[x];
            }
        }
    }
}
