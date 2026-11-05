#pragma warning disable EGAMIX001 // DICOM is a preview API.

using System.Buffers.Binary;
using System.Text;

using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dicom;
using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// DICOM pixel data (DICOM Build Plan tasks 17 to 19), one thread: a DICOM load of one frame (<see cref="Dicom"/>) against a
/// load of the same codestream on its own (<see cref="Codestream"/>), so the overhead of the container and the display pipeline
/// shows. Gray frames are a 1024 × 1024 12-bit CT-like image with a rescale and a window; the colour frame a 2048 × 1536
/// photo-like image in JPEG baseline (YBR_FULL_422). Inputs are made in memory from fixed formulas.
/// </summary>
[MemoryDiagnoser]
public class DicomBenchmarks
{
    private const int Size = 1024;

    private readonly EgamiContext _context = EgamiContext.Default.WithJpegLs().WithDicom() with { MaxParallelism = 1 };
    private byte[] _dicom = [];
    private byte[] _codestream = [];

    [Params("Native", "JPEG-LS", "JPEG lossless", "JPEG baseline")]
    public string Coding { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        if (Coding == "JPEG baseline")
        {
            using Raster<Rgb8> photo = Raster.Create<Rgb8>(2048, 1536, _context);
            for (int y = 0; y < photo.Height; y++)
            {
                Span<Rgb8> row = photo.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb8((byte)(x / 8), (byte)((y / 6) ^ (x / 32)), (byte)(128 + (100 * Math.Sin((x + y) * 0.01))));
                }
            }

            using MemoryStream jpeg = new();
            photo.Save(jpeg, JpegFormat.Info);
            _codestream = jpeg.ToArray();
            _dicom = File(2048, 1536, 3, "YBR_FULL_422", 8, "1.2.840.10008.1.2.4.50", _codestream, encapsulated: true);
            return;
        }

        ushort[] ct = new ushort[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double r = Math.Sqrt(((x - 512.0) * (x - 512.0)) + ((y - 512.0) * (y - 512.0)));
                ct[(y * Size) + x] = (ushort)(r > 480 ? 0 : 1000 + (r < 200 ? 300 * Math.Cos(x * 0.05) : 40) + ((x * 7 + y * 13) % 17));
            }
        }

        switch (Coding)
        {
            case "Native":
                byte[] words = new byte[ct.Length * 2];
                for (int i = 0; i < ct.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(words.AsSpan(i * 2), ct[i]);
                }

                _dicom = File(Size, Size, 1, "MONOCHROME2", 16, "1.2.840.10008.1.2.1", words, encapsulated: false);
                break;
            case "JPEG-LS":
                using (MemoryStream jls = new())
                {
                    JpegLsWriter.Write(jls, ct, 1, Size, 12);
                    _codestream = jls.ToArray();
                }

                _dicom = File(Size, Size, 1, "MONOCHROME2", 16, "1.2.840.10008.1.2.4.80", _codestream, encapsulated: true);
                break;
            default:
                using (MemoryStream lossless = new())
                {
                    JpegLosslessWriter.Write(lossless, ct, 1, Size, 12, 1);
                    _codestream = lossless.ToArray();
                }

                _dicom = File(Size, Size, 1, "MONOCHROME2", 16, "1.2.840.10008.1.2.4.70", _codestream, encapsulated: true);
                break;
        }
    }

    /// <summary>Loads the DICOM file: parse, decode the frame, rescale and window.</summary>
    [Benchmark]
    public int Dicom()
    {
        using Raster raster = Raster.Load(_dicom, DicomFormat.Info, _context);
        return raster.Width;
    }

    /// <summary>Parses the file only, for the share of <see cref="Dicom"/> the container takes.</summary>
    [Benchmark]
    public int Parse()
    {
        return DicomFile.Load(_dicom, _context).FrameCount;
    }

    /// <summary>Loads the frame's codestream on its own (nothing for native data).</summary>
    [Benchmark(Baseline = true)]
    public int Codestream()
    {
        if (_codestream.Length == 0)
        {
            return 0;
        }

        using Raster raster = Raster.Load(_codestream, _context);
        return raster.Width;
    }

    /// <summary>Writes a Part 10 file in explicit VR little endian with a rescale and, for gray, a window.</summary>
    internal static byte[] File(int width, int height, int samples, string photometric, int bits, string syntax, byte[] pixels, bool encapsulated)
    {
        using MemoryStream body = new();
        using BinaryWriter w = new(body);
        void Element(ushort group, ushort element, string vr, byte[] value)
        {
            if (value.Length % 2 == 1)
            {
                value = [.. value, vr == "UI" ? (byte)0 : (byte)' '];
            }

            w.Write(group);
            w.Write(element);
            w.Write(Encoding.ASCII.GetBytes(vr));
            if (vr is "OB" or "OW" or "SQ" or "UN")
            {
                w.Write((ushort)0);
                w.Write((uint)value.Length);
            }
            else
            {
                w.Write((ushort)value.Length);
            }

            w.Write(value);
        }

        byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
        byte[] Text(string s) => Encoding.ASCII.GetBytes(s);
        Element(0x0002, 0x0001, "OB", [0, 1]);
        Element(0x0002, 0x0010, "UI", Text(syntax));
        byte[] meta = body.ToArray();
        body.SetLength(0);
        Element(0x0002, 0x0000, "UL", BitConverter.GetBytes((uint)meta.Length));
        w.Write(meta);
        Element(0x0028, 0x0002, "US", U16(samples));
        Element(0x0028, 0x0004, "CS", Text(photometric));
        if (samples > 1)
        {
            Element(0x0028, 0x0006, "US", U16(0));
        }

        Element(0x0028, 0x0010, "US", U16(height));
        Element(0x0028, 0x0011, "US", U16(width));
        Element(0x0028, 0x0100, "US", U16(bits));
        Element(0x0028, 0x0101, "US", U16(bits == 16 ? 12 : 8));
        Element(0x0028, 0x0102, "US", U16(bits == 16 ? 11 : 7));
        Element(0x0028, 0x0103, "US", U16(0));
        if (samples == 1)
        {
            Element(0x0028, 0x1050, "DS", Text("40"));
            Element(0x0028, 0x1051, "DS", Text("400"));
            Element(0x0028, 0x1052, "DS", Text("-1024"));
            Element(0x0028, 0x1053, "DS", Text("1"));
        }

        if (encapsulated)
        {
            w.Write((ushort)0x7FE0);
            w.Write((ushort)0x0010);
            w.Write(Encoding.ASCII.GetBytes("OB"));
            w.Write((ushort)0);
            w.Write(0xFFFFFFFFu);
            w.Write((ushort)0xFFFE);
            w.Write((ushort)0xE000);
            w.Write(0u);
            byte[] fragment = pixels.Length % 2 == 0 ? pixels : [.. pixels, 0];
            w.Write((ushort)0xFFFE);
            w.Write((ushort)0xE000);
            w.Write((uint)fragment.Length);
            w.Write(fragment);
            w.Write((ushort)0xFFFE);
            w.Write((ushort)0xE0DD);
            w.Write(0u);
        }
        else
        {
            Element(0x7FE0, 0x0010, "OW", pixels);
        }

        w.Flush();
        return [.. new byte[128], .. "DICM"u8, .. body.ToArray()];
    }
}
