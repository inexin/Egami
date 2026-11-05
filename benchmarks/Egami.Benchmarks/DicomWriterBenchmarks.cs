#pragma warning disable EGAMIX001 // DICOM is a preview API.

using System.Buffers.Binary;
using System.IO.Compression;

using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dicom;
using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// DICOM writing (DICOM Build Plan tasks 21 and 22), one thread: a 1024 × 1024 12-bit CT-like native file saved again in a
/// transfer syntax (<see cref="Transcode"/>, which parses nothing: the file is read once in setup), a Secondary Capture image of
/// the same values from a <see cref="Gray16"/> raster (<see cref="Capture"/>), and the bare codec coding the same samples
/// (<see cref="Codec"/>), so the overhead of the container shows. Inputs are made in memory from fixed formulas.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "BenchmarkDotNet calls GlobalCleanup, which frees the fields.")]
public class DicomWriterBenchmarks
{
    private const int Size = 1024;

    private readonly EgamiContext _context = EgamiContext.Default.WithJpegLs().WithDicom() with { MaxParallelism = 1 };
    private DicomFile _file = null!;
    private ushort[] _ct = [];
    private Raster<Gray16> _raster = null!;
    private DicomWriterOptions _options = null!;
    private readonly MemoryStream _output = new();

    [Params("Explicit VR", "Deflated", "RLE", "JPEG lossless", "JPEG-LS", "JPEG XL lossless", "JPEG extended")]
    public string Syntax { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _ct = new ushort[Size * Size];
        _raster = Raster.Create<Gray16>(Size, Size, _context);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double r = Math.Sqrt(((x - 512.0) * (x - 512.0)) + ((y - 512.0) * (y - 512.0)));
                ushort v = (ushort)(r > 480 ? 0 : 1000 + (r < 200 ? 300 * Math.Cos(x * 0.05) : 40) + ((x * 7 + y * 13) % 17));
                _ct[(y * Size) + x] = v;
                _raster.Row(y)[x] = new Gray16(v);
            }
        }

        byte[] words = new byte[_ct.Length * 2];
        for (int i = 0; i < _ct.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(words.AsSpan(i * 2), _ct[i]);
        }

        _file = DicomFile.Load(DicomBenchmarks.File(Size, Size, 1, "MONOCHROME2", 16, "1.2.840.10008.1.2.1", words, encapsulated: false), _context);
        _options = new DicomWriterOptions
        {
            TransferSyntax = Syntax switch
            {
                "Explicit VR" => DicomTransferSyntax.ExplicitVRLittleEndian,
                "Deflated" => DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
                "RLE" => DicomTransferSyntax.RleLossless,
                "JPEG lossless" => DicomTransferSyntax.JpegLossless,
                "JPEG-LS" => DicomTransferSyntax.JpegLsLossless,
                "JPEG XL lossless" => DicomTransferSyntax.JpegXlLossless,
                _ => DicomTransferSyntax.JpegExtended,
            },
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _raster?.Dispose();
        _output.Dispose();
    }

    /// <summary>Saves the opened file in the transfer syntax: the pixel data coded again (copied for Explicit VR) and every element written.</summary>
    [Benchmark]
    public long Transcode()
    {
        _output.SetLength(0);
        _file.Save(_output, _options, _context);
        return _output.Length;
    }

    /// <summary>Saves the raster as a Secondary Capture image in the transfer syntax (16 bits stored).</summary>
    [Benchmark]
    public long Capture()
    {
        _output.SetLength(0);
        _raster.Save(_output, DicomFormat.Info, _options);
        return _output.Length;
    }

    /// <summary>Codes the frame with the bare codec (deflate for Deflated; nothing for Explicit VR and RLE, which have no other coder).</summary>
    [Benchmark(Baseline = true)]
    public long Codec()
    {
        _output.SetLength(0);
        switch (Syntax)
        {
            case "Deflated":
                using (DeflateStream deflate = new(_output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_ct.AsSpan()));
                }

                break;
            case "JPEG lossless":
                JpegLosslessWriter.Write(_output, _ct, 1, Size, 12, 1);
                break;
            case "JPEG-LS":
                JpegLsWriter.Write(_output, _ct, 1, Size, 12);
                break;
            case "JPEG XL lossless":
                _raster.Save(_output, JxlFormat.Info, new JxlWriterOptions { Lossless = true, StripMetadata = true });
                break;
            case "JPEG extended":
                using (Raster<Gray16> wide = Raster.Create<Gray16>(Size, Size, _context))
                {
                    for (int y = 0; y < Size; y++)
                    {
                        Span<Gray16> row = wide.Row(y);
                        for (int x = 0; x < Size; x++)
                        {
                            int v = _ct[(y * Size) + x];
                            row[x] = new Gray16((ushort)((v << 4) | (v >> 8)));
                        }
                    }

                    wide.Save(_output, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Precision = 12, ColorType = JpegColorType.Gray, StripMetadata = true });
                }

                break;
        }

        return _output.Length;
    }
}
