using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Egami.Pixels;
using Egami.Video.Audio;

namespace Egami.Video.Cli.Host;

/// <summary>
/// Hashes decoded frames in the layout FFmpeg's framemd5 hashes raw output with: video as its planes Y, U, V and alpha one after
/// another, rows without padding, 8-bit samples as bytes and deeper samples as 16-bit little-endian; audio as interleaved
/// little-endian PCM in the smallest of 8 (unsigned), 16, 24 and 32 bits that holds the frame's valid bits, filled from the top
/// as FFmpeg's decoders fill them, or as 32- or 64-bit floats.
/// </summary>
internal static class CheckHashing
{
    /// <summary>Creates the incremental hash for an algorithm name (md5 or sha256).</summary>
    public static IncrementalHash Create(string algorithm) => algorithm switch
    {
#pragma warning disable CA5351 // A content checksum to compare with FFmpeg's framemd5, not security.
        "md5" => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
#pragma warning restore CA5351
        _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
    };

    /// <summary>Appends a frame's planes to the hashes and returns how many bytes they were.</summary>
    public static long AppendPlanes(YuvRaster raster, IncrementalHash frame, IncrementalHash total)
    {
        long size = 0;
        if (raster is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte>? plane in (ReadOnlySpan<YuvPlane<byte>?>)[bytes.Y, bytes.U, bytes.V, bytes.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    Span<byte> row = plane.Row(y);
                    frame.AppendData(row);
                    total.AppendData(row);
                    size += row.Length;
                }
            }
        }
        else if (raster is YuvRaster<ushort> words)
        {
            byte[]? swapped = null;
            foreach (YuvPlane<ushort>? plane in (ReadOnlySpan<YuvPlane<ushort>?>)[words.Y, words.U, words.V, words.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(plane.Row(y));
                    if (!BitConverter.IsLittleEndian)
                    {
                        swapped ??= new byte[row.Length];
                        Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(swapped.AsSpan(0, row.Length));
                        BinaryPrimitives.ReverseEndianness(plane.Row(y), target);
                        row = swapped.AsSpan(0, row.Length);
                    }

                    frame.AppendData(row);
                    total.AppendData(row);
                    size += row.Length;
                }
            }
        }
        else
        {
            throw new NotSupportedException($"Frames with {raster.GetType().Name} samples cannot be hashed.");
        }

        return size;
    }

    /// <summary>The bytes per sample an audio frame is hashed with: 1 to 4 for integers, 4 or 8 for floats, 8 for 64-bit integers.</summary>
    public static int Width(AudioFrame frame)
    {
        SampleFormat format = frame.Format.ToInterleaved();
        if (format is SampleFormat.F32 or SampleFormat.F64 or SampleFormat.S64 or SampleFormat.U8)
        {
            return format.BytesPerSample();
        }

        int valid = frame.ValidBits > 0 ? frame.ValidBits : format.BitsPerSample();
        return valid <= 8 ? 1 : valid <= 16 ? 2 : valid <= 24 ? 3 : 4;
    }

    /// <summary>The FFmpeg PCM codec name the hashed audio bytes match, such as pcm_s16le.</summary>
    public static string PcmName(AudioFrame frame)
    {
        SampleFormat format = frame.Format.ToInterleaved();
        return format switch
        {
            SampleFormat.F32 => "pcm_f32le",
            SampleFormat.F64 => "pcm_f64le",
            SampleFormat.S64 => "pcm_s64le",
            _ => Width(frame) switch
            {
                1 => "pcm_u8",
                2 => "pcm_s16le",
                3 => "pcm_s24le",
                _ => "pcm_s32le",
            },
        };
    }

    /// <summary>Appends an audio frame's samples to the hashes and returns how many bytes they were.</summary>
    public static long AppendSamples(AudioFrame frame, IncrementalHash hash, IncrementalHash total)
    {
        SampleFormat format = frame.Format.ToInterleaved();
        bool planar = frame.Format.IsPlanar();
        int channels = frame.ChannelCount;
        int samples = frame.SampleCount;
        int width = Width(frame);
        int sourceBytes = format.BytesPerSample();
        int valid = frame.ValidBits > 0 ? frame.ValidBits : format.BitsPerSample();
        int shift = (width * 8) - valid;
        int length = checked(samples * channels * width);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            Span<byte> output = buffer.AsSpan(0, length);
            int at = 0;
            for (int i = 0; i < samples; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<byte> source = planar
                        ? frame.GetBytes(c).Slice(i * sourceBytes, sourceBytes)
                        : frame.GetBytes().Slice(((i * channels) + c) * sourceBytes, sourceBytes);
                    Span<byte> target = output.Slice(at, width);
                    at += width;
                    switch (format)
                    {
                        case SampleFormat.U8:
                        case SampleFormat.F32:
                        case SampleFormat.F64:
                        case SampleFormat.S64:
                            Copy(source, target);
                            break;
                        default:
                            long value = format == SampleFormat.S16 ? BitConverter.ToInt16(source) : BitConverter.ToInt32(source);
                            value <<= shift;
                            if (width == 1)
                            {
                                target[0] = (byte)(value + 128);
                            }
                            else
                            {
                                for (int b = 0; b < width; b++)
                                {
                                    target[b] = (byte)(value >> (8 * b));
                                }
                            }

                            break;
                    }
                }
            }

            hash.AppendData(output);
            total.AppendData(output);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies a sample's bytes as little-endian.</summary>
    private static void Copy(ReadOnlySpan<byte> source, Span<byte> target)
    {
        source.CopyTo(target);
        if (!BitConverter.IsLittleEndian)
        {
            target.Reverse();
        }
    }
}
