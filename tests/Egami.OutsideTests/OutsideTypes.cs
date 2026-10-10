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

/// <summary>
/// A packed 10-10-10-2 pixel declared as <see cref="PixelChannelType.Packed"/>. It relies on the default 16-bit members, so
/// conversions to and from <see cref="Rgba16"/> reach it through its scaled values.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Rgb30 : IPixelType<Rgb30>
{
    public Rgb30(uint value)
    {
        Value = value;
    }

    public uint Value { get; }

    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Packed, AlphaKind.Straight, 32, 10, 10, 10, 2);

    public static Rgb30 FromScaled(Vector4 rgba)
    {
        uint r = (uint)MathF.Round(Math.Clamp(rgba.X, 0, 1) * 1023);
        uint g = (uint)MathF.Round(Math.Clamp(rgba.Y, 0, 1) * 1023);
        uint b = (uint)MathF.Round(Math.Clamp(rgba.Z, 0, 1) * 1023);
        uint a = (uint)MathF.Round(Math.Clamp(rgba.W, 0, 1) * 3);
        return new Rgb30(r | (g << 10) | (b << 20) | (a << 30));
    }

    public static Rgb30 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    public Vector4 ToScaled()
    {
        return new Vector4((Value & 1023) / 1023f, ((Value >> 10) & 1023) / 1023f, ((Value >> 20) & 1023) / 1023f, (Value >> 30) / 3f);
    }

    public Rgba8 ToRgba8()
    {
        return Rgba8.FromScaled(ToScaled());
    }

    public bool Equals(Rgb30 other)
    {
        return Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is Rgb30 other && Equals(other);
    }

    public override int GetHashCode()
    {
        return (int)Value;
    }

    public static bool operator ==(Rgb30 left, Rgb30 right) => left.Equals(right);

    public static bool operator !=(Rgb30 left, Rgb30 right) => !left.Equals(right);
}

/// <summary>
/// An 8-bit RGBA pixel stored premultiplied. Its conversions take and return straight values, as the contract says, so
/// Egami must never treat its bytes as straight RGBA.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PremultipliedRgba : IPixelType<PremultipliedRgba>
{
    public PremultipliedRgba(byte r, byte g, byte b, byte a)
    {
        (R, G, B, A) = (r, g, b, a);
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public byte A { get; }

    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Unorm8, AlphaKind.Premultiplied);

    public static PremultipliedRgba FromScaled(Vector4 rgba)
    {
        return FromRgba8(Rgba8.FromScaled(rgba));
    }

    public static PremultipliedRgba FromRgba8(Rgba8 value)
    {
        return new PremultipliedRgba(Times(value.R, value.A), Times(value.G, value.A), Times(value.B, value.A), value.A);
    }

    public Vector4 ToScaled()
    {
        return ToRgba8().ToScaled();
    }

    public Rgba8 ToRgba8()
    {
        return A == 0 ? default : new Rgba8(Over(R, A), Over(G, A), Over(B, A), A);
    }

    public bool Equals(PremultipliedRgba other)
    {
        return (R, G, B, A) == (other.R, other.G, other.B, other.A);
    }

    public override bool Equals(object? obj)
    {
        return obj is PremultipliedRgba other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    public static bool operator ==(PremultipliedRgba left, PremultipliedRgba right) => left.Equals(right);

    public static bool operator !=(PremultipliedRgba left, PremultipliedRgba right) => !left.Equals(right);

    private static byte Times(byte c, byte a) => (byte)(((c * a) + 127) / 255);

    private static byte Over(byte c, byte a) => (byte)Math.Min(255, ((c * 255) + (a / 2)) / a);
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
