# Egami.Formats.Extended: guide for coding agents

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster, XBM,
XPM, WBMP, Farbfeld, JPEG-LS and DDS textures (BC1 to BC7, mips, cube maps, arrays and volumes), reads KTX and KTX 2 textures and writes KTX 2 (BC1 to BC7, ETC1, ETC2, EAC, 2D ASTC LDR and HDR, Basis Universal ETC1S and UASTC, and uncompressed; Zstandard and zlib supercompressed), reads and writes DPX and Cineon film frames, and reads camera RAW (DNG, CR2, NEF, PEF, ARW, ORF, RW2 and RAF; CR3 previews and metadata), developed on
load. Each is a plugin on core Egami's public format API.

Namespaces: `Egami.Formats.Extended` (the `With...` methods), `Egami.Formats.Extended.<Format>` (`PcxFormat` and the other
format infos and options), with `Egami` and `Egami.Formats`.

{{shared}}

## Entry points

- `WithExtendedFormats()`: every format in a context; `WithPcx()`, `WithSgi()`, `WithSunRaster()`, `WithXbm()`, `WithXpm()`,
  `WithWbmp()`, `WithFarbfeld()`, `WithJpegLs()`, `WithDds()`, `WithKtx()`, `WithKtx2()`, `WithDpx()`, `WithCineon()` and `WithRaw()` one at a time.
- DDS: Load gives mip 0 of the first surface; `DdsReaderOptions` (`Surface`, `MipLevel`, `IncludeMipLevels`,
  `ReconstructNormalZ`) and LoadAll reach every face, array slice, depth slice and mip; `DdsDetails` keeps the DXGI format.
  Save writes a full mip chain; `DdsWriterOptions` (`Format`, `MipLevels`, `Effort`, `Srgb`, `Cube`) and SaveAll write
  block formats, arrays and cube maps. Integer and depth formats write mip 0 alone by default.
- KTX and KTX 2: `KtxFormat` (read only) and `Ktx2Format` (read and write); `KtxReaderOptions` (`Surface`, `MipLevel`, `IncludeMipLevels`,
  `ReconstructNormalZ`, `KeepStoredLayout`) as DDS's; `KtxDetails` keeps the Vulkan or OpenGL format, supercompression,
  orientation, swizzle and key/value pairs. 3D ASTC, PVRTC, UASTC HDR 6 × 6, XUASTC and XUBC7 throw `UnsupportedFormatException`.
  `Ktx2WriterOptions` (`Format` a `KtxTextureFormat`, `MipLevels`, `Effort`, `Srgb`, `Cube`, `Supercompression`,
  `CompressionLevel`, `BasisQuality`, `UastcRdo`) writes uncompressed, BC, ETC, EAC, ASTC LDR, ETC1S and UASTC; no KTX 1.1
  writing, no ASTC HDR or UASTC HDR, no SNORM.
- `Texture` (`Egami.Formats.Extended.Textures`): `Texture.Load` a whole DDS, KTX or KTX 2 file; `GetImage`/`SetImage` by mip,
  layer, face and slice; `GenerateMips`, `TrimMips`; `Save` to .dds or .ktx2 with exactly its mips (volumes too).
- DICOM (preview, `[Experimental]` `EGAMIX001`; namespace `Egami.Formats.Extended.Dicom`): `DicomFile.Load` reads the file meta
  information and dataset (`DicomDataset`: by keyword, `DicomTag` or index; `DicomElement` getters `GetString`, `GetNumbers`,
  `GetIntegers`, `GetDate`, `GetTime`, `GetDateTime`, `GetPersonName`, `GetTags`, `Items`, `Fragments`, `RawValue`).
  `WithDicom()` (not in `WithExtendedFormats()`) registers `DicomFormat.Info`; `Raster.Load` gives one frame as a viewer
  shows it (Gray8/Gray16 after rescale, window or VOI LUT and MONOCHROME1 inversion; Rgb8/Rgb16 for RGB, YBR and palette),
  `DicomReaderOptions` picks `Frame`, `Values` (`Display`, `Modality` or `Stored`, the last two as GrayF32), a window
  (`WindowCenter`, `WindowWidth`, `WindowFunction`), `VoiIndex` and `PreferWindow`; `DicomDetails` says what was applied.
  Each frame is an image for `LoadAll`; `DicomFile.LoadFrame(i)` decodes frames without rereading. Native, RLE, deflated,
  JPEG, JPEG-LS, JPEG 2000, HTJ2K and JPEG XL pixel data; MPEG/H.264/HEVC throw `NotSupportedException`. Warnings are
  `Dicom.*` diagnostics.
- DPX and Cineon: `DpxFormat`, `CineonFormat`. Load gives the stored code values unchanged (10-bit RGB as `Rgb16`, code 1023
  at 65535); `DpxReaderOptions.Linearize` gives scene-linear `RgbF32` by Kodak's conversion (685 white, 95 black) for log
  elements; `DpxReaderOptions.Element` and LoadAll reach every image element. `DpxDetails` and `CineonDetails` keep the depth,
  packing, transfer and the file, film and TV fields (timecode, frame rate, slate), and the writers write them back.
  `DpxWriterOptions` (`BitsPerSample` 8, 10, 12 or 16, `BigEndian`, `Transfer`); Cineon writes 10-bit RGB only.
- RAW: Load develops a DNG into `Rgb16` sRGB (or straight into the RGB space `ConvertColorTo` names); `RawReaderOptions`
  (`Output` = `Developed`, `HalfSize`, `Preview` or `Mosaic`; `WhiteBalance`, `Temperature`, `Tint`, `Exposure`, `Demosaic`,
  `Highlights`, `LuminanceNoiseReduction`, `ColorNoiseReduction`, `LensCorrections`, `ApplyProfile`) sets it; `RawDetails`
  keeps the camera, sensor layout and the colour the develop used.
- JPEG-LS: `JpegLsFormat` (.jls, T.87) reads every interleave mode, 2 to 16 bits, palettes and HP colour transforms, and
  is detected ahead of JPEG. `JpegLsWriterOptions` (`Near`, `Interleave`, `ColorTransform`, `BitsPerSample`,
  `RestartInterval`); `JpegLsDetails` keeps NEAR, depth and interleave; `JpegLsWriter.Write` codes plain `ushort` samples
  (1 to 4 components), for DICOM and the like. No JPEG-LS part 2 (T.870).
- `EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())` at startup makes them work in every call.
- Load, Probe, streaming decodes, Save and Transcode then work as they do for core formats, inside the same safety limits.

## Samples

All of them:

{{sample FormatsExtended.cs#register}}

One at a time:

{{sample FormatsExtended.cs#register-one}}

DDS surfaces and mips:

{{sample FormatsExtended.cs#dds-surfaces}}

KTX surfaces, mips and the stored layout:

{{sample FormatsExtended.cs#ktx-surfaces}}

DPX code values and linear light:

{{sample FormatsExtended.cs#dpx-linearize}}

A RAW develop:

{{sample FormatsExtended.cs#raw-develop}}

JPEG-LS, near-lossless and from plain samples:

{{sample FormatsExtended.cs#jpegls-options}}

{{sample FormatsExtended.cs#jpegls-samples}}

## Common mistakes

- Referencing the package and expecting `.pcx` and the others to load. Register them in the context you load with, or as the
  default before any other Egami call.
- Saving to a path with a context that lacks them. `Save(path)` picks the writer through the raster's context, so a raster loaded
  without the formats cannot save as .pcx by extension; pass the format info (`PcxFormat.Info`) or load with the context.
- Expecting a DDS cube map or array to load whole with Load: it gives one surface. Use LoadAll, or `DdsReaderOptions.Surface`.
- Expecting a KTX texture written bottom-up (`KTXorientation` `ru`) to load bottom-up: Load turns it upright and applies
  `KTXswizzle`; `KtxReaderOptions.KeepStoredLayout` keeps the stored rows and channels.
- Expecting BC5 normal maps to have blue: it loads as 0 unless `DdsReaderOptions.ReconstructNormalZ` is set.
- Expecting a RAW load to turn the image upright: like every Egami load it returns the pixels as stored; `AutoOrient` turns
  them by `RasterInfo.Orientation`.
- Registering RAW with `WithFormat(RawFormat.Info)`: that puts it after TIFF, which then claims every DNG. Use `WithRaw()`.
- Expecting a DPX or Cineon scan to load as linear light: Load gives the log code values; set `DpxReaderOptions.Linearize`.
- Ignoring `Warnings` after a lenient read of a damaged file: it keeps what decoded and says what it recovered from.

## More

- Extended formats: https://inexin.github.io/Egami/v1/articles/formats/extended.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Formats.Extended.html
