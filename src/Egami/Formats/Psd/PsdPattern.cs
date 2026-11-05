using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Psd;

/// <summary>
/// A pattern of the document's pattern table (the Patt, Pat2 and Pat3 blocks), as the specification's "Pattern" and "Virtual
/// Memory Array List" lay it out: its id, image mode and size, and where its channels' data is. <see cref="Decode"/> reads
/// its pixels.
/// </summary>
internal sealed class PsdPattern
{
    private readonly byte[] _data;
    private readonly int[] _channels;
    private readonly int _alpha;
    private readonly byte[]? _palette;

    private PsdPattern(string id, int mode, int width, int height, byte[] data, int[] channels, int alpha, byte[]? palette)
    {
        Id = id;
        Mode = mode;
        Width = width;
        Height = height;
        _data = data;
        _channels = channels;
        _alpha = alpha;
        _palette = palette;
    }

    public string Id { get; }

    /// <summary>Gets the pattern's image mode, as the file header's color mode (1 gray, 2 indexed, 3 RGB, 4 CMYK, 9 Lab).</summary>
    public int Mode { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Reads the patterns of a pattern block; a damaged pattern ends the reading, the ones before it stand.</summary>
    public static void Read(byte[] block, Dictionary<string, PsdPattern> into)
    {
        int at = 0;
        try
        {
            while (at + 4 <= block.Length)
            {
                int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at)), int.MaxValue);
                int start = at + 4, end = (int)Math.Min((long)start + length, block.Length);
                if (length <= 0)
                {
                    break;
                }

                if (ReadOne(block, start, end) is PsdPattern pattern)
                {
                    into.TryAdd(pattern.Id, pattern);
                }

                at = (end + 3) & ~3;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A pattern runs past its block: the ones read stand.
        }
    }

    private static PsdPattern? ReadOne(byte[] block, int at, int end)
    {
        ReadOnlySpan<byte> data = block.AsSpan(at, end - at);
        int mode = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int height = BinaryPrimitives.ReadInt16BigEndian(data[8..]), width = BinaryPrimitives.ReadInt16BigEndian(data[10..]);
        int p = 12;
        int name = (int)BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
        if (name < 0 || name > (data.Length - p) / 2)
        {
            return null;
        }

        p += 4 + (2 * name);
        if (p >= data.Length)
        {
            return null;
        }

        string id = Encoding.ASCII.GetString(data.Slice(p + 1, data[p]));
        p += 1 + data[p];
        byte[]? palette = null;
        if (mode == 2)
        {
            palette = data.Slice(p, 768).ToArray();
            p += 768 + 4;
        }

        // The virtual memory array list: version, length, bounds, the channel count, then that many arrays plus a user mask and
        // a sheet mask, each led by a flag saying whether it is written.
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(p + 24)..]);
        if (width <= 0 || height <= 0 || count < 0 || count > 64)
        {
            return null;
        }

        p += 28;
        int colors = mode switch { 1 or 2 or 8 => 1, 4 => 4, _ => 3 };
        int[] channels = new int[count + 2];
        for (int c = 0; c < channels.Length; c++)
        {
            channels[c] = -1;
            if (BinaryPrimitives.ReadUInt32BigEndian(data[p..]) == 0)
            {
                p += 4;
                continue;
            }

            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(p + 4)..]);
            if (length < 23 || length > data.Length - p - 8)
            {
                return null;
            }

            channels[c] = at + p + 8;
            p += 8 + length;
        }

        if (Array.FindIndex(channels, 0, Math.Min(colors, channels.Length), c => c < 0) >= 0)
        {
            return null;
        }

        // A transparency channel, when written: the first after the colors, else the sheet mask.
        int alpha = Array.FindIndex(channels, colors, c => c >= 0);
        return new PsdPattern(id, mode, width, height, block, channels, alpha, palette);
    }

    /// <summary>
    /// Decodes the pattern into <paramref name="tile"/>, <see cref="Width"/> × <see cref="Height"/> pixels of four floats:
    /// straight sRGB-encoded red, green and blue (gray repeated; indexed through its palette; CMYK by the plain formula; Lab
    /// through <paramref name="lab"/>) and alpha, 0 to 1. Returns <see langword="false"/> when its data is damaged.
    /// </summary>
    public bool Decode(Span<float> tile, Func<double, double, double, (double R, double G, double B)> lab)
    {
        int n = Width * Height;
        int colors = Mode switch { 1 or 2 or 8 => 1, 4 => 4, _ => 3 };
        float[][] planes = new float[colors][];
        try
        {
            for (int c = 0; c < colors; c++)
            {
                planes[c] = new float[n];
                if (!Plane(_channels[c], planes[c]))
                {
                    return false;
                }
            }

            for (int i = 0; i < n; i++)
            {
                Span<float> pixel = tile.Slice(4 * i, 4);
                pixel[3] = 1;
                if (Mode == 2 && _palette is not null)
                {
                    int index = (int)MathF.Round(planes[0][i] * 255);
                    (pixel[0], pixel[1], pixel[2]) = (_palette[index] / 255f, _palette[256 + index] / 255f, _palette[512 + index] / 255f);
                }
                else if (colors == 1)
                {
                    pixel[0] = pixel[1] = pixel[2] = planes[0][i];
                }
                else if (colors == 4)
                {
                    // CMYK planes count ink down from 1; the color is (1 − ink)(1 − black) by the plain formula.
                    float white = planes[3][i];
                    (pixel[0], pixel[1], pixel[2]) = (planes[0][i] * white, planes[1][i] * white, planes[2][i] * white);
                }
                else if (Mode == 9)
                {
                    (double r, double g, double b) = lab(planes[0][i] * 100, (planes[1][i] * 255) - 128, (planes[2][i] * 255) - 128);
                    (pixel[0], pixel[1], pixel[2]) = ((float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1));
                }
                else
                {
                    (pixel[0], pixel[1], pixel[2]) = (planes[0][i], planes[1][i], planes[2][i]);
                }
            }

            float[] plane = planes[0];
            if (_alpha >= 0 && Plane(_channels[_alpha], plane))
            {
                for (int i = 0; i < n; i++)
                {
                    tile[(4 * i) + 3] = plane[i];
                }
            }

            return true;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Reads one channel's array (depth, bounds, depth again, compression, then raw or PackBits rows) into 0 to 1 values.</summary>
    private bool Plane(int at, float[] plane)
    {
        ReadOnlySpan<byte> data = _data.AsSpan(at);
        if (data.Length < 23)
        {
            return false;
        }

        int depth = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
        int top = BinaryPrimitives.ReadInt32BigEndian(data[4..]), left = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        int bottom = BinaryPrimitives.ReadInt32BigEndian(data[12..]), right = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        int compression = data[22];
        int w = right - left, h = bottom - top;
        if (depth is not (8 or 16) || w != Width || h != Height)
        {
            return false;
        }

        int bytes = depth / 8, rowBytes = w * bytes;
        byte[] row = new byte[rowBytes];
        int p = 23;
        int counts = p;
        if (compression == 1)
        {
            p += 2 * h;
        }

        for (int y = 0; y < h; y++)
        {
            if (compression == 1)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(counts + (2 * y))..]);
                if (!UnpackBits(data.Slice(p, length), row))
                {
                    return false;
                }

                p += length;
            }
            else
            {
                data.Slice(p, rowBytes).CopyTo(row);
                p += rowBytes;
            }

            for (int x = 0; x < w; x++)
            {
                plane[(y * w) + x] = bytes == 1 ? row[x] / 255f : BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(2 * x)) / 65535f;
            }
        }

        return true;
    }

    private static bool UnpackBits(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int s = 0, t = 0;
        while (s < source.Length && t < target.Length)
        {
            int n = (sbyte)source[s++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, Math.Min(target.Length - t, source.Length - s));
                source.Slice(s, count).CopyTo(target[t..]);
                (s, t) = (s + n + 1, t + count);
            }
            else if (n != -128 && s < source.Length)
            {
                int count = Math.Min(1 - n, target.Length - t);
                target.Slice(t, count).Fill(source[s++]);
                t += count;
            }
        }

        return t == target.Length;
    }
}
