# Guides

Each guide covers one area of Egami: what it does, the options and their defaults, and the limits. Many guides end with
**Recipes**, short answers to common tasks such as making a favicon or blurring a face, with code you can copy. Every code sample
compiles and runs in Egami's test run.

The guides are grouped by package, and within Egami they go from the basics to the specialized. If you are new, read
[Getting started](../getting-started.md) first.

## Egami: basics

What every user meets: pixel types, the file formats, metadata, and how Egami uses memory and handles untrusted files.

- [Pixel types](pixel-types.md): how pixels are stored, and which type to pick.
- [Formats](formats.md): what each format reads and writes, and how to choose one.
- [Metadata](metadata.md): EXIF, IPTC and XMP, kept, edited or stripped.
- [Memory and threading](memory-and-threading.md): pooled memory, streaming decode and parallel work.
- [Security](security.md): the limits that make untrusted files safe to load.
- [Diagnostics](../diagnostics.md): warnings about damaged or unusual files, and how to log or reject them.
- [Performance](../performance.md): what keeps loading, editing and saving fast.

## Egami: editing images

Operations that run on a raster through `Apply` and `Derive`.

- [Resizing and geometry](resizing-and-geometry.md): resize, crop, rotate, warp and combine images.
- [Color](color.md): brightness, contrast, saturation and other CSS-style color filters.
- [Tone and color](tone-and-color.md): levels, curves, grading, auto fixes and thresholds.
- [Filters](filters.md): blurs, sharpening, convolution, noise and median.
- [Edges and morphology](edges-and-morphology.md): edge detection, Canny, and dilate, erode, open and close.
- [Lighting and relief](lighting-and-relief.md): bevels, embossing and lighting from a height map.
- [Effects and shadows](effects.md): vignette, glow, pixelate, oil paint and shadows.
- [Compositing](compositing.md): blend modes and drawing one image over another.
- [Regions, masks and channels](regions-and-masks.md): edit part of an image, and move channels around.
- [Keying and arithmetic](keying-and-arithmetic.md): transparent colors, flood fill and arithmetic between images.

## Egami: color and HDR

- [Color management](color-management.md): ICC profiles, converting between color spaces, CMYK.
- [HDR files](hdr.md): Radiance, OpenEXR, gain maps and tone mapping.
- [Planar YUV](planar-yuv.md): video-style YUV planes in and out of JPEG, AVIF and video frames.

## Egami: analysis, palettes and animation

- [Comparing and measuring](analysis.md): differences, statistics, hashes and visual regression tests.
- [Palettes and dithering](palettes.md): reducing colors and indexed images.
- [Animation](animation.md): frames, timing and animated GIF, APNG and WebP.

## Egami: file formats

One guide per format, for options beyond `Load` and `Save`. [Formats](formats.md) gives the overview.

- [JPEG](jpeg.md)
- [WebP](webp.md)
- [TIFF](tiff.md)
- [JPEG XL](jxl.md)
- [AVIF](avif.md)
- [HEIC](heic.md) (in the Egami.Heic package)
- [JPEG 2000](jpeg2000.md)
- [JBIG2](jbig2.md)
- [CCITT fax](ccitt.md)
- [ICO and CUR](ico.md)
- [PSD and PSB](psd.md)
- [VP8 video frames](vp8.md)

## Egami: extending

- [Custom operations and formats](extending.md): write your own operation or plug in a format.

## Egami.Drawing

- [Drawing](drawing.md): paths, shapes, strokes, paints, layers and recorded drawings on a canvas.
- [The .egdr format](egdr-format.md): the file format for recorded drawings.

## Egami.Fonts

- [Fonts and text](text.md): loading fonts, drawing text and preparing fonts for PDF.
- [Unicode text and shaping](unicode-text.md): complex scripts, bidirectional text and emoji.
- [Paragraph layout](paragraph-layout.md): line breaking, justification, columns and vertical text.

## Egami.Svg and Egami.Css

- [SVG](svg.md): read, edit, save and draw SVG.
- [CSS](css.md): parse style sheets, match selectors and run the cascade.

## Egami.Metafiles

- [Windows metafiles](metafiles.md): play WMF, EMF and EMF+ pictures onto a canvas.

## Egami.Barcodes

- [Barcodes](barcodes.md): write 1D and 2D barcodes.
- [Reading barcodes](barcode-reading.md): find and decode barcodes in images.
