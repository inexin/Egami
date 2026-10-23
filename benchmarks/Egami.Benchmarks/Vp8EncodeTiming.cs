using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The 5.2 VP8 encoder speed gate: encodes a raw 4:2:0 clip with <see cref="Vp8StreamEncoder"/> at a fixed quantizer and effort,
/// the first frame a key frame and the rest inter frames against the last, writes the stream as IVF and prints its size, the
/// mean luma PSNR of the frames and the best encode time per frame of the runs (and the processor time, steadier on a busy
/// machine), after a warm-up (tools/time-vp8-encoder.py drives it).
/// </summary>
internal static class Vp8EncodeTiming
{
    internal static void Run(string input, int width, int height, int frames, int quantizer, int effort, int threads, string output, int runs)
    {
        byte[] raw = File.ReadAllBytes(input);
        int cw = (width + 1) / 2, ch = (height + 1) / 2, size = (width * height) + (2 * cw * ch);
        frames = Math.Min(frames, raw.Length / size);
        EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
        using YuvRaster<byte> frame = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        double best = double.MaxValue, bestCpu = double.MaxValue;
        List<byte[]> packets = [];
        double psnr = 0;

        // A first, untimed run of a few frames, so the timed runs see Tier-1 code.
        for (int run = -1; run < runs; run++)
        {
            using Vp8StreamEncoder encoder = new(width, height, context);
            packets.Clear();
            psnr = 0;
            long ticks = 0;
            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
            for (int i = 0; i < (run < 0 ? Math.Min(frames, 8) : frames); i++)
            {
                int at = i * size;
                for (int y = 0; y < height; y++)
                {
                    raw.AsSpan(at + (y * width), width).CopyTo(frame.Y.Row(y));
                }

                at += width * height;
                for (int y = 0; y < ch; y++)
                {
                    raw.AsSpan(at + (y * cw), cw).CopyTo(frame.U!.Row(y));
                    raw.AsSpan(at + (cw * ch) + (y * cw), cw).CopyTo(frame.V!.Row(y));
                }

                long start = Stopwatch.GetTimestamp();
                Vp8EncodedFrame encoded = encoder.Encode(frame, new Vp8FramePlan { Quantizer = quantizer, Effort = effort });
                ticks += Stopwatch.GetTimestamp() - start;
                packets.Add(encoded.Data.ToArray());
                psnr += Math.Min(encoded.LumaPsnr, 99);
            }

            if (run >= 0)
            {
                best = Math.Min(best, ticks * 1000.0 / Stopwatch.Frequency / frames);
                bestCpu = Math.Min(bestCpu, (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / frames);
            }
        }

        long bytes = WriteIvf(output, width, height, packets);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes, {psnr / frames:F4} dB, {best:F3} ms a frame on {threads} thread{(threads == 1 ? "" : "s")}, {bestCpu:F1} ms of processor time"));
    }

    private static long WriteIvf(string path, int width, int height, List<byte[]> packets)
    {
        using FileStream file = File.Create(path);
        byte[] header = new byte[32];
        "DKIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 32);
        "VP80"u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)packets.Count);
        file.Write(header);
        long bytes = 0;
        byte[] frameHeader = new byte[12];
        for (int i = 0; i < packets.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(frameHeader, packets[i].Length);
            BinaryPrimitives.WriteInt64LittleEndian(frameHeader.AsSpan(4), i);
            file.Write(frameHeader);
            file.Write(packets[i]);
            bytes += packets[i].Length;
        }

        return bytes;
    }
}
