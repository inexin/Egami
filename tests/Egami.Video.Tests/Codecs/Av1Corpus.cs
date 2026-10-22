using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Pixels;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// The AV1 clips of tests/video/corpus/av1 (aomenc output in IVF, remuxed into WebM and Matroska by tools/video/make-av1-corpus.py)
/// with libaom's frame hashes, and libaom's own test vectors when tools/make-av1-references.py has fetched them into .cache/avif.
/// </summary>
internal static class Av1Corpus
{
    /// <summary>The committed clips, by name without extension.</summary>
    public static IEnumerable<string> Clips() =>
        [.. Directory.GetFiles(Path.Combine(Corpus.Directory, "av1"), "*.ivf").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

    public static string PathOf(string name) => Path.Combine(Corpus.Directory, "av1", name);

    /// <summary>libaom's frames of a clip: each frame's MD5 with and without film grain.</summary>
    public static List<(string Md5, string Md5NoGrain)> ReadReference(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        return [.. json.RootElement.GetProperty("frames").EnumerateArray().Select(f => (f[4].GetString()!, f[5].GetString()!))];
    }

    /// <summary>The fetched libaom test vectors folder (.cache/avif/aom-test-data), or null.</summary>
    public static string? Vectors
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".cache", "avif", "aom-test-data");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>The MD5 of a frame as tools/avifref/av1frames.c hashes it: the planes cropped, each sample 16-bit little-endian.</summary>
    public static string Md5(YuvRaster picture)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (int plane in picture.ChromaFormat == ChromaFormat.Yuv400 ? [0] : new[] { 0, 1, 2 })
        {
            Append(md5, picture, plane, wide: true);
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>
    /// The MD5 of a frame as libaom's .md5 files hash it: 8-bit samples as bytes, deeper ones 16-bit little-endian, a monochrome
    /// frame with neutral gray 4:2:0 chroma.
    /// </summary>
    public static string LibaomMd5(YuvRaster picture)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        bool wide = picture.BitDepth > 8;
        Append(md5, picture, 0, wide);
        if (picture.ChromaFormat == ChromaFormat.Yuv400)
        {
            int count = 2 * ((picture.Width + 1) >> 1) * ((picture.Height + 1) >> 1);
            byte[] gray = new byte[count * (wide ? 2 : 1)];
            for (int i = 0; i < count; i++)
            {
                if (wide)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(gray.AsSpan(i * 2), (ushort)(1 << (picture.BitDepth - 1)));
                }
                else
                {
                    gray[i] = 128;
                }
            }

            md5.AppendData(gray);
        }
        else
        {
            Append(md5, picture, 1, wide);
            Append(md5, picture, 2, wide);
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    public static List<string> Md5s(VideoReader reader, Func<YuvRaster, string>? hash = null)
    {
        hash ??= Md5;
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                md5.Add(hash(frame.Raster));
            }
        }

        return md5;
    }

    private static void Append(IncrementalHash md5, YuvRaster picture, int plane, bool wide)
    {
        byte[] row = new byte[picture.Width * 2];
        if (picture is YuvRaster<byte> bytes)
        {
            YuvPlane<byte> p = plane == 0 ? bytes.Y : plane == 1 ? bytes.U! : bytes.V!;
            for (int y = 0; y < p.Height; y++)
            {
                Span<byte> r = p.Row(y);
                if (!wide)
                {
                    md5.AppendData(r[..p.Width]);
                    continue;
                }

                for (int x = 0; x < p.Width; x++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), r[x]);
                }

                md5.AppendData(row, 0, p.Width * 2);
            }
        }
        else
        {
            YuvRaster<ushort> words = (YuvRaster<ushort>)picture;
            YuvPlane<ushort> p = plane == 0 ? words.Y : plane == 1 ? words.U! : words.V!;
            for (int y = 0; y < p.Height; y++)
            {
                Span<ushort> r = p.Row(y);
                for (int x = 0; x < p.Width; x++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), r[x]);
                }

                md5.AppendData(row, 0, p.Width * 2);
            }
        }
    }
}
