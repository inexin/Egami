# Egami.Formats.Extended

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster,
XBM, XPM, WBMP, Farbfeld, JPEG-LS (lossless and near-lossless, 2 to 16 bits, as DICOM uses it) and DDS textures (BC1 to BC7, mipmaps, cube maps and arrays; writes BC1 to BC7 and uncompressed), and
reads KTX and KTX 2 textures (BC1 to BC7, ETC1, ETC2, EAC, 2D ASTC LDR and HDR, Basis Universal ETC1S and UASTC, and uncompressed; Zstandard and zlib supercompression;
writes KTX 2 in all of them but ASTC HDR and UASTC HDR; `Texture` loads and saves whole textures, volumes included) and camera RAW files (DNG, CR2, NEF, PEF, ARW, ORF, RW2 and RAF; CR3 previews and metadata), developed on load: white balance, AHD demosaicing, noise reduction, DNG opcodes and camera
profiles, into sRGB or any RGB colour space. It also reads and writes DPX and Cineon film frames (1 to 16-bit integer and float
samples, every packing, log to linear on request) and FITS images (BITPIX 8 to 64 and floats, tile-compressed images, stretched
for viewing), and has DICOM as a preview: datasets through `DicomFile` and frames as a viewer shows them, Secondary Capture and
transcoding on save. Each is a plugin on core Egami's public format API, the same one your own formats use.

- Register them all once with `WithExtendedFormats()`, for example
  `EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())` at startup, or one at a time with `WithPcx()`,
  `WithSgi()`, `WithSunRaster()`, `WithXbm()`, `WithXpm()`, `WithWbmp()`, `WithFarbfeld()`, `WithJpegLs()`, `WithDds()`, `WithKtx()`, `WithKtx2()`, `WithRaw()`,
  `WithDpx()`, `WithCineon()` and `WithFits()`.
- DICOM is in preview: `WithDicom()` and its types are `[Experimental]` (diagnostic `EGAMIX001`), `WithExtendedFormats()` leaves
  it out, and it may change in any minor release. Suppress `EGAMIX001` to opt in.
- Load, Probe, streaming decodes, Save and Transcode work as they do for core formats, inside the same safety limits, and a
  lenient read of a damaged file keeps what decoded and says what it recovered from in the Warnings.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
