# Formats

Egami reads and writes PNG, APNG, JPEG, GIF, WebP, BMP, TGA, QOI and the Netpbm formats (PBM, PGM, PPM, PAM and the PFM float
map), TIFF, Radiance .hdr, OpenEXR, JPEG XL, AVIF, and Windows icons and cursors, and reads JBIG2, raw CCITT fax data, JPEG 2000
and Photoshop's PSD and PSB. HEIC phone photos load through the separate, opt-in Egami.Heic package; core Egami detects and probes
them. SVG and Windows metafiles (WMF, EMF) load through the separate [Egami.Svg](../guides/svg.md) and
[Egami.Metafiles](../guides/metafiles.md) packages. Every reader checks sizes against the safety limits before it allocates, and
every reader has been fuzzed.

Each format has its own page with its options and what it keeps from the file; the table below links them. VP8 and AV1 video frames decode
through [`Vp8StreamDecoder`](vp8.md) and [`Av1StreamDecoder`](av1.md), and the boxes of MP4, QuickTime and HEIF files can be walked with [`IsoBoxReader`](iso-boxes.md). This page covers what all formats share: natural pixel types, detection, files with several images, and what writers drop.

## At a glance

| Format | Reads | Writes | Natural pixel type |
| --- | --- | --- | --- |
| [PNG](png.md) | Every color type and bit depth, interlaced, transparency, text, EXIF, XMP, ICC | Every color type and bit depth, adaptive filtering, built palettes, Adam7 interlacing, compression on several cores | The file's own: `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16` |
| [APNG](png.md) | Every PNG color type and depth, every disposal and blend | Any raster with more than one frame, changed rectangles only | `Rgba8`, or `Rgba16` for 16-bit |
| [JPEG](jpeg.md) | Baseline, extended and progressive, Huffman or arithmetic, 8 and 12-bit, lossless at 2 to 16 bits, any subsampling, gray, CMYK, YCCK, scaled decode, EXIF, XMP, ICC | Baseline or progressive, 8 or 12-bit, quality 1 to 100, 4:4:4, 4:2:2, 4:2:0, gray, CMYK or YCCK, optimized Huffman tables | `Gray8` or `Rgb8`; `Gray16`, `Rgb16` or `Cmyk16` above 8 bits |
| [GIF](gif.md) | 87a and 89a, every disposal, interlaced, transparency, loop count, comments, XMP, ICC | 89a, exact or quantized palettes, global or per frame, dithering, frame optimization | `Rgba8` |
| [WebP](webp.md) | Lossless (VP8L) with every transform; lossy (VP8) with alpha, as RGB or as YUV planes; ICC, EXIF, XMP | Lossless at effort 0 to 6, exact transparent colors; ICC, EXIF with resolution, XMP | `Rgb8`, or `Rgba8` with alpha |
| [BMP](bmp.md) | Core, info, V2 to V5 and OS/2 2.x headers; 1 to 32 bits; RLE4, RLE8, RLE24; bit fields; embedded PNG and JPEG; V5 ICC | 24-bit, 32-bit with alpha, 1, 2, 4 or 8-bit palettes; resolution and ICC | `Bgr8`, or `Bgra8` with alpha |
| [TGA](tga.md) | Color-mapped, true-color and gray, raw and RLE, 8 to 32 bits, all origins, TGA 2.0 footer and extension area | 8-bit gray, 24 and 32-bit, 8-bit palette, raw or RLE, TGA 2.0 footer | `Bgr8`, `Bgra8`, `Gray8` or `GrayAlpha8` |
| [QOI](qoi.md) | The whole format, 3 or 4 channels, both colorspaces | 3 or 4 channels, byte-identical to the reference encoder | `Rgb8` or `Rgba8` |
| [TIFF](tiff.md) | Every page, strips and tiles, chunky and planar, classic and BigTIFF; None, LZW, Deflate, PackBits, CCITT G3 and G4, JPEG, old-style JPEG, ThunderScan, SGILog, WebP, JPEG XL; gray, palette, RGB, CMYK, YCbCr, Lab and LogLuv at 1 to 64 bits | Every pixel type at its own depth, strips or tiles, classic or BigTIFF; None, LZW, Deflate, PackBits, CCITT G3 and G4, JPEG, WebP | The page's own: `Gray8`, `Gray16`, `Rgb8`, `Rgb16`, `Cmyk8`, `Cmyk16`, float types and more |
| [CCITT](ccitt.md) | Raw Group 3 and Group 4 data (T.4, T.6) with PDF's eight CCITTFaxDecode parameters, byte-aligned rows included | No | `Gray8`, black 0 and white 255 |
| [JBIG2](jbig2.md) | Every T.88 region and dictionary but the colour extension; standalone files (sequential and random-access, many pages) and the embedded form a PDF holds, with its globals | No | `Gray8`, black 0 and white 255 |
| [JPEG 2000](jpeg2000.md) | All of Part 1 (T.800): every progression order, code-block style, tiling and precinct size, reversible and irreversible; JP2 and JPX boxes, palettes, channel definitions, sYCC, CIELab, ICC; reduced and region decodes | No | `Gray8`, `Rgb8`, `Rgba8`, `Cmyk8` or their 16-bit forms, by depth and color space |
| [ICO, CUR](ico.md) | Every entry: PNG, and BMP at 1 to 32 bits with the AND mask; the best entry, or one by `MinimumSize` or index; cursor hotspots | One entry a raster, or sets resized to favicon or Windows sizes; PNG at 256 and 32-bit BMP below; hotspots | `Rgba8` |
| [PSD, PSB](psd.md) | The flattened image in every color mode (bitmap, gray, indexed, RGB, CMYK, Lab, duotone, multichannel) at 1 to 32 bits, raw, PackBits and Deflate; layer records and resources as metadata | No | By mode and depth: `Gray8`, `Rgb8`, `Rgba8`, `Cmyk8`, their 16-bit forms, float types |
| [Radiance](radiance.md) | RGBE and XYZE, every scan order, flat, old repeat-pixel and per-channel run-length scanlines | RGBE, run-length encoded or flat, optional EXPOSURE line | `RgbF32`, linear |
| [OpenEXR](openexr.md) | Scanline and tiled, single and multi-part, every OpenEXR 3 compression but HTJ2K, half, float and uint channels, luminance-chroma; any part, layer or channel | One scanline part, R, G, B and A, ZIP, ZIPS, RLE, PIZ or none, in half or float | `RgbaF16` when the channels are half, `RgbaF32` otherwise, `GrayF32` for luminance alone; linear |
| [JPEG XL](jxl.md) | Every still, lossless Modular and lossy VarDCT or Modular, at 1 to 31 bits and float, with alpha, CMYK, extra channels, animations and layers, previews, PQ and HLG, gain maps, and recompressed JPEGs (given back byte for byte) | Lossless in the raster's own precision, or lossy VarDCT in XYB, at cjxl's effort and quality scales; JPEGs recompressed losslessly | By bit depth: `Rgb8`, `Rgba8`, `Gray8`, their 16-bit forms, `RgbaF16` for 16-bit float, `RgbF32`, `RgbaF32` or `GrayF32` beyond |
| [AVIF](avif.md) | Still images: AV1 intra frames with every coding tool, equal to dav1d's decode and converted to RGB as libavif does; alpha, grids, overlays, clean aperture, orientation, ICC, CICP, HDR, gain maps, every item, and animated sequences | Yes: lossy and lossless still images with alpha, 8, 10 and 12 bits, 4:2:0, 4:2:2, 4:4:4 and 4:0:0 | By bit depth: `Rgb8` or `Rgba8` for 8-bit files, `Rgb16` or `Rgba16` for 10 and 12 bits, `Gray8` or `Gray16` (with alpha, `GrayAlpha8` or `GrayAlpha16`) for monochrome |
| [HEIC](heic.md) | With Egami.Heic: HEVC intra pictures up to 12 bits, grids, alpha, gain maps, depth maps, mattes and all-intra sequences; core Egami probes them | No | `Rgb8` for 8-bit files, `Rgb16` for 10 and 12 bits, with alpha when the file has it, gray for monochrome |
| [PNM](pnm.md) | P1 to P6 plain and binary, maxval 1 to 65,535, P7 PAM with alpha, PF and Pf float maps | P1 to P7, plain or binary; PF and Pf | `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16`; `RgbF32` or `GrayF32` for PFM |

`Raster.Load` returns the natural type; `Raster.Load<TPixel>` decodes straight into the type you ask for, converting row by row.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#natural-types)]

## Detection

Loading never trusts the file extension: the format comes from the first bytes, in the order PNG, JPEG, GIF, WebP, BMP, QOI, PNM,
TIFF, JBIG2, JPEG 2000, ICO, CUR, PSD, Radiance, OpenEXR, JPEG XL (FF 0A or its signature box), AVIF (an ftyp box with the avif or avis brand), HEIC (an ftyp box with a HEVC brand such as heic or heix), then TGA. TGA has no signature, so it is matched only when its whole 18-byte header is consistent. No file of another format in
Egami's test corpora is taken for TGA, and neither is any of a million random headers. When you know the format, register only
that one:

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#one-format)]

Data without a signature of its own, such as the JBIG2 or CCITT stream of a PDF image, loads with its format named, which skips detection.
`Load`, `LoadAll`, `Probe` and `RasterDecoder.Open` all take the format this way; naming the wrong one throws
`CorruptDataException`, as damaged data of that format would.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#named-format)]

Saving to a path picks the writer from the extension; an animated raster saved as `.png` writes an APNG. Saving to a stream
takes the format's `Info`.

## Several images in one file

Frames are animation and share one size. A TIFF's pages are separate images, each with its own size and pixel type, so they come
as a `RasterList` from `Raster.LoadAll`, which owns them; `RasterInfo.ImageCount` says how many there are. Every other format
gives its one image, so `LoadAll` works on any file. `Raster.SaveAll` writes several rasters to a format that holds several, and
one raster to any format.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-pages)]

## What each writer drops

Writers never change the raster. When a format cannot hold something, it is left out:

- **Alpha**: JPEG (unless `JpegWriterOptions.Background` blends it onto a color), BMP and TGA with `Bgr24`, PPM, QOI with
  `IncludeAlpha = false`.
- **Frames after the first**: JPEG, BMP, TGA, QOI and PNM.
- **Metadata**: every writer keeps what its format can carry; `StripMetadata = true` on any writer options drops it all.

See the [API reference](../../api/Egami.Formats.yml) for every option.

## What the file said

Every reader fills `RasterMetadata.FormatDetails` with the header's own terms: `PngDetails`, `JpegDetails`, `GifDetails`,
`BmpDetails`, `TgaDetails`, `QoiDetails` or `PnmDetails`. A writer of the same format uses them to keep what it can, and ignores
another format's details.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#format-details)]

Each format is described by a `FormatInfo`: its name, extensions, MIME types, and how to recognize it from its first bytes.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#format-info)]

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Batch convert a folder

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#batch-convert)]

### Read the size without decoding

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#read-size)]

`Probe` reads only the header, and reports the EXIF orientation, so you know how the image will be shown.

## Run it

[`samples/Formats`](https://github.com/inexin/Egami/tree/main/samples/Formats) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Formats` writes its results to `./output`.
