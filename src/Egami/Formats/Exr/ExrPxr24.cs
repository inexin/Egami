using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Exr;

/// <summary>
/// PXR24: per line and channel, the samples' bytes split into planes (float samples cut to their top 24 bits) and stored as
/// differences from the sample before, then deflated. Translated from OpenEXR's internal_pxr24.c (BSD-3-Clause; see
/// THIRD-PARTY-NOTICES).
/// </summary>
internal static class ExrPxr24
{
    public static bool Decompress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination, Span<byte> scratch)
    {
        int inflated = ExrCodec.InflateUpTo(source, scratch[..destination.Length]);
        if (inflated < 0)
        {
            return false;
        }

        ref byte input = ref MemoryMarshal.GetReference(scratch);
        ref byte output = ref MemoryMarshal.GetReference(destination);
        int inPos = 0, outPos = 0;
        ExrChannelInfo[] channels = block.Part.Channels;
        for (int y = block.Y0; y <= block.Y1; y++)
        {
            foreach (ExrChannelInfo channel in channels)
            {
                if (y % channel.YSampling != 0)
                {
                    continue;
                }

                int width = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling);
                int planes = channel.Type switch { ExrPixelType.UInt => 4, ExrPixelType.Half => 2, _ => 3 };
                if (inPos + (planes * width) > inflated || outPos + (width * channel.Bytes) > destination.Length)
                {
                    return false;
                }

                ref byte p0 = ref Unsafe.Add(ref input, inPos);
                ref byte p1 = ref Unsafe.Add(ref p0, width);
                ref byte p2 = ref Unsafe.Add(ref p1, width);
                ref byte p3 = ref Unsafe.Add(ref p2, width);
                uint pixel = 0;
                switch (channel.Type)
                {
                    case ExrPixelType.Half:
                        ref ushort halves = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref output, outPos));
                        for (int x = 0; x < width; x++)
                        {
                            pixel += ((uint)Unsafe.Add(ref p0, x) << 8) | Unsafe.Add(ref p1, x);
                            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref halves, x)), BitConverter.IsLittleEndian ? (ushort)pixel : BinaryPrimitives.ReverseEndianness((ushort)pixel));
                        }

                        break;
                    case ExrPixelType.UInt:
                        for (int x = 0; x < width; x++)
                        {
                            pixel += ((uint)Unsafe.Add(ref p0, x) << 24) | ((uint)Unsafe.Add(ref p1, x) << 16) | ((uint)Unsafe.Add(ref p2, x) << 8) | Unsafe.Add(ref p3, x);
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outPos + (4 * x)), BitConverter.IsLittleEndian ? pixel : BinaryPrimitives.ReverseEndianness(pixel));
                        }

                        break;
                    default:
                        for (int x = 0; x < width; x++)
                        {
                            pixel += ((uint)Unsafe.Add(ref p0, x) << 24) | ((uint)Unsafe.Add(ref p1, x) << 16) | ((uint)Unsafe.Add(ref p2, x) << 8);
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outPos + (4 * x)), BitConverter.IsLittleEndian ? pixel : BinaryPrimitives.ReverseEndianness(pixel));
                        }

                        break;
                }

                inPos += planes * width;
                outPos += width * channel.Bytes;
            }
        }

        return outPos == destination.Length;
    }
}
