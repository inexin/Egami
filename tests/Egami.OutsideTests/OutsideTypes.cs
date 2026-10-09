using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.OutsideTests;

/// <summary>A packed 5-6-5 pixel, a layout Egami does not know.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Rgb565 : IPixelType<Rgb565>
{
    public Rgb565(ushort value)
    {
        Value = value;
    }

    public ushort Value { get; }

    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Other, PixelChannelType.Other, AlphaKind.None);

    public static Rgb565 FromScaled(Vector4 rgba)
    {
        int r = (int)MathF.Round(Math.Clamp(rgba.X, 0, 1) * 31);
        int g = (int)MathF.Round(Math.Clamp(rgba.Y, 0, 1) * 63);
        int b = (int)MathF.Round(Math.Clamp(rgba.Z, 0, 1) * 31);
        return new Rgb565((ushort)((r << 11) | (g << 5) | b));
    }

    public static Rgb565 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    public Vector4 ToScaled()
    {
        return new Vector4((Value >> 11) / 31f, ((Value >> 5) & 63) / 63f, (Value & 31) / 31f, 1);
    }

    public Rgba8 ToRgba8()
    {
        return Rgba8.FromScaled(ToScaled());
    }

    public bool Equals(Rgb565 other)
    {
        return Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is Rgb565 other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Value;
    }

    public static bool operator ==(Rgb565 left, Rgb565 right) => left.Equals(right);

    public static bool operator !=(Rgb565 left, Rgb565 right) => !left.Equals(right);
}

/// <summary>A memory source that counts what it hands out, around the shared pool.</summary>
public sealed class CountingMemorySource : IPixelMemorySource
{
    private int _allocations;

    public int MaxBlockBytes => PooledPixelMemorySource.Shared.MaxBlockBytes;

    public int Allocations => _allocations;

    public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
    {
        Interlocked.Increment(ref _allocations);
        return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
    }
}

/// <summary>An operation that inverts color, in parallel bands, keeping alpha.</summary>
public sealed class InvertOperation : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> source = input.Source.Row(y);
                Span<TPixel> destination = input.Destination.Row(y);
                for (int x = 0; x < source.Length; x++)
                {
                    Vector4 v = source[x].ToScaled();
                    destination[x] = TPixel.FromScaled(new Vector4(1 - v.X, 1 - v.Y, 1 - v.Z, v.W));
                }
            }
        });
    }
}

/// <summary>An operation that halves the size by dropping every other pixel, to check that a size change updates metadata.</summary>
public sealed class HalveOperation : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return new Size(Math.Max(1, input.Width / 2), Math.Max(1, input.Height / 2));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < input.Destination.Height; y++)
        {
            Span<TPixel> source = input.Source.Row(Math.Min(2 * y, input.Source.Height - 1));
            Span<TPixel> destination = input.Destination.Row(y);
            for (int x = 0; x < destination.Length; x++)
            {
                destination[x] = source[Math.Min(2 * x, source.Length - 1)];
            }
        }
    }
}

/// <summary>Counts how many times it was disposed: the owner of wrapped memory.</summary>
public sealed class DisposalCounter : IDisposable
{
    public int Disposals { get; private set; }

    public void Dispose()
    {
        Disposals++;
    }
}

/// <summary>
/// A tiny single-frame format: "EGT1", width and height as big-endian 32-bit numbers, then RGBA8 rows. Probe, Load and Save all
/// go through Egami's pipeline.
/// </summary>
public static class TestFormat
{
    public static FormatInfo Info { get; } = new()
    {
        Name = "EGT1",
        Extensions = ["egt"],
        SignatureLength = 4,
        Matches = header => header.StartsWith("EGT1"u8),
        Reader = new Reader(),
        Writer = new Writer(),
    };

    private sealed class Reader : IFormatReader
    {
        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
        {
            (int width, int height) = Header(input);
            return new RasterInfo { Format = Info, Width = width, Height = height, Layout = Rgba8.Layout, FrameCount = 1, EstimatedBytes = 4L * width * height };
        }

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            (int width, int height) = Header(input);
            Raster<TPixel> raster = input.CreateRaster<TPixel>(width, height);
            Rgba8[] row = new Rgba8[width];
            for (int y = 0; y < height; y++)
            {
                if (input.Read(MemoryMarshal.AsBytes(row.AsSpan())) < row.Length * 4)
                {
                    input.Warn(raster, $"The data ends at row {y}.");
                    for (; y < height; y++)
                    {
                        raster.Row(y).Clear();
                    }

                    break;
                }

                PixelConvert.Convert<Rgba8, TPixel>(row, raster.Row(y));
            }

            return raster;
        }

        public Raster Read(EgamiInput input, ReaderOptions options)
        {
            return Read<Rgba8>(input, options);
        }

        private static (int Width, int Height) Header(EgamiInput input)
        {
            Span<byte> magic = stackalloc byte[4];
            input.ReadExactly(magic);
            return ((int)input.ReadUInt32BigEndian(), (int)input.ReadUInt32BigEndian());
        }
    }

    private sealed class Writer : IFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Span<byte> header = stackalloc byte[12];
            "EGT1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)raster.Width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)raster.Height);
            output.Write(header);
            Rgba8[] row = new Rgba8[raster.Width];
            for (int y = 0; y < raster.Height; y++)
            {
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), row);
                output.Write(MemoryMarshal.AsBytes(row.AsSpan()));
            }
        }
    }
}

/// <summary>
/// A tiny animated format: "EGA1", width, height and frame count as big-endian 32-bit numbers, then each frame's delay in
/// milliseconds and its RGBA8 rows. The reader builds frames with <see cref="EgamiInput.AddFrame"/> and checks the frame count first.
/// </summary>
public static class AnimatedTestFormat
{
    public static FormatInfo Info { get; } = new()
    {
        Name = "EGA1",
        Extensions = ["ega"],
        SignatureLength = 4,
        Matches = header => header.StartsWith("EGA1"u8),
        Reader = new Reader(),
        Writer = new Writer(),
    };

    private sealed class Reader : IFormatReader
    {
        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
        {
            (int width, int height, int frames) = Header(input);
            return new RasterInfo { Format = Info, Width = width, Height = height, Layout = Rgba8.Layout, FrameCount = frames, EstimatedBytes = 4L * width * height * frames };
        }

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            (int width, int height, int frames) = Header(input);
            input.CheckFrameCount(frames);
            Raster<TPixel> raster = input.CreateRaster<TPixel>(width, height);
            try
            {
                using IMemoryOwner<byte> scratch = input.RentScratch(width * 4);
                Span<Rgba8> row = MemoryMarshal.Cast<byte, Rgba8>(scratch.Memory.Span[..(width * 4)]);
                for (int f = 0; f < frames; f++)
                {
                    Frame<TPixel> frame = f == 0 ? raster.Frames[0] : input.AddFrame(raster);
                    frame.Metadata.Delay = TimeSpan.FromMilliseconds(input.ReadUInt32BigEndian());
                    for (int y = 0; y < height; y++)
                    {
                        input.ReadExactly(MemoryMarshal.AsBytes(row));
                        PixelConvert.Convert<Rgba8, TPixel>(row, frame.Row(y));
                    }
                }

                return raster;
            }
            catch
            {
                raster.Dispose();
                throw;
            }
        }

        public Raster Read(EgamiInput input, ReaderOptions options)
        {
            return Read<Rgba8>(input, options);
        }

        private static (int Width, int Height, int Frames) Header(EgamiInput input)
        {
            Span<byte> magic = stackalloc byte[4];
            input.ReadExactly(magic);
            return ((int)input.ReadUInt32BigEndian(), (int)input.ReadUInt32BigEndian(), (int)input.ReadUInt32BigEndian());
        }
    }

    private sealed class Writer : IFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Span<byte> header = stackalloc byte[16];
            "EGA1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)raster.Width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)raster.Height);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)raster.FrameCount);
            output.Write(header);
            Rgba8[] row = new Rgba8[raster.Width];
            Span<byte> delay = stackalloc byte[4];
            foreach (Frame<TPixel> frame in raster.Frames)
            {
                BinaryPrimitives.WriteUInt32BigEndian(delay, (uint)frame.Metadata.Delay.TotalMilliseconds);
                output.Write(delay);
                for (int y = 0; y < raster.Height; y++)
                {
                    PixelConvert.Convert<TPixel, Rgba8>(frame.Row(y), row);
                    output.Write(MemoryMarshal.AsBytes(row.AsSpan()));
                }
            }
        }
    }
}
