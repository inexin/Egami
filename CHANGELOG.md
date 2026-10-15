# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Unreleased

Additive to the 1.0 API: nothing in it changes or goes away.

### VP8 video frames

- `Vp8StreamDecoder` decodes VP8 video (RFC 6386) a frame at a time: inter frames with the last, golden and alternate references,
  motion vectors and split macroblocks, six-tap, bilinear and whole-pixel prediction, hidden frames and probability carry-over.
  Headers, modes, vectors and every frame equal libvpx 1.14.1's on its 59 test vectors and on clips made with its encoder in all
  four versions. `Decode` returns a `Vp8FrameInfo`; `CopyFrameTo` copies a shown frame into a 4:2:0 `YuvRaster` of the caller's.
- `ConcealErrors` fills the macroblocks of a frame whose data runs out from the previous frame and repeats the previous frame for
  one that cannot be read, instead of throwing `CorruptDataException`; `Vp8FrameInfo.IsConcealed` says which frames were.
- Four frame buffers from the context's memory source, macroblock rows in a wavefront on up to `MaxParallelism` threads; decoding
  takes at most 1.5 times libvpx's time at 720p and 1080p on one thread and on all cores.
- Lossy WebP decodes about 30% faster: the VP8 token loop keeps its decoder state in registers and the loop filter works in
  saturating 8-bit lanes, with the same pixels as before.
### Radiance, OpenEXR and HDR gain maps

- `RadianceFormat` reads Radiance pictures (RGBE and XYZE, flat, old and new run-length scanlines, all eight scan orders) to
  linear `RgbF32`, bit for bit what stb_image decodes, and writes RGBE with run-length scanlines. `RadianceDetails` keeps the
  header; the brightest value is measured into `HdrMetadata.MaxContentLightLevel`.
- `ExrFormat` reads OpenEXR scanline, tiled and multi-part files with NONE, RLE, ZIPS, ZIP, PIZ, PXR24, B44, B44A, DWAA and DWAB,
  half, float and uint channels, layers and luminance-chroma images, equal to OpenEXR 3.5.2's decode (DWA within one half-float
  step) on the openexr-images set. `ExrDetails` lists parts, channels, layers, windows and every header attribute;
  `ExrReaderOptions` picks the part, layer and window, and `ExrImage.ReadChannel` loads one channel. A damaged chunk table is
  rebuilt (`Exr.ChunkTableRebuilt`), and blocks that cannot be read are left black (`Exr.BlocksDamaged`). The writer writes
  NONE, RLE, ZIPS, ZIP or PIZ in half or float, read back by OpenEXR to the same values.
- JPEG reads the CIPA Multi-Picture index: `JpegDetails.MultiPicture` lists the images with ExifTool's offsets and sizes,
  `Raster.Probe` counts them and `Raster.LoadAll` loads each.
- Gain maps: Ultra HDR, ISO 21496-1, Adobe hdrgm and Apple JPEGs load with `RasterMetadata.GainMap` (`GainMap`, `GainMapInfo`);
  `ReaderOptions.Hdr` (`HdrDecode`) or `GainMap.Apply` gives the HDR rendition for a display headroom, within about 1% of
  libultrahdr 2.0.2. JPEG saves write the map back with MPF, ISO 21496-1 and hdrgm metadata as Skia and libultrahdr write
  them (`JpegWriterOptions.KeepGainMap`); crops, turns, flips and resizes move it, pixel operations drop it
  (`Operation.GainMapDropped`), as do other formats (`Save.GainMapDropped`). `JpegWriterOptions.GainMap` and `GainMap.Create`
  (`GainMapOptions`, libultrahdr's defaults) write Ultra HDR from an HDR raster; libultrahdr decodes Egami's files to the same
  HDR image.
- Saving a floating-point raster with light above reference white to an 8 or 16-bit format reports `Save.HdrClipped`.
- Speed, single thread on Linux: Radiance decodes and encodes 1.3 to 1.9 times as fast as stb_image; Ultra HDR decodes 2 to 3
  times and encodes 1.1 to 1.7 times as fast as libultrahdr; OpenEXR NONE, RLE, ZIP, ZIPS and PIZ are level with the OpenEXR
  library or faster, PXR24, B44 and DWA 1.2 to 1.6 times slower. With all cores Egami is faster than OpenEXR on every
  compression but DWA.

### ICO and CUR

- `IcoFormat` and `CurFormat` read and write Windows icons and cursors. Entries are PNG files, decoded by Egami's PNG reader, or
  BMPs at 1 to 32 bits with palettes, bit fields and the AND mask, decoded by its BMP reader; a 32-bit entry whose alpha is 0
  everywhere falls back to the mask, as Windows does. Every entry of the corpus equals ImageMagick's and Pillow's decode.
- `Raster.Load` returns the best entry (the largest, then the deepest, as SkiaSharp picks but preferring depth among equal
  sizes); `MinimumSize` picks the smallest entry at least that big before reducing; `IcoReaderOptions.Entry` picks one by index;
  `Raster.LoadAll` returns every entry and `Raster.Probe` lists them (`ImageCount`, `IcoDetails.Entries`) without decoding.
- `IcoWriterOptions`: `Sizes` with `FaviconSizes` and `WindowsSizes`, resized from the largest raster with `Filter` and centred on
  transparency when not square, or taken as drawn; `Encoding` (null: PNG at 256, 32-bit BMP with an AND mask below; or all PNG
  or all BMP); `Hotspot` for cursors, scaled to each entry. Rasters over 256 are fitted with the `Save.IcoFitted` warning. Files
  open in ImageMagick, Pillow, SkiaSharp and Windows' WIC with the same pixels; decoding is 1.1 to 2.2 times SkiaSharp's speed
  and writing Windows' six sizes twice Magick.NET's.

### PSD and PSB

- `PsdFormat` reads the flattened image of Photoshop documents and large documents in every color mode (bitmap, gray, indexed,
  RGB, CMYK, Lab, duotone, multichannel) at 1, 8, 16 and 32 bits, raw, PackBits and Deflate with and without prediction.
  Transparency is divided back out of the white Photoshop flattens onto; CMYK loads as inks, Lab converts to sRGB, 32-bit files
  are linear. A file without a real composite loads its placeholder with the `Psd.NoComposite` warning; Egami does not
  composite layers.
- `PsdDetails`: version, `PsdColorMode`, depth, channels, `PsdCompression`, `HasRealComposite`, extra channel names, the layer
  records as `PsdLayer` (name, bounds, opacity, blend mode, visibility, clipping, `PsdLayerKind` and nesting; no pixels), and
  every image resource Egami does not map as `PsdResource`. ICC, resolution, EXIF, XMP and IPTC map to `RasterMetadata`.
- Rows stream from the channel planes, reading only what a region needs; layers are skipped without being read. Pixels equal
  ImageMagick's in every mode it reads alike and the written values everywhere; layers equal psd-tools', metadata exiftool's.
  12-megapixel files decode three to ten times faster than with Magick.NET.

### Comparing and measuring

- `Raster.Compare` returns MAE, MSE, RMSE, PSNR, peak error and the pixels that differ (ImageMagick's AE, with a fuzz) per
  channel and overall in one pass, on the 0 to 1 scale, with SSIM and DSSIM on request (Wang et al. 2004, 11 × 11 Gaussian).
  Colors are premultiplied by default, as ImageMagick 7's metrics are; `AlphaCompare.Straight` compares stored channels. Every
  metric is within 1e-6 of ImageMagick 7.1, AE identical, SSIM within 1e-4, and results do not depend on the thread count.
- `Raster.Difference` draws ImageMagick compare's highlight picture (identical at 8 bits) or |a − b| per channel.
- `Raster.Statistics` (min, max, mean, sample standard deviation, skewness, kurtosis, entropy per channel and luma, within 1e-6
  of identify -verbose), `Histogram` with `Percentile` and `Median`, `CountColors` (exact, as %k), `GetColors`, and `IsOpaque` and
  `IsGray` with early exit.
- `PerceptualHash.Dct` and `Difference`: 64-bit hashes within 4 bits of Python imagehash, with `Distance`; `Raster.Signature`, a
  SHA-256 of the pixels as 16-bit RGBA, equal for any pixel type of the same picture.

### Palettes and dithering

- The palette machinery behind the GIF, palette PNG, BMP and TGA writers is public: `Palette` (`FromColors`, `Build`,
  `FromRaster`, `Nearest`), `Palettes` (web-safe, black and white, grays, color cubes), `RasterOps.Quantize` and `Remap` keeping the
  pixel type, and `Raster.ToIndexed` returning an `IndexedImage`.
- `DitherMode` adds Atkinson, Burkes, Jarvis–Judice–Ninke, three Sierras, Stevenson–Arce, Stucki, Riemersma and Bayer 2, 4 and 16
  and 3 × 3 patterns; `PaletteOptions` adds `FixedPalette` (honoured by every writer, which throws when it has too many colors),
  `DitherStrength`, `Serpentine`, `RefineIterations` (k-means polish), `ExactMatching` and a custom `ErrorDiffusionKernel`.
  `Posterize` takes every mode. Defaults are unchanged: every file 1.x wrote stays the same byte for byte.
- `Raster.DominantColors`: the main colors with their shares, by k-means in Oklab seeded by Wu's palette, the same on every run.
- Quantizing a 12-megapixel photo is 2.5 to 4 times faster than the reference library 3.1 with the same dither.

### Animation tools

- `MergeDuplicates`, `DropZeroDelay`, `Retime`, `ChangeSpeed`, `Reverse` and `DrawAnimation` work on whole animations in `Apply`,
  `Derive` and `Transcode`. The first two equal ImageMagick's -layers RemoveDups and RemoveZero; `Retime` samples fixed ticks as
  ffmpeg's fps filter and keeps the total duration; `DrawAnimation` lays one animation over another by time, with a repeat count
  and start, or by index as -layers Composite (identical), checking `MaxFrames` before drawing.

### Tone and color

- Tone steps with ImageMagick's formulas and values from 0 to 1: `Level` (`-level` and `+level` in one), `Gamma`,
  `SigmoidalContrast`, `Posterize` (with ordered or Floyd–Steinberg dithering), `Solarize`, `Threshold` (luminance, each channel,
  saturation or chroma, with any two colors), `Colorize`, `Tint` and `Duotone`; Skia's `HighContrast`; and
  `SimulateColorBlindness` from Machado, Oliveira and Fernandes (2009) with a severity. Each is within one level of ImageMagick 7
  at 8 bits and two at 16; thresholds are identical. Tone steps, color matrices and filters next to each other run as one pass,
  as exact lookup tables on 8- and 16-bit images.
- `ColorFilter` holds a color adjustment as a value, as Skia's color filters: every tone step and color matrix, `Curves` of
  `ToneCurve`s (monotone curves through points, tables, functions), 3D tables, `Then` and `Lerp`. `RasterOps.ApplyFilter` runs one;
  `Apply` runs one on your own pixels; `ToLut3D` samples one into a table.
- `Lut3D` with tetrahedral (the default), trilinear and nearest reading and a 1D shaper; `CubeFile` reads and writes .cube files
  (Adobe 1.0 and Resolve's 1D-plus-3D form), strictly and within `SafetyLimits`, and has a fuzz target; `Lut3D.FromHald`, `ToHald`
  and `Raster.HaldIdentity` round-trip Hald CLUT images. Trilinear tables equal ImageMagick's `-hald-clut` within a level.
- Adjustments that measure the image first: `AutoLevel`, `ContrastStretch`, `Normalize`, `AutoGamma`, `AutoWhiteBalance`,
  `Equalize` and `AutoThreshold` (Otsu, triangle, Kapur). Inside a chain they measure what the steps before them made; inside
  `Within` and `OnChannels`, only the scope; across an animation, all frames at once unless `perFrame`. Their black and white
  points, gammas and levels equal ImageMagick's. `Raster.MeasureAutoLevel`, `MeasureContrastStretch`, `MeasureAutoGamma`,
  `MeasureWhiteBalance`, `MeasureEqualize` and `FindThreshold` return the filter or level without changing anything.
- `WhiteBalance` by a picked neutral color or by color temperature and tint (Bradford adaptation to D65), also as
  `ColorFilter.WhiteBalance`; `Clahe` (Zuiderveld's
  CLAHE on L*, within the closeness gate of ImageMagick's `-clahe`); `LocalContrast` (ImageMagick's `-local-contrast`, within a
  level); and `AdaptiveThreshold` (Bradley and Roth) for unevenly lit pages.

### TIFF

- `TiffFormat` reads classic TIFF and BigTIFF: strips and tiles, chunky and planar, 1 to 64-bit integer and float samples, in
  gray, palette, RGB, CMYK and other inks, YCbCr, CIELab, ICCLab, ITULab and LogLuv. Codecs: LZW, Deflate, PackBits, CCITT RLE,
  Group 3 and Group 4, JPEG and old-style JPEG, WebP, ThunderScan and SGILog, with both predictors and either fill order. Every
  file in the test corpus and in libtiff's sample images decodes as libtiff does. Damaged files and IFD loops throw
  `CorruptDataException`, within `SafetyLimits`.
- `Raster.Load` reads the first full-resolution page, or `TiffReaderOptions.Page`; a `MinimumSize` read takes a SubIFD or reduced
  page of exactly the right size instead of decoding full size. Rows stream for strip and tile files alike. Exif, GPS, IPTC, XMP,
  ICC and resolution come from the page's tags; tags Egami doesn't model are kept in `TiffDetails.OtherTags` and written back.
- `TiffWriterOptions` writes uncompressed, LZW, Deflate, PackBits, CCITT Group 3 and 4, JPEG (gray and RGB) and WebP, with
  predictors, strips or tiles, classic or BigTIFF (`Layout`), and `Reduce` for the smallest exact form (1-bit, gray, palette).
  Each pixel type is written at its own depth. A TIFF source's compression, predictor, layout and byte order are kept unless set;
  a lossy compression is kept only while the pixels are unchanged. `ParallelCompression` compresses strips and tiles on up to
  `MaxParallelism` threads, with the same bytes. Files are within 1% of the reference library's size at the same settings, and libtiff,
  ImageMagick and Pillow read them all. JPEG compression takes CMYK too, written as libtiff writes separated JPEG.

### WebP

- `WebpFormat` reads and writes WebP: lossless (VP8L), lossy (VP8) with or without alpha, and animated, with no native code.
  Lossless files decode to libwebp's pixels exactly; lossy files to libwebp's YUV planes sample for sample and to RGB within one
  level (`YuvRaster.Load` returns the planes); animations compose every frame as libwebp's animation decoder does. Every kind
  streams through `RasterDecoder`, incrementally too, and an animation holds one canvas whatever its frame count.
- `WebpWriterOptions`: `Lossless` (by default a WebP source keeps its kind and anything else is lossy), `Quality` and `Effort` with
  cwebp's meanings, `AlphaQuality`, `KeepTransparentColors`, `SharpYuv`, `NearLossless`, `MixedFrames` and `FirstFrameOnly`. At
  quality 75 and effort 4 lossy files are the reference library's size at the same quality, written twice as fast; lossless files are 4 to 9%
  smaller than the reference library's at every effort. Animations store changed rectangles; a GIF or APNG converts with Load and Save.
- `WebpReaderOptions.UseBackgroundColor` starts animations from the ANIM color, and `FastUpsampling` repeats chroma samples.
  `WebpDetails` and `WebpFrameDetails` say what a file held and keep unknown chunks (`WebpChunk`, `WebpChunkPosition`) for round
  trips. ICC, EXIF and XMP are read and written; the resolution travels in EXIF.

### JBIG2

- `Jbig2Format` reads JBIG2 (T.88): generic, refinement, text, symbol dictionary, pattern and halftone segments, arithmetic and
  Huffman coded, with MMR and custom code tables, from standalone files (sequential or random access) and from the embedded
  streams PDF holds, with `Jbig2ReaderOptions.Globals` (`Jbig2Globals.Parse`) and `Page`. Every page in the test corpus decodes
  bit for bit as PDFium and jbig2dec do, two to seventeen times faster than PdfPig. Pages load as `Gray8` and stream a stripe at
  a time; `Jbig2Details` says how a page was coded. Damaged regions are skipped with a warning; decoding stays within
  `SafetyLimits`.
- `Raster.Load`, `LoadAll`, `Probe` and `RasterDecoder.Open` take a `FormatInfo` to read data in a known format without
  detection, as PDF's JBIG2 streams need.

### JPEG 2000

- `Jpeg2000Format` reads all of JPEG 2000 Part 1 (T.800): JP2 and JPX files and raw codestreams, including a PDF's JPXDecode
  streams, which load with plain `Raster.Load`. Every progression order and progression change, every code-block style, tiles,
  tile-parts, precincts, packed headers, ROI, the reversible 5/3 and irreversible 9/7 wavelets, RCT and ICT, subsampled and
  signed components. Every ISO/IEC 15444-4 conformance codestream decodes within its tolerances: reversible ones identical to
  OpenJPEG 2.5.4, irreversible ones within one level, and the same at reduced resolutions.
- Palettes, channel definitions, sYCC and CIELab (converted to sRGB), ICC profiles (kept in the metadata), XMP and resolution are
  read; `Jpeg2000ReaderOptions.Components` returns the codestream's components as stored instead, for a PDF that names its own
  color space; `MaxQualityLayers` decodes the first layers only; `FastUpsampling` repeats subsampled chroma as OpenJPEG does.
  Subsampled sYCC also loads into a `YuvRaster`. `Jpeg2000Details` says how the codestream was coded.
- `MinimumSize` drops whole wavelet levels, `Region` decodes only the tiles and code-blocks it needs, and rows stream a row of
  tiles at a time. Tier 1 runs on every core; at one thread lossy files decode faster than OpenJPEG through Magick.NET, and at
  all cores every file decodes two to four times faster. Part 2 coding tools and High-Throughput code-blocks throw
  `NotSupportedException` naming the feature.

### Several images in one file

- `Raster.LoadAll` and `LoadAllAsync` return a `RasterList` of every image in a file, each at its own size and pixel type;
  single-image formats give a list of one. The list disposes its rasters; `Detach` keeps one. `RasterInfo.ImageCount` says how
  many there are without decoding.
- `Raster.SaveAll` and `SaveAllAsync` write several rasters as one multi-page TIFF; a format that holds one image takes one.

### Geometry

- `Transform` takes an affine `Matrix3x2`, a perspective `Homography` or a `Matrix4x4` projected as CSS `matrix3d` draws a flat
  element; `Skew` and `QuadDistort` build one. `Homography` is a double-precision 3 × 3 matrix with `FromQuad`, the usual factories,
  `TryInvert` and `Map`. `TransformOptions` chooses the filter, the color space, the edges (transparent, clamp, mirror, wrap), the
  background and a viewport; without one the result is the transformed bounding box.
- Where a transform shrinks or tilts the image, each pixel averages the ellipse its footprint covers (elliptical weighted
  averaging, capped at 32 source pixels), within 40 dB of ImageMagick's `-distort`; elsewhere it samples exactly as `Rotate` does.
  A pure scale equals `Resize` and a pure rotation equals `Rotate`, pixel for pixel.
- `Warp` runs any `IWarp` (or `Warp.FromFunction`). `Warps` holds ImageMagick's Barrel, BarrelInverse, Polar, DePolar, Arc, Swirl,
  Wave and Implode, with its parameters, whose points land where ImageMagick's do, and Skia's Magnifier. `Displace`, `Offset` and
  `Tile` move pixels by a map, by whole pixels, and in repeats.
- `Trim` and `EntropyCrop` crop to the content, `Deskew` straightens a page, and `Raster.TrimBounds`, `Raster.EntropyBounds` and
  `Raster.DetectSkew` measure without changing anything. Their size depends on the pixels: the chain runs up to them, measures every
  frame and plans the rest from the real size. Trim's rectangles equal ImageMagick's `-trim -fuzz` at 8 and 16 bits.
- `ResampleFilter` gains `Lanczos2`, `Hermite`, `Gaussian` and `Spline`, with ImageMagick's definitions, everywhere a filter is taken.
- `MipChain` makes many sizes from one image fast, each from the smallest halved level at least twice its size.
- `Raster.Append`, `Raster.Grid` and `SpriteSheet.Pack` build strips, contact sheets and packed sprite sheets.

### Planar YUV

- `YuvRaster<TSample>` holds a picture as Y, U and V planes and an optional alpha plane: 4:2:0, 4:2:2, 4:4:4 and 4:0:0, 8 bits
  in bytes, 10, 12 and 16 bits in `ushort`s. `YuvColor` states the matrix (`YuvMatrix`), range (`YuvRange`) and chroma siting
  (`ChromaSiting`), with presets and ITU-T H.273 codes (`YuvColor.TryFromH273`, `ColorCodePoints.TryGetYuvColor`). `Create` allocates the planes in one block from the memory source;
  `Wrap` takes a decoder's planes with strides, without a copy; `YuvPlane.AsGray8` views an 8-bit plane.
- `ToRaster`, `CopyTo`, `FromRaster` (with an alpha plane only when asked) and `CopyFrom` convert to and from every pixel type, within one level of the exact H.273
  result, with bilinear chroma by siting (or nearest and fast with `YuvConvertOptions`), in SIMD on every core. A repeated
  `CopyTo` or `CopyFrom` allocates nothing. 1080p 4:2:0 to `Rgba8` takes 0.5 ms on 24 cores.
- `CopyFromPacked` and `CopyToPacked` copy NV12, NV21, P010, P016, YUYV and UYVY frames (`YuvPackedFormat`).
- `YuvRaster.Load` and `Probe` give a JPEG's own planes, identical to libjpeg-turbo's raw output, also at reduced scale; saving
  JPEG-colored planes encodes them straight, with libjpeg-turbo's bytes. `Save` writes any other format through a raster.
- `Crop` (on chroma boundaries), `Resize` and `ResizeTo` (into an existing raster) work on the planes.

### Blending

- `BlendMode` names 31 modes: the twelve Porter–Duff operators, Plus, Modulate, the W3C separable and non-separable blend modes
  (Multiply to Luminosity), Add and Subtract. `DrawImage` takes a mode, an opacity and a color space (stored values by default,
  linear light as an option); only the overlap changes. `SourceOver` on stored values gives exactly what 1.0's `DrawImage` gives.
- On 8-bit rasters the Porter–Duff operators, Plus, Modulate and the product-and-sum modes are exact integer math, equal bit for
  bit to the W3C formulas rounded to nearest; ColorDodge, ColorBurn, SoftLight and the non-separable modes are within a level,
  with alpha exact. Every mode is within a level of SkiaSharp (two for Difference and Exclusion, where Skia rounds) and within
  two levels of the reference library's modes.
- `BlendColor` blends one color over every pixel (tint with Multiply, colorize with Color). `DrawArithmetic` draws with SVG's
  arithmetic composite, k1·S·D + k2·S + k3·D + k4.
- `Compositor.Blend` blends spans of any pixel type, with per-pixel coverage for antialiased edges or one color through coverage,
  for custom operations and rasterizers. It allocates nothing once warm and runs on many threads at once.
- `Compositor.Blend` also takes an `RgbaF32` color, kept at full precision on float and 16-bit rows. `Raster.Context` is public, so
  code outside Egami (a canvas) uses a raster's memory source and `MaxParallelism`.
- Fixed: a one-pixel straight `Rgba8` or `Bgra8` source row blended as if premultiplied (Multiply of a half-transparent gray gave
  178 where 139 is right).

### Filters

- Fixed: `BoxBlur`, and `Blur` above σ 100, could differ by a level in a few pixels between one thread and many. Their running
  sums now start at fixed rows and columns, so the pixels are the same at any thread count. `Blur` above σ 100 runs its three
  boxes in one pass, without rounding between them: about three times faster on many cores, and a third faster on one.

### Paints and layers in core

- `RasterOps.Reach` and `OperationReach`: how far a chain reads past each pixel (blurs, convolutions, morphology, glows, drop and
  inner shadows; zero for pixel-by-pixel steps; `null` when a step reads the whole image), so a chain can run on a padded part of
  a picture. Egami.Drawing's layers use it to grow before a filter.
- `RasterOps.InnerShadow`: a shadow inside the alpha shape, cast by everything outside it, as an editor's inner shadow layer
  style; exactly the chain written by hand.

### Egami.Svg 0.1.0 and Egami.Css 0.1.0

Two new packages, first released at 0.1; their API may still change while they are 0.x. See [SVG](docs/articles/guides/svg.md) and [CSS](docs/articles/guides/css.md).

- `SvgDocument` loads SVG and SVGZ into an editable tree of `SvgElement`, `SvgAttribute` and text, with a locked-down XML reader:
  internal DTD entities are capped, nothing outside the file is opened unless an `SvgLoadOptions.Resolver` allows it
  (`SvgResolvers.SameDirectory`), and element, depth and size limits come from `SafetyLimits`. A lenient load reports what it
  skips as `Svg.*` warnings; a strict one throws `SvgException` with the line and column. `Save` writes the tree back with its
  declaration, DOCTYPE, comments and foreign namespaces, numbers set in code at `SvgSaveOptions.Precision`.
- Styles cascade with Egami.Css: `style` elements with media queries, the `style` attribute and presentation attributes.
- Drawing follows resvg where SVG leaves a choice: shapes, fills, strokes, paint-order, markers, nested svg, use and symbol,
  linear and radial gradients, patterns, Inkscape's mesh gradients, clip paths, masks, opacity, blend modes, raster and SVG
  images and switch. `Rasterize` draws into a new raster at the document's size, a scale or a target size; `Draw` draws on any
  Egami.Drawing canvas, the whole document or one element by id; `RenderTree` exposes the resolved `SvgNode` tree.
- Text: x, y, dx, dy and rotate lists, tspan, tref and textPath, text-anchor, the baseline properties, letter-spacing,
  word-spacing, textLength and lengthAdjust, decorations, vertical writing and font variations, laid out as resvg lays text
  out and drawn as `SvgTextRun` glyph runs (`SvgRenderOptions.TextAsPaths` draws outlines). `SvgLoadOptions.Fonts`
  (`SvgFontOptions`) gives the fonts: the caller's collection, the document's `@font-face` rules, then the machine's unless
  turned off. SVG 2's wrapped text: `inline-size` lines as 2.8.2's paragraph layout fills them, and `shape-inside` text flowed
  into shapes as Inkscape flows it (16 of 20 samples match Inkscape's export).
- OpenType SVG glyphs: `SvgGlyphRenderer.Instance` draws an SVG table's glyphs in color. Egami.Drawing gains the hook it
  plugs into, `IColorGlyphRenderer` on `CanvasOptions.ColorGlyphs`, asked after COLR version 1 and before COLR version 0,
  sbix and CBDT.
- Filters: every SVG 1.1 primitive, feDropShadow and the CSS filter functions, in linearRGB or sRGB, run in a raster lined up
  with the device pixels (`SvgRenderOptions.FilterScale` sets its resolution on vector canvases). feGaussianBlur is SVG's three
  box blurs on premultiplied colors; feConvolveMatrix and the lighting primitives follow the specification's kernels.
- `SvgCanvas` writes any Egami.Drawing canvas drawing as SVG: paths, strokes and dashes, gradients, patterns, pictures,
  clips, layers with opacity, CSS blend modes and masks, images and text as glyph outlines or `text` elements, with shared
  `defs` (`SvgWriteOptions`). What SVG cannot express is drawn by Egami into an embedded PNG and reported as
  `Svg.FallbackImage`. Read back by Egami.Svg its files draw like the canvas calls, and Chromium and Inkscape open them cleanly.
- `SvgFormat.Info` registers SVG and SVGZ as an Egami format with `EgamiContext.WithFormat`, so `Raster.Load` draws a file at
  its own size (times `SvgDecodeOptions.Scale`) and `Raster.Probe` gives the size without drawing. It is found by its `svg`
  root, opens nothing outside the file, and only reads. Detection now passes data shorter than the longest registered
  signature to every format, as `SignatureMatcher` describes, so short SVG files are found.
- Gates: on resvg's own test suite, 94 to 100% of the shapes, painting, structure, paint-server and masking tests match resvg's
  reference renders (at most 0.5% of pixels differ, allowing a one-pixel shift; deviations are listed with reasons in
  egami-compare), 99% of the filter tests and 98.7% of the text tests (78% of the text tests are also close to
  Chromium's renders); 196 of the 200 corpus files
  match resvg's CLI. Seven of eight mesh samples match Inkscape's export.
- Speed, one thread, on 200 real files: loading and drawing them takes 0.92 s, against 2.0 s for Svg.Skia, 3.1 s for the resvg
  CLI (which also writes PNG; Egami does that in 1.4 s) and 9.5 s for resvg's library with text on (0.69 s with text off).
  resvg's text chapter takes 0.12 s against Svg.Skia's 0.45 s and resvg's 26 s; its filter chapter 0.91 s against 1.25 s and
  12 s.
- A fuzz target for Load, Rasterize and Save (`svg` in tests/Egami.Fuzz); its first runs found image data that was damaged
  after its header made drawing throw, and images are now decoded when the document is read.
- Egami.Css parses CSS Syntax 3, matches Selectors 4 over any `ICssElement` tree, cascades with specificity, `!important`,
  custom properties and `var()`, evaluates media queries, and parses colors, lengths, transforms and fonts. It stays a preview
  until Egap's HTML reader has used it.

### Egami.Fonts 0.1.0

Egami.Fonts' first release, beside Egami.Drawing 0.1.0: everything in the 1.0.0 previews below, from reading faces to shaping
and paragraph layout. Its API may still change while it is 0.x.

### Egami.Fonts 1.0.0-preview.4: paragraph layout

Wrapped, aligned and interactive text in `Egami.Fonts.Layout`, in the same preview package. See
[Paragraph layout](docs/articles/guides/paragraph-layout.md).

- `LineBreaks.Enumerate`: UAX #14 break opportunities (Unicode 17) with CSS's `word-break`, `overflow-wrap` and `line-break`
  strictness and the Chinese, Japanese and Korean rules; LineBreakTest.txt passes in full.
- `Paragraph.Create` takes a string or `RichText` (`RichTextBuilder`: nested `TextStyle` spans and inline `Placeholder` boxes)
  and a `ParagraphStyle`; `Layout(width)` gives a `TextLayout` of `TextLine`s and `LayoutRun`s with shaped glyph runs, per
  character font fallback in CSS's order, bidi per line, alignment and justification (`TextJustify`), tabs and tab stops,
  line heights and struts, soft hyphens, balanced filling, `MaxLines` and `MaxHeight` with an ellipsis, and vertical writing
  (`WritingMode`, `TextOrientation`). `LineFlow` lays out a line at a time at changing widths, for columns and wrap-around.
- Interaction: `HitTest`, `GetCaret` (with a second caret at bidi boundaries and GDEF ligature carets), `MoveCaret`,
  `GetSelectionRects`, word and line boundaries; min-content and max-content widths, ink bounds and placeholder boxes.
- Decorations: underline, overline and line-through, solid, double, dotted, dashed or wavy, with skip-ink, as
  `TextLayout.Decorations`; `TextLayout.ToPath` writes glyphs and decorations to an outline sink.
- Gates: line breaks, line boxes, glyph origins, intrinsic widths, ellipses, hit tests, carets, selections and decoration ink
  equal headless Chromium 129's on a corpus of eleven scripts at five widths and the feature cases (the few differences are
  Chromium's own, listed in the tests). Breaking 1 MB of mixed text takes 17 ms against the reference font library 2.1.3's 2.6 s;
  a 2,000-word page at three widths 7.8 ms against 118 ms; 10,000 hit tests 19 ms against 196 ms.
- Fuzz target `paragraph`, and a robustness suite on the same harness.

### Egami.Drawing 1.0.0

The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it. Settled in the 1.0 API review, changes from 0.1.0:

- `CanvasOptions.ColorGlyphs` is `ColorGlyphRenderer`; `Canvas.ColorGlyphRenderer`, `DrawColorGlyphs` and `DrawRendererGlyphs`
  are `protected`, so other backends draw color glyphs as the raster backend does.
- `OnDrawLattice` is `OnDrawImageLattice`; `OnDrawImageMask` takes its arguments in `DrawImageMask`'s order.
- `EncodedImage.FromBytes` and `FromFile` are `EncodedImage.Load`, from a path, a stream or bytes, as `Raster.Load`.
- `PathOps.Combine` is `Path.Combine`; `Path.Offset` is `Path.Inflate`.
- `Patch.Coons` and `Patch.Tensor` take corner texture coordinates before values, as `Mesh` does; `Patch.ToTensorNet` returns
  read-only memory; `DrawMesh`'s paint is optional, as `DrawPatch`'s.
- `NoiseType` is `NoisePaintKind` (`Fractal`, `Turbulence`, `Simplex`), `NoisePaint.Type` is `Kind`, and the tile parameter is
  `tileSize` everywhere.
- `TextAnchor.Middle` and `VerticalAlign.Middle` are `Center`.
- New hook `OnClear`, so a document backend can write a clear; `GlyphCache.ByteLimit` is documented as process-wide.

### Egami.Drawing 0.1.0

Egami.Drawing's first release: everything in the 1.0.0 previews below, from the canvas and paths of 2.6.1 to the meshes,
patches, pictures and document hooks of 2.6.4 and the text drawing since. Its API may still change while it is 0.x; 1.0 freezes
it once Egap's PDF canvas has drawn real pages through the canvas hooks.

- `LayerOptions.Knockout` for PDF's knockout groups and `DrawImageMask` (hook `OnDrawImageMask`) for its stencil masks, drawn
  exactly by the raster backend; pictures and .egdr keep them (for Egap's PDF renderer).
- `Canvas.RasterizeFallback` scales a paint's mask blur with the raster, so a blur drawn through a PDF page's fallback at
  300 dpi keeps its size; layer filter and backdrop chains run on the raster's pixels as they are (found by Egap's PDF canvas).

### Egami.Drawing 1.0.0-preview.7

- `Canvas.DrawTextLayout` draws a laid-out paragraph in CSS's order (shadows, backgrounds, underlines, glyphs and placeholders,
  line-throughs), skipping lines outside the clip; an ellipsis or a soft hyphen's hyphen is marked content with ActualText.
  `Canvas.DrawText` into a `RectF` with `VerticalAlign` lays out and draws in one call, and `TextLayout.ToPath(origin)` gives a
  `Path`. Drawn paragraphs are within 2 levels of the exact coverage on over 99% of pixels, closer than SkiaSharp, and drawing
  costs at most 10% over drawing the glyph runs alone.

### Egami.Fonts 1.0.0-preview.3: text shaping

OpenType shaping in managed code, a translation of HarfBuzz 14.5.1's, in the same preview package. See
[Unicode text and shaping](docs/articles/guides/unicode-text.md).

- Unicode 17.0 in `Egami.Fonts.Unicode`, from Egami's own tables (never the runtime's): `UnicodeData` (categories, scripts and
  script extensions, bidi, joining, combining, Indic, vertical orientation, East Asian width, emoji properties), `Bidi.Resolve`
  (UAX #9 with isolates, brackets and mirroring; `BidiResult.Reorder` for a line), `TextSegmenter` graphemes and words (UAX #29)
  and `Script` with its OpenType tags. Unicode's BidiTest, BidiCharacterTest, GraphemeBreakTest and WordBreakTest pass in full.
- `TextShaper.ShapeRun` shapes one run into a reusable `GlyphBuffer` (glyph ids, UTF-16 clusters, `GlyphFlags`, positions) with
  every GSUB and GPOS lookup type, FeatureVariations, the variation deltas of variable fonts, vertical runs (vmtx, VORG, VVAR) and
  HarfBuzz's script shapers: Arabic and its family (with fallback shaping from the presentation forms), Hebrew, the nine Indic
  scripts, Khmer, Myanmar, Thai and Lao, Hangul and the Universal Shaping Engine. `RunProperties` give the script, BCP 47 language,
  direction, `FontFeature`s (HarfBuzz's syntax: `FontFeature.Parse("liga=0")`) and `ClusterLevel`.
- `TextShaper.Shape` shapes whole text: lines at hard breaks, bidi, script and span runs (`ShapingOptions`, `TextSpan`) and font
  fallback per grapheme cluster (`FontFallback`, `IFontFallbackResolver`, the opt-in `SystemFontFallback`), returning `ShapedText`.
- `Font.Shape` and `Font.MeasureText` take `ShapingOptions` and shape with all of the font's features; text without ligature or
  mark rules gives 2.7.1's glyphs and positions. `GlyphRun` gains `BidiLevel`, `Script` and `Language`.
- `Typeface.Layout` lists scripts, language systems and features and gives `GetClosure`, hb-subset's GSUB closure;
  `SubsetOptions.LayoutClosure` keeps it and the layout tables, for subsets that are shaped again.
- Gates: glyphs, clusters, positions and flags equal hb-shape 14.5.1's on all 276 AOTS tests, 4,974 of HarfBuzz's 4,987
  in-house tests (the rest test fonts without outlines, the Zawgyi encoding, repeated axis tags, other shapers and output Egami
  leaves out) and 24 corpus runs in 15 scripts; the closure equals HarfBuzz's on 1,116 cases. Shaping is faster than
  the reference font library 2.1.3 on English, Arabic and Hindi pages (3.7, 7.5 and 13.4 against 41, 64 and 269 ms) and about half
  HarfBuzzSharp's speed.
- Fuzz targets `shaping` and `bidi`.
- From the API review: a font with no glyph data (a subset made for shaping) loads with empty glyphs and a `Font.MissingTable`
  warning; `WithVariations` sets every axis of a repeated tag; the cmap subtable is chosen as HarfBuzz chooses it (a symbol
  subtable first, Windows 3.1 Arabic font pages through their private use characters), for drawing and shaping alike.

### Egami.Drawing 1.0.0-preview.6

- `Canvas.DrawText` takes `ShapingOptions` and draws shaped text (with fallback fonts when the options list them);
  `Canvas.DrawShapedText` draws a `ShapedText` shaped once. Latin text draws as before, at the same speed.

### Egami.Fonts 1.0.0-preview.2

Web, variable and color fonts, in the same preview package. See [Variable fonts](docs/articles/guides/text.md#variable-fonts),
[Color fonts](docs/articles/guides/text.md#color-fonts) and [Faces](docs/articles/guides/text.md#faces).

- WOFF and WOFF2 load wherever TrueType and OpenType do (`Typeface.FromFile`, `FromMemory`, `FontLoader`, `FontCollection`
  directory scans): WOFF with the BCL's zlib, WOFF2 with its Brotli decoder and the glyf, loca and hmtx transforms, WOFF2
  collections too. `Typeface.Container` says which container a face came in; `GetWoffMetadata` and `GetWoffPrivateData` return the
  extra blocks. Every decoded table equals fontTools' and the reference woff2 decoder's. Four WOFF2 files load in 15 to 21 ms
  against the reference font library 2.1.3's 31 to 69.
- Variable fonts: `Typeface.VariationAxes` (`VariationAxis`), `NamedInstances` (`NamedInstance`), `IsVariable`, `Variations`, and
  `WithVariations` and `WithNamedInstance`, which return a face at a position sharing the font's memory (positions are cached).
  `FontVariation` with `Weight`, `Width`, `Slant`, `Italic` and `OpticalSize`; avar 1 and 2; gvar with shared and private points,
  intermediate regions, IUP, composites and phantom points; CFF2 with blend and vsindex; HVAR, MVAR and GDEF variation deltas, so
  outlines, advances, extents, kerning and font metrics all vary. Each equals fontTools at 25 positions of five variable fonts.
  Names and PostScript names follow the position.
- `FontCollection` matching picks a position inside a variable family's weight, width and slant ranges by CSS Fonts 4 rules, as
  well as its named instances. `Font.OpticalSizing` (Auto by default) sets the opsz axis to the font size;
  `Font.EffectiveTypeface` is the face drawn.
- Static instances: `SubsetOptions.Variations`, or subsetting a face at a position, writes a static TrueType or CFF font with the
  outlines, advances and metrics at that position and no variation tables, equal to fontTools' instancer; glyphs keep their
  overlaps and are marked OVERLAP_SIMPLE or OVERLAP_COMPOUND as fontTools marks them. They open in fontTools and FreeType. An
  instance subset to 200 glyphs takes under 1 ms.
- Color fonts: `Typeface.ColorFormats`, `Palettes` (`FontPaletteInfo`, from CPAL), `HasColorGlyph`, `TryGetColorGlyph` and
  `TryGetSvgDocument`. COLR v0 layers and the full COLR v1 paint graph (solid, linear, radial and sweep gradients with their color
  lines, glyph and color-glyph references, transforms, composites, clip boxes, variable paints) come as `ColorGlyph` trees with a
  visitor; sbix and CBDT images as `BitmapColorGlyph` with the strike nearest the size. `FontPalette` (Default, Light, Dark, an
  index and color overrides) and `Font.Palette`; `Font.ColorGlyphs` turns color off. Paint trees, colors, clips and images equal
  fontTools'; cycles and graphs deeper than 64 paints are cut with the `Font.BadColorGlyph` warning.
- Rendering settings for canvases: `Font.Hinting` (`FontHinting.Light`: vertical-only snapping to blue zones from CFF or measured
  from TrueType outlines, up to 48 pixels per em; no x value or advance moves), `Font.Subpixel` (quarter-pixel glyph origins, on
  by default), `Font.Edging` (`FontEdging.Antialias`, `Alias`, `Lcd`), `Font.AllowSynthesis` with synthetic bold (an outset by
  the size) and italic (a 14-degree slant), in outlines and bounds too.
- `GlyphRun.CreateTransformed` and `GlyphRun.Transforms` (`GlyphTransform`, Skia's RSXform): a run whose glyphs each have their
  own turn and scale, kept as text.
- `FontOutlines.None` for bitmap-only faces. New warnings `Font.BadContainer` and `Font.BadColorGlyph`.
- Fuzz targets `woff2`, `gvar`, `cff2`, `colr` and `instance`; their first 10-minute runs found five crashes, all fixed and kept as
  regressions, and the reruns were clean.

### Egami.Drawing 1.0.0-preview.5

Text quality, text on a path and color glyphs. See [Rendering quality](docs/articles/guides/text.md#rendering-quality),
[Text on a path](docs/articles/guides/text.md#text-on-a-path) and [Color fonts](docs/articles/guides/text.md#color-fonts).

- The raster canvas draws `Font.Hinting`, `Subpixel` and `Edging`: each subpixel phase, edging and hinting gets its own cached
  mask; LCD edging renders at three times the resolution along `CanvasOptions.PixelGeometry` with a 5-tap filter, and falls back to
  grayscale with the `Text.LcdFallback` warning where it cannot be drawn (no geometry, a layer, a paint other than solid
  source-over). Synthetic bold and italic draw on every backend. Glyphs whose contours overlap (variable fonts, and instances
  marked so) are merged before rasterizing, so overlaps do not darken their edges.
- `DrawTextOnPath` and `Font.ShapeOnPath` with `TextOnPathOptions` (start offset or fraction, anchor, baseline shift, side,
  wrapping on closed paths), by SVG's textPath rules; the result is a transformed glyph run, so a PDF backend keeps it as text.
  Transformed runs draw on the raster canvas through paths for turned glyphs and masks for the rest.
- Color glyphs draw in color through ordinary canvas calls (clips by glyph outlines, solid and gradient fills, transforms and
  layers with blend modes; images for sbix and CBDT), so every backend gets them; on a raster they are cached as premultiplied
  images per palette and color. The glyph is one group under the paint's alpha; the paint's color fills foreground entries.
  `PictureRecorder` and .egdr files keep color glyphs as the calls they draw with.
- Gates against SkiaSharp 4.153: variable text at 12 to 48 pixels in five fonts and subpixel-positioned text at 9 to 24 pixels in
  four within a mean level of SkiaSharp filling the same glyphs and closer than it to the exact area; COLR v0 and v1 glyphs within
  a mean level of SkiaSharp's own drawing at 24 to 96 pixels, sbix and CBDT within 2. Aliased, LCD, light-hinted and synthetic text
  are recorded against SkiaSharp's nearest settings.
- Speed (ms, 1 thread): a 2,000-word page in Roboto Flex at 16 pixels draws in 20 at its default and at weight 700, width 75 alike;
  a page of 500 COLR v0 emoji in 2.7 cold and 1.6 warm against the reference drawing library 2.1.7's 60, and of COLR v1 emoji in 15 cold and
  2.1 warm; plain text is within 10% of 2.7.1 (11.0 against 10.7 at 12 pixels).
- Fixed: a curve under a transform so large that rounding split it again and again overflowed the stack.

### Egami.Drawing 1.0.0-preview.4

Advanced drawing, in the same preview package. See [Nine-patches and lattices](docs/articles/guides/drawing.md#nine-patches-and-lattices),
[Meshes and patches](docs/articles/guides/drawing.md#meshes-and-patches), [Recorded drawings](docs/articles/guides/drawing.md#recorded-drawings)
and [the .egdr format](docs/articles/guides/egdr-format.md).

- `DrawImageNinePatch` and `DrawImageLattice` with `Lattice` (x and y divisions, bounds, `LatticeCellKind` and cell colors), as
  Skia's; fixed spans shrink in proportion when the destination is small. The raster backend draws the cells as one mesh, so they
  meet without seams, each sampled inside its own part of the image. `NinePatch.FromAndroid` reads .9.png markers into the
  image, its lattice and its content padding. Within a mean level of SkiaSharp's drawImageNine and drawImageLattice.
- `DrawAtlas` with `SpriteTransform` (Skia's RSXform) and optional colors combined by a blend mode; each sprite blends on its own.
  Within a mean level of SkiaSharp's drawAtlas. 10,000 16-pixel sprites draw in 23 ms against the reference drawing library 2.1.7's 35 to
  43 ms drawing them one by one.
- `Mesh` (`MeshMode` `Triangles`, `Strip` or `Fan`, with colors, texture coordinates, values and indices) and `DrawMesh`: the
  triangles' union is filled once, antialiased on its outer edge only, so meshes have no cracks; Gouraud colors in the canvas's
  color space, textures mapped through any paint, and per-vertex values mapped through gradient stops for PDF's mesh shadings.
  Within a mean level of SkiaSharp's drawVertices.
- `Patch.Coons` and `Patch.Tensor` with corner colors, values or texture coordinates and `DrawPatch`, cut into triangles until
  they stray less than a quarter of a device pixel, later parts covering earlier ones where a patch folds, as PDF requires.
  Within a mean level of SkiaSharp's drawPatch.
- `PictureRecorder` records any canvas code into an immutable `Picture`: `DrawPicture` replays it under a matrix, with a paint
  that merges it as one layer or as an image cached at the current scale; `Playback`, `ToRaster`. `PicturePaint` tiles a
  drawing as a pattern rendered at the device scale. Within a mean level of SkiaSharp's drawPicture and picture shaders.
- `Picture.Save` and `Picture.Load`: Egami's own documented binary format, .egdr, versioned and chunked, with images as PNG;
  loading checks every count and number, honors `SafetyLimits` and bounds nesting and replay, and has a fuzz target (`egdr`).
- For document backends: `DrawImage` with an `EncodedImage` (a file's bytes, probed and decoded only when needed, so a PDF page
  can embed a JPEG as it is), `DrawAnnotation` with `Annotation` links and destinations as Skia's drawAnnotation, and
  `BeginMarkedContent` and `EndMarkedContent` with `MarkedContent` tags for tagged PDF; hooks `OnDrawEncodedImage` (decodes by
  default), `OnDrawAnnotation`, `OnBeginMarkedContent` and `OnEndMarkedContent` (nothing by default). Pictures and .egdr keep
  them.
- Backends get `OnDrawLattice`, `OnDrawAtlas`, `OnDrawMesh`, `OnDrawPatch` and `OnDrawPicture`, with defaults: images per cell or
  sprite, meshes through the raster fallback, patches as meshes, drawings replayed.
- Fixed: `DrawImage` under a transform that collapses the image threw; it draws nothing. Scaling overlapping `RoundRect` radii
  was not idempotent; scaled radii now round down.

### Egami.Drawing 1.0.0-preview.3

Paints and layers, in the same preview package. See [Gradients](docs/articles/guides/drawing.md#gradients),
[Layers](docs/articles/guides/drawing.md#layers) and [Blurs and shadows](docs/articles/guides/drawing.md#blurs-and-shadows).

- Gradients: `LinearGradient`, `RadialGradient`, `ConicalGradient` (two circles, as the HTML canvas, Skia and PDF) and
  `SweepGradient`, with `GradientStop`s, `TileMode` (`Clamp`, `Repeat`, `Mirror`, `Decal`), PDF's one-sided extend
  (`ExtendStart`, `ExtendEnd`), mixing in encoded sRGB, linear sRGB, Oklab or Oklch with CSS Color 4's hue methods
  (`GradientInterpolation`), premultiplied by default, and ordered `Dither` (off by default). Every paint has a `LocalMatrix`.
  Within a mean level of SkiaSharp's gradients on every kind and tile mode, and of the reference drawing library's linear and radial brushes.
- `ImagePaint`: an image as a paint, tiled per axis, sampled with Egami's filters and from a `MipChain` level when shrunk; a
  whole-pixel shift copies rows. Within a mean level of SkiaSharp's image shaders and the reference drawing library's image brush.
- `NoisePaint`: SVG feTurbulence's `Fractal` and `Turbulence` (within a mean level of SkiaSharp's noise shaders unstitched) and
  `Simplex`, smoother and exactly tileable.
- `BlendPaint` (two paints with any blend mode), `Paint.WithColorMatrix` (`ColorMatrixPaint`) and `CustomPaint` (rows from C#,
  the same at any core count; vector backends use the raster fallback).
- Layers: `SaveLayer` with `LayerOptions` (`Bounds`, `Opacity`, `BlendMode`, `Filter` as any chain of Egami operations,
  `Backdrop`, `Mask`, `Isolated`) and `SaveLayer(opacity)`; backends get `OnSaveLayer` and `OnRestoreLayer`. Layers are
  premultiplied in the canvas's class and cover only their bounds in the clip, grown by the filter's reach. `SoftMask` with
  `SoftMaskKind.Alpha` or `Luminosity`, a backdrop color and a transfer curve, as PDF soft masks. Within a mean level of SkiaSharp's
  saveLayer with alpha and blend modes.
- `Paint.Blur` with `MaskBlur` and `BlurStyle` (`Normal`, `Solid`, `Outer`, `Inner`), on Egami's fast blur, as Skia's blur mask
  filter (within a mean level of SkiaSharp's in every style); axis-aligned rectangles are blurred exactly from the error
  function. `DrawShadow` with `ShadowOptions`: ambient and spot shadows after Skia's shadow model.
- Speed (ms, 2048 × 2048, 1 thread and 24 cores): a full-frame linear gradient 26 and 3.8 against the reference drawing library 2.1.7's 378
  and 33; radial 22 and 2.9 against 254 and 30; a tiled image brush 5.6 and 0.8 against 41 and 4.3. A full-frame half-opacity
  layer costs 1.38 times the drawing inside it at 1 thread and 1.14 at 24.

### Egami.Drawing 1.0.0-preview.2

Path geometry, in the same preview package. See [Path geometry](docs/articles/guides/drawing.md#path-geometry).

- Boolean operations on the curves themselves: `Path.Op` with `PathOp` (`Union`, `Intersect`, `Difference`, `Xor`,
  `ReverseDifference`) and shortcuts, `Path.Simplify`, and `PathOps.Combine` for many paths in one pass. Results keep the inputs'
  lines, quadratics and cubics, cut exactly where outlines cross, with outer contours clockwise and holes counterclockwise. Each
  input keeps its fill rule. Area identities hold on 100,000 random cases and 10,000 nearly parallel ones, and results are within
  a mean level of SkiaSharp's PathOps on the 2.6.2 corpus.
- `Stroke.ToPath`: the outline a pen draws, which fills to exactly the stroke's pixels; `simplify: true` removes its overlaps.
  `Path.Offset` grows or shrinks an area with a join.
- `Stroke.Dash` with `DashPattern` (absolute lengths and a phase, as PDF, SVG and Skia; `Scaled` for width-relative patterns),
  received by backends as data and cut along the curves by the raster backend.
- `PathMeasure` and `ContourMeasure`: lengths by adaptive Gauss–Legendre quadrature (within 1e-4 units, and 1e-3 of SkiaSharp's
  PathMeasure), positions, tangents, placing matrices and curve-keeping pieces.
- `PathEffect`: `Dash`, `Corner`, `Trim`, `Jitter` (seeded, the same on every thread and machine), `Stamp` with `StampStyle`,
  `Hatch` and `HatchPath`, combined with `Then` and `Plus`; `path.Apply` or `Stroke.Effect`. Within a mean level of SkiaSharp's
  matching effects.
- SVG path data: `Path.ParseSvg` (every command of SVG 1.1 and 2, compact numbers, arc flags without separators; throws
  `SvgPathException` with the character position, or keeps what comes before an error with `lenient: true`), `TryParseSvg`, and
  `ToSvg` with `SvgPathFormat` (absolute or relative, compact, a precision; relative offsets never drift). Fuzzed for 20 minutes.
- Boolean speed (ms, 1 thread, best of 7): 100 random 12-gon pairs under four operations 28 against SkiaSharp 39 and
  the reference drawing library 11; 50 curved pairs 12.5 against 4.7 and 5.4; 200 letters unioned 24 against 503 and 26.

### Egami.Drawing 1.0.0-preview.1

A new package, over Egami's public API only, that draws antialiased paths, shapes, strokes, clips and images on a canvas. A
preview: its API may change before 1.0. See the [Drawing guide](docs/articles/guides/drawing.md). Text arrives with Egami.Fonts,
below.

- `Canvas` is abstract: public calls are checked and tracked (transform, clip bounds, save stack) and each ends in one protected
  hook, so other backends (a PDF page, a recorder) see shapes and pens as data. `RasterCanvas<TPixel>` draws into any pixel type;
  `raster.CreateCanvas()` and the `Draw` operation step (per frame, inside `Within`) are the ways in.
- `Path` (immutable lines, quadratics and cubics with a `FillRule`) and `PathBuilder` with SVG endpoint arcs, tangent arcs, rectangles,
  round rectangles with per-corner radii (`RoundRect`, scaled as CSS does), ovals and polygons; `Contains`, `TightBounds`,
  `IsConvex`, `Reverse` and `Transform` by a matrix or a homography.
- Exact-area antialiasing: every pixel gets its covered area to 1/255, abutting shapes leave no seams, and results are identical at
  any core count. Fills are within a mean level of SkiaSharp and closer than SkiaSharp to the exact area.
- `Stroke` with butt, round and square caps, miter, round and bevel joins and a miter limit; width 0 is a one-pixel hairline.
- Antialiased clips by rectangle, round rectangle or path, intersecting or cutting out; pixel-aligned rectangles are free, and a
  fill through a mask costs about 1.16 times the plain fill.
- `DrawImage` under the transform and clip with Egami's samplers; at whole-pixel placement it equals `DrawImage` in a chain.
- Faster than the reference drawing library 2.1.7 in every scenario timed (ms, 1 thread): 1,000 small shapes 4.0 against 11.1, a 4000×4000
  star 19.8 against 49.4 (4.8 on 24 cores against 47.6), 10,000 icons 109 against 312, 200 thick curves 40 against 360, 10,000
  hairlines 48 against 208.

### Egami.Fonts 1.0.0-preview.1 and text on the canvas

A new package, over Egami's public API only, that reads TrueType and OpenType fonts in managed code; Egami.Drawing uses it to draw
text. A preview: its API may change before 1.0. See the [Fonts and text guide](docs/articles/guides/text.md).

- `Typeface` loads TrueType, OpenType CFF (CID-keyed too), collections and bare CFF streams as PDF embeds them, lazily from one
  block of memory; leniently by default, with `Font.*` diagnostics and documented defaults for missing or damaged tables, or
  strictly. Names in every language, `FontStyle`, `FontEmbedding` (reported, never enforced), cmap formats 0, 4, 6, 10, 12, 13 and
  14 with symbol and Mac Roman fonts, glyph outlines through `IGlyphOutlineSink` (TrueType quadratics, a Type 2 charstring
  interpreter with subroutines, flex and seac), advances, glyph bounds, `FontExtents` by the browsers' vertical-metrics rule, and
  GPOS pair kerning (or the kern table). Every table field, mapped code point, outline point, bound and sampled kerning pair of
  the test fonts equals fontTools'.
- `FontCollection` with `FontCollection.System`, found without native calls (503 installed faces indexed in 152 ms on first use),
  names in any language, CSS Fonts 4 style matching with the synthesis a lighter or upright face wants, and generic families.
- `FontSubsetter` writes TrueType, OpenType CFF or bare CID-keyed CFF subsets with composite and seac closure, dense or kept glyph
  ids and unused CFF subroutines dropped; every subset re-loads with its glyphs unchanged and opens in fontTools and FreeType;
  200 glyphs from a 16 MB CJK font in about 2 ms.
- `Font` (a typeface at an em size in canvas units), `Font.Shape` (cmap, pair kerning, newlines) and `MeasureText`; `GlyphRun`
  keeps the source text and a cluster per glyph. `Canvas.DrawText`, `DrawGlyphRun` and the `OnDrawGlyphRun` hook; small glyphs
  are drawn from `GlyphCache` masks at whole pixels, which changes speed, not pixels; `GetGlyphPath`, `GetTextPath` and
  `GlyphRun.ToPath`. Drawn text is within a mean level of SkiaSharp filling the same glyphs and closer than it to the exact area.
- Faster than the reference font library 2.1.3 at loading (0.18 against 127 ms for nine fonts), matching and measuring (3.3 against 122 ms
  for 1,000 strings), and than the reference drawing library 2.1.7 at drawing a 2,000-word page (15 against 402 ms at 12 pixels).
- Fuzz targets `font`, `cff` and `subset`. Shaping, bidi and wrapping come in 2.8; WOFF, WOFF2 and variable fonts in 2.7.2.

### Regions and masks (in progress)

- `Within(Rect, …)` runs any same-size chain so that only pixels inside the rectangle change. Filters read the real pixels
  around it, so a blurred rectangle has no seam; flips mirror the rectangle; custom operations work unchanged. Steps that change
  the size or the color encoding throw when the chain is recorded, naming the step.
- `Within(Mask, …)` mixes the chain's result with the original by a `Mask`'s coverage: any raster's gray value, alpha or
  luminance (`MaskChannel`), placed at an offset and optionally inverted. Coverage 0 and full are exact; 16-bit masks keep their
  gradient on 16-bit images. Scopes nest.
- `ExtractChannel` returns one channel (`ColorChannel`) as a gray raster and `CombineChannels` builds an image from gray rasters,
  both identical to ImageMagick's -separate and -combine at 8 and 16 bits. `ShuffleChannels` rearranges channels (`ChannelSource`)
  as one color matrix that fuses with the other color steps. `OnChannels` keeps a chain's result only in chosen channels
  (`ColorChannels`), as ImageMagick's -channel does.
- `AlphaToGray`, `LuminanceToAlpha`, `CopyAlpha` and `Flatten` match ImageMagick's -alpha extract, copy and remove; the JPEG
  writer's Background option now runs on Flatten's arithmetic, with unchanged bytes.

### Keying and arithmetic (in progress)

- `MakeTransparent`, `ReplaceColor` and `FloodFill` equal ImageMagick's -transparent, -opaque and -floodfill with -fuzz at 8 and
  16 bits; the tolerance is ImageMagick's color distance, and `MakeTransparent`'s softness ramps alpha for clean edges. `FloodFill`
  is four-connected by default (`Connectivity`). `ColorMask` and `FloodMask` return selections for `Within(Mask, …)`.
- `Arithmetic` applies a `PixelMath` operator with a constant, identical to ImageMagick's -evaluate at 8 and 16 bits.
  `Raster.Stack` combines several rasters by mean, median, min or max (`StackMode`) a few rows at a time.

### Streaming

- `RasterDecoder`: open an image and read it a band of rows (`ReadRows`) or a frame (`ReadFrame`) at a time, in any pixel
  type. `RasterInfo.Access` says whether a file streams or is decoded into a buffer first, and `WorkingBytes` what it holds.
- `ReaderOptions.Region` decodes only a rectangle, for every format, exactly as Load then Crop gives it.
- `Raster.LoadInto` decodes into a raster the caller already has.
- PNG, APNG, JPEG, GIF, BMP, TGA, PNM and QOI stream: `RasterDecoder` holds a few rows (or, for animations, one canvas) instead
  of the whole image. Interlaced PNG, progressive JPEG and bottom-up BMP and TGA on streams that cannot seek are buffered at the
  cheapest stage.
- `RasterDecoder.OpenIncremental`, `Append` and `EndOfInput` decode while bytes are still arriving; `Status` (`DecodeStatus`) and
  `HeaderRead` say how far it has got, and `RenderPreview` draws an interlaced PNG, a progressive JPEG or a GIF frame as far
  as it has arrived. Every 1.x format decodes as its bytes arrive, except files that are decoded into a buffer first.
- `LoadAsync` from a stream that cannot seek, such as an upload, decodes it in 256 KB chunks as it arrives instead of reading the
  whole file into memory first; results are the same. Seekable streams, plugins, progressive JPEG and files decoded into a buffer
  first keep 1.0's faster buffered path. `RasterDecoder.OpenAsync` with `ReadRowsAsync`, `ReadFrameAsync` and `NextFrameAsync`
  reads rows and frames from an asynchronous stream.
- `RasterEncoder.Create` with `WriteRows`, `WriteFrame` and `Complete` (and their async forms) writes an image a band of rows or
  a frame at a time, with the bytes `Save` writes. Options that need every pixel first throw `NotSupportedException` at
  `Create`. `PngWriterOptions.FrameCount` declares an APNG's frame count for streams that cannot seek. `SaveAsync` now writes
  each band out as it is encoded instead of holding the whole file.
- `Raster.Transcode` and `TranscodeAsync` decode, run a chain of streamable operations (crop, the resize family, horizontal flip,
  per-pixel color adjustments) and encode, a band of rows at a time. A leading resize shrinks on load and a leading crop
  becomes a region; other operations throw `NotSupportedException`. A 20,000 × 20,000 PNG to a 1,024-pixel JPEG peaks at
  0.4 MB of pixels.
- Plugins can stream: `IRowFormatReader` with `RowReader<TNatural>`, and `IRowFormatWriter` with `RowEncoder<TPixel>` and
  `EncodeStart<TPixel>`. Formats without them keep working through the buffered fallback.
- A 32-bit BMP whose alpha byte is zero everywhere now loads into 16-bit and premultiplied pixel types without a lossy round trip
  when the stream can seek.
- Behaviour change: `ReaderOptions.MinimumSize` now reduces every format, by the largest whole factor that keeps the image at
  least that big, averaging blocks of pixels. JPEG keeps its 1/2, 1/4 and 1/8 IDCT scaling and averages blocks beyond 1/8.
  The average is bit-identical to a full decode and a box resize, and a quarter-size decode is 1.2 to 2 times as fast as
  the reference library's `TargetSize` with the Box sampler.
- `ConvertColorTo` applies to streamed reads too, with the same HDR tone mapping as `ConvertColor`.

### Metadata

- IPTC: `IptcData`, `IptcEntry` and `IptcKey` read and write IPTC-IIM datasets, with typed getters for keywords, caption,
  headline, bylines, copyright, credit and the creation date. Text follows the coded character set (1:90), and Egami sets it to
  UTF-8 whenever a value is not ASCII; `Set` and `Add` enforce the IIM length caps. `RasterMetadata.Iptc` holds it, parsed on
  first use.
- XMP: `XmpData` parses a packet into properties by namespace URI and local name (`XmpName`, `XmpNamespaces`), each text, an
  `XmpArray` (bag, sequence or language alternatives) or an `XmpStruct`, with typed properties for the common Dublin Core,
  XMP Basic, Photoshop and TIFF values. `RasterMetadata.XmpData` is a live view of `RasterMetadata.Xmp`: an edit is what every
  format saves. DTDs are refused and nothing outside the packet is read.
- EXIF: the Windows XP tags (`ExifKey.XPTitle` and four more) and the encoded strings (`UserComment`, `GpsProcessingMethod`,
  `GpsAreaInformation`) read and write as text. `ExifData.ReadThumbnail` decodes the IFD1 thumbnail; `SetThumbnail` makes one
  from any raster.
- Behaviour change: JPEG keeps APP13 (`JpegDetails.PhotoshopResources`, with the IPTC) through load and save; 1.0 dropped it.
  After an IPTC edit the other Photoshop resources are written back unchanged and the IPTC digest is kept current.
- Behaviour change: PNG reads its XMP iTXt chunk and the legacy "Raw profile type" chunks (exif, APP1, iptc, xmp) into `Xmp`,
  `Exif` and `Iptc` instead of `Text`, and writes `Xmp` (1.0 ignored it) and `Iptc` (as "Raw profile type iptc").
- Behaviour change: flips, turns, rotation and auto-orient drop the IFD1 thumbnail, which would show sideways; crops and resizes
  already did.
- Behaviour change: `ExifEntry.AsString()` returns readable text for the XP tags and encoded strings instead of their raw bytes.
- Behaviour change: geometry operations move EXIF `SubjectArea` and `SubjectLocation` with the picture, and keep the XMP copies
  of the orientation, size and subject in step.

### Color management

- `ColorProfile.FromIcc` reads RGB, gray and CMYK ICC profiles, v2 and v4, matrix-shaper and table based; `ToIcc` writes
  v4.3 (v4.4 with cicp for HDR encodings). `IccData` and `IccTag` read any profile's header and tags.
- `ColorTransform` converts spans of any pixel types between two profiles under the four rendering intents, with black point
  compensation. It is compiled once and cached per `EgamiContext`: a matrix-shaper pair runs as curves, one matrix and curves
  with SIMD, anything else through a 33³ or 17⁴ grid. It matches Little CMS 2 within 0.0005 in float and one level in
  8 bits on 13,040 matrix transforms, and within ΔE2000 0.3 on table and CMYK ones; Display P3 to sRGB runs about 1.8× as
  fast as SkiaSharp.
- `RasterOps.ConvertColor`, `Raster.ConvertColor<TTo>` and `ReaderOptions.ConvertColorTo` convert images, the last while
  decoding, a band of rows at a time. `WriterOptions.OmitSrgbProfile` leaves out an embedded profile that is just sRGB.
- `RasterMetadata.CodePoints`, `Primaries` and `Hdr` (`HdrMetadata`) hold format-neutral color tags: PNG cICP, cHRM, gAMA,
  mDCV and cLLI and BMP endpoints are read into them and written from them. `GetColorProfile` resolves what the pixels mean.
- HDR: PQ and HLG convert to SDR with tone mapping (`ToneMapping.Bt2390` by default, `Reinhard`, `Clip`) and a hue-keeping
  gamut clip; `RasterOps.ToneMap` tone maps in place.
- Color values: `HslColor`, `HsvColor`, `XyzColor`, `LabColor` (with `DeltaE2000`), `LchColor`, `OklabColor`, `OklchColor`,
  `YCbCrColor` and `CmykColor`, all `IColorValue<TSelf>`; `ColorConvert` converts single values and whole rows (Lab, HSL,
  HSV and Y'CbCr rows run 1.1× to 11× as fast as the reference library). `YuvMatrix` and `YuvRange` name the Y'CbCr coefficients.
- Behaviour change: writers now emit the color tags held in `CodePoints`, `Primaries` and `Hdr` (PNG cICP, cHRM, mDCV, cLLI;
  BMP endpoints), which 1.0 left out.
- Behaviour change: operations that work in linear light (`ColorSpace.Linear`) now use the raster's own transfer curve, so a
  gamma 2.2 or Rec. 709 image is linearized with its curve rather than sRGB's.
- New enums: `RenderingIntent`, `IccProfileClass`, `ToneMapping`, `YuvMatrix`, `YuvRange`.

### Pixel types

- `Rgb16` and `GrayAlpha16`: 16-bit color without alpha and 16-bit gray with alpha, with exact conversions and SIMD to and
  from `Rgb8` and `GrayAlpha8`.
- `Argb8` and `Abgr8`: alpha-first byte orders, with SIMD byte shuffles to and from `Rgba8` and `Bgra8`.
- `RgbaF16`, `RgbF32` and `GrayF32`: half-float color, and float color and gray without alpha. Half conversions round as
  `(Half)f` does, with SIMD kernels identical to the casts for every one of the 2^32 float and 65,536 half values.
- `Bgr565`, `Bgra4444` and `Rgb10A2`: packed 16 and 32-bit color in the DXGI and Vulkan layouts. Widening and narrowing both
  round to nearest, so every route gives the same pixel and every packed value round-trips; SIMD kernels convert 5-6-5 to
  and from `Rgba8` and `Bgra8`.
- `Cmyk8` and `Cmyk16`: ink amounts, 0 meaning none, converted to and from RGB with the plain formula until color profiles
  arrive. `Raster.Load<Cmyk8>` of a CMYK or YCCK JPEG returns the file's inks without the RGB step. Resize, blur, sharpen,
  convolution and rotation filter the four inks, so black stays black; `DrawImage` blends inks; color adjustments go through
  RGB and rebuild black.
- `PremulRgba8`, `PremulBgra8` and `PremulRgbaF32`: color stored premultiplied by alpha. Conversions take and return straight
  colors (exact rounding both ways, SIMD for the 8-bit pairs, every stored pixel round-trips); resize, blur, sharpen,
  convolution, rotation and `DrawImage` run on the stored values directly. On translucent images resize is about 30% and
  `DrawImage` about 35% faster than on `Rgba8`.
- `Alpha8`: an alpha-only mask. It reads as black with that alpha; converting a color to it keeps only the alpha.

### Formats

- PFM float maps (PF and Pf) in the Netpbm family: both byte orders, loaded as `RgbF32` or `GrayF32` in linear light, written
  little-endian; `.pfm` saves as PFM, and `PnmDetails` gains `Scale` and `LittleEndian`. New enum value `PnmKind.FloatMap`.
- PNG and PNM read and write `Rgb16` and `GrayAlpha16` directly, and BMP reads 5-6-5 and 4-4-4-4 bit fields straight into
  `Bgr565` and `Bgra4444`, with no intermediate type. Natural types (what `Raster.Load` returns without a type) are unchanged.

### PNG writing

- `PngWriterOptions.Interlace` writes Adam7 interlaced PNG and APNG, for every color type, bit depth and palette. Off by default,
  and not taken from an interlaced source. A streamed encode holds the rows until the last one.
- `PngWriterOptions.ParallelCompression` compresses images with more than 4 MiB of image data in fixed 1 MiB bands on up to
  `MaxParallelism` threads. The bytes are the same at any thread count; files were 0.1% to 0.3% larger, and a 24-megapixel
  photo saved 8 times as fast on 12 cores. Off by default; with it off, or below the threshold, files are unchanged.

### JPEG reading

- Arithmetic-coded files (SOF9 and SOF10, sequential and progressive, with DAC conditioning) load like Huffman-coded ones, with
  the same pixels as libjpeg-turbo's djpeg, at every scale and when streamed. `JpegDetails.Arithmetic` says which a file is.
- 12-bit files (extended and progressive, Huffman or arithmetic) load in their natural 16-bit types, `Gray16`, `Rgb16` or
  `Cmyk16`, each sample scaled from 0..4095 to the full range; `Load<Rgba8>` and other 8-bit types round them. Every corpus file
  matches djpeg at 12 bits exactly, including at 1/2, 1/4 and 1/8 scale. `JpegDetails.Precision` gives the bits per sample.
- Lossless files (SOF3, and SOF11 with arithmetic coding) at 2 to 16 bits, every predictor and point transform, decode to
  exactly the samples djpeg gives (Thomas Richter's libjpeg for arithmetic coding, which libjpeg-turbo lacks): up to 8 bits as
  `Gray8` or `Rgb8`, deeper as `Gray16`, `Rgb16` or `Cmyk16`. They have no DCT, so a smaller size comes from the general
  reduction after a full decode. `JpegDetails.Lossless` says so. Hierarchical files are still refused.
- Large files decode on several cores, with the same pixels at every `MaxParallelism`. A sequential file with restart markers is
  split at the markers already in it; the scans of a progressive file run as a pipeline, each refinement scan following the
  scan before it row by row, and the last pass is split by rows. The 4000 × 3000 progressive benchmark photo decodes in half the
  time on 8 cores; the same photo with a marker every MCU row in under half. Files without markers decode as before.
- Faster arithmetic decoding than the reference library 3.1.12 at one thread and on all cores.

### JPEG writing

- `JpegWriterOptions.Progressive` writes progressive files with libjpeg's standard scan script (ten scans for color, six for
  gray) and Huffman tables optimized per scan. They decode to exactly the baseline file's pixels and are the same bytes as
  libjpeg-turbo's `cjpeg -progressive` at the same quality and sampling. The quantized coefficients are held until the last
  scan, about 2 bytes per sample, within `SafetyLimits.MaxTotalBytes`; a streamed encode buffers. Off by default, and a
  progressive source is re-saved as baseline unless it is set.
- `JpegColorType.Cmyk` and `JpegColorType.Ycck` write four components behind an Adobe marker, inverted as Photoshop writes CMYK;
  `Auto` picks CMYK for `Cmyk8` and `Cmyk16` pixels, which 1.x converted to RGB on save. YCCK subsamples its chroma and keeps
  black at full size. djpeg, ImageMagick and Pillow read every file to within a level of Egami's inks. 12 MP encodes faster than
  the reference library 3.1.12's CMYK and YCCK encoder at one thread (95 against 106 ms, 91 against 98) and on all cores (2.2 and 3.3 times
  as fast).
- `JpegWriterOptions.Precision = 12` writes 12-bit files (SOF1, or SOF2 when progressive) that keep the detail of 16-bit and
  float pixels, with optimized Huffman tables and 16-bit quantization tables where quality needs them. djpeg reads them within a
  level of Egami's decode; browsers, Pillow and many ImageMagick builds don't read 12-bit JPEG at all.
- TIFF's JPEG compression takes CMYK pages (see TIFF above).
- Arithmetic and lossless JPEG are still read only.

### Lossless JPEG transforms

- `JpegTransform.Apply` rotates (90, 180, 270), flips, transposes and transverses JPEG files on their DCT coefficients, as
  `jpegtran` does, so nothing is lost; `JpegTransformOperation.AutoOrient` turns a photo upright by its EXIF orientation and
  resets the tag. `Crop` keeps an area (its corner moved to the MCU grid), `Grayscale` keeps luma alone, and `Edges` says what
  happens to partial MCUs at a mirrored edge: `Trim` (the default, `-trim`), `Strict` (`-perfect`) or `Keep` (jpegtran's
  default). Every mode the reader takes works except lossless JPEG; arithmetic sources are written with Huffman coding.
- Results decode to the same pixels as jpegtran 3.0's on every chroma sampling, gray, CMYK, progressive, arithmetic and 12-bit
  sources, with partial MCUs on both edges, for every operation, edge mode, crop and gray conversion.
- Output is sequential with optimized Huffman tables by default, or progressive. EXIF, ICC, XMP, IPTC and comments are copied;
  EXIF's pixel dimensions follow the result and its thumbnail is turned too (dropped after a crop). A file can be transformed in
  place. The coefficients are held while it runs, within `SafetyLimits.MaxTotalBytes`.
- A 12-megapixel photo turns faster than jpegtran: 170 against 305 ms with optimized tables, 130 against 246 ms with standard
  ones, 260 against 458 ms written progressive.

### Pixel layouts

- New enum values, for code that switches on them: `PixelChannelOrder.Argb`, `Abgr`, `Alpha` and `Cmyk`;
  `PixelChannelType.Float16` and `Packed`; `AlphaKind.Premultiplied`.
- `PixelLayoutInfo` describes packed layouts such as 5-6-5: a constructor taking the pixel size and channel widths, and the
  `BitsPerPixel` and `ChannelBits` properties. `BytesPerPixel` and `BitsPerChannel` are unchanged for every 1.0 type.
- `IPixelType` gains `FromRgba16` and `ToRgba16` with default bodies, so types written against 1.0 keep compiling.
  Conversions between integer types wider than 8 bits now go through `Rgba16` and are exact; results for the 1.0 types are
  unchanged.
- Writers and color operations handle unknown, packed and premultiplied layouts: PNG and PNM write 8-bit samples for packed
  types no wider than 8 bits, and an unknown layout without alpha is written as RGB. Color operations never read premultiplied
  bytes as straight values.

## 1.0.0 (unreleased)

The first public release.

### Formats

- PNG and APNG: every color type and bit depth, interlaced, transparency, text, EXIF, XMP, ICC; writing with adaptive filtering,
  built palettes and changed-rectangle animation.
- JPEG: baseline and progressive, any subsampling, gray, CMYK and YCCK, scaled decode, pixels identical to libjpeg-turbo;
  writing at quality 1 to 100 with optimized Huffman tables, EXIF, XMP and ICC.
- GIF: every disposal, interlaced, transparency, loop count; writing with exact or quantized palettes (Wu's quantizer,
  Floyd–Steinberg or ordered dithering) and frame optimization.
- BMP: every Windows and OS/2 2.x header, 1 to 32 bits, RLE4, RLE8, RLE24, bit fields, embedded PNG and JPEG; writing 24 and
  32-bit and 1, 2, 4 and 8-bit palettes.
- TGA: color-mapped, true-color and gray, raw and RLE, all origins, the TGA 2.0 footer and extension area.
- QOI: the whole format, with output byte-identical to the reference encoder.
- PBM, PGM, PPM and PAM: plain and binary, any maxval.

### Operations

Resize, fit and cover with six filters, in encoded or linear light; crop, pad, flip, rotate, auto-orient; brightness, contrast,
saturation, hue, sepia, grayscale, invert, opacity and color matrices; Gaussian and box blur, unsharp mask and any convolution;
source-over drawing of whole images or areas. Chains are planned before they run and run in parallel bands of rows.

### Core

- Nine pixel types, and outside pixel types through `IPixelType<TSelf>`.
- Editable frames for animations; per-frame delays and loop counts.
- Typed EXIF reading and editing; XMP, ICC, text and resolution kept through edits and saves.
- Safety limits checked before any allocation; lenient or strict recovery from damaged files.
- Pooled pixel memory with live usage counts and opt-in leak tracking.
- Custom operations and formats through public interfaces, including animated formats.

### Diagnostics

- `EgamiContext.Diagnostics` takes an `Action<EgamiDiagnostic>` that receives Egami's warnings and notable decisions, so one line
  sends them to `ILogger`, Serilog or the console; Egami takes no logging dependency. Off by default and free: with no handler
  nothing is built, and a load or save allocates exactly what it would without diagnostics.
- Each `EgamiDiagnostic` has a level (`Warning`, `Info`, `Debug`; `EgamiContext.DiagnosticLevel` sets the lowest delivered), a
  source, a stable id such as `Png.ChecksumMismatch`, a message, and the byte offset and frame when known. Every id is listed in
  the diagnostics article.
- `RasterMetadata.Warnings` is an `IReadOnlyList<EgamiDiagnostic>`: every problem a lenient read recovered from, with its id. One
  id is kept at most 10 times per call, then counted; the handler gets the same list as each call ends.
- Saves report what they lose without being asked (alpha, depth, CMYK inks, frames, metadata the format cannot hold), and Info
  and Debug events say what Egami chose: the format detected, a scaled decode, conversion on load, writer settings, parallel
  bands, chain planning.
- `EgamiContext.StartDiagnostics()` and `EgamiInput.Diagnostics` let format plug-ins and other libraries report through the same
  handler, filter and cap; `EgamiInput.Warn` takes an id.
