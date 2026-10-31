using System.Buffers;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Sensor data as decoded: <see cref="Samples"/> values per pixel, row by row, as 16-bit integers or as floats. The memory comes
/// from the load's scratch pool and goes back on <see cref="Dispose"/>.
/// </summary>
internal sealed class RawPixels : IDisposable
{
    private IMemoryOwner<byte>? _memory;

    public RawPixels(EgamiInput input, int width, int height, int samples, bool isFloat)
    {
        long bytes = (long)width * height * samples * (isFloat ? 4 : 2);
        input.CheckMemory(bytes);
        if (bytes > Array.MaxLength)
        {
            throw new NotSupportedException($"The sensor data needs {bytes} bytes, more than one buffer can hold.");
        }

        _memory = input.RentScratch((int)bytes, ClearMode.Zero);
        Width = width;
        Height = height;
        Samples = samples;
        IsFloat = isFloat;
    }

    public int Width { get; }

    public int Height { get; }

    public int Samples { get; }

    public bool IsFloat { get; }

    /// <summary>Gets the values per row.</summary>
    public int Stride => Width * Samples;

    public long Bytes => (long)Width * Height * Samples * (IsFloat ? 4 : 2);

    public Span<ushort> U16 => MemoryMarshal.Cast<byte, ushort>(Memory.Span)[..(Stride * Height)];

    public Span<float> F32 => MemoryMarshal.Cast<byte, float>(Memory.Span)[..(Stride * Height)];

    public Span<ushort> U16Row(int y) => MemoryMarshal.Cast<byte, ushort>(Memory.Span).Slice(y * Stride, Stride);

    public Span<float> F32Row(int y) => MemoryMarshal.Cast<byte, float>(Memory.Span).Slice(y * Stride, Stride);

    private Memory<byte> Memory => (_memory ?? throw new ObjectDisposedException(nameof(RawPixels))).Memory;

    public void Dispose()
    {
        Interlocked.Exchange(ref _memory, null)?.Dispose();
    }
}
