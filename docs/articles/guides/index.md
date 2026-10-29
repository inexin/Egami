# Guides

Each guide covers one area of Egami: what it does, the options and their defaults, and the limits. Many guides end with
**Recipes**, short answers to common tasks such as making a favicon or blurring a face, with code you can copy. Every code sample
compiles and runs in Egami's test run.

The guides are grouped by package, and within Egami the groups go from the basics to the specialized. Within a group, pages are
in alphabetical order. Each file format has its own
page in [Formats](../formats/index.md). If you are new, read [Getting started](../getting-started.md) first.

## Egami: basics

What every user meets: pixel types, metadata, and how Egami uses memory and handles untrusted files.

- [Diagnostics](../diagnostics.md): warnings about damaged or unusual files, and how to log or reject them.
- [Memory and threading](memory-and-threading.md): pooled memory, streaming decode and parallel work.
- [Metadata](metadata.md): EXIF, IPTC and XMP, kept, edited or stripped.
- [Performance](../performance.md): what keeps loading, editing and saving fast.
- [Pixel types](pixel-types.md): how pixels are stored, and which type to pick.
- [Security](security.md): the limits that make untrusted files safe to load.

## Formats

The [overview](../formats/index.md) says what each format reads and writes and how a format is detected; then one page per
format, with its options:

- [AVIF](../formats/avif.md)
- [BMP](../formats/bmp.md)
- [CCITT fax](../formats/ccitt.md)
- [GIF](../formats/gif.md)
- [HEIC](../formats/heic.md) (in the Egami.Heic package)
- [ICO and CUR](../formats/ico.md)
- [JBIG2](../formats/jbig2.md)
- [JPEG](../formats/jpeg.md)
- [JPEG 2000](../formats/jpeg2000.md)
- [JPEG XL](../formats/jxl.md)
- [Netpbm](../formats/pnm.md)
- [OpenEXR](../formats/openexr.md)
- [PNG and APNG](../formats/png.md)
- [PSD and PSB](../formats/psd.md)
- [QOI](../formats/qoi.md)
- [Radiance](../formats/radiance.md)
- [TGA](../formats/tga.md)
- [TIFF](../formats/tiff.md)
- [VP8 video frames](../formats/vp8.md)
- [WebP](../formats/webp.md)
- [Your own format](../formats/custom.md): plug in a reader and writer for a format Egami does not have

## Egami: editing images

Operations that run on a raster through `Apply` and `Derive`.

- [Color](color.md): brightness, contrast, saturation and other CSS-style color filters.
- [Compositing](compositing.md): blend modes and drawing one image over another.
- [Edges and morphology](edges-and-morphology.md): edge detection, Canny, and dilate, erode, open and close.
- [Effects and shadows](effects.md): vignette, glow, pixelate, oil paint and shadows.
- [Filters](filters.md): blurs, sharpening, convolution, noise and median.
- [Keying and arithmetic](keying-and-arithmetic.md): transparent colors, flood fill and arithmetic between images.
- [Lighting and relief](lighting-and-relief.md): bevels, embossing and lighting from a height map.
- [Regions, masks and channels](regions-and-masks.md): edit part of an image, and move channels around.
- [Resizing and geometry](resizing-and-geometry.md): resize, crop, rotate, warp and combine images.
- [Tone and color](tone-and-color.md): levels, curves, grading, auto fixes and thresholds.

## Egami: color and HDR

- [Color management](color-management.md): ICC profiles, converting between color spaces, CMYK.
- [HDR images](hdr.md): HDR pixels, gain maps and Ultra HDR photos.
- [Planar YUV](planar-yuv.md): video-style YUV planes in and out of JPEG, AVIF and video frames.

## Egami: analysis, palettes and animation

- [Animation](animation.md): frames, timing and animated GIF, APNG and WebP.
- [Comparing and measuring](analysis.md): differences, statistics, hashes and visual regression tests.
- [Palettes and dithering](palettes.md): reducing colors and indexed images.
- [Text recognition (OCR)](ocr.md): one shape for every OCR engine, any OCR program as an engine, and hOCR and ALTO files.

## Egami: extending

- [Custom operations and formats](extending.md): write your own operation or pixel type, or wrap your own memory.

## Egami.Drawing

- [Drawing](drawing.md): paths, shapes, strokes, paints, layers and recorded drawings on a canvas.
- [Drawing files (.egdr)](egdr-format.md): the file format for recorded drawings.

## Egami.Fonts

- [Fonts and text](text.md): loading fonts, drawing text and preparing fonts for PDF.
- [Paragraph layout](paragraph-layout.md): line breaking, justification, columns and vertical text.
- [Unicode text and shaping](unicode-text.md): complex scripts, bidirectional text and emoji.
- [Matching System.Drawing text](system-drawing-text.md): drawing UI text as GDI+ and GDI do on Windows.

## Egami.Svg and Egami.Css

- [CSS](css.md): parse style sheets, match selectors and run the cascade.
- [SVG](svg.md): read, edit, save and draw SVG.

## Egami.Metafiles

- [Windows metafiles](metafiles.md): play WMF, EMF and EMF+ pictures onto a canvas.

## Egami.SystemDrawing

- [Migrating from System.Drawing to Egami](system-drawing-migration.md): move drop-in code to Egami's own API one piece at a time.
- [System.Drawing drop-in](system-drawing.md): System.Drawing's API on every OS: bitmaps, codecs, Graphics, fonts and text.

## Egami.Video

- [Video](video.md): probe, read, write and remux video, with Egami rasters as frames.
- [Streaming MP4](streaming-mp4.md): fragmented MP4 for forward-only streams, and HLS and DASH segments.
- [Audio](audio.md): decode, encode, resample and mix audio.
- [Encoding Opus](opus-encoding.md): Opus files, settings and WebM audio.
- [Subtitles](subtitles.md): read, edit, retime and convert SRT, WebVTT and ASS subtitle files.

## Egami.Barcodes

- [Barcodes](barcodes.md): write 1D and 2D barcodes.
- [Reading barcodes](barcode-reading.md): find and decode barcodes in images.
