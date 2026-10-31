# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Every library package, unreleased

- Each NuGet package carries `AGENTS.md`, a short guide for coding agents next to its readme: Egami's own names, the package's
  entry points, samples from the tested docs samples and common mistakes. The same guides are on the docs site under
  `agents/`, linked from `llms.txt`.
- Each NuGet package carries `THIRD-PARTY-NOTICES.md` at its root, so the notices of the third-party tables, data and code
  Egami uses (libtiff, Unicode, GS1, HarfBuzz and the rest) travel with the package, not only with the repository.

## Egami.Metafiles, unreleased

- A GDI pen whose dash pattern is shorter than a device pixel (zero-length user-style entries, say) draws solid instead of
  cutting each line into tens of thousands of dashes it could not show: fuzz-found 600-byte EMF files rendered four times at
  128 pixels took 0.9 s and now take 0.09 s. Pens with longer patterns draw as before.

## Egami.Heic, unreleased

2.9.10: HEVC inter frames (H.264 and HEVC Build Plan tasks 19 to 25). All additive.

- Egami.Heic's HEVC decoder decodes inter pictures: reference picture sets and the decoded picture buffer with output in order
  (bumping), merge, AMVP and temporal motion vector prediction, 8-tap and 4-tap interpolation, default and explicit weighted
  prediction, inter residuals and the deblocking filter's motion-based strengths, for Main, Main 10 and the range extensions
  formats to 12 bits. Every frame of the 147 JCT-VC HEVC_v1 and 35 RExt conformance streams in scope equals HM 18.0's and its
  decoded picture hash, on one thread and, for the streams with tiles, wavefronts or slices, on five.
- `HevcStreamDecoder`, `HevcDecoderOptions`, `HevcFrameInfo` and `HevcPictureKind`: a public HEVC video decoder, as
  `Av1StreamDecoder` is for AV1, for Egami.Video's HEVC support and any other source of HEVC access units (Annex B, or an hvcC
  record and length-prefixed samples).
- HEIC image sequences with inter frames (bursts, animations) load every frame, equal to libheif's `heif-dec -S`; a sequence
  with a still image loads as its sequence, as an all-intra one already did, and no longer gives a `Heic.SequenceFirstFrame`
  warning.

Video Build Plan task 31: HEVC decode speed. No API changes.

- HEVC decodes 3.5 times faster at 1080p and 5 times faster at 4K 10-bit on one thread: inter prediction, weighted
  prediction, SAO, the deblocking filter, the inverse transforms and intra prediction run with AVX2 or SSE2 (scalar code
  elsewhere, with the same output), and the merge and AMVP candidate lists stop at the candidate the stream picks. In
  instructions a frame it now takes 1.99 times FFmpeg's at 1080p and 1.73 times at 4K 10-bit, from 13.9 and 19.3 times.

## Egami.Formats.Extended, unreleased

Camera RAW, stage A (DNG). All additive; it ships with the rest of camera RAW in one release.

- `RawFormat`, registered by `WithExtendedFormats()` and `WithRaw()` ahead of TIFF, reads DNG 1.0 to 1.7: colour filter arrays
  of any pattern and LinearRaw, uncompressed integer and floating-point samples, lossless JPEG, Deflate with the integer and
  floating-point predictors, lossy JPEG and JPEG XL, in strips or tiles, with the linearization table, black level patterns
  and deltas, white levels, active area and default crop.
- Load develops the image (`RawReaderOptions`): white balance as shot, automatic or custom (`Temperature`, `Tint`) with
  dual-illuminant matrix interpolation and ForwardMatrix; bilinear, Malvar-He-Cutler and AHD demosaicing (`Demosaic`, AHD by
  default); exposure and BaselineExposure; highlights clipped, blended or rebuilt (`Highlights`); DNG opcode lists 1 to 3 and
  camera profiles (HueSatMap, LookTable, ProfileToneCurve); raw-domain wavelet luminance noise reduction with the file's noise
  profile (`LuminanceNoiseReduction`, off by default) and chroma noise reduction (`ColorNoiseReduction`, 25 by default); lens
  corrections from the file's opcodes (`LensCorrections`). Output is `Rgb16` sRGB, or rendered straight into the RGB space
  `ConvertColorTo` names, its ICC profile attached.
- `RawOutput.Preview`, `HalfSize` and `Mosaic` return an embedded preview, a half-size develop or the untouched sensor data;
  `ReaderOptions.MinimumSize` picks the smallest preview or half-size develop that covers it, and LoadAll gives the develop and
  each preview. `RawDetails` keeps the camera, sensor layout, levels, the develop's white balance and colour matrix, opcodes,
  previews, exposure settings and the maker note; EXIF, GPS and XMP come through.

DDS textures, stage A (reading). All additive; it ships with the rest of DDS in one release.

- `DdsFormat`, registered by `WithExtendedFormats()` and `WithDds()`, reads DDS textures: the legacy header (DXT1 to DXT5,
  ATI1 and ATI2, bit masks, luminance, P8 palettes, YUY2 and UYVY) and the DX10 header's DXGI formats, every one DirectXTex
  reads: BC1 to BC7 with BC6H signed and unsigned, UNORM, SNORM, sRGB, float, UINT and SINT formats, the packed floats and
  packed 565, 5551 and 4444 formats, depth and stencil, and packed and planar video. Each loads as its nearest pixel type,
  BC4, BC5, BC6H and BC7 bit for bit as the Direct3D specification defines them. Premultiplied files are un-premultiplied and
  `_SRGB` formats tagged sRGB.
- Mips, cube faces, array slices and volume depth slices: Load reads mip 0 of the first, LoadAll every surface (and with
  `DdsReaderOptions.IncludeMipLevels` every mip), `DdsReaderOptions.Surface` and `MipLevel` pick one, and
  `ReaderOptions.MinimumSize` picks the smallest mip that covers it. `DdsReaderOptions.ReconstructNormalZ` rebuilds blue for
  two-channel normal maps. `DdsDetails` keeps the DXGI format, mips, dimension, cube flag and alpha mode.

DDS textures, stage B (writing).

- Save writes DDS: the uncompressed formats and BC1 to BC5 (`DdsTextureFormat`), with a full mip chain made by `MipChain`
  (`DdsWriterOptions.MipLevels`), an encoder effort of 0 to 6 (`Effort`), sRGB formats (`Srgb`), premultiplied alpha
  (`PremultipliedAlpha`, DXT2 and DXT4 under the legacy header) and the legacy header whenever the format allows
  (`Dx10Header` forces DX10). SaveAll writes texture arrays and cube maps (`Cube`). A raster loaded at mip 0 and unchanged
  since writes the file's own mips.

DDS textures, stage C (BC7 and BC6H encoders).

- `DdsTextureFormat.Bc7`, `Bc6h` and `Bc6hSigned`: BC7 searches modes, partitions, rotations and p-bits by effort; BC6H fits
  HDR colour in the decoder's domain, with the two-region modes from effort 3. The `egami` command writes them with
  `--set dds.format=bc7` and the other `dds.` keys. A Textures guide says which format suits which content.
- Against texconv 2026.5.8.1: every file it writes decodes to its own decode, on every mip (BC4 and BC5 SNORM within an
  8-bit level, which texconv rounds through R8_SNORM), and at the default effort the encoders reach a higher PSNR on 28 photos:
  BC1 +0.84 dB, BC3 +0.65, BC4 +0.96, BC5 +0.98, BC7 +0.71. BC7 encodes 15.7 times as fast as texconv's CPU path, and BC1 and
  BC3 five times as fast as ImageMagick. Legacy Q8W8V8U8 files as texconv writes them (an alpha mask without the alpha flag)
  now load.

## Egami, unreleased

Arithmetic and lossless JPEG writing, stage A: arithmetic coding. All additive.

- `JpegWriterOptions.Arithmetic` writes arithmetic-coded JPEG (T.81 annex D's QM-coder with the models of annexes F and G):
  sequential (SOF9) and, with `Progressive`, progressive (SOF10) files, 8 and 12-bit, gray, YCbCr, CMYK and YCCK, any
  subsampling and restart interval, at the default conditioning so no DAC segment is written. Each file decodes, in Egami and
  in libjpeg-turbo's `djpeg`, to exactly the pixels of the Huffman file with the same options, and is no larger than
  `cjpeg -arithmetic`'s file. A sequential arithmetic file streams in one pass at either precision. An Ultra HDR gain map is
  left out with a `Save.GainMapDropped` warning; `GainMap` with `Arithmetic` throws `NotSupportedException`. The Huffman paths
  are unchanged.
- `JpegTransformOptions.Arithmetic` writes the transform's result arithmetic-coded, so a Huffman file can be repacked smaller
  with no change to its pixels, as `jpegtran -arithmetic` does. The default stays Huffman.

TIFF ZSTD (Build Plan "Egami TIFF: ZSTD, LZMA and LERC", tasks 1 to 4). All additive.

- TIFF reads ZSTD compression (50000), with the horizontal and floating-point predictors, through a fully managed Zstandard
  decoder written from RFC 8878 (no native code, no packages): every frame the zstd CLI writes at levels 1 to 22, with
  `--long`, with and without the checksum, decodes byte for byte, at about three quarters of libzstd's speed on one thread. A
  damaged strip keeps what decoded before the damage, with the usual short-strip warning.
- `TiffCompression.Zstd` and `TiffWriterOptions.ZstdLevel` (1 to 22, default 9, as libtiff's): TIFF writes ZSTD with a managed
  Zstandard encoder following libzstd's level ladder, within 5 % of libzstd's size at levels 1 to 15 and at 0.6 times its speed
  or better. libtiff and GDAL read Egami's ZSTD files, and Egami reads theirs, with identical samples. The predictor defaults as
  for Deflate; `ParallelCompression` and streamed saves work; `CompressionLevel` stays Deflate's.

AVIF extras (progressive AVIF, sample transforms, mini files, more encode options). All additive; defaults write and read what
they did, except that a layered image now loads its best layer.

- A progressive (layered) AVIF loads its best layer at the image's full size, equal to avifdec, instead of its first layer
  with the `Avif.BaseLayerOnly` warning, which is gone. `AvifReaderOptions.Layer` picks a layer (an lsel property in the file
  is honoured otherwise), and `AvifDetails.Layers` lists each layer's size.
- `RasterDecoder.RenderPreview` draws a progressive AVIF's best layer received so far at full size while its bytes arrive, as
  it draws a progressive JPEG's scans; other AVIF files still decode at the end.
- Sample transforms (sato items, AVIF 1.2): files that combine AV1 images into one of more bits, such as the 16-bit files
  `avifenc --depth 16` writes, load at the transform's depth, every expression the specification allows evaluated over the
  planes; `AvifDetails.HasSampleTransform` says so, and `AvifReaderOptions.SampleTransform = false` loads the fallback image.
- Files with the compact mini box (the mif3 brand of the HEIF amendment draft) load with their alpha, gain map, ICC, Exif and
  XMP; `AvifFormat` recognizes them by their ftyp minor version.
- `AvifWriterOptions.Progressive` writes two layers as `avifenc --progressive` does, and `Layers` up to four `AvifLayer`s, each
  with its quality and scale; `BitDepth = 16` writes 16 bits through a sample transform (8+8 lossless, 12+8 lossy), with the
  8- or 12-bit image as the fallback; `TargetSize` searches the quality for the largest file not above a size
  (`Avif.TargetSizeExceeded` when quality 0 is larger); `GainMapQuality` codes a gain map at its own quality;
  `FilmGrainDenoise` (1 to 50) denoises and writes AV1 film grain parameters that decoders add back. A raster loaded with
  `AvifReaderOptions.FilmGrain = false` gets its grain parameters written back on save. Layered images larger than one AV1
  frame are written in one layer (`Avif.LayersDropped`).

For camera RAW (Egami.Formats.Extended). Additive.

- `FormatInfo.ConvertsColor`: a format whose reader renders `ReaderOptions.ConvertColorTo`'s colour space itself (a RAW
  develop) gets the option passed through, instead of Egami converting the pixels after the load. Every other format loads
  as before.

JPEG XL extras, PR A: animations, layers and extra channels written. All additive.

- JPEG XL animations through `Save`: a raster with more than one frame is written as an animation, lossless or lossy, with
  each frame's delay (1 ms ticks) and the loop count, each frame cut to the rectangle that changed and blended or replaced as
  cjxl does from an APNG. Sizes are within 2 % of cjxl 0.12's at efforts 1 to 4 and lossy (at equal Butteraugli and
  SSIMULACRA2), and djxl decodes them to the same frames. `JxlWriterOptions.FirstFrameOnly` writes a still on purpose; before,
  only the first frame was written, with a `Save.FramesDropped` warning.
- `JxlImage.WriteLayers` and a public `JxlLayer` constructor: layers with their place, blend mode, duration and name, which
  `ReadLayers` reads back field for field.
- `JxlWriterOptions.ExtraChannels` and `JxlExtraChannelImage`: depth, selection masks, thermal data, spot colors and other
  extra channels of any bit depth and subsampling, coded lossless in lossless and lossy files. Lossless CMYK keeps its black
  channel instead of being converted to RGB.
- Lossless JPEG XL at effort 2 codes a frame of more than eight groups group by group, each with its own context model, as
  libjxl does: screenshots are up to 20 % smaller and match cjxl -e 2 within 0.01 %. Smaller images are written as before.

2.3.10: PSD layers. All additive.

- `PsdImage.ReadLayers` (and `ReadLayers<TPixel>`) reads every layer of a PSD or PSB with its pixels into a `PsdDocument`:
  `PsdLayerImage`s bottom to top, each in the type Load would use with straight alpha, at its own size and place, with its
  group in `ParentIndex` and its pixel mask as a `PsdMask`; CMYK layers come as an `InkRaster`. The channels are read in file
  order (raw, PackBits, Deflate and Deflate with prediction, 1 to 32 bits, PSD and PSB), so the stream need not seek; damaged
  layer data is a warning outside strict mode. A file without layers reads as one Background layer.
- `PsdDocument.Flatten` composites the layers as Photoshop does, with each layer's `Info` as changed: all of Photoshop's blend
  modes (Soft Light by Photoshop's formula), opacity and fill opacity, pixel masks with density and default color, vector masks
  (filled non-zero, antialiased), clipping, pass-through and isolated groups, blending ranges, solid color fills, and levels,
  curves, hue/saturation, brightness/contrast and invert adjustment layers. 8 and 16-bit documents blend their stored values,
  32-bit ones linear light. Effects, other adjustments, gradient and pattern fills, knockout and mask feathering are left out
  with one warning a kind (`Psd.EffectsSkipped`, `Psd.AdjustmentSkipped`, `Psd.FillSkipped`, `Psd.KnockoutSkipped`,
  `Psd.MaskFeatherSkipped`). `PsdDocument.FlattenInk` flattens CMYK documents onto inks with the ink canvas's math.
  `PsdFlattenOptions.ApplyAdjustments` turns adjustment layers off. On Photoshop-saved files (psd-tools' and ag-psd's test sets)
  the flattening is within a mean half level of Photoshop's own composite.
- `PsdReaderOptions.Composite` (`PsdComposite.Auto`, `Stored`, `Flatten`): by default Load, Probe and the row readers flatten
  the layers of a file saved without Maximize Compatibility, one layer at a time, instead of returning the white placeholder
  with `Psd.NoComposite`; `Stored` keeps the old behavior and `Flatten` flattens any file.
- `PsdLayer.FillOpacity` and `PsdLayer.LayerType` (`PsdLayerType`: pixel, text, shape, smart object, fill, adjustment, group);
  PSD's Linear Dodge and Subtract keys now map to `BlendMode.Add` and `Subtract`.
- `BlendMode` gains Photoshop's other modes: `Divide`, `LinearBurn`, `LinearLight`, `VividLight`, `PinLight`, `HardMix`,
  `DarkerColor`, `LighterColor` and `Dissolve` (values 31 to 39), in DrawImage, BlendColor, Compositor and on inks. Dissolve's
  pattern is a fixed hash of the pixel's place, not Photoshop's.

Small format gaps from the on-demand list. All additive.

- ICNS: Apple icon families read and write, registered in `EgamiContext.Default` next to ICO (`IcnsFormat`,
  `IcnsReaderOptions`, `IcnsWriterOptions`, `IcnsDetails`, `IcnsEntry`, `IcnsEntryEncoding`). Every entry kind reads: PNG,
  JPEG 2000, run-length encoded ARGB, run-length encoded RGB with its 8-bit mask, and the old 1, 4 and 8-bit icons on the
  Mac OS palettes with their 1-bit mask. Load gives the best entry and LoadAll every entry; Save writes 8-bit RGBA PNG
  entries, one a raster or `IcnsWriterOptions.MacSizes` (16 to 1024) resized from one, with a table of contents.
- BMP: `BmpWriterOptions.RunLength` writes `Indexed4` and `Indexed8` as RLE4 and RLE8, which ImageMagick and Pillow read
  with the same pixels. OS/2 Huffman 1D files, which threw `UnsupportedFormatException`, now load, with
  `BmpCompression.Huffman1D` in their details.
- `ColorProfile.ToIccV2` writes a version 2.4 ICC profile for software that reads only version 2: `curv` curves, text tags
  and the encoding's own white point. Little CMS converts through it as through the version 4 profile, within 1 in 255.

- `IMultiImageReader` and `IMultiImageWriter` are public, so a format plugin's files can hold several images that LoadAll
  returns and SaveAll writes, as TIFF pages and DDS surfaces are; `FormatDetails.PixelsChanged` is overridable, so a plugin's
  details can drop what held only for the pixels as loaded. Additive.
- Progress on long saves. All additive. `SaveProgress`, and overloads of `Save`, `SaveAsync`, `SaveAll`, `Transcode` and
  `TranscodeAsync` that take an `IProgress<SaveProgress>` and a `CancellationToken`, shaped like Egami.Video's
  `MediaProgress`: a report as the save starts, at most one every 250 ms, and a final one. Row writers (PNG, JPEG, BMP, TGA,
  TIFF, QOI, Netpbm, Radiance) count rows, GIF and APNG count frames and multi-page TIFF pages; AVIF counts AV1 superblock rows, lossy JPEG XL its
  stages and lossy WebP its macroblock rows. Lossless JPEG XL and WebP and the other formats report only start and end. The
  token now also stops a synchronous save, between bands and inside those codecs, and a canceled file save deletes the file.
  A save without a handler or token runs as before.
- Tracing through `System.Diagnostics.ActivitySource`. All additive. `EgamiTracing.SourceName` ("Egami") names the source;
  loads, probes, saves and transcodes start the activities `Egami.Load`, `Egami.Probe`, `Egami.Save` and `Egami.Transcode`,
  tagged with the format, size, frame or image count and bytes, and marked as errors when they throw. OpenTelemetry picks them
  up with `AddSource("Egami")`. While nobody listens, nothing is started or allocated.
- 8-bit blends through `Compositor` (the Porter–Duff operators and the integer blend modes, with or without coverage) run two
  to three times faster with the same bits: their vector lanes now inline into the kernels, and a solid color loads as one
  splat. A 512-pixel solid source-over row through coverage takes 0.78 µs instead of 2.35 µs; a 10-pixel row 54 ns instead of
  124 ns.

## Egami.Drawing, unreleased

- The ink canvas blends with core Egami's ink math, which the PSD flattener shares, and takes the new blend modes (Divide to
  Dissolve, 2.3.10); its pixels and speed are unchanged.

- Text on premultiplied 8-bit canvases blends a run's glyphs a row at a time instead of a glyph row at a time, with the same
  pixels (glyphs that would cover the same pixel go one after the other): 20 lines of 16 px text take 0.50 ms instead of
  0.93 ms. With Egami's faster 8-bit blends, Egami.SystemDrawing's `DrawString` of 20 lines takes 0.78 ms instead of
  1.00 ms (GDI+: 0.73 ms).
- Fills without antialiasing allocate nothing per row (about 88 bytes a row before), and thin shapes in a large box, such as
  a one-pixel diagonal line, rasterize on one thread instead of fanning out, with the same pixels: Egami.SystemDrawing's
  100 one-pixel `DrawLine` calls take 1.06 ms instead of 2.06 ms and allocate 287 KB instead of 3 MB (GDI+: 0.49 ms).
- An `ImagePaint` drawn with `GdiPlusHighQualityBicubic` or `GdiPlusHighQualityBilinear` and no rotation or skew sums each
  source row across once per thread instead of once per device pixel, with the same pixels: an EMF+ file of high-quality
  images through Egami.SystemDrawing plays in 5.4 ms instead of 14.4 ms (GDI+: 5.4 ms). Taps inside the image read its rows
  directly.
- Image paints convert their texels row by row straight into the shader, with no converted raster in between and no
  clearing: Egami.SystemDrawing's scaled `DrawImage` of a 640 × 480 photo takes 3.3 ms instead of 3.8 ms (GDI+: 3.5 ms).
- Fills without antialiasing take a shorter path on rows with exactly two edge crossings, as every row of a line, rectangle or
  other convex shape has: the one run between them, with the crossings and pixel columns computed as before, so the pixels are
  the same. Egami.SystemDrawing's 100 one-pixel `DrawLine` calls take about 30 % less time.
- An `ImagePaint` filled straight onto a raster canvas, mapped without rotation or skew and with no mip level, reads its image's
  rows as the shading reaches them instead of converting the whole image to float texels first, when the image is not the
  canvas's own pixels; the pixels are the same. Each shading thread keeps the few rows it read last. Egami.SystemDrawing's
  scaled `DrawImage` of a 640 × 480 photo allocates 6 KB instead of 4.9 MB, and drawing a 64 × 64 part of it takes 0.7 ms
  instead of 2.6 ms.

## Egami.Fonts, unreleased

- Shaping text for the first time takes about 30 % less: each glyph's GDEF properties are looked up in the class tables once
  per face and kept (the tables are searched for every glyph before, and again after each substitution), code points 0 to 255
  map through a 256-entry table read once from the character map, and `Font.Shape` fills the run's arrays in place instead of
  growing lists and copying them. The glyphs and positions are the same. Egami.SystemDrawing's `MeasureString` of a line it has
  not measured before takes about 8.3 µs instead of 12 µs on a slow cloud machine.

## Egami.SystemDrawing, unreleased

- `Graphics.MeasureString` takes about 0.7 µs instead of 7.1 µs for a 43-character line it has measured before, and allocates
  0.7 KB instead of 13 KB, with the same sizes; GDI+ takes 0.56 µs. Shaped runs of strings up to 256 characters are kept per
  font (up to 1,024, emptied when full), a family's face match is kept per style, and measuring builds no per-character
  list. A string not measured before still costs about 7 µs, the cost of shaping it. `DrawString` gets the same cache and
  allocates half as much (239 KB instead of 451 KB for 20 lines).

## Egami.Cli, unreleased

- `--progressive`, `--target-size`, `--qgain-map` and `--denoise` on every command that writes, for the AVIF extras (and
  `--progressive` for JPEG and JPEG XL too); 16-bit AVIF is `--bit-depth 16`.
- `egami layers in.psd folder`: each layer of a PSD or PSB as a PNG at its own size, each pixel mask as a gray PNG, and
  `layers.json` with every layer's and group's name, place, size, blend mode, opacity, visibility and group (2.3.10). Also an
  MCP tool.

## Egami 1.3.1 (2026-10-29)

A bug-fix release of the Egami train for GDI+-look text, with Egami.SystemDrawing 1.0.0-preview.2. Egami.Drawing and
Egami.Fonts carry the fixes below; Egami, Egami.Svg, Egami.Css, Egami.Heic, Egami.Cli and Egami.Formats.Extended have no
changes and ship as 1.3.1 to keep the train together. Two members are added (`GdiPlusText.Baseline` and
`Font.GdiPlusAscent`); nothing is removed or changed.
Egami.SystemDrawing 1.0.0-preview.2 needs Egami 1.3.1.

## Egami.Drawing 1.3.1 (2026-10-29)

- `Antialiasing.GdiPlus` draws two to three times faster and allocates nothing per fill once warm, with the same pixels: a
  filled ellipse through Egami.SystemDrawing with `SmoothingMode.AntiAlias` takes 78 µs instead of 255 µs, close to Egami's
  exact coverage.
- `LcdSampling.Gdi` (the GDI+ look's ClearType) keeps a hinted TrueType font's dropout control (SCANCTRL and SCANTYPE), as
  GDI+ does, so thin horizontal strokes no longer vanish: SimSun's bars at 18 to 24 px, about a third of a pixel thick, fell
  between two rows' sample points. With embedded bitmaps off, SimSun at 18, 20 and 24 px now differs from GDI+ by 0.00 to
  0.88 levels per pixel instead of 0.27 to 11.35; Microsoft YaHei, whose dropout leaves out stubs, stays within 0.08. Latin
  text matches GDI+ slightly closer. Egami's own sampling is unchanged. Egami.Fonts' hinter now reports each glyph's scan
  control, internally.

## Egami.Drawing and Egami.Fonts 1.3.1, Egami.SystemDrawing 1.0.0-preview.2 (2026-10-29)

Where GDI+ puts text in a layout box (2.7.6 task 28), measured on Windows. Additive; Egami's own text output is unchanged.

- `GdiPlusText.Baseline(font, box, align, typographic)` (Egami.Drawing) gives the baseline `Graphics.DrawString` puts one line
  on in a box with a line alignment, and `Font.GdiPlusAscent` (Egami.Fonts) the ascent below the line's top it uses: for a
  grid-fitted font, the cell ascent rounded or the VDMX maximum at the size, in whole pixels. All 9,720 baselines probed
  (10 fonts, 11 to 24 px, box heights, fractional positions, both formats, three alignments) equal GDI+'s, and the email
  button of the report matches GDI+ with no vertical shift (mean difference 0.91 levels on black, 0.50 on #0078d4).
- Egami.SystemDrawing's `DrawString` follows the same rule: one line is a cell tall plus 1/8 em with the default format (more
  lines are a line spacing each), and ClearTypeGridFit puts the first baseline that whole-pixel ascent below the rounded line
  top. Of 3,900 single-line ClearType placements probed, 3,899 now equal GDI+'s (1,362 before); AntiAlias placements improve
  from 1,634 to 2,751. With `StringFormatFlags.LineLimit` (as in `GenericTypographic`) text of one line is drawn in a box
  shorter than a line, as GDI+ does. `FontFamily.GetCellAscent` and `GetCellDescent` use the typographic metrics of fonts that
  set USE_TYPO_METRICS, as GDI+ does.

## Egami.Fonts 1.3.1 (2026-10-29)

- A face that `FontFallback` picks now takes all of the font's rendering settings: `EmbeddedBitmaps`, `HintingTarget`,
  `Metrics`, `Embolden`, `StemDarkening` and `ColorGlyphs` were dropped before (the palette stays the face's own). So SimSun
  found by fallback from `Font.WithGdiPlusLook()` text draws its 12 to 17 px bitmaps, as GDI+ font linking does (probed: the
  linked face's aliased bitmaps), and grid-fitted metrics apply to fallback runs too. Fonts that set none of these shape and
  draw as before.

## Egami.Video.H264, unreleased

Video Build Plan task 30: general H.264 decode speed. No API changes.

- H.264 decoding takes about 2% fewer instructions a frame at 1080p, progressive and MBAFF, with the same output: small hot
  helpers of the deblocking strengths, motion and CABAC syntax inline, the CABAC engine's saved state stays in registers,
  and short row fills are single stores.

## Egami.Video.Hevc, unreleased

V8: HEVC decode (H.264 and HEVC Build Plan tasks 26 to 28). A new opt-in package at Egami.Video's version.

- `HevcCodec`, `HevcFormat` and `WithHevc()`: HEVC video in MP4, MOV, Matroska and raw Annex B `.h265` files decodes through
  Egami.Heic's `HevcStreamDecoder`, Main, Main 10, Main 12 and the format range extensions to 12 bits. Frames equal FFmpeg's on
  the 23 clips of the new HEVC test corpus (`tools/video/make-hevc-corpus.py`), and HM's on the one whose chroma FFmpeg 5.1
  and 8.1 decode against its own picture hashes; every corpus picture also matches its MD5 picture hash.
- HDR10 mastering display and content light level SEI fill the frame's `Hdr`, HDR10+ SEI its `Hdr10Plus`; Dolby Vision dvh1
  and dvhe tracks decode as their HEVC base layer.
- Decoding starts at an IRAP picture, after a seek too, and drops the RASL pictures a CRA picture skips there; broken data
  reports `Hevc.CorruptFrame` and follows the error mode.
- An `hevc` fuzz target, and `--time-hevc` in the video benchmarks.

## Egami.Video.Cli, unreleased

- `egami-video overlay --key auto` reads the screen colour from the overlay's first frame: the median of its green or blue
  pixels, whichever covers more of the frame (refused when neither covers 5 %). Without `--blend`, auto also picks a softer
  edge for a pale screen, so grays and whites in front of it stay solid.

## Egami.Video.Cli 1.1.0 (2026-10-29)

- `egami-video remux --fragment` and `encode --fragment` write fragmented MP4 (`--fragment=4s`, `--fragment=keyframe`), and
  `segment --hls --dash` writes CMAF segments with HLS and DASH playlists into a folder. ffmpeg mode takes `-movflags
  faststart`, `frag_keyframe`, `empty_moov` and `default_base_moof`, and `-frag_duration` with frag_keyframe.
- V9.1 stage D (Build Plan tasks 16 to 19): `egami-video audio` and `encode` (for its `--audio`) take `--gain`, `--fade-in`,
  `--fade-out`, `--tempo`, `--normalize` (ebu, atsc, streaming, podcast or LUFS, two passes), `--trim-silence` (refused with
  video) and `--af`, which parses ffmpeg's `-af` syntax for volume, afade, atempo, adelay, apad, atrim, asetpts, highpass,
  lowpass, bandpass, bandreject, allpass, equalizer, bass, treble, acompressor, agate, alimiter, loudnorm and silenceremove;
  `run` takes them all but the silence cut for the audio it copies, and ffmpeg mode turns an `-af` chain of them into
  `audio --af`. A new `afilters` fuzz target runs random chains.
- FFV1 in egami-video: `encode` and `run` take `--codec ffv1` with `--slices`, `--no-slice-crc`, `--coder` and `--context`, and
  `--keyframe-interval` for every codec; `egami-video verify` (also an MCP tool) checks FFV1 files' slice and configuration CRCs
  without decoding; `info` names FFV1 tracks; ffmpeg mode takes `-c:v ffv1` with `-level 3`, `-coder`, `-context`, `-slices`,
  `-slicecrc` and `-g`.
- `egami-video run` reads and writes Egami.Formats.Extended's formats, with `--set sgi.rle=` and `--set sunraster.rle=` for
  their writer options.
- `egami-video subs convert`, `shift`, `sync`, `extract` and `embed` (also MCP tools): SRT, WebVTT and ASS files converted,
  cut to a range, sorted and with overlaps fixed; shifted, scaled (`--fps 23.976:25`) and synced by one or two points; a media
  file's text subtitle tracks written as files; subtitle files added to MKV, WebM or MP4 as tracks with languages, names and a
  default. `info` and `embed --add` take subtitle files as inputs.

## Egami.Video 1.1.0 (2026-10-29)

With Egami.Video.H264 1.1.0. All additive to 1.0. The V9.1 stages B to D API review was accepted on 2026-10-29 (all as
recommended), with no API changes.

MP3 decode speed (the V5.8 speed gate, left for 1.0.x). No API changes.

- `Mp3Codec` decodes 2.3 to 3 times faster and now meets its gate of 1.5 times minimp3's time on one thread: 0.93x on CBR
  128 kbit/s joint stereo, 0.92x on VBR V2, 1.01x on MPEG-2 64 kbit/s mono and 0.86x on Layer II 192 kbit/s (minimp3's SIMD
  build; egami-compare `video/native/mp3-gate.py`, whose timing now warms up for a second so tiered compilation settles).
  The output is bit for bit what 1.0.0 decodes (81 clips and compliance streams compared). The synthesis filterbank, its
  DCT-II, the IMDCT windows, the antialias butterflies and the sign changes run four lanes at a time in the layout of
  minimp3's SIMD build (CC0), with the same products and sums in the same order; Huffman decoding sets signs without
  branches and reads its code tables unchecked; Layers I and II read whole words and divide grouped codes by a reciprocal.

V7.1: interlaced H.264 (Build Plan tasks 16 to 18). All additive.

- Egami.Video.H264 decodes interlaced streams: field pictures (PAFF) with field reference lists, marking and order counts,
  and MBAFF frames, whose macroblock pairs are frame or field macroblocks, with MBAFF neighbours, co-located motion, context
  selection and deblocking. A frame's fields come out woven. All 15 new MBAFF clips of the corpus (x264 interlaced, CAVLC and
  CABAC, P and B slices, spatial and temporal direct, weighted prediction, the 8x8 transform, several slices, High 10 and
  4:2:2) equal FFmpeg's decode frame for frame, and so do all 84 interlaced and FRExt conformance bitstreams of H.264.1
  (`tools/video/make-h264-conformance.py` fetches them into `.cache`; `ConformanceTests` runs them when they are there).
  Progressive decoding is as fast as before (1080p: 60.9 M instructions a frame before and after).
- `VideoFrame.FieldOrder` and `VideoFrame.RepeatFields`: whether a frame is interlaced and which field comes first, and the
  fields it is repeated for, from the picture timing SEI's pic_struct or from how the frame was coded, matching ffprobe's
  interlaced_frame, top_field_first and repeat_pict. `VideoReader.ReadFrames` keeps them.
- A field picture without its second field reports `H264.UnpairedField` and fills the frame from its own rows.

V6 FFV1 API review (Build Plan task 17), accepted 2026-10-28 with every question as recommended: `Ffv1EncoderOptions.BitsPerSample`
is now `BitDepth`, matching `Vp9EncoderOptions` and `Av1EncoderOptions`; everything else stays as built.

V6 FFV1 stage C: the FFV1 encoder (Build Plan tasks 11 to 14). All additive.

- `Ffv1Codec` now encodes (`CanEncode`): version 3 in Matroska and AVI, through `VideoWriter` (`VideoWriterOptions.Ffv1`) or
  `CreateEncoder` (`VideoEncoderOptions.Ffv1`). Planes keep their layout (4:2:0, 4:2:2, 4:4:4, 4:1:1, 4:1:0, gray, with or
  without alpha, 8 to 16 bits); RGB rasters and Identity-tagged 4:4:4 planes are coded as RGB through the reversible color
  transform. Defaults: every frame a keyframe, slice CRCs, the range coder with a custom state table, slices picked from the
  frame size. Slices are coded in parallel with the same file at any thread count.
- `Ffv1EncoderOptions` (`Slices`, `SliceCrc`, `Coder`, `ContextModel`, `BitDepth`) with `Ffv1Coder` and
  `Ffv1ContextModel`. `KeyframeInterval` spaces keyframes; `Effort` 7 and up picks the large context model; `Quality` is
  ignored and reported as `Ffv1.QualityIgnored`.
- Gates: all 51 corpus clips round trip in every coder; FFmpeg decodes 294 re-encoded files (every clip in five or six settings,
  Matroska and AVI) to the same samples (`tools/video/check-ffv1-encoder.py`). At matched settings files are -0.17 % to +0.59 %
  of FFmpeg's size at 1080p and 2160p, 8 and 10 bits; encoding takes 0.94 to 1.26 times FFmpeg's time on one thread and 0.79 to
  1.05 times on all cores (`tools/video/time-ffv1.py`, benchmarks `--encode-ffv1`). Steady-state encoding allocates 168 bytes a
  frame beyond the packet on one thread.
- The decoder's Golomb-Rice parameter is now worked out with a logarithm instead of a loop.

V6 FFV1 stage B: decode speed and fixity checks (Build Plan tasks 9 and 10). All additive.

- FFV1 slices decode in parallel up to `EgamiContext.MaxParallelism`, the same frame at any thread count, and the line decoders
  were reworked (branch-free median and sign, neighbours kept in registers, no bounds checks). On 1080p camera-like clips one
  thread now takes 0.80 to 0.86 times FFmpeg's time with the range coder and 1.18 times with Golomb-Rice (gate: 1.5); on all
  cores 0.62 to 0.83 times. Steady-state decoding allocates 168 bytes a frame beyond the frame itself on one thread.
- `Ffv1Codec.Verify` (a path or a stream) checks the configuration record's CRC and every slice's without decoding pictures, about
  50 times faster than a decode, giving an `Ffv1VerifyResult` with each `Ffv1VerifyError`.
- `tools/video/time-ffv1.py` makes the speed gate's clips and times FFmpeg; the benchmarks' `--time-ffv1` times Egami.Video.

V6 FFV1 stage A: the FFV1 decoder (Build Plan tasks 1 and 3 to 8). All additive; speed, the fixity check, the encoder and the
API review come in later stages.

- `Ffv1Codec` (`VideoCodecs.Ffv1`, `CodecIds.Ffv1`, registered by default) decodes FFV1 (RFC 9043) versions 0, 1 and 3:
  Golomb-Rice and the range coder with the default or a custom state table, both context models, coded initial states and
  quantization tables, non-keyframes, YCbCr 4:2:0, 4:2:2, 4:4:4, 4:1:1, 4:1:0 and gray with or without alpha, and RGB with or
  without alpha through the reversible color transform, at 8 to 16 bits. All 51 clips of the new corpus
  (`tools/video/make-ffv1-references.py`) equal FFmpeg's decode frame for frame. Depths 9, 11, 13, 14 and 15 sit in the next
  supported depth with the samples shifted up; RGB comes out as G, B and R planes with `YuvMatrix.Identity`.
- Slice CRCs are checked: `Ffv1.SliceCrc`, `Ffv1.ConfigCrc`, `Ffv1.CorruptFrame` and `Ffv1.MissingReference` follow
  `DecodeErrorMode`, and `Conceal` fills a damaged slice from the last good frame.
- AVI tracks with FourCC `FFV1` are now `V_FFV1` with the configuration record as `CodecPrivate` (they were
  `V_MS/VFW/FOURCC`), and the AVI writer stores `V_FFV1` tracks, so FFV1 remuxes between Matroska and AVI. Version 3 tracks
  give `BitDepth` and `ChromaFormat` from the record. `CodecHeaders.Describe` lists FFV1 parameters and slice headers.

V9.1 stage C: loudness normalization and silence removal in `Egami.Video.Audio.Filters` (Build Plan tasks 12 to 15). All
additive.

- `LoudnessTarget` (integrated, true peak, range) with the `EbuR128`, `AtscA85`, `Streaming` and `Podcast` presets, and
  `LoudnessNormalizer`: linear from a first pass's `LoudnessReport` (one gain, a true-peak `Limiter` only when the gain would
  take the peaks over the ceiling), or one pass with `LoudnessNormalizer.Dynamic` (1.5 s lookahead, kept within the range).
- `AudioFilters.NormalizeLoudnessAsync` measures and normalizes every audio track of a file, copying the other tracks, and
  returns a `LoudnessNormalization` per track; `AudioFilters.ApplyAsync` runs filters of your own over a file's audio tracks.
- `SilenceRemover` with `SilenceRemoval.Trim` (start and end) and `Shorten` (pauses over 1 s cut to 0.3 s), -50 dBFS RMS over
  20 ms, 10 ms fades and crossfades at the cuts.
- Gates: on the EBU Tech 3341 cases 1 to 5 and Tech 3342 cases 1, 3 and 4 (generated from the specs' descriptions) the linear
  normalizer lands within 0.5 LU of the EBU R 128, streaming and ATSC targets, under their true-peak ceilings, with the range
  kept within 0.2 LU; a tone with clicks lifted 16 dB stays within 0.5 LU and under -0.9 dBTP. The dynamic mode lands within
  0.5 LU on the 3341 cases and 1.5 LU on the 3342 steps, with the range within 1.5 LU of the target's.

V9.1 stage B: EQ and dynamics in `Egami.Video.Audio.Filters` (Build Plan tasks 8 to 11). All additive.

- `Biquad` with the eight Audio EQ Cookbook `BiquadKind`s (low and high pass, band pass, notch, all pass, peaking, low and high
  shelf), its width a Q or, with `Biquad.FromBandwidth`, octaves; `Butterworth` low and high pass of order 2 to 8; and
  `ParametricEqualizer` of `EqualizerBand`s. Transposed direct form II with double state, in place.
- `Compressor` (threshold, ratio, soft knee, attack, release, makeup, `LevelDetection.Rms` or `Peak`) with an optional
  sidechain (`Sidechain`, `SendSidechain`, `CompleteSidechain`) for ducking, and `Gate` (threshold, ratio, range).
- `Limiter` with a 5 ms lookahead that keeps every sample under its ceiling, and optional `TruePeak` detection by 4x
  oversampling (BS.1770).
- Gates: every biquad kind, Butterworth order and the parametric EQ within 0.1 dB of the analytic curve; low and high pass, band
  pass, band reject, all pass, equalizer (Q and octave widths), bass and treble within -90 dBFS RMS of FFmpeg's output (new
  references from `tools/video/make-audio-filter-references.py`; FFmpeg's shelf `w=0.5` is the cookbook's Q of 0.7071). On
  48 kHz stereo a gain, high pass and 3-band EQ chain runs 790 times real time (gate 200), an order 8 Butterworth 800 times, and
  compressor, gate and true-peak limiter 134 times (`AudioFilterBenchmarks`). A steady EQ and dynamics chain allocates only
  the chain's frame object (128 bytes per frame).

V9.1 stage A: streaming audio filters in `Egami.Video.Audio.Filters` (Build Plan tasks 1 to 7). All additive; the stage A
API review was accepted on 2026-10-28 with no API changes, and the docs now say that filter times are the frames' own
timestamps.

- `IAudioFilter` (`Send`, `TryReceive`, `Flush`, `Reset`, `Latency`), the `Resampler` shape, and `AudioFilterChain`, which runs
  filters on one input in one pass. Frames of any format go in, F32 planar frames with exact timestamps come out; filters that
  keep the length work in place, so a steady chain allocates no sample memory (130 bytes of frame object per frame).
- `Gain` (decibels or a factor, 5 ms ramp on changes) with `GainEnvelope`, built of `Keyframe`s with an `Easing`.
- `AudioFade` (named `Fade` before the V9 review) with `FadeDirection` and five `FadeCurve`s, and the two-input `Crossfade`, matching FFmpeg's `afade` and
  `acrossfade` curves.
- `AudioMixer` (per-input weights, `Normalize` off by default, `MixDuration`, inputs placed by timestamp and resampled and
  remixed on entry) and `Pan` (constant-power position, balance, swap, invert).
- `Delay` (per channel), `Pad`, `Trim` and `Retime`.
- `Tempo` (WSOLA, 0.25 to 4, `TempoPreset.Music` and `Speech`, pitch and exact length kept) and `Speed` (resampled, pitch
  follows).
- Gates: gain, fades, crossfades, pan, mix and delay within -144 to -152 dBFS RMS of FFmpeg's output (gate -90 dBFS, references
  from `tools/video/make-audio-filter-references.py`); tempo length exact and pitch within 1 % from 0.25 to 4. On 48 kHz stereo
  a gain, fade, pan and envelope chain runs 520 times real time, `Tempo` 610 times and `Speed` 500 times
  (`AudioFilterBenchmarks`).

V9 Editing API review for stages C and D (task 25), accepted 2026-10-25 (all as recommended), with tasks 21 to 23's gates.

- `StabilizerOptions` is now `MotionAnalysisOptions`, so it no longer reads like `StabilizeOptions`.
- `StabilizeOptions.MaxZoom` (default 1.5, like vidstab's `maxzoom`) caps the zoom that hides the edges; the edit file stores
  it as `maxZoom`.
- Kept as built: `Smoothing` in frames, `FrameMotion` in pixels and radians, `Stabilizer.Analyze` of a whole file,
  `ClipStabilization`'s constructor, smart render off by default with warnings only, and ffmpeg mode's refusals
  (freezeframes, loudnorm, and picture and sound changed apart).

V9 Editing stage D: ffmpeg mode's editing filters, smart render and the editing guide (Build Plan tasks 22 to 24). All
additive; the API review for stages C and D comes next (task 25).

- `egami-video ffmpeg` renders `-vf`, `-af` and `-filter_complex` graphs of fade, setpts, reverse, loop, tpad, fps, scale, xfade,
  concat, overlay, hstack, vstack, xstack, zoompan, chromakey, colorkey, despill, hflip and vflip, with afade, acrossfade,
  amix, atempo, volume, areverse and aloop, as one edit (`--explain` prints the edit file); `enable='between(t,a,b)'` sets a
  layer's or an effect's time window, `-loop 1` takes an image, and deshake and the vidstab pair run `stabilize`. Lines the
  edit cannot match stop with exit code 2 and the reason: freezeframes (it adds no time), loudnorm, setpts without the same
  atempo, and expressions beyond the documented forms.
- Gate: 78 copied ffmpeg lines run on real clips with the right length and, where ffmpeg would write one, a sound track as
  long as the picture, or stop with the right message (`ffmpeg-edit-lines.txt`).
- Smart render (task 23): `EditRenderOptions.SmartRender` and `--smart` on the editing commands copy the untouched parts of
  clips (no transition, fade, layer, effect or change of time, the canvas's size and rate, and a stream that matches the
  encoder's) packet for packet from the first keyframe on, with V5.6's stream checks; the encoder starts again with a keyframe
  after each copied part. Gate: VP8, VP9 and AV1 edits with a fade, a dissolve and a layer decode cleanly to the full render's
  frame count, within 28 dB of it, with 60 to 72 of 90 packets copied byte for byte.
- The editing guide gains plug-in points and a model on every frame: a tested `IRasterOperation` adapter that runs a model
  on a planar float tensor, with a stand-in model, and the ONNX Runtime lines an app would use, marked as untested app code.

V9 Editing stage C: stabilization (Build Plan tasks 20 and 21). All additive; the API review for stages C and D comes after
stage D (task 25).

- `Stabilizer.Analyze` measures a clip's `CameraMotion` (a `FrameMotion` per frame: move, turn and zoom from the frame before)
  with Shi-Tomasi corners, pyramidal Lucas-Kanade tracking and a RANSAC similarity fit on a downscaled luma copy
  (`MotionAnalysisOptions.AnalysisWidth`, 480 by default); it saves as JSON. Known synthetic shakes up to 6 px and 1 degree are
  recovered within 0.18 px at the frame's corners (gate 0.25 px).
- `Clip.Stabilize` with `StabilizeOptions` (`Smoothing`, `Edges`: `StabilizeEdges.Zoom`, `Black` or `Blur`) smooths the camera
  path with a Gaussian and warps each frame onto it; the edit file stores it with the motion.
- egami-video `stabilize` (also an MCP tool), with `--smoothing`, `--edges`, `--zoom auto|none`, `--save-motion` and `--motion`.
- Gate in egami-compare: on 10 shaky clips the shake left is within 1.2 times what FFmpeg's vidstab two passes leave at the
  same smoothing (10 of 10; 0.10 to 1.16 times).

V9 Editing API review for stages A and B (task 19), accepted 2026-10-25 (all as recommended), with task 18's gates.

- The V9.1 audio filter `Fade` is now `AudioFade`, so it no longer clashes with `Egami.Video.Analysis.Fade`.
- `VideoEdit.ReadFrames` renders an edit's pictures as `VideoFrame<Rgba8>`s without encoding, for a preview or an app's own encoder.
- `ChromaKey` defaults to `Similarity` 0.05 and `Blend` 0.35 (they were 0.1 and 0.05), which recovers a synthetic soft-edged
  alpha within 0.75 % instead of 3.9 %; `egami-video overlay --key` and edit files take the same defaults.
- The overlay's speed (2.2 times FFmpeg's at one thread, its plane resize) is accepted below the 1.5x gate for now.

V9 Editing task 18: the gates in egami-compare, and what they needed.

- Edits render cuts, fades, hard wipes and opaque, unkeyed layers straight from the decoded planes when they match the canvas,
  without going through RGBA, as FFmpeg's filters do; a 1080p dissolve renders in 0.31 times FFmpeg's time at one thread.
- `EditRenderOptions.Context` sets the context an edit renders with, such as one with fewer threads.
- A seekable YUV4MPEG2 file of plain FRAME lines now reports its duration (`MediaInfo.Duration` and the track's), and the
  editor counts packets for a source that reports none. `egami-video sheet` of such a file now spaces its frames over that
  duration (its reference picture was made again).

V9 Editing stage B: speed, reverse, loops, freezes, stills and Ken Burns, and time windows on run steps (Build Plan tasks 12
to 17). All additive; the API review for stages A and B comes next (task 19).

- `Clip.Speed` (0.1 to 10, the sound's pitch kept with V9.1's `Tempo` or following it with `Speed`), `Reverse` (one keyframe
  interval decoded at a time, sound reversed in 1 s windows), `Loop`, `Hold` and `Freeze` with `ClipFreeze`.
- `Clip.Still` (a file or a raster), `Clip.Animation` (GIF, WebP, APNG at their delays), `Clip.Color`, `Clip.FromSource` with an
  app's own `IFrameSource`, `Clip.PanZoom` with a `PanZoomPath` drawn by a sub-pixel transform, and `Slideshow.Create` with
  `SlideshowOptions` (seeded pan and zoom).
- egami-video `speed`, `reverse`, `loop`, `freeze` and `slideshow` (also MCP tools), and `@start-end` time windows on `run`
  steps, such as `blur 8 @0:05-0:12`. The edit file stores the new clip settings.
- Gates against FFmpeg on raw clips: `reverse`, `loop`, `tpad`, a freeze (trim, tpad and concat) and `setpts` with `fps` at 2x
  and 0.5x give the same frames; durations are exact from 0.1x to 10x. Frames exactly at an output slot's middle now count as
  before it, as FFmpeg's `fps` filter does.

V9 Editing stage A: joins, fades, transitions, layers, chroma key, stacks and effects in `Egami.Video.Editing` (Build Plan
tasks 3 to 11). All additive; the API review comes after stage B (task 19).

- `VideoEdit` (an `EditCanvas`, an `EditTrack` of `TrackItem`s, `Layers` and `Effects`), `Plan` to an `EditPlan` of
  `PlannedClip`s and `PlannedLayer`s in whole frames with warnings, `Render` to a file or stream, and `ToJson` and `FromJson`
  (the CLI's edit file; effects are stored by `EditEffect.Name`).
- `Clip.Open` (file or seekable stream), `Trim`, `FadeIn`, `FadeOut`, `Volume`, `Muted`, `WithEffect`; clips of any size, rate
  and audio are fitted with `ClipFit`, take the nearest source frame, and have their sound resampled and remixed.
- `Transition` with ten kinds (`TransitionKind`), `TransitionDirection`, soft wipes, easing, `ITransition` for an app's own,
  and an equal-power audio crossfade through V9.1.
- `Layer` with `Place` (a rectangle or an anchor), `Opacity`, `Blend`, `Between`, fades, `HoldLastFrame`, `Animate` over
  `LayerProperty` keyframes, and `Key` with a `ChromaKey` (Egami 2.5.8's chroma key and despill).
- `Stack.Horizontal`, `Vertical` and `Grid` with `StackOptions`; `EditEffect.Apply`, `Of` and `Between`, `IFrameEffect` and
  `FrameEffectContext`.
- egami-video `join`, `fade`, `overlay` (with `--key`), `pip`, `stack` and `edit`, with `--save-edit`, and the same as MCP tools.
- Gates against FFmpeg on raw clips (`tools/video/make-edit-references.py`): join frame counts equal on 10 joins; `fade`,
  `overlay`, `hstack`, `vstack` and `xstack` matched exactly and `xfade=fade` at 51.6 dB (gate 40 dB); wipes within 1 px of
  `xfade`'s edge. The chroma key recovers a synthetic known alpha within 0.75 % (gate 3 %).
- V9.1: an `AudioFade` that starts at `MediaTime.Zero` now lasts its duration (it used to round to a cut, since `MediaTime`'s `+`
  keeps the left side's whole-second time base).

## Egami 1.3.0 (2026-10-29)

The release train: Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic, Egami.Formats.Extended and Egami.Cli
move to 1.3.0 together; Egami.Formats.Extended is new. It adds API and removes none.

### Packed 5-5-5 pixel types

Packed 5-5-5 pixel types (step 2.13 task 2), for 16-bit BMP and TGA and the GDI+ 16-bit bitmaps of Egami.SystemDrawing. All
additive.

- `Bgr555` and `Bgra5551`: blue in bits 0 to 4, green in 5 to 9, red in 10 to 14, and in `Bgra5551` a 1-bit alpha in bit 15.
  Every value survives a round trip through `Rgba8` (with the unused top bit clear for `Bgr555`), and converting to and from
  `Rgba8` and `Bgra8` runs eight or sixteen pixels at a time.
- `BmpPixelFormat.Bgr555`, `Bgr565` and `Bgra5551` write 16-bit BMP, and 16-bit files with matching bit fields load straight
  into `Bgr555`, `Bgr565` and `Bgra5551`.
- `TgaPixelFormat.Bgr555` and `Bgra5551` write 16-bit TGA, raw or RLE, and 15 and 16-bit files load straight into the two
  types.

### Plugin helpers

Helpers a format plugin needs, made public for Egami.Formats.Extended. All additive.

- `EgamiInput.CheckFrameSize`, for a row reader that rents its buffers before any raster exists.
- `EgamiInput.Warn(RasterMetadata, ...)`, for warnings from an open `RowReader`.
- `SaveKeeps` and `EncodeStart<TPixel>.ReportLosses`, so a plugin's writer reports what a save loses as Egami's own writers do.

### OCR

- `CommandOcrEngine` hands the program a gray PNG when the image is gray and opaque, and an RGB PNG when it is opaque, instead
  of RGBA. Tesseract reads a scanned page about a quarter faster that way: 100 scanned pages at 4 at a time took 35 s instead
  of 45 to 56 s, with the same words.

### GDI+ image scaling

GDI+'s image scaling (2.7.6 stage 4), recovered from DrawImage's impulse responses on Windows in egami-compare. All additive.

- New `ResampleFilter` members named after GDI+'s interpolation modes, for Resize, Rotate and an `ImagePaint`'s sampling:
  - `GdiPlusBilinear` (Default, Low, Bilinear) is the two-pixel tent at every scale.
  - `GdiPlusBicubic` (Bicubic) is Keys cubic with a = −0.5 at every scale. Neither averages when shrinking, as GDI+'s don't.
  - `GdiPlusHighQualityBilinear` is the tent, widened by the reduction, convolved with a one-pixel box.
  - `GdiPlusHighQualityBicubic` (High, HighQualityBicubic) is the same with Keys cubic, a = −1.
- With `CanvasOptions.PixelOffset.None`, `DrawImage` uses GDI+'s default grid, with source pixel centres on destination pixel
  centres: an image scaled by s moves (1 − s) / 2 pixels, and one drawn 1:1 does not move.
- Against DrawImage at PixelOffsetMode Default and Half, up and down, the mean difference over those modes drops from 2.53 levels
  (Egami's closest filter before) to 0.37. Bilinear and HighQualityBilinear are within 0.2 levels.

## Egami.Fonts and Egami.Drawing 1.3.0 (2026-10-29)

GDI-matching text rendering (2.7.5), measured against GDI and GDI+ on Windows in egami-compare. All additive, and every
default draws exactly what Egami 1.2.0 drew.

- `CanvasOptions.TextGamma` blends glyph coverage per channel through a power law, as GDI ClearType does, and
  `CanvasOptions.TextContrast` boosts coverage as Skia and DirectWrite do. Both apply to glyphs drawn from the glyph cache.
- `CanvasOptions.LcdFilter` (`Default`, `Balanced`, `Light`, `Gdi` or `LcdFilter.Custom`), `CanvasOptions.LcdSampling`
  (`Smooth` or `Gdi`) and `CanvasOptions.ClearTypeLevel` shape LCD text.
- `Font.Embolden` grows outlines on every side, and `Font.StemDarkening` grows thin stems by FreeType's documented default
  CFF darkening curve.
- `FontHinting.Full` runs a TrueType font's own instructions with Egami's interpreter. `Font.HintingTarget` picks
  `TrueTypeTarget.GdiPlus` (the default), `Gdi` (both with Microsoft's ClearType compatibility rules, x on a 1/16-pixel grid)
  or `FreeType`. CFF, uninstructed and variable faces fall back to `FontHinting.Light`.
- `Font.Metrics` takes advances from the design (`FontMetrics.Design`, the default), from the hinted outlines
  (`GridFitted`) or from GDI's compatible widths (`Compatible`, equal to GDI's `GetCharABCWidths` for 9,688 of 9,690 glyph
  sizes of the six Windows UI fonts from 8 to 24 pixels per em). Measured text always matches drawn text.
- `CanvasOptions.GdiPlus` and `Font.WithGdiPlusLook()` set all of it as GDI+'s `ClearTypeGridFit` draws: on Lars's
  button test the mean difference from GDI+ drops from 9.50 levels (light hinting) to 1.92 on black and from 5.36 to 1.06 on
  #0078d4, before the 2.7.6 work below.

Closer GDI+ gradients (2.7.6 stage 3), measured on Windows in egami-compare and opt-in like the rest.

- `InterpolationSpace.GdiPlusGamma` and `GradientInterpolation.GdiPlusGamma` mix as `LinearGradientBrush.GammaCorrection` does.
  Each pair of stops is cut into 16 parts, the 17 colors where they meet are mixed in gamma-2.2 light and rounded to 8 bits,
  and the gradient runs straight through them. On the stage 0 gamma probes the mean difference from GDI+ drops from 0.83 to
  1.04 levels (Egami's closest, `LinearSrgb`) to 0.08 to 0.16.

Closer GDI+ text (2.7.6 stage 1), measured against GDI+ on Windows in egami-compare. Egami's default output does not change.

- `LcdSampling.Gdi` samples as GDI+ does: six on-or-off samples per pixel across the stripes, so each channel is a whole
  number of sixths. With a `TextGamma`, that text blends through GDI+'s 8-bit gamma tables, which reproduce GDI+'s drawn value
  for every coverage on every gray background measured. `CanvasOptions.GdiPlus` gets both.
- The GDI+ hinting target rounds an advance its instructions leave alone up from about 0.458 of a pixel, as GDI+ does.
- `FontMetrics.GridFitted` keeps hinted advances for every run, as `DrawString` does with the default `StringFormat`. The new
  `FontMetrics.GridFittedToDesign` has the 2.1-pixel line rule for `GenericTypographic` lines, with design advances rounded
  up to sixteenths of a pixel.
- Lars's button test against GDI+: a mean difference of 0.67 levels on black and 0.38 on #0078d4, down from 1.92 and 1.06.
  95.8% of glyph sizes in the six-font advance test match GDI+'s drawn widths, up from 90.6%.
- `Font.EmbeddedBitmaps` (2.7.6 task 22) draws a face's EBLC and EBDT strikes as Windows does: at exactly a strike's size, the
  glyphs it holds are drawn from their black-and-white or gray bitmaps (bit depth 1, 2, 4 or 8; image formats 1, 2, 5, 6 and
  7), and grid-fitted layout takes the strike's advances. Off by default; `Font.WithGdiPlusLook()` turns it on. Calibri's
  printable ASCII glyphs at its six strike sizes (12, 13, 15, 16, 17 and 19 pixels) now equal GDI+'s pixel for pixel.

Closer GDI+ shapes (2.7.6 stage 2), measured the same way. These are opt-in too.

- `CanvasOptions.PixelOffset`: `Half` is Egami's grid. `None`, GDI+'s default, puts whole coordinates on pixel centres, so paths,
  their paints, clips and unhinted text move half a pixel, and hinted text stays put, as in GDI+. Images go onto GDI+'s grid,
  source pixel centres on destination pixel centres (2.7.6 stage 4).
- `CanvasOptions.Antialiasing`: `Exact` is Egami's coverage. `GdiPlus` samples as GDI+'s `SmoothingMode.AntiAlias` does,
  with 8 by 4 point samples a pixel and a top-left fill rule. It also draws pens of 1.5 device pixels or less as GDI+'s thin
  lines, one pixel thick with pointed ends. Each row is computed on its own, so the result is the same at any core count.
- `CanvasOptions.GdiPlus` sets `PixelOffset.None` and `Antialiasing.GdiPlus` too.
- Against GDI+ at its defaults (AntiAlias, PixelOffsetMode.None), the stage 0 shape probes' mean difference drops from 1.94
  levels (Egami's closest settings before) to 0.12: rects 1.60 to 0.00, polygons 2.17 to 0.01, ovals 0.85 to 0.32, lines 3.15
  to 0.16.

## Egami.Formats.Extended 1.3.0 (2026-10-29)

A new package of format plugins (Build Plan tasks 1 to 12). It depends on core Egami only and rides the Egami release train.

- PCX, SGI, Sun raster, XBM, XPM, WBMP and Farbfeld, each read and written, registered with `WithExtendedFormats()` or one
  `With<Format>()` each (`WithPcx`, `WithSgi`, `WithSunRaster`, `WithXbm`, `WithXpm`, `WithWbmp`, `WithFarbfeld`).
- Every format probes, streams through `RasterDecoder`, saves through `RasterEncoder` and `Transcode`, keeps the safety limits,
  and in a lenient read of a damaged file keeps what decoded with a warning such as `Pcx.RowsMissing`.
- `PcxDetails`, `SgiDetails`, `SunRasterDetails`, `XbmDetails` and `XpmDetails` keep what the headers say; `SgiWriterOptions.Rle`
  and `SunRasterWriterOptions.Rle` choose run-length coding.
- Egami reads every file ImageMagick 7.1.2 writes in these formats to ImageMagick's pixels, and ImageMagick reads every file
  Egami writes to Egami's; ImageMagick's 1-bit PCX loads inverted, since it sets the bits of black pixels against its own
  palette. Loading and saving the 12-megapixel files is faster than ImageMagick in every case, at 1 thread and on all cores.
- The format's name for Sun raster is `Sun raster` (it was `SUN`), so it matches `SunRasterWriterOptions`.
- A page per format in the docs, a Package column in the formats overview, and the `EGAMIX` diagnostic ids for formats in
  preview (none yet). The package has its own icon and lockups.

## Egami.Cli 1.3.0 (2026-10-29)

- `egami ocr`: the text of images through an OCR program you name with `--engine-command`, such as Tesseract, as plain
  text, hOCR or ALTO (`--as`, or `-o`'s extension), with `--lang`, `--dpi` (the image's own by default) and
  `--engine-timeout`. `egami mcp` does not offer it, since it runs the program a call names.
- `egami` reads and writes Egami.Formats.Extended's formats, with `--set sgi.rle=` and `--set sunraster.rle=` for their
  writer options. `--preview-formats` adds the formats in preview; there are none yet.

## Egami.Svg, Egami.Css and Egami.Heic 1.3.0 (2026-10-29)

- No changes of their own: they move to 1.3.0 with the release train. Egami.Heic depends on exactly Egami 1.3.0.

## Egami.SystemDrawing 1.0.0-preview.1 (2026-10-29)

A new package: System.Drawing.Common's API on every OS, in managed code (step 2.13, stages A, C to F). Bitmaps, pixel access,
codecs, icons, Graphics, pens, brushes, paths, fonts and text, metafiles, effects and the rest of the API.

- `Bitmap`, `Image` and `Icon` in the `Egami.SystemDrawing` namespace, and `PixelFormat`, `BitmapData`, `ColorPalette`,
  `ImageFormat`, `ImageCodecInfo`, `Encoder`, `EncoderParameters`, `FrameDimension` and `PropertyItem` in
  `Egami.SystemDrawing.Imaging`, with System.Drawing's members, defaults and exceptions. Color, Point, Rectangle and the other
  primitives are .NET's own System.Drawing types.
- All 14 pixel formats in GDI+'s memory layout, with GetPixel, SetPixel, LockBits (including user buffers), Clone,
  ConvertFormat, MakeTransparent, RotateFlip and GetThumbnailImage converting exactly as GDI+ does, measured on Windows for
  every 8-bit value and every color and alpha pair; GDI+'s default and halftone palettes and its indexed color mapping.
- Image.FromFile, FromStream and Save through Egami's codecs: GDI+'s pixel format, flags and resolution for each file type,
  multipage TIFF and animated GIF frames, property items (EXIF, TIFF tags, JPEG quantization tables, PNG and GIF fields),
  GDI+'s codec list, JPEG quality, TIFF compression and depth, and SaveAdd.
- Icons choose the image nearest a size as System.Drawing does and save the file they came from.
- `Graphics` on any bitmap GDI+ draws on, with GDI+'s defaults and its rules measured on Windows: page units and scale,
  transforms, `Save`, `Restore` and containers, the clip and `Region` as whole device pixels, quality settings, and every Draw,
  Fill and DrawImage overload for lines, rectangles, ellipses, arcs, pies, polygons, Beziers, cardinal splines, rounded
  rectangles, regions, images and icons; `Matrix`, `Region`, `RegionData`, `ImageAttributes`, `ColorMatrix`, `ColorMap`, `Pen`
  and `SolidBrush`.
- Pens with every cap, join, dash style, compound line, inset alignment, arrow and stroke cap, and a nib-shaping transform;
  `Pens` and `SystemPens`. `HatchBrush` (all 53 styles as GDI+ draws them), `TextureBrush`, `LinearGradientBrush` and
  `PathGradientBrush` with GDI+'s brush transforms, blends, sigma bell and triangular shapes and preset colors; `Brushes` and
  `SystemBrushes`.
- `GraphicsPath` with every builder, GDI+'s point and type arrays, `Flatten`, `Widen`, `Warp`, `Reverse`,
  bounds, hit tests and markers; `GraphicsPathIterator`; regions, clips, `DrawPath` and `FillPath` from paths.
- `FontFamily`, `Font`, `InstalledFontCollection`, `PrivateFontCollection`, `SystemFonts`, `FontConverter` and
  `Interop.LOGFONT`, with GDI+'s metrics; `SystemDrawingOptions.FontSubstitutes` takes Egami.Fonts.Substitutes'
  `SubstituteFonts.Find` for metric-compatible stand-ins where the Windows core fonts are not installed;
  `StringFormat` and `CharacterRange`; `DrawString`, `MeasureString`, `MeasureCharacterRanges` and `GraphicsPath.AddString`
  laid out as GDI+ lays text out (padding, widening, wrapping, trimming, alignment, tabs, hotkeys, vertical text), and
  `Font.ToEgamiFont`.
- `Metafile` over Egami.Metafiles: GDI+'s sizes, headers (`MetafileHeader`, `MetaHeader`, `WmfPlaceableFileHeader`) and
  record enumeration (`EnumerateMetafile`, `EmfPlusRecordType`), drawn as vectors by `DrawImage`; the recording constructors
  throw (EGSD0004).
- The GDI+ 1.1 effects (`Bitmap.ApplyEffect`, `DrawImage(image, effect)`), most of them matching GDI+'s measured results;
  `BufferedGraphics`, `CachedBitmap`, `ImageAnimator`, the type converters and designer attributes, `SystemIcons` (which
  throws, EGSD0003), and the escape hatches `Bitmap.AsRaster`, `Bitmap.FromRaster` and `Graphics.Canvas`.
- Members that need Windows handles throw PlatformNotSupportedException and warn at compile time (EGSD0001 to EGSD0004).
- With implicit usings on, the package adds `System.Drawing` and `Egami.SystemDrawing` as implicit usings.
- `SystemDrawingOptions.ArgbDrawing` and `Graphics.ArgbDrawing` choose between drawing 32-bit ARGB bitmaps in place (the
  default) and through GDI+'s exact premultiplied rounding.
- A migration guide from System.Drawing to Egami's own API, through the escape hatches.
- Measured against native Egami (`benchmarks/Egami.SystemDrawing.Benchmarks`): fills, DrawString and LockBits within 5 % of the
  native calls; a `sysdrawing` fuzz target for `Image.FromStream`; the package icon.
- GDI+'s measured settings from 2.7.6 (task 20): `Graphics.InterpolationMode` takes the `ResampleFilter.GdiPlus*` filters, `SmoothingMode` AntiAlias and
  HighQuality take `Antialiasing.GdiPlus`, `CompositingQuality` HighQuality and GammaCorrected blend in linear light, and
  `LinearGradientBrush.GammaCorrection` mixes with `GradientInterpolation.GdiPlusGamma`. ClearType text keeps its own pixel
  offset instead of the preset's. Against GDI+ in the System.Drawing probes, AntiAlias goes from 94.5% to 96.5% of pixels
  exact, WideAntiAlias from 94.0% to 96.6%, images from 87.8% to 89.9% (99.6% within 8 levels), gradients from 65.8% to 67.9%
  and pen drawing from 95.2% to 96.7%.

## Egami.Metafiles 1.0.1 (2026-10-29)

GDI+'s measured settings from 2.7.6 (task 20). No API changes; the package now needs Egami 1.3.0.

- The EMF+ player takes the `GdiPlus*` filters for each interpolation mode and `GdiPlusGamma` for
  gamma-corrected linear brushes; against GDI+ the gradient brushes' mean goes from 1.15 to 1.11 levels and images' from 3.53
  to 3.39. This changes how such metafiles render with the default options, toward GDI+. With `PixelOffset.None` in
  `MetafileRenderOptions.CanvasOptions`, it leaves the pixel offset to the canvas.
- The shared GDI+ code's approximation of gamma-corrected gradients (eight unrounded stops per pair) is gone.

## Egami.Video 1.0.0 (2026-10-25)

The first release of Egami.Video on NuGet, with Egami.Video.H264 and Egami.Video.Cli at the same version. From 1.0 its
public API follows Semantic Versioning. Every step's API was reviewed; the previews 0.1 to 0.6 were never published, and
their notes are folded in below, newest first. It needs Egami 1.2.0.

- `Hdr10PlusMetadata.Parse` is public, so codec packages such as Egami.Video.H264 read HDR10+ T.35 payloads with Egami.Video's
  parser (internals are made public as outside packages need them).

V4.3: fragmented MP4 writing, and HLS and DASH segments. API reviewed and accepted 2026-10-23 (all as recommended).

- `Mp4Format.Fragments` and `Mp4FragmentOptions`: fragmented MP4 (ISO/IEC 14496-12 8.8), moov with mvex and then a moof and
  mdat pair a fragment as packets come, cut at the first video keyframe after `Duration` (2 s) or at `MaxBytes` (32 MB), with
  mfra at the end. A seekable output gets its durations written back; tags, cover art and Nero chapters are kept (the
  QuickTime chapter track is left out, with an mp4-chapter-track warning). Setting it with `MoovAtEnd` throws.
- `Mp4Format.NonSeekable` and `Mp4NonSeekableLayout`: an output that cannot seek and read back now gets fragmented MP4 with an
  mp4-fragmented warning, so `MediaFile.Remux` and `MediaFile.Transcode` with a `Stream` output use no temporary file;
  `TempFile` keeps the flat layout.
- `Mp4SegmentWriter`, `Mp4SegmentOptions` and `SegmentPlaylists`: CMAF init and media segments a track (styp, sidx, moof and
  mdat), cut at a keyframe after `SegmentDuration` (6 s), to a folder or a `Func<string, Stream>`, with an HLS multivariant
  playlist and media playlists (RFC 8216) and a static DASH MPD (ISO/IEC 23009-1).
- Writing fragmented MP4 takes 0.69 times as long as flat MP4 on 720p Motion JPEG and allocates no memory per packet or
  fragment in steady state; Chromium and Chrome (Media Source Extensions), hls.js, dash.js, ffprobe, MP4Box and Bento4 read
  the files (egami-compare).

V5.2: VP8 encoding, on an encoder core that VP9 and AV1 encoding will share. The V5 encoder steps had one combined API review
after V5.6 (Design Q17), accepted 2026-10-24.

### VP8 encoding

- `Vp8Codec.CreateEncoder` and `VideoWriterOptions.Codec = VideoCodecs.Vp8`: VP8 into WebM, Matroska and IVF through Egami's
  `Vp8StreamEncoder`, with WebM alpha as a second VP8 stream in each packet's addition 1. 8-bit 4:2:0 planes are coded as
  they are; other frames are converted to BT.601 limited range.
- `Vp8EncoderOptions` (`VideoEncoderOptions.Vp8`): quantizer limits, alternate references, golden frame spacing, temporal
  filter frames and strength, token partitions, error resilience, alpha.
- `egami-video encode --codec vp8` with `--effort` and `--bitrate`, and `run` keeps VP8 sources VP8; `egami-video ffmpeg`
  takes `-c:v libvpx`.

### Encoder settings

- `VideoEncoderOptions` (and `VideoWriterOptions`) gain `Effort`, `RateControl` (`RateControlMode.Quality`, the default,
  `Bitrate`, `ConstantBitrate`, `Quantizer`), `Bitrate`, `MaxBitrate`, `BufferSize`, `Quantizer`, `LatencyMode` (`Quality`
  with a 25-frame lookahead, or `Realtime`), `KeyframeInterval` (10 seconds by default), `SceneCutKeyframes` and
  `FirstPassStats`.
- `IVideoEncoder.Send(VideoFrame, EncodeFrameOptions)` with `EncodeFrameOptions.ForceKeyframe`; Motion JPEG and uncompressed
  video treat it as `Send(VideoFrame)`.
- Two passes: `VideoWriter.AnalyzeFirstPass` over frames or a file gives `FirstPassStats`, which `Save` and `Load` keep in a
  file.
- The lookahead places keyframes at scene cuts, plans groups with hidden, temporally filtered alternate references where the
  group's first frame still predicts its last well, and gives each frame its quantizer. Output bytes do not depend on the thread count.

V5.7: Opus encoding, translated from libopus 1.5.2. API reviewed in the combined V5 encoders review, accepted 2026-10-24.

### Opus encoding

- `OpusCodec.CanEncode` and `CreateEncoder`: SILK, CELT and hybrid in every mode, 1 to 8 channels (mapping family 0 for mono
  and stereo, 1 with libopus's surround masking and stream split above), 8, 12, 16, 24 and 48 kHz with other rates resampled
  to 48 kHz and kept in the header. The packets are byte for byte libopus 1.5.2's in 46 reference settings (every application,
  rate mode, frame duration from 2.5 to 120 ms, FEC, DTX, forced modes and bandwidths, multistream up to 7.1).
- `OpusEncoderOptions` (`AudioEncoderOptions.Opus`, `AudioWriterOptions.Opus`): `Bitrate` (64 kbit/s per channel by default),
  `RateMode` (`OpusRateMode.Vbr`, `ConstrainedVbr`, `Cbr`), `Application` (`OpusApplication.Audio`, `Voice`, `LowDelay`),
  `FrameDuration` (20 ms), `Effort` (0 to 10, libopus's complexity, default 10), `Bandwidth` (`OpusBandwidth`), `InbandFec`,
  `ExpectedLoss` and `Dtx`.
- The pre-skip is the encoder's look-ahead (312 samples at 48 kHz) in OpusHead and `CodecDelay`, and the last packet's
  duration trims the end, so .opus, WebM, Matroska and MP4 files decode to exactly the samples written in `AudioReader`,
  opusdec and FFmpeg (which keeps the end padding of MP4).
- `AudioWriter` writes Opus by default for Ogg (.opus, .ogg, .oga) and WebM files, and `MediaFile.Transcode` with no codec
  and a WebM output encodes the audio in Opus unless it is Opus or Vorbis.
- 60 s of stereo at 128 kbit/s encodes in 0.70 s, 1.4 times libopus 1.5.2's plain C build on the same machine, and
  steady-state encoding allocates nothing per packet.

### MP3 (V5.8)

V5.8: MPEG audio decoding and .mp3 files. API reviewed and accepted 2026-10-25 (all as recommended).

- `Mp3Codec` (and `AudioCodecs.Mp3`, decode only), registered by default, translated from minimp3 (CC0): Layers I, II and III
  of MPEG-1, MPEG-2 and MPEG-2.5, 8 to 48 kHz, every channel mode and free-format bit rates, as planar float. It passes the
  ISO/IEC 11172-4 compliance streams at full accuracy (maximum error at most 2^-14, PSNR about 101 dB) and matches FFmpeg's
  float decoder within 2e-7 on LAME files. Frames are found by three matching headers; damaged frames and CRC-16 mismatches
  (`Mp3.CorruptFrame`, `Mp3.BadCrc`) become silence of their own length with `DecodeErrorMode.Conceal`. `CodecIds.Mp1` is new.
- `Mp3Format` (.mp3, .mp2, .mp1, .mpa), registered by default, read and written. ID3v2.2 to 2.4 tags (text frames, APIC cover
  art, CHAP chapters), ID3v1, APEv2 and Lyrics3 tags are read; Xing, Info and VBRI frames give the frame count, and the LAME
  tag's delay and padding become `CodecDelay` and the last packet's duration, so LAME files decode to the source's exact
  sample count. Seeks land on the exact sample (output equals a straight decode); `Mp3Format.FastSeek` seeks by the Xing table.
  The writer stores ID3v2.4 and, on a seekable stream, a Xing header and LAME tag that FFmpeg reads gaplessly.
- MP3 in Matroska gets sample-exact packet durations and its DiscardPadding; WAV's id3 chunk now reads ID3v2.2 too.
- On 37 clips (CBR, VBR and free-format at MPEG-1, 2 and 2.5 rates, Layer II, CRC-protected and damaged files) the decode is
  within 1e-7 of minimp3's, and Chromium plays the .mp3 files Egami.Video writes (egami-compare `video/native/mp3-gate.py`).
  Decoding takes 2.1 to 2.6 times minimp3's time on one thread, short of the 1.5x gate (accepted for 1.0; speed work follows in
  1.0.x).

### Text subtitles (V1.2)

API reviewed and accepted 2026-10-23 (Q1 applies ASS styles in SRT and WebVTT; the rest as recommended).

- `SubtitleDocument` in `Egami.Video.Subtitles` reads and writes SRT, WebVTT and ASS, and reads SSA v4: `Load`, `Parse`,
  `Save`, `ToText` and `DetectFormat`. Cues (`SubtitleCue`) are spans of styled text (`SubtitleSpan`) with exact `MediaTime`
  times, a keypad `SubtitleAlignment` and an exact `SubtitlePosition`; ASS styles, layers, margins, embedded fonts and every
  override tag are kept, and WebVTT cue settings, regions, voices, classes, ruby and karaoke timestamps.
- An unchanged file saved in its own format gives back the same bytes. Reading is lenient, with `Subtitle.Repaired` warnings
  (`SubtitleReaderOptions.Strict` throws instead); encodings are found from the byte order mark and UTF-8, then
  `SubtitleReaderOptions.Encoding`, then a reported guess (`Subtitle.Encoding`). Converting reports what the target cannot
  hold as `Subtitle.Dropped`, once per kind.
- WebVTT is parsed as the W3C spec says and passes the web-platform-tests parsing suite (pinned to a commit by
  tools/video/fetch-webvtt-tests.py), with the full HTML named character references.
- `Shift`, `Scale`, `Sync`, `Clip`, `Sort` and `FixOverlaps` retime without rounding; times are rounded only when written.
- Subtitle tracks: `SubtitleDocument.ReadTrack`, `FromPackets` and `IsTextCodec` read them, and `SubtitleTrackWriter` writes them.
  Matroska takes `S_TEXT/UTF8`, `S_TEXT/WEBVTT` and `S_TEXT/ASS`, and reads `S_TEXT/SSA`. WebM takes `D_WEBVTT/SUBTITLES`.
  MP4 takes mov_text and WebVTT (`wvtt`, ISO/IEC 14496-30, on a `text` track). New `CodecIds` constants name them.
- Matroska subtitle blocks carry their BlockDuration, so cue durations survive a remux. Files from FFmpeg, MKVToolNix and
  MP4Box read back to their source cues (tools/video/make-subtitle-tracks.py). ffprobe, mkvmerge and MP4Box read Egami's
  files cleanly (tools/video/check-subtitle-tracks.py).
- egami-video `trim` keeps subtitle cues shown across the cut, clipped to it (ffmpeg's stream copy drops a cue that starts
  before the cut and lets the last one run past it); `concat` keeps each clip's cues inside the clip; `timestamps --fix`
  leaves cues that start together alone.
- Conversions give FFmpeg's cues on a 50-conversion set (tools/video/make-subtitle-references.py); the guide lists the three
  places where they differ. An ASS style's bold, italics, underline and strikeout show in SRT and WebVTT as tags, as in
  FFmpeg; ASS comment events are left out of them and reported (API review Q1). Reading and writing is 8 to 12 times faster than FFmpeg's (tools/video/time-subtitles.py).
- Fuzz targets `subs` and `subtrack`. Their first runs found times too late to hold, karaoke past them, an ASS file cut off
  in its header saved with a line end added, ASS events after a changed format line, and bad UTF-8 in WebVTT read without a
  report; all fixed, with tests.

V1.4 stage A: scene detection and the analysis pass, in the new `Egami.Video.Analysis` namespace. The API review comes once the
whole step is built (V1.4 Build Plan).

### Scene detection

- `SceneDetector`: cuts from a 16 by 9 grid of mean Y, Cb and Cr and a 64-bin luma histogram per frame, read from the planes
  with no RGB conversion. `SceneMethod.Adaptive` (the default) weighs each frame's change against the frames around it, so
  pans and fast motion are not taken for cuts; `Content` scores against the mean of the last frames, as ffmpeg's scene score
  does; `Histogram` compares luma histograms. A cut within `MinSceneLength` (0.6 s) of the last is not taken, and with
  `FlashMode.Merge` a flash that comes back to the picture before it is no scene.
- Fades: a fade or cut through black (or white, with `FadeToWhite`) starts the next scene inside the dark run (`FadeBias`
  moves it), and `Fades` lists each fade out and fade in.
- `Scenes` (`Scene` with first and last frame, start, end, middle and how it starts), `Cuts`, `Scores` with `KeepSeries`, and
  `SettledFrames` for callers that hold recent frames. No memory is allocated per frame.

### The analysis pass

- `MediaAnalysis.Run` reads a file once and feeds the scene detector and any `IVideoDetector` and `IAudioDetector` of the
  caller's, video and audio interleaved by time, over an optional `Start` and `Duration`, with progress and cancellation.
- `SceneFrames.Read`: the first, middle or last frame of each scene, by seeking.

### Test sources

- `TestSources`: SMPTE RP 219 HD bars (with the -2%, +2% and +4% pluge bars) and SMPTE EG 1 SD bars at their code values in
  any bit depth, a moving pattern with a frame counter, solid colors and gradients as video; sine tones, silence, white and
  pink noise and log sweeps as audio, at a level in dBFS.

V1.4 stage B: the video detectors. Each keeps ffmpeg's documented measure and defaults, with thresholds as fractions of the
stream's range so 8-bit and 10-bit video behave alike; they run in `MediaAnalysis.Run` or on frames of the caller's own.

- `BlackDetector` (as blackdetect: 10 % pixel threshold, 98 % of the picture, 2 s) and `FreezeDetector` (as freezedetect:
  mean absolute difference over Y, Cb and Cr against the frame that froze, -60 dB, 2 s) return `DetectedRange`s.
- `CropDetector` (as cropdetect's black mode, limit 24/255, even sizes): a `CropReport` with the rectangle that keeps every
  frame's picture, the most common one and the runs of frames that share one.
- `InterlaceDetector` (as idet, ratios 1.04, 1.5 and 3): `InterlaceReport` with top field first, bottom field first,
  progressive and undetermined frames, repeated fields and an `InterlaceVerdict` that names 3:2 telecine.
- `DuplicateDetector`: mpdecimate's 8 by 8 block test without dropping, as runs of repeated frames; its thresholds are
  12/255 and 5/255 of the range per sample (mpdecimate's 768 and 320) and 0.33.
- `SignalMeter` (as signalstats and colordetect): `PlaneLevels` for Y, Cb, Cr and saturation, the share of pixels outside
  broadcast range, the `SignalRange` really used and `AlphaUse`; with `Blur` the mean edge width (blurdetect's thresholds) and
  with `Blockiness` how much a block grid stands out (blockdetect's periods). Per-frame `SignalFrame`s with `KeepSeries`.

V1.4 stage C: audio detectors and the loudness meter.

- `SilenceDetector` (as silencedetect: under -60 dBFS for 2 s) returns silent stretches exact to the sample, over all channels
  together or, with `PerChannel`, for each channel.
- `AudioLevelMeter` (as volumedetect and astats): an `AudioLevelReport` with `ChannelLevels` per channel and overall: peak,
  RMS, DC offset, crest factor, clipped samples, peak count, noise floor, zero crossings and a 1 dB histogram.
- `LoudnessMeter` in `Egami.Video.Audio` (ITU-R BS.1770-5, EBU R 128): momentary and short-term loudness every 100 ms, gated
  integrated loudness, loudness range (EBU Tech 3342), 4x oversampled true peak and sample peak, at any rate from 8 to 384 kHz
  with the surround channels weighted and LFE left out. It passes the EBU Tech 3341 and 3342 tone cases, runs about 200 times
  faster than real time on 48 kHz stereo, and is the meter V9.1's loudness normalization will use.
- No detector allocates memory per frame once running.

V1.4 stage D: audio pictures.

- `AudioPictures.Waveform` draws each column's min-to-max outline with the RMS level shaded inside, linear or in dB, with the
  channels together or stacked (`WaveformOptions`); about 800 times faster than real time on 48 kHz stereo.
- `AudioPictures.Spectrogram` draws a Hann-windowed FFT per column (2048 samples by default) from -120 to 0 dBFS in magma or
  grayscale, on a linear or logarithmic frequency axis, mixed or per channel (`SpectrogramOptions`); a row that spans several
  bins shows the strongest, so pure tones never fall between rows. About 400 times faster than real time at 1800 x 512.
- Both read a file or any run of `AudioFrame`s once with constant memory: three hours of audio cost no more than three
  seconds. Both take a cancellation token, and the file overloads report progress. The pictures carry no text; the CLI adds
  axes and legends.

V1.4 stage E: the command line.

- `egami-video analyze`: every detector in one pass (`--detect` or `--all`: scenes, black, freeze, crop, interlace, duplicates,
  signal, blur, blockiness, silence, levels, loudness), with each ffmpeg filter's settings under its own names
  (`--black-min`, `--silence-noise -50dB`, `--crop-limit` …), `--start` and `--duration`, text, `--json` or `--csv`, and
  `--series` for per-frame and per-100 ms values. It is a read-only tool in `egami-video mcp`.
- `scenes`: `--min-length` (`--min-gap` still works), `--fades`, `--flash merge|suppress`, `--frame first|middle|last` and
  `--series`.
- `waveform` and `spectrum` draw audio pictures, with `--legend` for a time axis and level or frequency labels;
  `generate` writes test clips and sounds (pattern, bars, solid, gradient; sine, silence, noise, sweep).
- `segment --scenes` splits at scene changes, each part from the keyframe at or before its scene, reporting the gap and
  warning past a second; `--exact` starts each part on its first frame by smart cut (V5.6). `run --crop auto` crops black bars off before the steps.
- `egami-video ffmpeg`: blackdetect, freezedetect, cropdetect, idet, signalstats, blurdetect, blockdetect, silencedetect,
  volumedetect, astats and ebur128 with `-f null -` run `analyze` and print ffmpeg's log lines (`analyze --ffmpeg-log`);
  `-f lavfi` testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc run `generate`; showwavespic and
  showspectrumpic run `waveform` and `spectrum`. `-af` is parsed; filters outside these stop with exit code 2.

V1.4 gates (tasks 5, 14 and 21, egami-compare).

- Scenes: on hand-labeled cuts of the first 10 minutes of Big Buck Bunny, Sintel and Tears of Steel, `SceneDetector` scores F1
  0.953 (0.939 with the old FadeLevel), against PySceneDetect 0.7.2's AdaptiveDetector 0.935 and V1.1's detector 0.886.
- Loudness: `LoudnessMeter` passes every EBU Tech 3341 and 3342 case of the EBU test set v05 and matches ffmpeg's ebur128
  within 0.05 LU on 69 of its 70 files; on the 70th (1.78 s long) a plain BS.1770 computation agrees with Egami, 0.5 LU from
  ffmpeg. `MaxMomentary` and `MaxShortTerm` now take every window that starts on a 10 ms boundary, so a burst that starts between
  100 ms steps reads its full level (Tech 3341 cases 10 and 13; ffmpeg's ebur128 reads 0.2 to 0.5 LU low there).
- `SceneOptions.FadeLevel` defaults to 0.02 (was 0.06): dark shots are no longer taken for black, which made fades that were
  not there and hid real cuts. On the scene gate's films F1 goes from 0.939 to 0.953, Sintel's dark scenes from 0.866 to
  0.903.
- `analyze --ffmpeg-log` matches ffmpeg 7.1's log lines: cropdetect lines carry `pts`, a six-decimal `t` and `limit`;
  volumedetect lists its `histogram_<n>db` lines; the ebur128 summary lists both gate thresholds and prefixes only its first
  line. In `egami-video ffmpeg`, cropdetect rounds to 16 by default, as ffmpeg's does.
- Speed: scenes, black and freeze in `MediaAnalysis.Run` cost little next to decoding (the scene grid adds rows column by column
  with vector adds; black and freeze stop measuring a frame once the answer is known, so `BlackDetector.LastRatio` and
  `FreezeDetector.LastDifference` are bounds for such frames; `MediaAnalysis.Run` hands the freeze detector the frame it keeps
  instead of a copy). `InterlaceDetector`, `DuplicateDetector` and `SignalMeter` run 2 to 6 times faster.
- QuickTime files whose user data ends with a 32-bit zero (QuickTime 7 wrote them so) open; they failed as cut off.

V5.3: VP9 encoding. API reviewed in the combined V5 encoders review, accepted 2026-10-24.

### VP9 encoding

- `Vp9Codec.CanEncode` is true and `Vp9Codec.CreateEncoder` encodes VP9 into WebM, Matroska, IVF and MP4, with the encoder
  core's keyframes, groups, hidden alternate references and rate control modes, on VP8's `Quality` (0 to 100, default 60) and
  `Effort` (0 to 10, default 4) scales; `Quantizer` is VP9's 0 to 255 (0 lossless). Hidden frames go out in a superframe with
  the next shown frame, so every packet shows one frame.
- The search: partitions from 64x64 down to 4x4 with libvpx's breakout and a partition range from the previous frame, all ten
  intra modes with ADST and DCT transforms to 32x32 and the transform size chosen by rate and distortion, motion search to 1/8
  pixel, NEARESTMV, NEARMV, ZEROMV and NEWMV from LAST, GOLDEN and ALTREF, compound prediction, the regular, sharp and smooth
  filters, libvpx's trellis, forward probability updates, a loop filter level search, tile columns and adaptive quantization
  by segments. The encoder decodes what it writes, so its references are what every decoder holds.
- Profiles 0 to 3: 8, 10 and 12 bits, 4:2:0, 4:2:2 and 4:4:4, from the frames' planes or `Vp9EncoderOptions.BitDepth`; the
  frames' color code points and static HDR metadata go into the track (WebM and Matroska Colour, MP4 colr, mdcv and clli).
  Frames with alpha get a second VP9 stream in each packet's addition 1, as WebM carries it.
- `egami-video encode --codec vp9` (with `--effort` and `--bitrate`), `run` keeps VP9 sources VP9, and `egami-video ffmpeg`
  takes `-c:v libvpx-vp9`.
- `Vp9EncoderOptions` (`VideoEncoderOptions.Vp9`, `VideoWriterOptions.Vp9`): quantizer limits, alternate references, golden
  frame spacing, temporal filter, tile columns, adaptive quantization, error resilience, bit depth and alpha.
- The output is the same at any thread count and with or without hardware intrinsics: tile columns depend only on the width
  and the options, and superblock rows run as a wavefront. Forward transforms run on Vector128 and Vector256 lanes, sums of
  absolute differences, squared errors and residuals on AVX2.
- libvpx 1.14.1's vpxdec decodes every test stream (33, all profiles and options) exactly as Egami does
  (`tools/video/check-vp9-encoder.py`); 10 minutes of AFL++ on the vp9enc target found nothing.
- Against libvpx 1.14.1 `vpxenc --good` (one-pass VBR, 25 frames of lag, alternate references; `tools/video/time-vp9-encoder.py`),
  at effort 4: one thread at 1080p codes 1.6 to 2.4 s a frame, between `--cpu-used=1` and `=3` (about `=2`); all 24 cores
  0.39 to 0.51 s, about `--cpu-used=1`. BD-rate against `--cpu-used=2` (720p, 60 frames, PSNR-Y / SSIM): crowd_run +6.1 % /
  +19.9 %, ducks_take_off +0.4 % / -9.3 %, park_joy +3.9 % / +10.3 %, old_town_cross +24.5 % / +34.9 %. The +15 % gate is met on
  PSNR-Y by three clips of four and on SSIM by two; static, grainy content (old_town_cross) and SSIM are where libvpx is still
  ahead. Egami matches libvpx's PSNR-Y BD-rate at about `--cpu-used=4` on crowd_run, park_joy and old_town_cross and `=2` on
  ducks_take_off, taking 0.65 to 1.8 times its time there on one thread and 1.1 to 2.0 times on all cores.

V5.9 stage A: AIFF and AIFF-C, and G.711. V5.9 (stages A to D and the CLI) API reviewed and accepted 2026-10-24 (all as
recommended).

### AIFF

- `AiffFormat`: .aif, .aiff and .aifc files. Reads big-endian PCM, AIFF-C's sowt, in24, in32, fl32 and fl64 and G.711 (ulaw,
  alaw), at the sample count COMM gives; other AIFF-C compressions (ima4, MACE, QDesign) pass through as `X_AIFC/<type>`
  packets. NAME, AUTH, "(c) " and ANNO become tags, an ID3 chunk adds tags, cover art and chapters, MARK markers become
  chapters and a CHAN chunk gives the channel layout. Seeking is sample exact.
- Writes integer PCM (8 to 32 bits, either byte order in) as AIFF and float as AIFF-C, with text chunks, ID3, MARK and CHAN,
  through a temporary file when the output cannot seek. `AudioWriter` defaults to big-endian PCM for .aiff, and ffmpeg reads
  the files sample for sample with their layout and tags.

### G.711

- `G711Codec.MuLaw` and `G711Codec.ALaw` (`CodecIds.G711MuLaw`, `CodecIds.G711ALaw`) decode ITU-T G.711 to 16-bit samples, from
  AIFF-C, WAV, AVI (format tags 6 and 7) and MP4 and MOV (ulaw, alaw). Matroska has no G.711 id, so it does not take them.
- Fuzz target `aiff`.

V5.9 stage B: CAF.

### CAF

- `CafFormat`: Apple's Core Audio Format (.caf). Reads PCM (integer and float, either byte order), G.711, ALAC, Opus, AAC, MP3,
  MP2, AC-3 and FLAC; other formats pass through as `X_CAF/<format>` packets. The packet table cuts variable-size packets and
  gives the priming frames (`CodecDelay`) and the end padding (the last packets' durations); the magic cookie becomes
  `CodecPrivate` (ALAC's 24-byte config, AAC's AudioSpecificConfig, FLAC's metadata blocks; Opus gets an OpusHead). The info
  chunk becomes tags, markers and regions (named from strg) become chapters, and chan gives the layout. Sample-exact seeking
  for PCM, packet-exact for the rest.
- Writes one track of any of those codecs. PCM and G.711 stream, with a data chunk sized -1 on a non-seekable output; other
  codecs get their packet table after the samples, or before them through a temporary file on a non-seekable output.
  ffmpeg reads the files with their tags and layout. `CodecIds.Alac` names ALAC tracks (decoding comes in stage C).
- Fuzz target `caf`; `tools/video/fuzz-afl.py` now knows the `aiff` and `caf` targets. A CHAN or chan chunk whose bitmap names
  more or fewer speakers than the file has channels is now ignored instead of throwing (AIFF-C too).

V5.9 stage C: ALAC.

### ALAC

- `AlacCodec` (`AudioCodecs.Alac`): Apple Lossless decoding and encoding, translated from Apple's Apache-2.0 reference codec.
  16, 20, 24 and 32 bits, 1 to 8 channels in ALAC's layouts, partial last frames. The encoder's packets equal Apple's byte for
  byte at level 1 (the default, full stereo and predictor search) and level 0 (Apple's fast mode), with 4096 samples per
  packet unless `FrameSize` is set; the track's `CodecPrivate` is the 24-byte ALACSpecificConfig with the largest packet and
  average bit rate filled in. `tools/video/alacref` builds Apple's code as the reference, and
  `tools/video/make-alac-references.py` makes the gate streams.
- MP4 reads and writes the `alac` sample entry and box, and reads QuickTime's `wave` atom; the rate, channels and bit depth come
  from the config (so 96 kHz and up work). Matroska `A_ALAC` and CAF (already in stage B) decode too. FFmpeg's ALAC files in
  all four decode to FFmpeg's samples.
- `AudioWriter` keeps 20-bit samples at 20 bits for ALAC and writes float as 24-bit ALAC. A track with no layout gets ALAC's
  layout for its channel count. The CLI extracts ALAC tracks to .m4a. ALAC decoding trims a packet to its duration, so
  Matroska DiscardPadding and CAF remainder frames apply, and stays sample exact across Matroska's millisecond block times.
- Fuzz target `alac`.

V5.9 stage D (container part): AC-3 and E-AC-3.

### AC-3 and E-AC-3

- `Ac3Format`: raw AC-3 and E-AC-3 streams (.ac3, .eac3, .ec3). Each packet is one AC-3 frame, or an E-AC-3 independent frame
  with its dependent substreams; rate, channels and layout from the first frame; junk between frames is skipped (a frame found
  after junk must be followed by another); duration from the frame size and seeking through a frame index. Writes the packets
  back to back. New `CodecIds.EAc3` (`A_EAC3`), passed through.
- MP4 reads ac-3 and ec-3 sample entries as `CodecIds.Ac3` and `CodecIds.EAc3` (they used to come through as `A_QUICKTIME`)
  and writes them, with a dac3 or dec3 box made from the first packet's headers; the boxes match FFmpeg's. CAF maps ec-3 too.
- Fuzz target `ac3`.

V5.9 tasks 14, 15 and 17: AC-3 decoding.

### AC-3 decoding

- `Ac3Codec` (`AudioCodecs.Ac3`, registered by default) decodes AC-3, written from ATSC A/52:2018 alone: every channel mode
  (1/0 to 3/2 and dual mono) with or without the LFE, 32, 44.1 and 48 kHz, 32 to 640 kbit/s, coupling, rematrixing, block
  switching, delta bit allocation and dither, with both frame CRCs checked (`Ac3.BadCrc`, `Ac3.CorruptFrame`, handled as
  `AudioDecoderOptions.Errors` says). Frames are 1536 samples of planar float in Egami.Video's channel order, so AudioReader
  reads AC-3 from .ac3 files and from MP4, Matroska, AVI and CAF tracks. E-AC-3 still passes through.
- `AudioDecoderOptions.Ac3` takes an `Ac3DecoderOptions`: `DynamicRange` (1 by default, the stream's dynamic range control in
  full; 0 turns it off) and `Downmix`, a layout to mix to with the stream's center and surround mix levels (A/52's LoRo
  equations, scaled so the mix cannot clip). AudioReader hands out frames in the downmix layout, and an
  `AudioReaderOptions.Layout` of mono or stereo on a wider AC-3 track uses this downmix too unless `Downmix` is set. Dialogue
  normalization is not applied.
- Within 2^-14 and at 90 dB SNR or more of FFmpeg's float decode on 24 FFmpeg-encoded streams (every mode FFmpeg writes, 32 to
  640 kbit/s, LFE and coupling on and off, dual mono), made by `tools/video/make-audio-corpus.py --ac3-decode`.
- Fuzz target `ac3raw`.

V5.9 task 19: the CLI.

### egami-video

- `audio --codec alac` writes Apple Lossless (`--level` 0 or 1) to .m4a, .mov, .mka and .caf; .m4a outputs get ALAC by
  default. `audio` also writes .aiff (PCM) and .caf (PCM by default, or FLAC, ALAC or Opus).
- `extract` writes ALAC tracks to .m4a and AC-3 and E-AC-3 tracks to raw .ac3 and .eac3 files; `remux` writes .ac3, .aiff and .caf.
- ffmpeg mode maps `-c:a alac` and the big-endian `pcm_s16be`, `pcm_s24be` and `pcm_s32be`, and sends .aiff and .caf outputs
  to `audio` unless the audio is copied. `info` and ffprobe mode name ALAC, E-AC-3, G.711, AIFF, CAF and raw AC-3 as ffprobe
  does.

V5.4: AV1 encoding, through Egami's `Av1StreamEncoder` (Egami 2.9.9). API reviewed in the combined V5 encoders review, accepted 2026-10-24.

### AV1 encoding

- `Av1Codec.CanEncode` is true and `Av1Codec.CreateEncoder` encodes AV1 into WebM, Matroska, MP4, IVF, raw OBU and Annex B,
  with the encoder core's keyframes, groups and rate control modes on the shared `Quality` (0 to 100, default 60, the same
  quantizer steps as VP9) and `Effort` (0 to 10, default 4) scales; `Quantizer` is AV1's base_q_idx, 0 to 255 (0 lossless).
  Each packet is a temporal unit; the track's av1C is built from the sequence header.
- Hierarchical groups: each group of 16 frames is a five-layer pyramid (Phase 2.9.11 stage B). Its hidden alternate reference,
  then hidden frames halfway to it, halfway again and down to pairs, are coded ahead, lead the next shown frame's temporal unit,
  and are shown at their own place with `show_existing_frame`, a packet of a few bytes, instead of an overlay frame. The frames
  left between them (the leaves) predict from both sides and refresh no slot. Every frame reads its three nearest past frames as
  LAST to LAST3, the golden frame and the hidden frames ahead as BWDREF, ALTREF2 and ALTREF. Each frame's layer goes to the AV1
  encoder, which weighs rate more the deeper the layer; the steps fall from the leaves' halfway toward the alternate reference's
  at each layer up.
- Before a group is coded, it and the group after it (AV1 looks 40 frames ahead) run through `Av1StreamEncoder.Analyze`: the
  keyframe's and the alternate reference's steps fall with the square root of how much the frames after them reuse them, the
  base frames get superblock quantizers and the frames above them block multipliers from the same analysis. Keyframes and
  alternate references are blended by the temporal filter, whose strength follows each picture's noise and the frame's
  quantizer (`TemporalFilterStrength` null, the default). `AdaptiveQuantization` also gives the other frames Variance Boost.
- Main, High and Professional profiles: 8, 10 and 12 bits, 4:2:0, 4:2:2 and 4:4:4, from the frames' planes or
  `Av1EncoderOptions.BitDepth`. The frames' color code points and 4:2:0 chroma siting go into the sequence header and the
  container; static HDR metadata into content light level and mastering display metadata OBUs on every keyframe and into the
  container (Matroska Colour, MP4 mdcv and clli); HDR10+ from a frame into its temporal unit as an ITU-T T.35 metadata OBU.
  The level is the smallest whose picture size, dimensions and sample rate hold the stream. No alpha (AV1 has no alpha in
  Matroska or MP4; animated AVIF has it).
- `Av1EncoderOptions` (`VideoEncoderOptions.Av1`, `VideoWriterOptions.Av1`): quantizer limits, alternate references and the
  pyramid, golden frame spacing, temporal filter, adaptive quantization (delta q per superblock), screen content tools, error
  resilience and bit depth.
- Rate control plans later groups with their hidden frames, and a real-time constant bitrate stream plans its first inter frame
  from the keyframe probe (both for AV1 only; VP8 and VP9 output is unchanged).
- `egami-video encode --codec av1` (with `--effort` and `--bitrate`), `run --codec av1` (and AV1 sources stay AV1), and
  `egami-video ffmpeg` takes `-c:v libaom-av1`, `libsvtav1`, `librav1e` and `av1`.
- dav1d 1.5.4 and libaom 3.15.2's aomdec decode every test stream (28: every effort from 0 to 8, the pyramid, real time, the
  bitrate modes, keyframes at cuts and the interval, quantizers 0 and 255, error resilience, sizes down to 1x1, all three
  profiles at 8, 10 and 12 bits, HDR metadata) exactly as Egami does (`tools/video/check-av1-encoder.py`); each is the same
  bytes at one thread and at all cores, and with hardware intrinsics off. 10 minutes of AFL++ on the av1enc target found no
  crash (two inputs at effort 10 ran past AFL++'s 5 s limit: 5.5 s each).
- Against libaom 3.15.2 `aomenc --good` (one-pass VBR, 25 frames of lag, alternate references) and SVT-AV1 3.0.2
  (`tools/video/time-av1-encoder.py`), at effort 4, 720p, 60 frames, BD-rate on PSNR-Y / SSIM, with stage B's pyramid,
  temporal model and filters: against `--cpu-used=4` crowd_run +18.4 % / +17.3 %, ducks_take_off +15.4 % / -8.8 %, park_joy
  +21.4 % / +17.0 %, old_town_cross +42.8 % / +13.3 %; against `--cpu-used=5` +16.2 % / +8.3 %, +13.5 % / -10.3 %, +14.2 % /
  +7.1 %, +37.0 % / -1.5 %; against `--cpu-used=6` +7.9 % / +3.4 %, +12.4 % / -11.1 %, +13.8 % / +6.3 %, +25.7 % / -1.5 % (stage
  A: +26.7 % / +7.6 %, +31.9 % / -4.0 %, +34.5 % / +13.8 %, +131 % / -3.5 %). SVT-AV1 preset 6: -9.8 % / -8.0 %, -5.3 % /
  -19.8 %, -4.4 % / -4.4 %, +9.5 % / -6.2 %. The +15 % gate is met on both metrics against `--cpu-used=6` by three clips of
  four and against `=5` by two (crowd_run misses PSNR-Y by 1.2 points); old_town_cross misses PSNR-Y at every setting, and
  against `=4` PSNR-Y is met by none but ducks_take_off's +15.4 % is close.
- Speed (1080p, 10 frames, effort 4, crowd_run and park_joy, best of two runs): one thread 6.3 and 4.9 s a frame (stage A:
  8.3 and 5.4 s), between libaom's `--cpu-used=4` (8.3 and 7.2 s) and `=5` (3.3 and 2.8 s); all 24 cores 1.6 and 1.4 s (stage A
  1.8 and 2.2 s) against 2.5 and 2.3 s at `=4`, 0.76 and 0.66 s at `=5`, 0.63 and 0.55 s at `=6`. So the setting matched by
  time is `=4`, where the BD-rate gate is missed, and the setting matched by BD-rate is faster than `=6`, against which Egami
  takes 2.3 to 2.4 times as long on one thread and 2.5 to 2.6 times on all cores: the 2x speed gate is missed, by much less
  than before stage A (12 to 14 times and 48 to 57 times).
- Rate control (720p, 100 frames, 3 Mbit/s, effort 0): one-pass bitrate +0.8 % to +2.1 % of the target and two-pass +1.0 % to
  +3.1 %, within the 5 % gate; constant bitrate -6.4 % to +2.2 %, its one-second buffer never below 55 %.

V5.6: smart cut. API reviewed in the combined V5 encoders review, accepted 2026-10-24.

### Trimming

- `MediaFile.Trim` (a file or a `Stream` in and out, with progress and a cancellation token) cuts from a start up to an end,
  with timestamps from 0, and returns a `TrimResult`: the mode that ran, where the cut starts in the input, the frames written
  and encoded again, the packets, and the warnings. The first frame is the first at or after the start, within half of the
  container's tick; the video ends after the packet that shows the last frame before the end, and a hidden frame after it
  that nothing shows is dropped.
- `TrimMode.Smart`, the default, is frame-exact: it decodes from the keyframe before the start and encodes the frames up to
  the next keyframe again as a closed group starting with a keyframe, then copies every packet from that keyframe byte for
  byte. VP8, VP9 and AV1; the group matches the stream (codec, profile, size, bit depth, chroma, VP9's color_space and range,
  AV1's sequence header byte for byte through `Av1SequenceOptions.FromSequenceHeader`) and is coded at the average quantizer
  of the packets it replaces. Motion JPEG, uncompressed video and starts on a keyframe are a plain copy. A stream the encoder
  cannot match is encoded again whole (a `trim-reencode` warning); a codec Egami.Video cannot encode, or frames that change
  size, start at the keyframe (`trim-keyframe`).
- `TrimMode.Keyframe` copies from the keyframe at or before the start; `TrimMode.Reencode` encodes the whole cut again at the
  source's quantizer.
- Audio is cut at the sample for PCM (packets sliced), MP4 (the edit list) and Opus in Matroska and WebM (CodecDelay with 80 ms
  of pre-roll, DiscardPadding), else at the nearest packet (`trim-audio-packet`); subtitle cues and chapters are clipped to
  the cut; tags and attachments are kept.
- Matroska writes DiscardPadding as the signed element it is: a padding whose top bit was set (14 ms, for one) read back as
  none, so the last Opus packet kept its padding.
- Matroska reads each Vorbis packet laced in one block at its own time, where the samples before it end: all of them had the
  block's time, so a trimmed or remuxed laced Vorbis track lost about a fifth of its samples.
- vpxdec, dav1d and FFmpeg decode the cuts without errors, to the same frames as Egami (`tools/video/check-smart-cut.py`).
- 10 minutes of AFL++ on the new trim target found no crash once broken timestamps were rejected (two inputs that overflowed
  are regression tests).

V5.0 audio core: decoded audio, PCM and FLAC, WAV and FLAC files, resampling and channel mixing; V5.1: Opus decoding and Ogg;
V5.5: Vorbis decoding. V5 audio API reviewed and accepted 2026-10-22 (all as recommended).

V4.2, API reviewed and accepted 2026-10-22 (all as recommended): AV1 decode, through Egami's AV1 stream decoder (Egami 1.1.0).

### Frames and conversion

- `AudioFrame`: samples for every channel with a timestamp, rate, `ChannelLayout` and `SampleFormat`, from the context's
  memory source. Twelve sample formats: unsigned 8-bit, signed 16, 32 and 64-bit, 32 and 64-bit float, each interleaved or
  planar; `ValidBits` says how many bits an integer sample uses (24-bit audio is `S32` with 24).
- `Convert` and `CopyTo` change the format, with optional triangular dither (`AudioConvertOptions.Dither`).
- `ChannelLayout` and `Speakers`: channels in WAVE_FORMAT_EXTENSIBLE order, presets from mono to 7.1.4, unpositioned channels.
- `ChannelMixer`: ITU-R BS.775 downmix and upmix matrices, or one of your own.
- `Resampler`: polyphase windowed-sinc (Kaiser) between any two rates, `ResamplerQuality.Fast`, `Good` (default: under 0.1 dB
  ripple to 0.9 of Nyquist, 100 dB stopband) and `Best` (140 dB), with exact timestamps.

### Codecs

- `AudioCodec`, `IAudioDecoder` (with `SendLoss`), `IAudioEncoder`, `AudioDecoderOptions`, `AudioEncoderOptions` and
  `AudioCodecs`, registered in `MediaContext.AudioCodecs` with `MediaContext.WithCodec(AudioCodec)`.
- `PcmCodec`: 8 to 32-bit integer PCM, little and big-endian, and 32 and 64-bit float, decoded and encoded.
- `FlacCodec`: all of RFC 9639 decoded, bit-exact with libFLAC on every IETF FLAC test file it decodes, with CRC and MD5
  checks (`Flac.BadCrc`, `Flac.Md5Mismatch`); encoding at levels 0 to 8 (default 5), 4 to 32 bits, 1 to 8 channels, files
  within 1 % of libFLAC's size at every level and accepted by `flac -t`.
- `AudioTrackInfo.Layout` and `AudioTrackInfo.SeekPreRoll`.

### Containers

- `WavFormat`: WAV, RF64 and BW64 (`WavFormat.Instance`) and Sony Wave64 (`WavFormat.Wave64`), read and written:
  WAVE_FORMAT_EXTENSIBLE, streamed files with unfilled sizes, LIST/INFO and id3 tags, cue points as chapters, and RF64 by
  itself past 4 GB.
- `FlacFormat`: native FLAC read and written, with Vorbis comments, pictures as attachments, seek tables and the channel mask.
- FLAC and PCM in Matroska and MP4 decode and encode through the existing containers.

### Reading and writing

- `AudioReader` and `AudioReaderOptions`: the first audio track of any file as planar float, or the format, rate and layout
  asked for; codec delay and edit lists trimmed (`KeepAllSamples` keeps them); sample-exact `Seek`.
- `AudioWriter` and `AudioWriterOptions`: frames of any length and format into WAV, Wave64, FLAC, Matroska or MP4.
- `MediaFile.Transcode`: copies a file and encodes its audio again with another codec, for example FLAC in Matroska. Opus
  sources are encoded at the 48 kHz Opus decodes to, not the source rate its header names.
- `VideoWriterOptions.AudioCodec` (with `AudioSampleRate`, `AudioLayout` and `AudioBitsPerSample`) and
  `VideoWriter.WriteAudio`: an audio track encoded next to the video.
- AFL++ fuzz targets `wav` and `flac`.

### Opus and Ogg (V5.1)

- `OpusCodec` (decode only), registered by default: SILK, CELT and hybrid frames at every bandwidth, mono, stereo and the RFC
  7845 multichannel mappings 0, 1 and 255, translated from libopus 1.5.2. All twelve RFC 8251 test vectors pass opus_compare
  in mono and stereo (96.2 to 100 %), with libopus's final range on every packet; files from opusenc decode within one 16-bit
  step of opusdec. Output is planar float at 48 kHz in the canonical channel order (5.1 and 7.1 remapped from Vorbis order).
- Lost packets, reported with `IAudioDecoder.SendLoss` or seen as a gap in timestamps, are concealed, or recovered from the
  next packet's in-band FEC. Ambisonic mappings (families 2 and 3) are reported as `Opus.UnsupportedMapping`.
- `AudioDecoderOptions.KeepPadding`: end padding past a packet's stated duration is trimmed by default (Ogg's last granule
  position, Matroska's DiscardPadding); `AudioReaderOptions.KeepAllSamples` keeps it.
- `OggFormat`: Ogg (.ogg, .opus, .oga) read and written with Opus, Vorbis and FLAC; CRC-checked pages,
  timestamps from granule positions, seeking from a one-pass index, and chained files joined into one track with each
  link's title as a chapter (`ogg-chained-stream` when a link changes the settings).
- Opus in Matroska and WebM gains SeekPreRoll and DiscardPadding, read and written; Opus in MP4 gets its 80 ms pre-roll.
- Decoding the twelve test vectors in stereo takes 0.72 s on one thread, against 0.89 s for libopus's opus_demo.
- AFL++ fuzz targets `ogg` and `opusraw`.

### Vorbis (V5.5)

- `VorbisCodec` (and `AudioCodecs.Vorbis`, decode only), registered by default, written from the Vorbis I specification:
  1 to 255 channels, floors 0 and 1, residues 0, 1 and 2 (floor 0 untested: no current encoder writes it). On eleven oggenc clips (8 to 48 kHz, mono to 7.1, q-1 to q10) every
  sample is within 2^-22 of libvorbisfile's float output, with the same sample count. Output is planar float in the
  canonical channel order (3 to 8 channels remapped from Vorbis order, as for Opus).
- Packet durations from the setup header's modes, in Ogg, Matroska and WebM, and MP4 (esds object type 0xDD, as FFmpeg
  writes it). The end is trimmed to Ogg's last granule position, Matroska's DiscardPadding (now read and written for Vorbis
  too) or a shorter last MP4 sample; Ogg's granule positions are written in samples when remuxing from Matroska.
- A corrupt packet is reported as `Vorbis.CorruptPacket`; with `DecodeErrorMode.Conceal` it becomes silence.
- Decoding 60 s of stereo 44.1 kHz takes 75 ms on one thread, against 227 ms for a libvorbisfile run (vorbisref, which also
  starts a process and writes its output).
- AFL++ fuzz target `vorbisraw`.

### AV1

- `Av1Codec` (and `VideoCodecs.Av1`), registered in `MediaContext.Codecs` by default: AV1 from WebM, Matroska, MP4 and IVF, 8, 10
  and 12 bit, 4:0:0 to 4:4:4, film grain, every frame equal to libaom's on our clips and libaom's size-changing Matroska vectors.
  Hidden frames give no frame; frames shown with show_existing_frame come out. The sequence header's color wins over the
  container's.
- Exact and keyframe seeks in IVF, WebM, Matroska and raw AV1 land on shown key frames; 100 random exact seeks per container
  match decoding from the start.
- Broken data: `Skip` (the default) reports `Av1.CorruptFrame`, or `Av1.MissingReference` for a unit that needs frames from
  before the first key frame, and resumes at the next shown key frame; `Conceal` repeats the last good frame in between (mid-gray
  before the first); `Throw` throws.
- `VideoDecoderOptions.ApplyFilmGrain` turns film grain off.
- HDR: content light level and mastering display metadata OBUs (in the units, or in av1C's configOBUs) give `VideoFrame.Hdr`,
  equal to ffprobe's side data on an SVT-AV1 HDR10 clip; an ITU-T T.35 metadata OBU with HDR10+ gives `VideoFrame.Hdr10Plus`.
- Keyframes are units that start with a shown key frame (forward key frames are not); `Av1.KeyframeFlagMismatch` when a
  container disagrees. The sequence header comes from the track's av1C when a unit has none.
- IVF AV1 tracks get their bit depth, chroma format and av1C from the first sequence header; the Matroska writer builds av1C
  from the first packet for a track that has none.
- `Av1Packets`: `SplitFrames`, `MergeFrames` and `ExtractConfig` (av1C from a sequence header, equal to FFmpeg's on every clip).
- `CodecHeaders.Describe` for AV1: each OBU, the sequence header, metadata (content light level, mastering display, ITU-T T.35,
  scalability, timecode) and the start of each frame header, under the specification's names. `CodecHeaderReader` reads a
  track's packets in order and lists each frame header whole, as the decoder read it.

### Raw AV1

- `Av1ObuFormat` (.obu, low-overhead OBUs as aomdec and dav1d read them) and `Av1AnnexBFormat` (the specification's Annex B,
  as the conformance streams use), registered by default and detected from their first bytes, read and written. Frames are timed
  at a `FrameRate` you set (30 a second by default). Reading and writing back gives the same bytes; packets match ffprobe's,
  and aomdec and dav1d decode the files the writers make to libaom's frames.

### Progress, cancellation and streams

V1.3, API reviewed and accepted 2026-10-22 (all as recommended).

- `MediaFile.Remux` and `MediaFile.Transcode` take `IProgress<MediaProgress>? progress` and `CancellationToken
  cancellationToken` as their last parameters. Reports come once the output is open, then at most every 250 ms, then a final
  one with `IsFinal` and `Fraction` 1. A cancelled job throws `OperationCanceledException` within a packet, without finishing
  the output, and deletes a file it created. Without a handler and a token the jobs run as before.
- `MediaProgress`: position, duration, fraction (from the time, else the bytes read, else `null`; never backwards), frames,
  packets, bytes read and written, elapsed time and speed.
- `Stream` overloads of `Remux` and `Transcode` with the output container named and `leaveOpen`, to read from and write to
  cloud storage without a local copy. Non-seekable input works (except MP4 with moov at the end); Matroska, WebM, IVF and
  YUV4MPEG2 write to non-seekable output as they go, MP4 through a temporary file, and AVI refuses it.

V4.1, API reviewed and accepted 2026-10-22 (all as recommended): MP4 and QuickTime. Built on Egami's public ISO BMFF box
walker (Egami 1.1.0).

### MP4 and QuickTime

- `Mp4Format` (registered by default) reads .mp4, .m4v, .m4a and .mov files: moov before or after the media, 64-bit sizes and
  chunk offsets, compact sample sizes, composition offsets, several sample descriptions, and fragmented files (moof, traf,
  tfhd, tfdt, trun) read one fragment at a time. Packets, times, flags and bytes equal ffprobe's on the test files.
- Codecs: AV1 (av1C), VP9 and VP8 (vpcC), Motion JPEG (jpeg, mjpa, mp4v 0x6C), Opus (dOps), FLAC (dfLa), PCM (sowt, twos,
  lpcm, ipcm, fpcm), and mov_text map to their codec ids; H.264 and HEVC (`CodecIds.Avc`, `CodecIds.Hevc`), MPEG-4 Part 2, AAC
  and MP3 pass through with the setup Matroska stores; any other sample description passes through whole
  (`CodecIds.QuickTimeVideo`, `QuickTimeAudio`, `QuickTimeOther`).
- Edit lists are applied as FFmpeg shows them; `EncodedPacket.IsDiscardable` marks packets before the shown part, and readers
  drop their frames. `Mp4Format.IgnoreEditLists` turns this off.
- `VideoTrackInfo.Orientation` from the display matrix, and `VideoReaderOptions.AutoOrient` to turn frames upright.
- `MediaInfo.Timecode` from a timecode track; colr, pasp, fiel, clli, mdcv, SmDm and CoLL into the track info.
- Tags (iTunes items, QuickTime keys and udta text), cover art as attachments, and chapters from Nero chpl or a QuickTime chapter
  track.
- `MovTextSample`, `MovTextStyle` and `MovTextFace`: mov_text subtitles read and written with their style runs.
- Writing ISO MP4: half-second chunks interleaved by time, moov first by default (or last with `Mp4Format.MoovAtEnd`), edit
  lists, the display matrix, color and HDR boxes, each codec's config box, tags, cover art, and chapters both as Nero chpl
  and as a QuickTime chapter track. FFmpeg and MP4Box read the files without a warning they do not give for the originals.
- Seeking by sync samples in flat and fragmented files; exact seeks match decoding from the start.
- Broken files are read up to the damage, with `mp4-*` warnings; sample tables count against `MediaLimits.MaxIndexEntries`.
- AFL++ fuzz targets `mp4` (demux, seek, non-seekable) and `mp4mux` (read, write three ways, read back).

Egami.Video now lives in the Egami repository (it was inexin/Egami.Video), built and tested with the other packages; its
version and NuGet package are its own.

V3, API reviewed and accepted 2026-10-22: VP9 decode in all four profiles, equal to libvpx 1.14.1 on the test vectors (the
decoder is translated closely from libvpx, notice in THIRD-PARTY-NOTICES.md); WebM alpha; HDR10 and HDR10+ metadata
(`Hdr10PlusMetadata`, `VideoFrame.Hdr10Plus`); `VideoFrame.RenderSize`; color-managed `YuvVideoFrame.ToRaster`; SIMD kernels
and threads within 1.5x libvpx's decode time.

### VP9 decoding

- `Vp9Codec` (and `VideoCodecs.Vp9`), registered in `MediaContext.Codecs` by default: VP9 from WebM, Matroska and IVF in all four
  profiles, 8, 10 and 12 bit, 4:2:0, 4:2:2, 4:4:4 and RGB, with superframes, hidden frames, show-existing frames, intra-only
  frames, tiles, lossless coding, compound prediction and references of another size. Every frame of libvpx's VP9 test vectors
  equals libvpx 1.14.1's. Frames come out as `YuvRaster<byte>` at 8 bits and `YuvRaster<ushort>` above, at their own coded size.
  4:4:0 streams are reported (`Vp9.UnsupportedSubsampling`) and not decoded.
- WebM VP9 with alpha (AlphaMode 1): the second VP9 stream in each packet's addition 1 decodes into the frame's alpha plane, as
  for VP8, unless `VideoDecoderOptions.DecodeAlpha` is off.
- `CodecHeaders.Describe` lists VP9 header fields: the superframe index, each frame's uncompressed header and the compressed
  header's modes with probability updates as counts.
- Streams that change size come out frame by frame at each frame's coded size; `VideoFrame.RenderSize` reports VP9's
  render_size without scaling to it, as libvpx and FFmpeg do. Exact seeks land on the same frames as decoding from the start,
  over hidden and show-existing frames.
- Broken VP9 data follows `VideoReaderOptions.Errors`: `Throw` throws, `Skip` drops frames up to the next keyframe, and
  `Conceal` repeats the last good frame (mid-gray before the first). Diagnostics: `Vp9.CorruptFrame` (a broken frame or alpha
  frame), `Vp9.MissingReference` (an inter frame with no keyframe before it), `Vp9.UnsupportedSubsampling`,
  `Vp9.KeyframeFlagMismatch` (the container's keyframe flag disagrees with the frame; the frame wins) and
  `Vp9.CodecPrivateMismatch` (the track's CodecPrivate disagrees with the stream; the stream wins).

### Safety

- VP9 frame sizes are checked against `EgamiContext.Limits` (width, height, pixels, and the memory of the nine frame buffers a
  stream can hold) before anything is allocated. A stream holds at most nine frame buffers, eighteen with alpha.
- AFL++ fuzz targets `vp9` (files through VideoReader in each error mode) and `vp9raw` (packets, superframes and CodecPrivate
  straight into the decoder).

### Speed

- VP9 decoding uses SSE2, SSSE3 and AVX2 vectors for the sub-pixel filters, the inverse transforms and the loop filter, at 8 bits
  and above, with the same output. One thread decodes 8-bit 720p, 1080p and 2160p within 1.5 times libvpx 1.14.1's time.
- VP9 frames decode their tile columns in parallel and loop filter their superblock rows as a wavefront, on up to
  `EgamiContext.MaxParallelism` threads, with the same output at any thread count.

### Color and HDR

- VP9 frames carry their color code points (the bitstream's color space and range, or the container's Colour element when it
  says more) and the track's mastering display and light level metadata, on the frame and in the raster's metadata.
- `YuvVideoFrame.ToRaster<TPixel>(ColorProfile, ColorTransformOptions?)` converts a frame to another encoding through Egami's
  color pipeline, tone mapping PQ and HLG to SDR from the static HDR metadata.
- `Hdr10PlusMetadata` and `VideoFrame.Hdr10Plus`: HDR10+ dynamic metadata (SMPTE ST 2094-40) from WebM's BlockAdditional 4,
  read per frame and kept unchanged when remuxing.

### Containers

- WebM and Matroska VP9 tracks take their bit depth and subsampling from the CodecPrivate feature records; IVF VP9 tracks from
  the first keyframe.
- Writing Matroska or WebM joins VP9 hidden frames that arrive as packets of their own into a superframe with the next shown
  frame, as WebM players expect.

Built on the Egami with faster VP8 decoding; no API changes, and every frame is the same as before. Single-thread VP8 decode
through Egami.Video now takes 1.40 times libvpx 1.14.1's time on the 720p grain clip and 1.52 times on the 1080p one, down from
1.60–1.67; all-core decode takes 1.23–1.27 times. Egami's own decoder takes 1.43 times on both; Egami.Video's per-frame copy into
its frames adds the rest.

V2, API reviewed and accepted 2026-10-15: VP8 decode through Egami's Vp8StreamDecoder, equal to libvpx on the test vectors;
WebM alpha (packet additions, DecodeAlpha); IVF read and write; exact seek by default with SeekMode.Keyframe; CodecHeaders.Describe
for VP8 header fields; DecodeErrorMode (Throw, Skip, Conceal) with Skip the reader's default, replacing
VideoReaderOptions.SkipCorruptFrames. Single-thread VP8 decode is 1.6x libvpx's (accepted; an Egami step to speed it up follows).

First release (V1), API reviewed and accepted 2026-10-11: frame and packet model with bit depths to 12, alpha, packed
layouts, HDR and color carried end to end; AVI (with OpenDML), Matroska/WebM and YUV4MPEG2 read and write, and headerless raw
video; Motion JPEG and uncompressed video decode and encode; audio passthrough, keyframe seeking, tags, chapters and
attachments; PSNR, SSIM and framemd5-style hashes.

## Egami.Video.H264 1.0.0 (2026-10-25)

The first release: V7, H.264 decoding, in an opt-in package that ships at Egami.Video's version. API review questions decided 2026-10-23 (all as
recommended): the raw reader is named "H.264 Annex B", `FrameRate` stays nullable, and broken data reports `H264.CorruptFrame`.

- `H264Codec` and `WithH264()`: a managed H.264 decoder written from ITU-T H.264 for progressive Constrained Baseline, Main,
  High, High 10 and High 4:2:2 streams, 8 and 10 bit, 4:0:0, 4:2:0 and 4:2:2, and their Intra profiles: CAVLC and CABAC, B
  slices with spatial and temporal direct prediction, explicit and implicit weighted prediction, the 8x8 transform and scaling
  matrices, long-term references and memory management, and several slices per frame. Frames equal FFmpeg's decode on all 31
  corpus clips. Interlaced streams, 4:4:4, lossless, FMO, ASO, SP and SI slices and data partitions give
  `UnsupportedFormatException`.
- Decoding starts at the first IDR or recovery point; a broken slice reports `H264.CorruptFrame` and its macroblocks are
  concealed from the frame before (`Skip` and `Conceal`) or the frame is finished and the error thrown (`Throw`); a missing
  reference reports `H264.MissingReference`. Colour comes from the VUI, then the container, then BT.601 or BT.709 by size; HDR10
  mastering display and content light level SEI become `Hdr`, HDR10+ (T.35 user data SEI) `Hdr10Plus`.
- Slices of a frame decode in parallel and deblocking runs as a wavefront on `MaxParallelism` threads, with the same frames as
  one thread. Motion compensation, deblocking and the output copy use Vector128 with a scalar reference the tests compare.
- `H264Format`: raw Annex B streams (.h264, .264, .avc), read with a keyframe index for seeking, timed by the VUI frame rate,
  25 fps, or `FrameRate`.
- The CABAC initialisation values and CAVLC tables are transcribed from OpenH264 (BSD-2), see THIRD-PARTY-NOTICES.md.

## Egami.Video.Cli 1.0.0 (2026-10-25)

The first release of the `egami-video` command-line tool (Video V1.1), a .NET tool built on Egami.Video's public API that
carries Egami.Video's version. Command and tool review accepted 2026-10-23 (all as recommended). The subtitle commands
follow in 1.1.

### Package icon

- The package has its own icon on NuGet: a play mark and a prompt on a magenta tile.

### Commands

- `info`: container, duration, tracks, codecs, tags, chapters and attachments from the headers; `--all` adds packet and keyframe
  counts and bit rates, `--headers` the codec headers, `--packets` and `--frames` every packet and decoded frame. `--format`
  writes text, JSON (schema `egami-video-info-1.json`), CSV, XML with the JSON's fields, or JSON with ffprobe's field names.
  Track counts, packet counts and timestamps equal ffprobe's on the corpus.
- Stream copy, never decoding: `remux` (another container, chosen tracks, tags, chapters and attachments kept), `trim` (from the
  keyframe at or before `--from`, exactly to `--to`, reporting the shift), `concat`, `segment` (`--every`, `--at`),
  `timestamps` (`--shift`, `--rate`, `--fix`, `--start-at-zero`), `extract` (a track, attachments or cover art to their own
  files), `embed` (tracks from other files, attachments, cover art, track languages, names and defaults) and `metadata` (tags,
  chapters and track fields as JSON with schema `egami-video-metadata-1.json`, and loading them back).
- Frames: `frames` (every frame, `--every`, `--at`, `--keyframes`, `--count`, ranges; Motion JPEG frames to JPEG byte for byte),
  `encode` (images, image sequences or Y4M into Motion JPEG or uncompressed clips), `run` (egami's steps and `+` syntax on every
  frame, the same pixels as `egami run`), `thumbnail`, `scenes`, `sheet` (a contact sheet with times) and `animate` (GIF, WebP,
  APNG).
- Checks: `hash` (per frame or packet, framemd5's layout and hashes) and `compare` (PSNR and SSIM per plane, within 0.01 dB of
  ffmpeg's psnr filter; `--min-psnr`, `--min-ssim`).
- `audio`: a track to WAV, Wave64, FLAC, Ogg FLAC or Opus with resampling, remixing and sample format conversion, or the audio
  of a video file encoded again while its video is copied. `--codec opus` (the default for .opus and WebM outputs) encodes Opus
  with `--bitrate`; ffmpeg mode maps `-c:a libopus` and `-b:a` onto it.
- `formats`: the containers and codecs this build reads and writes.
- `ffmpeg` and `ffprobe`: a documented subset of their command lines, mapped onto the commands above; anything else stops with
  exit code 2 and names the option. `--explain` prints the native command.
- `mcp`: the commands as tools for AI assistants over the Model Context Protocol, inside the folders allowed, with progress
  notifications and cancellation.

### Conventions

- Tracks with `--track` (`v`, `a:1`, `s:eng`, a track number), `--no-audio` and `--no-subtitles`; times as seconds,
  `[HH:]MM:SS[.mmm]` or `#frame`.
- Globs, batches, output templates and `-` for standard input and output, read and written as they go.
- A live progress line on a terminal; `--progress <file|->` writes ffmpeg's `-progress` keys, or JSON lines with
  `--progress-format json`. Ctrl+C stops between packets, deletes the output it was writing and exits with 130.
- `egami video <command>` runs it from the `egami` tool.
- `scenes` uses Egami.Video's `SceneDetector` (V1.4): `--method adaptive|content|histogram` (adaptive by default), each
  method's own default `--threshold`, and `--min-gap` 0.6 s by default; `ffmpeg -vf select='gt(scene,X)'` maps to
  `--method content`.

### Smart cut (V5.6)

- `trim --smart` cuts on the exact frame, encoding again only the frames from `--from` up to the next keyframe; `trim
  --reencode` encodes the whole cut again. Both read a file, not standard input.
- `segment --scenes --exact` starts every part on its scene's first frame, by smart cut.
- `egami-video ffmpeg` maps a cut that does not copy the video (`ffmpeg -ss 5 -to 9 -i in.webm out.webm`) to `trim --smart`.
- `#n` frame numbers no longer count a VP8 hidden frame, which shares the time of the frame after it.
- `info` names MP1 tracks (`mp1`, MP1 (MPEG audio layer 1)) and gives them the `fltp` sample format, as MP2 and MP3 (V5.8 API
  review Q13).

## Egami 1.2.0 (2026-10-24)

The release train: Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic, Egami.Cli and Egami.Cli.Host move
to 1.2.0 together. It adds API and removes none.

### AV1 encoder speed (Phase 2.9.11, stage A)

- `Av1StreamEncoder` decides each tile's superblock rows as a wavefront on up to `EgamiContext.MaxParallelism` threads, so one
  tile uses every core. The output is byte for byte the same at any thread count and without SIMD: rates come from a fixed CDF
  rule (intra frames start a row from the row above's CDFs after its second superblock and blend 3:1 with the above-right
  superblock's; inter frames cost every symbol at the frame's starting CDFs). Still pictures keep their rows in raster order,
  so still AVIF files are byte for byte as before.
- A motion pre-pass searches every reference for every 16x16 cell (quarter, half and full size, rows in parallel). The block
  search starts from its vectors and the vector of the block around it, searches only the references the pre-pass ranks best,
  and does not try a block whole whose cells move apart. Sub-sample refinement uses the decoder's SIMD filters and a pruned
  pattern; vector rates, reference vector stacks and coefficient rates are kept instead of costed again.
- `Av1FramePlan.Referenced` says whether later frames predict from a frame. One that is not is searched without transform size
  search, chroma from luma, inter-intra, overlapped and warped motion, filter choice and global motion.
- At effort 4, inter frames keep 64x64 blocks whole, try no 4x4 blocks and fewer intra modes. On the four derf clips (720p, 60
  frames) the BD-rate (PSNR-Y) is 5.2% higher than 1.1.0's, for 4 to 6 times the speed on one thread and 12 to 20 times on 24 at
  1080p. Encoded bytes of video and AVIF sequences change.

### AV1 encoder compression (Phase 2.9.11, stage B)

- `Av1FramePlan.Tune` chooses a frame's tools: `Av1Tune.Video` (the default) codes one quantizer for luma and chroma, no
  quantization matrices and no loop filter sharpness, without Variance Boost or SSIM-weighted multipliers, and weighs rate by
  the square of the DC quantizer step a little more for key frames and by `Av1FramePlan.Layer`, the frame's pyramid layer, more
  for frames fewer others predict from. `Av1Tune.Image` keeps the still-picture tools of libaom's tune IQ that AVIF uses.
  `Av1FramePlan.VarianceBoost` turns Variance Boost on for video frames too (off by default under `Video`).
- `Av1StreamEncoder.Analyze` runs a temporal dependency model over frames ahead in coding order (`Av1AnalysisFrame` names
  each frame's references): every 16x16 block's intra and inter cost from the motion pre-pass on the source, propagated back
  from the last frame, in a fixed order. `Av1FrameAnalysis.Importance` can set a group's base quantizer, and a plan's
  `Av1FramePlan.Analysis` gives a referenced layer-0 frame superblock quantizers (within nine steps of four) and the other
  frames block multipliers by how much of each block the frames after it reuse.
- Video reference frames search their deblocking levels in one pass over all 64 levels (each line's outcomes and the levels
  where they change, summed as differences), luma across vertical edges then horizontal ones, chroma for both; luma
  loop restoration with a closed-form Wiener filter per 256x256 unit, kept where it pays. Frames nothing predicts from try only
  the CDEF strengths of their nearest references.
- Effort 3 and 4 try 4x4 inter blocks again in the 16x16 cells the motion pre-pass predicts worst.
- Through Egami.Video's pyramid (720p derf clips, 60 frames, effort 4), BD-rate on PSNR-Y / SSIM against libaom 3.15.2
  `--good --cpu-used=6` falls from stage A's +26.7 % / +7.6 % (crowd_run), +31.9 % / -4.0 % (ducks_take_off), +34.5 % /
  +13.8 % (park_joy) and +131 % / -3.5 % (old_town_cross) to +7.9 % / +3.4 %, +12.4 % / -11.1 %, +13.8 % / +6.3 % and
  +25.7 % / -1.5 %. The output is the same at any thread count and without SIMD.
- Video and animated AVIF bytes change (animated AVIF codes its frames with the video tools and Variance Boost); still AVIF
  files are byte for byte as before.

### Chroma key

- `RasterOps.ChromaKey(color, similarity, blend)` keys out a green or blue screen by chroma: the distance between Rec. 709 Cb and
  Cr, leaving luma out, so lighter and darker patches of an unevenly lit screen key with it. `blend` ramps alpha back up for
  soft edges. `MakeTransparent` stays the RGB color key. As the `chroma-key` step in `egami run`.
- `RasterOps.Despill(DespillColor, mix, expand)` takes a screen's green or blue cast off edges and see-through pixels by holding
  that channel down to a limit from the other two. As the `despill` step.

### Command host

- A new package, Egami.Cli.Host: the command host of `egami` as source with internal types, so other command-line tools of the
  family (Egap's `egap`) build on the same exit codes, glob expansion, output templates, batches, folder policy and MCP server.
  It adds no public API and no assembly. `egami` now compiles it in and behaves as before.
- Exit code 130 (`ExitCodes.Cancelled`) for a run stopped with Ctrl+C, and progress from within one file for the MCP server's
  notifications, both for `egami-video`.

### Package icons

- Egami.Cli (`egami`) and Egami.Cli.Host have their own icons on NuGet: a prompt on an indigo tile, and a terminal window on a
  slate tile.

### Icons and cursors

- A 32-bit BMP entry that is wholly transparent is written with black colors, so Windows, which ignores an alpha channel that is
  0 everywhere and draws the colors through the AND mask, draws nothing.

### Text recognition (OCR)

- A new namespace, `Egami.Ocr`: one shape for every OCR engine, so code that needs text from an image works with whichever engine
  is plugged in. Egami ships no engine and links no native code. `IOcrEngine.RecognizeAsync` takes an `OcrRequest` (a
  `Raster<Rgba8>`, its resolution, languages and page index) and returns an `OcrResult`: lines of `OcrWord`s with text, an
  `OcrQuad` box (four corners, so turned and skewed words keep their shape), confidence and language, plus the page rotation the
  engine found. `OcrEngineInfo` gives an engine's languages, largest image and whether calls may overlap.
- `Hocr` and `Alto` read and write hOCR (as browsers read HTML, with `textangle` turns) and ALTO 1 to 4 (pixels, 1/10 mm and
  1/1200 inch, `HYP` hyphens), under `OcrReadOptions` limits and without fetching any DTD or file.
- `CommandOcrEngine` runs any OCR program that writes hOCR or ALTO, such as `tesseract {image} {output} -l {langs} hocr`, without a
  shell, with a timeout and its own temporary folder per run; failures throw `OcrEngineException`.
- Samples (not packed): Tesseract and Azure AI Document Intelligence in `samples/Ocr`, Windows OCR in `samples/OcrWindows`.

### Animated cursors

- Windows animated cursors (.ani) load: `AniFormat.Info` is registered in `EgamiContext.Default`. Load gives one animated raster
  with a frame per step of the `seq ` list, each with its rate as `Delay` and its hotspot in `IcoDetails`, at one size for all
  frames (the largest, or the smallest at least `MinimumSize`); LoadAll gives the animation at every size its frames hold, and
  Probe lists steps and sizes without decoding. `AniDetails` has the title, artist, default delay, stored frames, steps and
  sizes. Cursor, icon and raw-bitmap frames are read; damaged files load in lenient mode with `Ani.*` warnings.
- Animated cursors save too, with `AniWriterOptions`: every frame is a step, repeated frames are stored once with a `seq ` list,
  each stored frame is a cursor at the raster's size or at `Sizes` (`AniWriterOptions.CursorSizes` is 32 to 128), hotspots come
  from the options or each frame, delays are rounded to jiffies, and the title and artist go into the `INFO` list. With several
  sizes every entry has one encoding, 32-bit BMP while each starts within the frame's first 64 KiB and PNG otherwise (so
  `CursorSizes` are PNG), since Windows loads nothing past that and draws another size when BMP and PNG mix; an explicit BMP
  that cannot fit warns with `Save.AniTooLarge`. A GIF converts with `egami convert spinner.gif spinner.ani`.

### Planar YUV

- `ChromaFormat.Yuv411` (chroma at a quarter of the luma width, as NTSC DV stores it) and `ChromaFormat.Yuv410` (a quarter of
  the width and height). `YuvRaster` creates, wraps, crops and resizes them, and `ToRaster`, `CopyTo` and `FromRaster` convert them
  with every siting and the existing upsampling and downsampling options, within one level of the exact reference. Saving them as
  JPEG writes 4:2:2 or 4:2:0; saving as AVIF goes through RGB, and `AvifWriterOptions.Chroma` set to either chooses as if unset.

### VP8 video encoding

- `Vp8StreamEncoder` encodes VP8 video (RFC 6386) a frame at a time from 4:2:0 `YuvRaster` pictures: key frames through the lossy
  WebP encoder, inter frames with motion search against the last, golden and alternate references to quarter pixels, every vector
  mode (ZERO, NEAREST, NEAR, NEW and SPLIT in all four layouts) chosen by rate and distortion against the intra modes, and the
  mode, vector and coefficient probability updates that save bits. libvpx 1.14.1 decodes every test stream exactly as Egami does.
- `Vp8FramePlan` sets each frame's type, `Quality` or `Quantizer`, `Effort` (0 to 10), references and their refreshes and copies,
  sign biases, hidden frames, error resilience and token partitions; `Vp8References` and `Vp8ReferenceCopy` go with it.
  `Vp8EncodedFrame` returns the frame's data with its bits, quantizer, filter level, squared error and macroblock counts.
- At the default effort the frames are within a few percent of libvpx `--good --cpu-used=1` in BD-rate on natural clips (about
  6% larger on test patterns). Lossy WebP stills are unchanged, byte for byte.
- The encoder codes each frame's macroblock rows on `EgamiContext.MaxParallelism` threads with the same output on any number of
  threads, measures loop filter levels on its own reconstruction instead of decoding the frame for each, and gates the golden
  and alternate frame searches and the intra modes where they rarely win. At 1080p and the default effort it takes 0.5 to 1.0
  times libvpx `--good --cpu-used=1`'s time on one thread and 0.6 to 0.8 times on all cores (2.3 to 3.6 times faster than
  before on one thread); the efforts are retuned so that each step buys compression (split vectors from effort 1, 4 × 4 splits
  from 5, the trellis from 6).

### AV1 video encoding

- `Av1StreamEncoder` encodes AV1 video a frame at a time from `YuvRaster` pictures: key and intra-only frames with the AVIF intra
  encoder, and inter frames that predict each block from up to seven references with motion search to an eighth of a pixel from
  the reference vector stack, NEARESTMV, NEARMV, GLOBALMV and NEWMV, the compound pairs (averaged, distance-weighted, wedge and
  difference-weighted), skip mode, inter-intra, overlapped and locally warped motion and dual interpolation filters, chosen by
  rate and distortion against the intra modes as `Effort` allows. From Effort 7 each reference's global motion (translation,
  rotation and zoom, or affine) is estimated. Hidden frames are held for the next temporal unit and shown later with
  `ShowExisting`. libaom 3.15.2 and dav1d 1.5.4 decode every test stream exactly as Egami does, and the output is the same at one
  thread and at all cores.
- `Av1SequenceOptions` sets the stream's size, bit depth, chroma format, color, siting and level, or with `FromSequenceHeader`
  reproduces an existing sequence header byte for byte. `Av1FramePlan` sets each frame's kind, `Quality` or `QIndex`, `Effort`,
  reference slots, refreshes, hidden frames and order hints, error resilience, primary reference, superblock quantizers and screen
  content tools; `Av1References` goes with it. `Av1EncodedFrame` returns the temporal unit with its bits, quantizer, order hint and
  squared error.
- AVIF stills are unchanged, byte for byte.

### Animated AVIF

- A raster with several frames saves as an animated AVIF (avis): one AV1 stream in a color track with each frame's delay, the
  alpha, when any frame is transparent, in an auxiliary track, the loop count in the edit list, and the first frame as the primary
  item. avifdec decodes every frame exactly as Egami does.
- AVIF and HEIC image sequences now load their loop count from the edit list and the track duration (it was 0 or 1).

### AV1 decoding

- 8-bit AV1 decodes about 9 % faster on one thread: 8-bit frames keep their samples as bytes, and the motion filter, the
  loop filter's vertical edges and the warp read them with byte kernels. On 1080p and 720p clips decoding takes about 3.2
  and 3.4 times dav1d's time, from 3.5 and 3.7. Output is unchanged: every frame still equals libaom's.

## Egami.Fonts 1.2.0 (2026-10-24)

### Kashida

- `TextJustify.Kashida` justifies Arabic lines by lengthening joined words with tatweels (U+0640) where shaping marks a
  cluster safe for one: one point per word by the Arabic justification priorities, whole tatweel glyphs of the run's font
  spread over the words up to `ParagraphStyle.Kashida` (`KashidaLevel.Low`, `Medium` or `High`: 1, 2 or 4 per word), and word
  separators take what is left. Vertical lines, scripts that do not join and fonts without a tatweel justify at spaces.
- `GlyphFlags.SafeToInsertTatweel`, produced when `ShapingOptions.ProduceSafeToInsertTatweel` or
  `RunProperties.ProduceSafeToInsertTatweel` is set, marks the clusters a tatweel can go before without changing the
  shaping around it, as HarfBuzz's flag of that name does.

## Egami.Drawing 1.2.0 (2026-10-24)

### Knockout groups

- A layer inside a knockout layer now knocks out as one of its calls, on `RasterCanvas` and `InkCanvas` alike: it composites
  with what the knockout layer started as and replaces what is under it wherever it drew, even where it is translucent, as
  PDF's knockout groups require. A non-isolated layer inside one also starts from that first content. Before, such a layer
  merged over the earlier calls, so they showed through it, and a non-isolated one blended with them.

### Ink drawing

- An image drawn on an `InkCanvas` with overprint off now knocks out every plate it covers, as a solid fill does. Before, the
  plates the image had no ink for (spot plates under an RGB photo, or under an ink image of process plates only) kept what
  was below where the image was opaque, so a spot showed through.

### Kashida

- Tatweels that kashida justification inserts are drawn in a marked-content span with an empty `ActualText`, so text
  extracted from the drawing has none.

## Egami.Svg, Egami.Css, Egami.Heic and Egami.Cli 1.2.0 (2026-10-24)

- No changes of their own beyond Egami 1.2.0: they move to 1.2.0 with the release train. Egami.Heic depends on exactly
  Egami 1.2.0. Egami.Cli gains the `chroma-key` and `despill` steps, reads and writes animated cursors, and compiles in
  Egami.Cli.Host (see Egami 1.2.0).

## Egami.Cli 1.1.0 (2026-10-22)

A new package: the `egami` command-line tool, a .NET tool built on Egami's public API (Roadmap 2.0). It carries its own copy of
Egami, so it shares the Egami release train's version and releases with it. The command review froze its names and options
before this first release. The `cli-native` workflow builds it as a native program for win-x64, linux-x64 and osx-arm64,
which needs no .NET runtime.

### Commands

- `info` (summary, `--all`, `--json` with schema 1), `formats` (what egami reads and writes, and each format's writer settings).
- `convert`, `resize` (the size syntax `W`, `xH`, `WxH`, `P%` with `!`, `^`, `>` and `<`, and `--fit contain|cover|fill|pad`),
  `thumbnail` (upright, shrunk, sRGB, metadata left out), `crop`, `rotate`, `flip` (a direction is required) and `auto-orient`, the last three
  lossless on JPEG.
- `compare` (MAE, MSE, RMSE, PSNR, SSIM, DSSIM, peak and AE, `--threshold`, `--diff`, exit code 1 on a difference) and `stats`.
- `frames split` and `frames join` for GIF, APNG and WebP animations, with their delays.
- `metadata show`, `strip` (lossless for JPEG) and `copy`, which write a lossy format again only with `--reencode`; `color` for ICC conversion and HDR tone mapping.
- `text` (captions and watermarks with the paragraph layout, any font, a bundled default, `--font-size`) and `overlay` (any image, SVG
  included, by anchor, with opacity and blend mode); `montage` contact sheets.

### Pipelines and batches

- `run` chains any steps separated by `+`; every public `RasterOps` method is a step, generated at build time from the
  library, and `save` may come more than once. `--dry-run` prints the plan.
- JSON workflow files with a published schema, `run --workflow` and `run --save-workflow`.
- Every command takes many inputs, with egami's own glob expansion (`*`, `?`, `**`), `--recursive`, output templates (`{name}`,
  `{ext}`, `{dir}`, `{index}`, `{format}`), `--jobs`, `--fail-fast`, and `--in-place` to allow replacing an input.
- Writer settings: `--quality`, `--lossless`, `--effort`, `--bit-depth`, `--strip` (keeps the color profile), and `--set fmt.key=value` for every
  `WriterOptions` property, generated from the library.
- Exit codes 0 to 3, diagnostics on standard error (`-v`, `-vv`, `-q`), safety limits with `--limit` and `EGAMI_LIMITS`.
- `egami mcp` serves every command as a tool to AI assistants over the Model Context Protocol (Roadmap 2.0.1), with the official
  C# SDK (ModelContextProtocol.Core): one tool per command with the command's options and help text, `run` taking a workflow
  object, JSON results with the exit code and the files written, an optional PNG preview of the result, progress and
  cancellation for batches, and a cap of 1,000 inputs per call (`--max-inputs`). Tools read and write only inside the folders the
  assistant opened and those given with `--allow` (the working folder when there are neither), following links, and never
  replace an input without `in_place`. The package is also listed as an MCP server on NuGet, with its `server.json`.
- `egami <name>` runs an `egami-<name>` program from the PATH, so other families' tools join in; `egami formats` names
  `egami-heic` for HEIC, which the tool leaves out.
## Egami.Barcodes 1.0.1 (2026-10-29)

### Package icon

- The package has its own icon on NuGet: a graphite tile with barcode bars, in `branding/` with the other package icons.

### Faster reading

- Reading takes about 0.9 times zxing-cpp 3.1.1's time on one thread over the 817 ZXing test images, each read for its own symbology (3.1 times before), with
  the same symbols read or more. Linear barcodes, PDF417 and QR codes are within 1.5 times zxing-cpp's time; Aztec and Data
  Matrix within about 1.8 times.
- The inverted pass reuses the first pass's thresholds with dark and light swapped, the linear scan reads light-on-dark bars
  from the same scan lines, and inside a symbol the first pass found the inverted pass looks for no other.
- The thresholds, run cutting and Reed-Solomon decoding work on several pixels or values at a time; QR, Data Matrix and
  Aztec check a grid's fixed patterns (timing patterns, the L and clock tracks, the mode message) before sampling and
  decoding it.
- PDF417 rows are read every three quarters of a module instead of every pixel.

### Reading

- From effort 5 the linear scan also reads rows through the local threshold, which keeps bars under glare apart (from effort 6
  before), and scans 29 lines instead of 25, including effort 0 to 2's lines. EAN-13 photos with glare read 16 of 22 ZXing
  test images (11 before).
- QR looks for finder patterns again with more slack when it finds fewer than three, and tries every alignment pattern
  near the expected place, nearest first, then the nearest one nudged by quarter modules, so
  strongly tilted and curled symbols read (ZXing qrcode-3 39 of 42, 37 before; qrcode-4 38 of 48, 35 before).
- PDF417 cut off before its start pattern reads from the stop pattern, using the right row indicators (ZXing pdf417-3 19 of
  19, 16 before).

### Faster writing

- `Barcode.Code128` takes about 0.6 times ZXing.Net 0.16.10's time (1.6 times before): the code set search runs on the
  stack, and the symbol characters and bars are built without lists or copies. Code 128 and Code 39 no longer copy
  text without control characters for the human-readable text.

## Egami.Barcodes 1.0.0 (2026-10-19)

A new package that writes and reads barcodes, drawn through Egami.Drawing.

### Documentation

- A guide to reading barcodes (options, camera frames, GS1 data, what reads well, speed) and cookbook recipes for camera
  frames, pages of barcodes, GS1 labels and SVG output.

### API review changes (breaking against the previews)

- `BarcodeFormat` is a `[Flags]` enum (`Qr = 1` to `UpcE = 1024`, with `None`, `Linear`, `Matrix` and `All`), and
  `BarcodeReaderOptions.Formats` takes it; `BarcodeFormats` is gone. `Barcode.Format` and `BarcodeResult.Format` hold one flag.
- `Barcode.Gs1_128` is now `Barcode.Gs1Code128`.
- `BarcodeResult.AddOn` holds an EAN or UPC add-on; `Text` has the main digits only.
- `BarcodeReader.ReadBarcodes` and `ReadBarcode` also take an 8-bit luminance buffer with its width, height and stride, read in
  place.

### Modules and error correction

- `BarcodeModules` holds a symbol's modules: a grid of dark and light modules for 2D codes, or bar and space widths for linear
  codes. `BarcodeModules.Create` and `CreateLinear` make one from modules you already have.
- Reed-Solomon error correction for every symbology the package will write and read: QR, Data Matrix, PDF417 and Aztec, with
  errors and erasures corrected together.

### Reading

- `ReadBarcodes` and `ReadBarcode` read barcodes from a raster of any pixel type, with `BarcodeReaderOptions` for formats,
  effort, inverted symbols, a region and rendered (pure) barcodes. Each `BarcodeResult` has the format, text, bytes,
  corners, ECI, GS1 element strings and the codewords error correction restored.
- Linear barcodes are found in photos along rows, columns and (from effort 7) diagonals, read both ways with sub-pixel edges
  and two thresholds per line; two lines must agree.
- QR Codes are found by their finder patterns at any rotation and in perspective, corrected by the alignment pattern, several
  per image.
- Data Matrix symbols are found by their L finder and clock tracks, square and rectangular (DMRE too), at any rotation
  and in perspective, several per image.
- PDF417 symbols are found by their start and stop patterns at any rotation and in perspective, compact ones too.
- Aztec Codes are found by their bullseye at any rotation and in perspective: compact, full-range (with the reference grid)
  and runes.
- Read rates on the ZXing blackbox photos at or above zxing-cpp's for most sets, with no false positives; ink spread,
  glare and one-pixel bars handled (Code 128 and EAN edge distances, gray-level PDF417 rows, extra scan lines).
- Many symbols per image, each once and in reading order; inverted and mirrored symbols; large images searched on
  several threads with the same results at any thread count.
- The decoders read every symbology the package writes: QR (mirrored too), Data Matrix (DMRE and 144 x 144 too), PDF417
  (compact too), Aztec (runes too), Code 128 and GS1-128, Code 39 (Full ASCII too), ITF, EAN, UPC and their add-ons.

### QR Code

- `Barcode.Qr` writes QR Codes from text, bytes or GS1 element strings, versions 1 to 40, with the fewest bits over numeric,
  alphanumeric, byte and Kanji segments, and ECI 26 (UTF-8) for text outside ISO 8859-1 and Shift JIS.
- Level M by default, raised to Q or H while the symbol keeps its size; `QrOptions` fixes the level, version or mask.

### Data Matrix

- `Barcode.DataMatrix` writes Data Matrix ECC 200 from text, bytes or GS1 element strings, switching between its six
  encodation modes for the fewest codewords. Square by default; rectangular and DMRE sizes on request
  (`DataMatrixOptions`).

### PDF417

- `Barcode.Pdf417` writes PDF417 and compact PDF417 from text or bytes, with text, numeric and byte compaction, the error
  level from the standard's table and columns for about 3:1; `Pdf417Options` sets columns, level and the compact form.

### Aztec Code

- `Barcode.Aztec` writes compact and full-range Aztec Code from text or bytes, with the fewest bits over its modes and Binary
  Shift and at least 23 % error correction by default (`AztecOptions`); `Barcode.AztecRune` writes Aztec Runes.

### Code 128 and GS1

- `Barcode.Code128` writes Code 128 with the shortest mix of code sets A, B and C.
- `Gs1.Parse` and `Gs1.TryParse` read GS1 element strings such as `(01)09501101530003(17)261231` and check each against the
  GS1 General Specifications (AI, characters, lengths, check digits, dates and times), with messages that say what is wrong.
- `Barcode.Gs1Code128` writes GS1-128, with FNC1 separators only where the AIs need them.

### Code 39 and ITF

- `Barcode.Code39` writes Code 39, with Full ASCII when the text needs it and an optional mod 43 check character.
- `Barcode.Itf` writes Interleaved 2 of 5, and `Barcode.Itf14` writes ITF-14 with its check digit added or checked and bearer
  bars around it.

### EAN and UPC

- `Barcode.Ean13`, `Ean8`, `UpcA` and `UpcE` add the check digit when it is left out and check it when it is given.
  `Barcode.UpcE` also compresses a UPC-A number. `EanUpcOptions.AddOn` adds an EAN-2 or EAN-5 add-on.
- With a font, the digits go between the guard bars, which reach into the text line, and an add-on's digits above it.

### Drawing

- `Barcode` is one symbol: format, modules, the quiet zone of its standard and its human-readable text.
  `Barcode.FromModules` makes one from modules you already have, and `Barcode.Create` makes one of a format chosen at run
  time.
- `DrawBarcode` draws a barcode on any canvas as one path, fitted into a rectangle or at a module size. On raster canvases with
  no rotation or skew, modules snap to whole pixels.
- `ToPath` returns the symbol as one path, and `ToRaster` renders it to a `Raster<Gray8>` with whole-pixel modules.
- `BarcodeDrawOptions` turns the quiet zone off, sets a bar width reduction for ink spread, and gives the font for the
  human-readable text, which is drawn only when a font is given.

## Egami.Heic 1.1.0 (2026-10-22)

### HEIC package

- New opt-in package for HEIC, the HEVC-coded HEIF files phones save photos in. `WithHeic()` registers it in a context; nothing
  registers it on its own, because HEVC is covered by patents (the package's readme and XML docs carry the notice).
- `Raster.Load` and `YuvRaster.Load` read still HEIC images: grids (their tiles decoded in parallel), overlays, alpha, the clean
  aperture, `irot` and `imir` as the orientation, ICC profiles, Exif and XMP, `ReaderOptions.Region` (only the tiles it touches
  are decoded) and streaming rows, under the rules AVIF follows.
- RGB is converted as libheif's heif-dec converts it. Every phone photo and HEVC-coded file of the test corpus equals heif-dec's
  output sample for sample, except a clean aperture at half-sample offsets and one file where heif-dec's HEVC decoder departs
  from HM, the reference.
- An iPhone photo loads in about the time heif-dec takes on one thread, and well under half of it on all threads.

### Gain maps, items and sequences

- iPhone HDR photos: Apple's HDRGainMap auxiliary image and ISO 21496-1 tone-map items load into `RasterMetadata.GainMap`, and
  `ReaderOptions.Hdr` renders them as HDR. Apple's map loads with the same samples as heif-dec's auxiliary image output.
- `HeicDetails` (core) describes a file: brands, HEVC profile, color, grid, and `Items`, every item with its `HeicItemRole`.
  `HeicImage.ReadItem` reads thumbnails, depth maps and mattes on their own.
- Image sequences whose frames are all intra load every frame; others give their still image or first frame.

### HEVC decoder

- A managed HEVC decoder for intra pictures: Main, Main 10, Main Still Picture and the 4:0:0 to 4:4:4 range extensions up to 12
  bits, with tiles, wavefronts, dependent slices, PCM, lossless coding and scaling lists. The intra pictures of the HEVC
  conformance streams in those profiles, and every HEIC test image, decode bit-exact to HM 18.0, the reference decoder.
- Single-threaded it decodes an iPhone photo in about 1.3 times the time of libde265. Tiles, wavefront rows and the loop
  filters run on several threads, with the same output at any thread count.
- Ships at Egami's version and depends on exactly that version of Egami.

## Egami 1.1.0 (2026-10-22)

### AV1 video

API reviewed and accepted 2026-10-22 (all as recommended). Decoding runs at about 3.7 times dav1d's time on one thread.

- `Av1StreamDecoder` (in `Egami.Formats.Av1`) decodes AV1 video a temporal unit at a time: key, inter, intra-only and switch
  frames with every inter tool of the Main, High and Professional profiles, 8 to 12 bits, 4:0:0 to 4:4:4, film grain, scaled
  references, superres and scalable streams (operating point and layer options). Every frame equals libaom's on libaom's test
  vectors and on clips for each tool. `Av1DecoderOptions`, `Av1FrameInfo` and `Av1FrameKind` go with it. Egami.Video uses it for
  AV1 in WebM, Matroska, MP4 and IVF.
- Animated AVIF: `Raster.Load` gives every frame of an image sequence, inter frames included, with each frame's duration, the
  loop count and the alpha track; `Avif.SequenceFirstFrame` is no longer reported. Every frame equals avifdec's.

### ISO BMFF boxes

- `IsoBoxReader`, `IsoBox` and `FourCC` (in `Egami.Formats.Iso`) walk the boxes of MP4, QuickTime, HEIF and AVIF files from
  bytes in memory, one level at a time, with every size checked; `IsoBoxReader.ReadHeader` reads box headers from a stream,
  so a large file's media data can be skipped. This is the walker Egami's AVIF and HEIC readers use, now public for
  Egami.Video's MP4 reader and your own tools. API reviewed and accepted 2026-10-22 (all as recommended).

### HEIC detection

- HEIC files (HEIF with HEVC-coded images, as iPhones and most Android phones save photos) are recognized by their brands.
  `Raster.Probe` reads their container (size after the clean aperture, pixel type by bit depth, alpha, orientation), and
  `Raster.Load` throws an `UnsupportedFormatException` that names the Egami.Heic package. Core Egami holds no HEVC code.
- `HeicDetails`, `HeicItem` and `HeicItemRole` describe a HEIC file's brands, HEVC profile, color, grid and items; Egami.Heic
  fills them when it loads.
- `AvifImage.ReadItem` gives an item's own rotation and mirroring (irot, imir) as its orientation, as `HeicImage.ReadItem` does.

### AVIF writing

- `Save` writes AVIF, coded by Egami's own managed AV1 intra encoder: lossy on avifenc's quality scale (`AvifWriterOptions.Quality`,
  default 60), or lossless as 4:4:4 RGB with the identity matrix (`Lossless`); 8, 10 and 12 bits; 4:2:0, 4:2:2, 4:4:4 and 4:0:0;
  transparency as an alpha item (`AlphaQuality`). Files decode in dav1d and libavif to the same pixels as in Egami.
- `AvifDetails.Lossless` says whether a file's image is coded losslessly, so saving it again writes lossless.
- `AvifWriterOptions.Effort` (0 to 10, default 4) trades time for size, like avifenc's speed. The encoder searches partitions,
  modes and transforms by rate and distortion, quantizes with a trellis, varies the quantizer by superblock as libaom's tune IQ
  does, and picks the loop filters.
- Screen content (text, user interfaces, graphics) is detected and coded with palettes and intra block copy.
- From effort 5, 8-bit 4:2:0 images are converted with sharp RGB to YUV.
- `YuvRaster.Save` writes AVIF from the planes as they are, at 8, 10 or 12 bits, with their matrix, range and chroma siting.
- AVIF files keep EXIF and XMP, the orientation (irot and imir), the ICC profile when the colors need one, HDR luminances
  (clli and mdcv) and gain maps (tmap), which libavif reads and applies.
- Images larger than 8192 x 4352 are written as grids of equal cells (`AvifWriterOptions.GridTileSize` sets the cells); tiles
  and cells are coded in parallel, with the same file at any thread count.

### Faster lossless WebP

- Lossless WebP decodes about 2.4 times faster: a 2048 × 1536 photo in 41 ms instead of 100 ms, against libwebp's 53 ms
  through SkiaSharp, and a 1920 × 1080 screenshot in 2.2 ms instead of 7.2 ms (libwebp 4.8 ms), on one thread. Alpha in
  lossy files, which is coded the same way, gains too.
- The fastest lossless encode (`Effort = 0`) is about 2.3 times faster on screen content: the screenshot in 15 ms instead
  of 35 ms, as fast as libwebp at its defaults, with a 27% smaller file. Higher efforts, and photos at effort 0, gain up to 20%.
  Files are byte for byte the same as before at every effort.

### Faster lossy WebP encoding

- Lossy WebP encodes about 1.5 times faster at the default effort: a 1920 × 1080 screenshot in 190 ms instead of 289 ms on
  one thread, and photos gain as much. Efforts 0 to 5 are 15% to 40% faster, effort 6 about 15%. The 4 × 4 mode search tries
  two modes at once with AVX2, the rate of a mode is only worked out when its distortion leaves it a chance, and the token
  costs and bits are computed with fewer branches. Files are byte for byte the same as before at every effort, with or
  without AVX2.

### Package links

- Every package's NuGet page now links to the documentation at [inexin.github.io/Egami](https://inexin.github.io/Egami/) as its
  project website, and its readme links to the guides there instead of the Markdown sources on GitHub.

## Egami.Fonts 1.1.0 (2026-10-22)

### Speed
- `Paragraph.Create` allocates about a third less for short text: a face's color formats are worked out once, glyph buffers
  are sized to their run, and the GSUB matcher keeps no unused scratch array.

## Egami.Drawing, Egami.Svg and Egami.Css 1.1.0 (2026-10-22)

- No changes of their own. From 1.1.0, Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic and Egami.Cli share
  one version and release together, so these three move to 1.1.0 with Egami.

## Egami.Fonts.Substitutes 1.0.0 (2026-10-18)

A new opt-in package with open fonts that stand in for the fonts documents name most, so text keeps its line breaks on
machines that lack them. It replaces Egap.Fonts 1.0.0, which held the same fonts in the Egap repository:
`Egap.Fonts.StandardFonts` is now `Egami.Fonts.Substitutes.SubstituteFonts`, with the same members.

- Liberation Sans, Serif and Mono for Helvetica, Arial, Times, Times New Roman, Courier and Courier New, Carlito for Calibri
  and Caladea for Cambria, each in four styles, under the SIL Open Font License 1.1.
- `SubstituteFonts.Find` finds a face by family name or common alias in the nearest style; `SubstituteFonts.Collection` holds
  all twenty as a `FontCollection`, for text layout, Egami.Metafiles and Egami.Svg.

## Egami 1.0.0 (2026-10-18)

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

### Package icons

- Every package has its own icon on NuGet, and the docs site has the Egami logo and favicon. The icons, logos and favicons
  are in `branding/`.

### CCITT fax encoding

- `CcittFax.Encode` writes packed bi-level rows as Group 4 (ITU-T T.6) or one-dimensional Group 3 (T.4) with PDF's
  CCITTFaxDecode parameters, the inverse of `CcittFax.Decode`, so a PDF optimizer can store scans as G4. It is the encoder
  TIFF's compressions 3 and 4 already used; `Decode` gives the rows back bit for bit.

### Faster blur, shadow, lighting and emboss on 8-bit images

- `Blur(σ, …, BlurMethod.Fast)` and `Blur` above σ 100 on 8-bit images (Rgba8, Bgra8, Rgb8, Gray8 and the rest of the
  whole-byte types) run each axis as one streaming sweep, rows in vector lanes, in encoded or linear light: 1.4 to 2.5 times
  faster at one thread (a 4000 × 3000 photo at σ 20 in about 105 ms, at σ 60 about 120 ms). Results move by at most one level
  from 1.x; they are still the same at any thread count.
- `DropShadow` reads alpha from its byte and blurs its alpha plane the same way: σ 10 on a 1000 × 1000 logo in 10 ms at one
  thread, down from 48.
- `Lighting` takes the spot cone and specular shine from a power table and writes Rgba8 and Bgra8 bytes directly: on a
  2000 × 2000 logo a distant light takes 15 ms (was 27) and a specular spot 38 ms (was 70), within a level of before.
- `Emboss` reads and writes Rgba8 and Bgra8 bytes directly, twice as fast with the same pixels.

### FOGRA39 print profile

- `ColorProfile.Fogra39` is a built-in CMYK profile for offset printing on coated paper (FOGRA39L, ISO 12647-2, built for
  a 300% ink limit), so RGB to CMYK and back works without finding an .icc file. It is colord's free profile, built by
  ArgyllCMS from Fogra's data, shipped unchanged from CTAN (121 KB in Egami.dll, read on first use); `ToIcc` gives its bytes
  for embedding. Conversions through it match Little CMS 2 within 0.03% of ink and 0.01 in L*a*b*. CMYK files without a
  profile still convert by the plain formula.

### Printing with inks

- Inks: `Colorant` (process, spot or technical, with an `InkAppearance` in CMYK, sRGB or Lab for where it cannot be printed),
  `InkColor` (PDF's DeviceCMYK, Separation and DeviceN: `Cmyk`, `Spot`, `Mix`) and `InkSet` (a profile's process inks plus
  spots, at most 64; `FromProfile`, `WithSpot`, `WithDisabled`).
- n-colour ICC profiles (2CLR to FCLR) read as `ColorModel.NColor`, with `ChannelCount` and `ColorantNames`;
  `ColorTransform.Apply(ReadOnlySpan<float>, Span<float>)` converts to and from any channel count. Conversions match Little
  CMS 2.19 within 0.4 ΔE2000, its black point rules included.
- `InkRaster`: a page as 8 or 16-bit plates plus alpha (`InkPlate`, each also a gray raster over the same memory), saved and
  loaded as one separated TIFF with InkNames (`InkTiffOptions`); libtiff's tools read the files.
- `InkRaster.Proof` shows the plates on screen (spots add their CMYK equivalent to the process inks, as Ghostscript's and
  MuPDF's overprint simulation; within 1.1 ΔE2000 of Ghostscript's), optionally with the paper's tone (`ProofOptions`);
  `MeasureCoverage` gives the largest total ink, where it is, the share over a limit and each plate's coverage (`InkCoverage`).
- Fixed: a TIFF with an empty Deflate strip threw instead of loading the strip as empty.

### AVIF

- `AvifFormat` reads AVIF (AV1 Image File Format over HEIF) stills, registered by default. The AV1 decoder, written from the
  specification with libaom and dav1d as references, covers every intra coding tool, the loop filter, CDEF, loop restoration,
  superres and film grain at 8, 10 and 12 bits and every chroma format; its output is bit-exact against dav1d on libaom's and
  dav1d's test streams, the Argon conformance suite and the corpus.
- `Raster.Load` converts to RGB as libavif does (equal to avifdec sample for sample) in the natural pixel type: `Rgb8` or
  `Rgba8` for 8-bit files, `Rgb16` or `Rgba16` for 10 and 12 bits, gray types for monochrome. Alpha items (straight or
  premultiplied, which loads straight), grids (alpha grids and per-cell alpha included) and overlays (iovl) are supported;
  the clean aperture is cropped, and `irot` and `imir` become the orientation.
- `YuvRaster.Load` gives the decoded planes. ICC, CICP, HDR metadata (clli, mdcv), Exif and XMP are read; `AvifDetails`
  describes the AV1 configuration, color, alpha, grid and brands, and `AvifFilmGrain` the grain parameters.
  `AvifReaderOptions.FilmGrain` false loads the image without its grain.
- Tiles and the in-loop filters run on up to `MaxParallelism` threads with the same output at every thread count; rows stream,
  and `ReaderOptions.Region` decodes only the grid cells it touches. On all cores Load is faster than libavif with dav1d on
  large photos; on one thread it is about 2 to 3.5 times slower, which a later release works on.
- Gain maps (tmap items) load into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr` renders the other rendition (from an SDR
  or an HDR base) within 2.9.1's tolerance of libavif's.
- `AvifDetails.Items` lists every item with its role (`AvifItem`, `AvifItemRole`), and `AvifImage.ReadItem` decodes thumbnails,
  depth maps and other auxiliary images on their own.
- Image sequences (avis) load every frame as an animation when all are key frames, else the first frame; layered images load
  their base layer. The rest of both waits for inter frames.

### JPEG XL

- `JxlFormat` reads JPEG XL (ISO/IEC 18181) in the box format or as a bare codestream, at Level 5 and 10, registered by
  default. Lossless Modular files (every predictor, the self-correcting one included, RCT, palette and delta palette,
  squeeze, 1 to 31-bit and float samples) decode bit for bit as djxl (libjxl 0.12.0) decodes them; lossy VarDCT files
  (every transform from DCT2x2 to DCT256x256, AFV, Gaborish, the edge-preserving filter, LF frames, upsampling, patches,
  splines and noise) within one 8-bit level of djxl, and every 18181-3 conformance file within its thresholds.
- Lossy color comes out in the file's own color space: enumerated spaces as djxl gives them, ICC profiles through Egami's
  color pipeline (where djxl gives linear sRGB), PQ and HLG kept with intensity_target as `HdrMetadata.MaxContentLightLevel`;
  the space is recorded on the raster. `JxlReaderOptions.LinearOutput` gives linear light in float instead. Pixel types come
  from the declared bit depth.
- Animations load through `Raster.LoadAll` as their blended frames with delays and loop count; `JxlImage.ReadLayers` gives
  the frames before blending (`JxlLayer`, `JxlLayerBlend`), `JxlImage.ReadPreview` the preview, and
  `JxlReaderOptions.MaxDownscale` 8 a lossy still's LF image alone (1:8, much faster).
- Alpha loads straight, CMYK as `Cmyk8` or `Cmyk16`, spot colors are mixed in (`JxlReaderOptions.RenderSpotColors` turns
  that off); `JxlDetails.ExtraChannels` lists every channel (`JxlExtraChannel`, `JxlExtraChannelKind`) and
  `JxlImage.ReadExtraChannel` reads one alone.
- Recompressed JPEGs load as pixels, and `JxlImage.ReconstructJpeg` gives the original JPEG back byte for byte from the jbrd
  box (`JxlDetails.HasJpegReconstruction`). A jhgm gain map loads into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr`
  renders HDR from it as for Ultra HDR JPEGs.
- Exif, XMP and orientation are read, Brotli-compressed (brob) boxes included; JUMBF boxes are kept in `JxlDetails.Jumbf`.
  Damaged metadata or gain map boxes are dropped with a warning (`Jxl.BoxDropped`, `Jxl.BrobDropped`, `Jxl.ExifDropped`,
  `Jxl.GainMapDropped`).
- Groups decode on up to `MaxParallelism` threads with the same output at every thread count; `ReaderOptions.Region` on a
  still decodes only the groups it touches (`Jxl.RegionGroups` at Debug level counts them).
- Speed against djxl, threads matched: lossless within 1.5 times; lossy 1.4 to 2.3 times on one thread and 1.9 to 3 times on
  all cores; recompressed JPEGs further behind.
- `Raster.Save` writes lossless JPEG XL (`JxlWriterOptions`): every sample back exactly in the raster's own precision (8 or
  16-bit, half or single float, gray or RGB, alpha unless opaque everywhere), at `Effort` 1 to 7 on cjxl's scale, with files
  within 0.3% of cjxl's (libjxl 0.12.0) at the same effort and djxl decoding every one bit for bit. Effort 1 is libjxl's fast
  lossless path, 2 and 3 its fixed trees, 4 and up learn the context tree, 5 and up try local palettes and color transforms.
  The color space goes in enumerated when it is sRGB, linear, PQ or HLG, else as the ICC profile; EXIF and XMP go in boxes,
  the orientation in the codestream too. Encoding runs on up to `MaxParallelism` threads with the same file at every count,
  within 1.75 times cjxl's time on one thread and about as fast as cjxl on all cores.
- `Raster.Save` writes lossy JPEG XL too: VarDCT in XYB as libjxl 0.12.0's encoder makes it at efforts 1 to 7, with files
  within 5% of cjxl's size and Butteraugli score at the same settings and decoding in djxl. `JxlWriterOptions.Lossless` picks
  the kind; by default a JPEG XL source keeps its own and anything else is written lossy, as cjxl does for PNG. `Quality` is
  cjxl's `-q` (90, distance 1.0, by default) and `Distance` its `-d` (0.1 to 25); from distance 10 the image is coded at half
  size, as cjxl codes it. Effort 5 and up add adaptive quantization, chroma from luma and Gaborish, 6 the full block-size
  search and per-block edge-preserving filter strength, 7 patches for text and repeated shapes.
- Lossy files keep alpha exact (`AlphaDistance` codes it lossy through squeeze) and the color under transparent pixels
  (`KeepTransparentColors` false lets the encoder replace it, as libjxl does by default); CMYK keeps its black channel. sRGB,
  Display P3, Rec. 2020, PQ and HLG go in as enumerated spaces (HDR with its intensity target), other profiles as ICC, and a
  gain map as a jhgm box. `Progressive` writes cjxl `-p`'s LF frame and three HF passes, `PhotonNoiseIso` its film-like noise.
- Large lossy images are coded in 2048-pixel chunks as cjxl codes them, so a 16,384 × 16,384 image encodes in about 2.5 GB.
  Encoding runs on up to `MaxParallelism` threads with the same file at every count, within 1.9 times cjxl's time on all
  cores and 2.5 times on one thread.
- `JxlImage.RecompressJpeg` recompresses a JPEG into JPEG XL without loss, as cjxl does: the JPEG's coefficients become a
  VarDCT frame with chroma from luma, and a jbrd box keeps what rebuilding the file needs, so `ReconstructJpeg` (and djxl)
  give the original back byte for byte. Files come out 0.82 of the JPEGs' size, within 0.1% of cjxl's (libjxl 0.12.0), and
  decode in djxl to cjxl's pixels exactly. ICC goes in the codestream, Exif and XMP in Brotli-compressed boxes, the Exif
  orientation in the header. Arithmetic-coded, lossless, hierarchical, 12-bit and CMYK JPEGs, 4:1:1 sampling and two DRI
  markers are refused (`UnsupportedFormatException`), as cjxl refuses them. Each file is checked by rebuilding the JPEG from it;
  the few damaged JPEGs whose data does not re-encode exactly (which cjxl recompresses into files djxl cannot rebuild) are
  refused too.
- Fixed: `ReconstructJpeg` failed on scans holding some of the components (such as Cb and Cr together), and rebuilt JPEGs
  whose components all have the same sampling other than 1×1 (such as 1×2) wrongly; those files' pixels also lacked chroma
  from luma. It also wrote padding bits that are not all ones in the wrong order, and named quantization tables by list
  position where the JPEG used other slots.

### Raw CCITT fax data

- `CcittFax.Decode` decodes Group 3 and Group 4 fax data (ITU-T T.4 and T.6) as PDF's CCITTFaxDecode filter does, taking all
  eight of its parameters in `CcittFaxParameters`: K, EndOfLine, EncodedByteAlign (byte-aligned Group 4 included, which a TIFF
  wrapper cannot say), Columns, Rows, EndOfBlock, BlackIs1 and DamagedRowsBeforeError. It returns packed rows in PDF's bit sense.
  Every CCITT stream of a 4,000-file PDF corpus and of a generated matrix of every parameter combination decodes bit for bit as
  PDFium and pdf.js decode it, about six times faster than PDFium and two and a half times faster than PdfPig.
- `CcittFormat` loads the same data as a `Gray8` image with its parameters in `CcittReaderOptions`, with Probe, the row reader,
  regions and reductions. It has no signature, so detection never picks it.
- A damaged row ends decoding: the rows before it are kept, the rest are white, and `Ccitt.Damaged` warns (strict loading
  throws). With EOLs, up to DamagedRowsBeforeError bad rows are skipped first. TIFF and JBIG2's MMR regions use the same decoder.

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

### VP8 decode speed

No API changes. Every frame decodes as before: equal to libvpx 1.14.1's on its 59 test vectors and the VP8 clips, and lossy WebP
equal to libwebp's.

- One-thread VP8 video decodes about 8% faster on the 1080p speed-gate clips and 20% faster on Egami.Video's film-grain clips.
  Against libvpx 1.14.1 (its SIMD build, one thread) it now takes 1.28–1.40 times libvpx's time on the 720p and 1080p gate clips
  and 1.43 times on the grain clips, down from 1.32–1.50 and 1.53–1.67. All-core decoding is no slower. Intra prediction fills
  whole rows with vector stores; one-thread decoding reconstructs a macroblock row before filtering it, as libvpx does, so
  inter macroblocks no longer keep copies of their edges; residuals are added two blocks at a time in 16-bit lanes; each
  coefficient's sign is decoded without a branch.
- One-thread video decoding allocates nothing per frame after the first key frame: the frame decoder and its arrays are kept
  from frame to frame (they took 85 KB a frame at 720p and 183 KB at 1080p).
- Lossy WebP decodes 9–18% faster from the same changes (a 2048 × 1536 photo in 25 ms instead of 29 ms on one thread).

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
  `MaxParallelism` threads, with the same bytes. libtiff,
  ImageMagick and Pillow read them all. JPEG compression takes CMYK too, written as libtiff writes separated JPEG.
- JPEG XL strips and tiles (compression 50002, and DNG 1.7's 52546 for previews and other pages that are not camera RAW) are
  read through Egami's JPEG XL decoder: chunky gray, RGB and YCbCr pages of 8 or 16-bit or float samples, lossless or lossy,
  each unit as the same codestream decodes alone (`TiffCompression.JpegXl`, `TiffCompression.DngJpegXl`, read only).

### WebP

- `WebpFormat` reads and writes WebP: lossless (VP8L), lossy (VP8) with or without alpha, and animated, with no native code.
  Lossless files decode to libwebp's pixels exactly; lossy files to libwebp's YUV planes sample for sample and to RGB within one
  level (`YuvRaster.Load` returns the planes); animations compose every frame as libwebp's animation decoder does. Every kind
  streams through `RasterDecoder`, incrementally too, and an animation holds one canvas whatever its frame count.
- `WebpWriterOptions`: `Lossless` (by default a WebP source keeps its kind and anything else is lossy), `Quality` and `Effort` with
  cwebp's meanings, `AlphaQuality`, `KeepTransparentColors`, `SharpYuv`, `NearLossless`, `MixedFrames` and `FirstFrameOnly`.
  Animations store changed rectangles; a GIF or APNG converts with Load and Save.
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
  with alpha exact. Every mode is within a level of SkiaSharp (two for Difference and Exclusion, where Skia rounds).
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

### Regions and masks

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

### Keying and arithmetic

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
  The average is bit-identical to a full decode and a box resize.
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
  `YCbCrColor` and `CmykColor`, all `IColorValue<TSelf>`; `ColorConvert` converts single values and whole rows. `YuvMatrix` and `YuvRange` name the Y'CbCr coefficients.
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

### PFM and more direct pixel types

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

### JPEG writing

- `JpegWriterOptions.Progressive` writes progressive files with libjpeg's standard scan script (ten scans for color, six for
  gray) and Huffman tables optimized per scan. They decode to exactly the baseline file's pixels and are the same bytes as
  libjpeg-turbo's `cjpeg -progressive` at the same quality and sampling. The quantized coefficients are held until the last
  scan, about 2 bytes per sample, within `SafetyLimits.MaxTotalBytes`; a streamed encode buffers. Off by default, and a
  progressive source is re-saved as baseline unless it is set.
- `JpegColorType.Cmyk` and `JpegColorType.Ycck` write four components behind an Adobe marker, inverted as Photoshop writes CMYK;
  `Auto` picks CMYK for `Cmyk8` and `Cmyk16` pixels, which 1.x converted to RGB on save. YCCK subsamples its chroma and keeps
  black at full size. djpeg, ImageMagick and Pillow read every file to within a level of Egami's inks. 12 MP encodes in 95 ms as CMYK
  and 91 ms as YCCK at one thread.
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

## Egami.Drawing 1.0.0 (2026-10-18)

The first public release. The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it.

### Canvas, paths and shapes

Over Egami's public API only, it draws antialiased paths, shapes, strokes, clips and images on a canvas. See the
[Drawing guide](docs/articles/guides/drawing.md). Text comes with Egami.Fonts, below.

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
- Speed (ms, 1 thread): 1,000 small shapes 4.0, a 4000×4000 star 19.8 (4.8 on 24 cores), 10,000 icons 109, 200 thick curves
  40, 10,000 hairlines 48.

### Path geometry

Path geometry. See [Path geometry](docs/articles/guides/drawing.md#path-geometry).

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
- Boolean speed (ms, 1 thread, best of 7): 100 random 12-gon pairs under four operations 28 against SkiaSharp's 39;
  50 curved pairs 12.5 against 4.7; 200 letters unioned 24 against 503.

### Paints and layers

Paints and layers. See [Gradients](docs/articles/guides/drawing.md#gradients),
[Layers](docs/articles/guides/drawing.md#layers) and [Blurs and shadows](docs/articles/guides/drawing.md#blurs-and-shadows).

- Gradients: `LinearGradient`, `RadialGradient`, `ConicalGradient` (two circles, as the HTML canvas, Skia and PDF) and
  `SweepGradient`, with `GradientStop`s, `TileMode` (`Clamp`, `Repeat`, `Mirror`, `Decal`), PDF's one-sided extend
  (`ExtendStart`, `ExtendEnd`), mixing in encoded sRGB, linear sRGB, Oklab or Oklch with CSS Color 4's hue methods
  (`GradientInterpolation`), premultiplied by default, and ordered `Dither` (off by default). Every paint has a `LocalMatrix`.
  Within a mean level of SkiaSharp's gradients on every kind and tile mode.
- `ImagePaint`: an image as a paint, tiled per axis, sampled with Egami's filters and from a `MipChain` level when shrunk; a
  whole-pixel shift copies rows. Within a mean level of SkiaSharp's image shaders.
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
- Speed (ms, 2048 × 2048, 1 thread and 24 cores): a full-frame linear gradient 26 and 3.8;
  radial 22 and 2.9; a tiled image brush 5.6 and 0.8. A full-frame half-opacity
  layer costs 1.38 times the drawing inside it at 1 thread and 1.14 at 24.

### Advanced drawing

Advanced drawing. See [Nine-patches and lattices](docs/articles/guides/drawing.md#nine-patches-and-lattices),
[Meshes and patches](docs/articles/guides/drawing.md#meshes-and-patches), [Recorded drawings](docs/articles/guides/drawing.md#recorded-drawings)
and [the .egdr format](docs/articles/guides/egdr-format.md).

- `DrawImageNinePatch` and `DrawImageLattice` with `Lattice` (x and y divisions, bounds, `LatticeCellKind` and cell colors), as
  Skia's; fixed spans shrink in proportion when the destination is small. The raster backend draws the cells as one mesh, so they
  meet without seams, each sampled inside its own part of the image. `NinePatch.FromAndroid` reads .9.png markers into the
  image, its lattice and its content padding. Within a mean level of SkiaSharp's drawImageNine and drawImageLattice.
- `DrawAtlas` with `SpriteTransform` (Skia's RSXform) and optional colors combined by a blend mode; each sprite blends on its own.
  Within a mean level of SkiaSharp's drawAtlas. 10,000 16-pixel sprites draw in 23 ms.
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

### Text quality, text on a path and color glyphs

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
  a page of 500 COLR v0 emoji in 2.7 cold and 1.6 warm, and of COLR v1 emoji in 15 cold and
  2.1 warm; plain text is within 10% of 2.7.1 (11.0 against 10.7 at 12 pixels).
- Fixed: a curve under a transform so large that rounding split it again and again overflowed the stack.

### Shaped text

- `Canvas.DrawText` takes `ShapingOptions` and draws shaped text (with fallback fonts when the options list them);
  `Canvas.DrawShapedText` draws a `ShapedText` shaped once. Latin text draws as before, at the same speed.

### Paragraphs

- `Canvas.DrawTextLayout` draws a laid-out paragraph in CSS's order (shadows, backgrounds, underlines, glyphs and placeholders,
  line-throughs), skipping lines outside the clip; an ellipsis or a soft hyphen's hyphen is marked content with ActualText.
  `Canvas.DrawText` into a `RectF` with `VerticalAlign` lays out and draws in one call, and `TextLayout.ToPath(origin)` gives a
  `Path`. Drawn paragraphs are within 2 levels of the exact coverage on over 99% of pixels, closer than SkiaSharp, and drawing
  costs at most 10% over drawing the glyph runs alone.

### Document backends

- `LayerOptions.Knockout` for PDF's knockout groups and `DrawImageMask` (hook `OnDrawImageMask`) for its stencil masks, drawn
  exactly by the raster backend; pictures and .egdr keep them (for Egap's PDF renderer).
- `Canvas.RasterizeFallback` scales a paint's mask blur with the raster, so a blur drawn through a PDF page's fallback at
  300 dpi keeps its size; layer filter and backdrop chains run on the raster's pixels as they are (found by Egap's PDF canvas).

### Printing with inks

- `InkCanvas` draws on an `InkRaster` with every `Canvas` call (`InkCanvasOptions`: what RGB means, intent, black point
  compensation): RGB colours, gradients and images convert into the process inks; ink colours print their plates.
  `Clear(InkColor)` and `DrawImage(InkRaster)` are its own. Blend modes, groups (isolated, non-isolated, knockout) and soft
  masks follow ISO 32000-2 plate by plate; every plate is within 2 levels of Ghostscript 10.05's tiffsep on the gate scenes.
- Ink paints: `SolidPaint(InkColor)`, `GradientStop(offset, InkColor)`, `Mesh.WithInks` and `Patch.WithInks`. Every other
  backend draws their appearance.
- `Paint.Overprint` (`Off`, `On`, `NonZero`, PDF's overprint and overprint mode 1): plates a fill does not paint keep what is
  under them. RGB canvases ignore it, as PDF viewers do by default.
- `LayerOptions.ColorSpace`: a layer blends in an RGB or gray space and converts into the inks as it merges; spots stay plates.
- Pictures keep ink paints, overprint and layer colour spaces; .egdr 1.1 holds them in an INKS chunk, which 1.0 readers skip
  to draw the appearances.

### Changes from the previews

Renamed or changed in the 1.0 API review, for code written against the previews:

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
- Fixed: a non-isolated layer merged with a blend mode other than source-over blended the backdrop it started from with itself;
  the backdrop's part is now removed first, as PDF's non-isolated groups require (ISO 32000 11.4.8). Found by Egap's renderer.

## Egami.Fonts 1.0.0 (2026-10-18)

The first public release. The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it. Glyph ids stay
`ushort` for all of 1.x.

### Fonts and text on the canvas

A new package, over Egami's public API only, that reads TrueType and OpenType fonts in managed code; Egami.Drawing uses it to draw
text. See the [Fonts and text guide](docs/articles/guides/text.md).

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
- Speed: nine fonts load in 0.18 ms, 1,000 strings match and measure in 3.3 ms, and a 2,000-word page draws in 15 ms at 12
  pixels.
- Fuzz targets `font`, `cff` and `subset`.

### Web, variable and color fonts

Web, variable and color fonts. See [Variable fonts](docs/articles/guides/text.md#variable-fonts),
[Color fonts](docs/articles/guides/text.md#color-fonts) and [Faces](docs/articles/guides/text.md#faces).

- WOFF and WOFF2 load wherever TrueType and OpenType do (`Typeface.FromFile`, `FromMemory`, `FontLoader`, `FontCollection`
  directory scans): WOFF with the BCL's zlib, WOFF2 with its Brotli decoder and the glyf, loca and hmtx transforms, WOFF2
  collections too. `Typeface.Container` says which container a face came in; `GetWoffMetadata` and `GetWoffPrivateData` return the
  extra blocks. Every decoded table equals fontTools' and the reference woff2 decoder's. Four WOFF2 files load in 15 to 21 ms.
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

### Text shaping

OpenType shaping in managed code, a translation of HarfBuzz 14.5.1's. See
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
  leaves out) and 24 corpus runs in 15 scripts; the closure equals HarfBuzz's on 1,116 cases. Shaping English, Arabic and
  Hindi pages takes 3.7, 7.5 and 13.4 ms, about half HarfBuzzSharp's speed.
- Fuzz targets `shaping` and `bidi`.
- From the API review: a font with no glyph data (a subset made for shaping) loads with empty glyphs and a `Font.MissingTable`
  warning; `WithVariations` sets every axis of a repeated tag; the cmap subtable is chosen as HarfBuzz chooses it (a symbol
  subtable first, Windows 3.1 Arabic font pages through their private use characters), for drawing and shaping alike.

### Paragraph layout

Wrapped, aligned and interactive text in `Egami.Fonts.Layout`. See
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
  Chromium's own, listed in the tests). Breaking 1 MB of mixed text takes 17 ms;
  a 2,000-word page at three widths 7.8 ms; 10,000 hit tests 19 ms.
- Fuzz target `paragraph`, and a robustness suite on the same harness.

### Font data for PDF

What a PDF font needs to reach its glyphs, public before Egami.Fonts 1.0. See [Font data for PDF](docs/articles/guides/text.md#font-data-for-pdf).

- `Typeface.GetGlyphName` and `TryGetGlyphByName`: names from a name-keyed CFF font's charset (the Expert and ExpertSubset
  charsets included) or the post table, versions 1, 2 and 2.5.
- `Typeface.CidSystemInfo` (`CidSystemInfo`), `GetCid` and `TryGetGlyphByCid` for CID-keyed CFF, bare or in OpenType.
- `Typeface.CharacterMaps` and `FindCharacterMap`: every cmap subtable as stored (`CharacterMap`, `CharacterMapping`), with
  format 2 newly read; `Typeface.BuiltInEncoding`, a CFF font's own encoding as a platform 7 map, as FreeType gives it.
- `CffStandardStrings`: the 391 CFF standard strings and the Standard and Expert encodings.
- On the 1,723 font programs embedded in Egap's PDF corpus, names, CIDs, subtables and built-in encodings equal FreeType's and
  fontTools' (FreeType's where they differ: the first of overlapping format 4 segments wins, and a code past the font's glyphs
  maps to 0). The Expert Encoding gives code 175 Macronsmall, as pdf.js does, where FreeType repeats Dotaccentsmall.
- Fixes: a CID-keyed CFF font's Font DICT FontMatrix now applies before the Top DICT's, so a font whose Font DICTs scale by
  1/2048 (pdf.js's text_clip_cff_cid.pdf) draws its glyphs at their size rather than about twice it; outlines and advances
  equal FreeType's on every CID-keyed CFF in the corpus. A CFF DICT with a reserved operator (pdf.js's bug1308536.pdf) loads,
  the operator skipped as FreeType, PDFium and pdf.js skip it. A cmap format 6 subtable whose count runs past the table keeps
  the entries there are.
- A `fontdata` fuzz target reads every name, CID, subtable and encoding of what loads; the font targets do too.

### Changes from the previews

Renamed or changed in the 1.0 API review, for code written against the previews:

- Fixed: shaping a run longer than 65,536 characters threw on a thread that had shaped a short run before, because the shaper
  kept the short run's limits. Each run now gets limits from its own length.
- Breaking: `FontCollection.System` is read-only; `Add`, `AddFile` and `AddDirectory` on it throw `InvalidOperationException`.
  To match your own fonts beside the installed ones, call the new `FontCollection.AddSystemFonts` on a collection of your own:
  it copies the installed-font index, and the faces load with that collection's options.
- Breaking: `IndicSyllabicCategory`, `IndicPositionalCategory` and `UnicodeData.GetIndicSyllabicCategory` and
  `GetIndicPositionalCategory` are internal. The shaper still uses them; they were shaping data rather than a public feature.
- `default(Font)` has no typeface: its metrics, advances, `Shape` and `MeasureText` now throw `InvalidOperationException`
  instead of a `NullReferenceException` or `ArgumentNullException` from deeper in.
- `Font.MeasureText(ReadOnlySpan<char>, ShapingOptions?)` measures a slice of a longer text or a reused buffer without making a
  string of it. Measuring a string allocates about 8% less than before.
- `IGlyphOutlineSink`, `IColorPaintVisitor`, `IFontFallbackResolver` and `ITextPaint` are frozen as they are; a member added
  in 1.x comes as a default interface method, so your implementations keep compiling.
- `FontCollection.Families` and `Count` keep the grouped families until the next `Add` or `Refresh`, instead of grouping every
  face on each call.

## Egami.Svg 1.0.0 (2026-10-18)

The first public release. The API is frozen: 1.x versions add to it without breaking your code. See [SVG](docs/articles/guides/svg.md).

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

## Egami.Css 1.0.0 (2026-10-18)

The first public release. The API is frozen: 1.x versions add to it without breaking your code. See [CSS](docs/articles/guides/css.md).

- Egami.Css parses CSS Syntax 3, matches Selectors 4 over any `ICssElement` tree, cascades with specificity, `!important`,
  custom properties and `var()`, evaluates media queries, and parses colors, lengths, transforms and fonts.

### Pages, boxes and the document properties

- `CssPropertyRegistry.CreateDefault` knows the box properties (`margin`, `padding`, `border` with its sides and
  `border-radius`, `width` and `height` with their `min-` and `max-` forms, `box-sizing`), backgrounds (the `background-*`
  layers and the `background` shorthand), `list-style`, the table properties, `float`, `clear`, `overflow`, the
  fragmentation properties (`break-*`, `orphans`, `widows`, `page` and the legacy `page-break-*` as shorthands) and
  `content` with the counters, each with its value grammar, so invalid declarations are dropped as browsers drop them.
- `@page` is interpreted: `CssPageRule` with `CssPageSelector`s (page type names and `:first`, `:left`, `:right`,
  `:blank`, with their specificity) and `CssPageMarginRule`s for the sixteen `CssMarginBox`es. `CssCascade.CascadePage`
  cascades them for a `CssPage` into a `CssPageStyle`, and `CssPageSize` reads `size`, page size names included.
- `overflow` is now a shorthand of `overflow-x` and `overflow-y`; Egami.Svg reads the two longhands and draws as before.

### Changes from the previews

- `CssAttribute`, `CssPage`, `CssRange` and `CssTransformOrigin` declare their `ToString`, so it is annotated as returning a
  non-null string. The text is the same as before.
- `ICssElement` is frozen as it is; a member added in 1.x comes as a default interface method, as `MatchesPseudoClass` already
  is, so your element trees keep compiling.

## Egami.Metafiles 1.0.0 (2026-10-18)

The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it. It is 0.1.1's API plus one overload; nothing
was removed or renamed.

- `Metafile.Load(ReadOnlyMemory<byte>)` reads a picture already in memory, such as one taken out of a Word or PowerPoint
  file, without wrapping it in a stream.

## Egami.Metafiles 0.1.1 (2026-10-18)

The first release; its API may still change while it is 0.x.

- `Metafile` reads WMF (placeable or not), EMF and EMF+ files and their gzip forms WMZ and EMZ, and plays them onto any
  Egami.Drawing canvas, so pictures from Word, Excel and Visio stay vectors in PDF and SVG output and rasterize on any OS.
  `Draw` stretches the picture's frame to a box as Word does; `Rasterize` draws at a DPI or a size. Kind, size, frame,
  DPI and description come from the header, matching GDI+ and Pillow on 2,106 of 2,111 corpus files (the other five are WMFs
  without a placeable header, sized by their window extent).
- The GDI records play as GDI does: map modes, world transform, pens, brushes, hatches and pattern brushes, paths, clip
  regions, bitmaps and raster operations, gradients and text. Drawn aliased, the test files match Windows GDI within a mean of
  one level and 99% of pixels within 16 levels; text within a looser bound. Text keeps the file's glyph positions in a
  substitute face (Carlito for Calibri, Liberation for Arial, Times New Roman and Courier New, and your own substitutions).
- EMF+ records play as GDI+ plays them: transforms and containers, clips, gradients (path gradients as meshes), pens with
  dashes, compound lines and caps, images (JPEG and PNG passed to PDF unchanged, nested metafiles as vectors), image
  effects, `DrawString` and `DrawDriverString`; dual files draw their EMF+ records, or their GDI part with
  `MetafilePlayback.Gdi`.
- `MetafileFormat` lets `Raster.Load` and `Raster.Probe` read metafiles (opt-in, as SVG). Drawing the 2,111-file corpus
  takes 0.74 times what GDI+ takes.
