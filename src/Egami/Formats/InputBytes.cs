using System.Buffers;

namespace Egami.Formats;

/// <summary>Reads a whole input into memory, for formats that need random access to all of it (JBIG2, JPEG 2000).</summary>
internal static class InputBytes
{
    /// <summary>Reads the input to its end, counting the bytes against <see cref="SafetyLimits.MaxTotalBytes"/>.</summary>
    internal static (byte[] Data, int Length, bool Pooled) ReadAll(EgamiInput input)
    {
        long max = Math.Min(input.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= max)
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, length + 1L);
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(max, (long)buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int n = input.Read(buffer.AsSpan(length, (int)Math.Min(buffer.Length, max) - length));
                if (n == 0)
                {
                    return (buffer, length, true);
                }

                length += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}
