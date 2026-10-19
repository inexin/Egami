# Cookbook

Task-sized recipes. Each one compiles and runs in Egami's test run.

## Thumbnails for the web

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#thumbnail)]

The JPEG decodes at 1/2, 1/4 or 1/8 scale, `AutoOrient` turns only the small result, and the metadata is left out of the file.

## Safe handling of untrusted uploads

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#untrusted)]

See the [security guide](../guides/security.md) for every limit.

## Log Egami's warnings with ILogger

[!code-csharp[](../../../tests/Egami.DocSamples/Diagnostics.cs#logger)]

Set it once at startup; every call that passes no context then reports to the log. The [diagnostics](../diagnostics.md) page lists
every id.

## Log Egami's warnings with Serilog

[!code-csharp[](../../../tests/Egami.DocSamples/Diagnostics.cs#serilog)]

## Know which file a warning came from

[!code-csharp[](../../../tests/Egami.DocSamples/Diagnostics.cs#per-file)]

## Reject damaged uploads

[!code-csharp[](../../../tests/Egami.DocSamples/Diagnostics.cs#errors)]

A handler that throws turns every warning into an error; `RecoveryMode.Strict` does the same for damage alone, without a handler.

## Use from ASP.NET Core

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#upload)]

Egami has no ASP.NET Core dependency: it reads and writes any `Stream`. The async calls read the upload into a pooled buffer,
counted against `MaxTotalBytes`, so a slow client never holds a thread.

## Batch convert a folder

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#batch-convert)]

## Convert images to WebP for the web

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#webp-web)]

Quality and Effort mean what cwebp's `-q` and `-m` mean; lossless keeps every pixel. See the [WebP guide](../guides/webp.md) for
the other options.

## Turn an animated GIF into WebP

[!code-csharp[](../../../tests/Egami.DocSamples/Webp.cs#webp-animation)]

## Make an animated GIF from frames

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#gif-from-frames)]

## Show video frames

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#reuse)]

Decoder frames in NV12 or P010 copy into the planes first; see [planar YUV](../guides/planar-yuv.md#interleaved-layouts).

## Read and write JPEG planes

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#jpeg)]

## Read a CMYK JPEG's inks

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#cmyk-inks)]

## Save a CMYK JPEG for print

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-cmyk)]

## Save a progressive JPEG for the web

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-progressive)]

## Rotate a JPEG without losing quality

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-transform)]

## Make a favicon

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-save)]

`FaviconSizes` writes 16, 32 and 48; every browser and Windows read the file. See the [ICO guide](../guides/ico.md).

## Pick an icon size

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-load)]

## List an icon's sizes

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-sizes)]

## Flatten a PSD to PNG and read its profile

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-load)]

A file saved without Maximize Compatibility holds only a placeholder; `PsdDetails.HasRealComposite` says so. See the
[PSD guide](../guides/psd.md).

## List a PSD's layers

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-layers)]

## Load any upload as sRGB

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#convert-on-load)]

Display P3 phone photos, Adobe RGB camera files and CMYK print JPEGs all come out as sRGB, converted through their own
profiles while they decode; files with no color information are taken as sRGB and left alone.

## Show a CMYK print file on screen

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#convert-type)]

## Show an HDR image on an SDR screen

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#tone-map)]

## Keyword and rate a photo

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#xmp-live)]

Lightroom, Bridge and Windows read the keywords and rating from XMP. For news and stock workflows, set the same keywords in
IPTC too:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#iptc-values)]

## Keep the thumbnail upright

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#exif-thumbnail)]

## Draw into a premultiplied BGRA surface

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#premultiplied-surface)]

## Fill a 16-bit display framebuffer

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#packed-framebuffer)]

## Draw a rounded badge

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#badge)]

Drawing at twice the size through `Scale(2)` keeps every shape's coordinates as designed. See [Drawing](../guides/drawing.md).

## Draw chart axes

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#chart-axes)]

Hairlines on half-pixel coordinates fill exactly one column or row of pixels.

## Clip a photo to a circle

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#clipped-photo)]

## Cut a frame out of a shape

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#booleans)]

Boolean operations keep curves, so the frame exports to PDF or SVG exactly. See [Path geometry](../guides/drawing.md#path-geometry).

## Draw a dashed route

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#dashes)]

## Animate a line drawing itself

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#draw-itself)]

`PathEffect.Trim` keeps a fraction of the path's length; stepping it from 0 to 1 gives the frames.

## Draw an SVG icon

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#svg)]

## Render an SVG to PNG

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#svg-to-png)]

Egami.Svg draws as resvg does where SVG leaves a choice; see [SVG](../guides/svg.md).

## Recolor an SVG icon

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#recolor)]

## Draw icons from an SVG sprite sheet

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#sprite-sheet)]

A symbol's viewBox fits it into each cell; the same calls draw on a PDF page.

## Label an SVG with your own fonts

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#fonts)]

With system fonts off, the label looks the same on every machine.

## Flow SVG text into a shape

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#wrapped-text)]

## Render an EMF or WMF to PNG

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-to-png)]

Egami.Metafiles plays the records as Windows does (GDI for WMF and EMF, GDI+ for EMF+), in managed code on any OS.

## Draw a Word or Excel picture onto a page

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-draw)]

## Load metafiles with Raster.Load

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-format)]

## Metafiles that name fonts you do not have

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-fonts)]

Each glyph still goes where the file put it, so lines keep their length in a substitute face.

## Put a QR code on a label

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-label)]

On a raster, the QR code's modules snap to whole pixels, so its edges stay sharp at any size.

## Save a barcode as SVG

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-svg)]

`Barcode.ToPath` gives the whole symbol as one path, so any canvas can take it; Egami.Svg's `SvgCanvas` writes it as one SVG
`<path>`. Egami.Barcodes itself does not depend on Egami.Svg.

## Print a GS1 shipping label

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-shipping)]

At a module size in points, the barcode has the X dimension a scanner expects, on a PDF page or any other canvas.

## Paint a dusk sky

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#sky-gradient)]

Mixing in Oklch keeps the hues from turning gray between stops, and `Dither` hides the bands of a wide soft ramp. See
[Gradients](../guides/drawing.md#gradients).

## Draw a card with a shadow

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#card-shadow)]

## Fade a photo's edges

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#faded-edge)]

## Fade a group as one

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#group-opacity)]

See [Layers](../guides/drawing.md#layers).

## Stretch a button image to any size

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#stretchable-button)]

The corners keep their size and the edges and center stretch. An Android .9.png carries its own lattice: see
[Nine-patches and lattices](../guides/drawing.md#nine-patches-and-lattices).

## Draw a particle field

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#atlas)]

## Shade a gradient mesh

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#gradient-mesh)]

See [Meshes and patches](../guides/drawing.md#meshes-and-patches).

## Reuse a logo at any size

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#recording)]

The drawing is recorded once and replayed as vectors, so it is sharp at every size; `cacheAsImage` trades that for speed when
it is drawn many times at one size.

## Watermark with Multiply

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#watermark-multiply)]

A dark logo drawn with `Multiply` reads as ink on the photo: light areas of the logo leave the photo unchanged.

## Tint a photo

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#tint)]

## A basic tone panel

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#tone)]

Levels, an S-curve and a tint run as one pass over the pixels.

## A poster look, and how it reads with color blindness

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#effects)]

## Apply a .cube grade at 70%, and export curves as a .cube

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#cube)]

## Grade in any editor through a Hald image

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#hald)]

## Fix a dull scan

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#auto)]

## White-balance a shoot from one reference

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#measure)]

## Threshold a page for OCR

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#auto-threshold)]

## Bring out detail in a foggy landscape

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#clahe)]

## Blend a row in a custom operation

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#blend-row)]

`Compositor.Blend` works on any pixel type, so one custom step serves every raster.

## Blur a face

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-rect)]

The blur reads the real pixels around the rectangle, so its edge has no seam.

## Add film grain

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#noise)]

The grain depends only on the seed and the pixel's position, so a batch of frames with the same seed grains alike.

## Blur the background like a lens

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#lens-blur)]

`LensBlur` works in linear light, so bright points stay bright discs instead of gray smudges; `Within` with a mask feathers the
edge between the sharp subject and the blurred background.

## Clean a scan

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#despeckle)]

For heavy salt-and-pepper noise use `Median(1)` or `Median(2)` instead; both keep edges, and both give ImageMagick's pixels.

## Find edges

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#edges)]

See [Edges and morphology](../guides/edges-and-morphology.md) for the nine operators.

## A selection from Canny

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#canny)]

Canny's white-on-black map works directly as a `Mask`; thicken it with `Dilate` first to catch the pixels beside each edge.

## Clean a scanned form with Close

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#morphology)]

## A bevelled logo

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#bevel)]

## Embossed text

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#emboss)]

## A variable weight slider

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#weight-slider)]

## Emoji in a caption

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#emoji-caption)]

Color glyphs draw in color on every canvas; on a raster they are cached as images, like other glyphs. A cluster with emoji
presentation prefers a color face from the fallback list; text presentation (VS15) prefers an outline face.

## An Arabic caption

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#arabic-caption)]

The shaper picks each letter's joining form and the bidi algorithm orders the words; the origin is still the line's left end, so
right alignment subtracts the measured advance.

## A Hindi label with fallback

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#hindi-fallback)]

Fallback works on whole grapheme clusters, so a conjunct never splits across two faces. `FontFallback.System` would find an
installed Devanagari face instead, at the cost of output that depends on the machine.

## Searchable text in a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#pdf-text)]

A `GlyphRun` from the shaper carries the text and a cluster per glyph, in visual order; that is all a PDF writer needs for
ToUnicode and ActualText.

## A caption in a box

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#caption-box)]

The layout's height is known before drawing, so the band behind the text fits the lines exactly; `MaxLines` stops a long
caption from climbing up the photo.

## A justified article column

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#article-column)]

Balanced filling evens out line lengths, so justified lines stretch less; soft hyphens break long words only where a line
needs them.

## A chat bubble with an emoji

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#chat-bubble)]

`LongestLine` is the width the lines actually use, which is what a bubble should wrap; `DidExceed` and `HiddenRange` say what
the ellipsis cut.

## Japanese vertical text

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#vertical-japanese)]

Kana and kanji stand upright and Latin turns sideways, as `TextOrientation.Mixed` says; hit tests, carets and selections work
along the columns.

## Text flowing across two columns

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#two-columns)]

Each line's top is 0, so the caller places it; a line that does not fit goes back with `Rewind` and starts the next column.

## A label along a curve

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#label-curve)]

## A variable font in a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#variable-pdf)]

The subset of a variable face is always a static instance; `SubsetOptions.Variations` gives the position without making a face.

## A drop shadow for a logo

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#logo-shadow)]

The canvas grows to hold the shadow; pass `Expand = false` to keep the size, as inside `Within`.

## Pixelate a face

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#blur-face)]

## An oil-paint look

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#oil-look)]

## Edit only the sky

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-mask)]

A soft mask gives a soft transition; blur the mask raster first to feather a hard selection.

## Swap red and blue

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#shuffle)]

## Alpha from a gray image

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#alpha)]

## Remove a green screen

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#green-screen)]

Raise the tolerance until the screen is gone, then add softness for clean hair and edges.

## Select by color, then edit

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#select-by-color)]

## Average a burst of photos

[!code-csharp[](../../../tests/Egami.DocSamples/Arithmetic.cs#stack)]

## Straighten a photographed document

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#homography)]

Give the page's four corners in the photo, top-left first and clockwise; the perspective maps them onto an upright canvas.

## Skew a banner

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#skew)]

## Rotate a region in place

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#rotate-region)]

A viewport the size of the area keeps the size, so the transform runs inside `Within`.

## Correct a wide-angle lens, swirl a region

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#warps)]

Barrel takes the PanoTools coefficients a lens database such as lensfun gives for the camera and focal length.

## Ripple with a displacement map

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#displace)]

## Check that a texture tiles seamlessly

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#offset-tile)]

## Trim a scan

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#trim)]

## Straighten a page

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#deskew)]

## A responsive image set from one mip chain

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#mip-chain)]

## A caption on a photo

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#caption)]

## A label measured to fit

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#measure)]

## A font subset for a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#subset)]

## A contact sheet

[!code-csharp[](../../../tests/Egami.DocSamples/Combining.cs#grid)]

## A sprite sheet with its JSON

[!code-csharp[](../../../tests/Egami.DocSamples/Combining.cs#sprite-sheet)]

## Split a multi-page scan into PNGs

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-split)]

Each page keeps its own size and pixel type; see [TIFF](../guides/tiff.md#reading).

## Combine pages into one TIFF

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-save-all)]

## Archive a black-and-white page as a fax

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-fax)]

Group 4 is the usual choice for scanned text; it takes 1-bit pages only, so threshold first or set `Reduce`.

## Strip metadata

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#strip)]

## Read the size without decoding

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#read-size)]

`Probe` reads only the header, and reports the EXIF orientation, so you know how the image will be shown.

## Interop with raw buffers

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#raw-buffers)]

## Native AOT

Egami is trimming- and AOT-clean: no reflection, no runtime code generation. Publish as usual:

```sh
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

Egami's own build publishes a small console app this way on every change and checks that it runs with no warnings.

## Test renders against a baseline

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#visual-regression)]

## Find near-duplicate photos

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#hashes)]

Keep the 64-bit hashes in a database column; a lookup within a few bits finds recompressed and resized copies.

## Check an image is grayscale

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#grayscale-check)]

## A 16-color poster

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#quantize)]

## Prepare a photo for a six-color e-ink display

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#remap)]

## Brand colors from a logo

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#dominant)]

## An indexed image for a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#indexed)]

## Shrink a screen-recording GIF

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#shrink)]

## Speed up an animation 2x

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#speed)]

## A looping sticker on an animated background

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#sticker)]

## A render pass from OpenEXR as JPEG

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-layer)]

`ExrReaderOptions.Layer` picks a pass by name; `ExrDetails` lists the parts and layers a file has. `ToneMap` rolls off what is
brighter than white instead of clipping it. See the [HDR guide](../guides/hdr.md).

## Show an Ultra HDR photo as HDR

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-ultrahdr-view)]

The gain map is applied for the display's headroom: 1 gives the SDR picture, and the map's own capacity (leave `Headroom` null)
the full HDR rendition.

## Make an Ultra HDR JPEG from an OpenEXR render

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-to-ultrahdr)]

The SDR picture is the render tone mapped with BT.2390; the gain map, half the size here, brings the HDR render back.


## Load a JPEG XL image

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#cookbook-load)]

Lossless and lossy files, animations (as their blended frames), extra channels and recompressed JPEGs all load through
`Raster.Load`; lossy color comes out in the file's own color space. See the [formats guide](../guides/formats.md).

## Get the original JPEG back from a JPEG XL file

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#reconstruct)]

A JPEG recompressed losslessly into JPEG XL (`cjxl` does this by default for JPEG input) keeps a jbrd box, and
`JxlImage.ReconstructJpeg` writes the original file from it, byte for byte. Loading such a file gives its pixels, as viewers show them.

## Recompress a JPEG into JPEG XL without loss

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#recompress)]

The JPEG's own coefficients become the JPEG XL image, so nothing is decoded again or lost: the file loads to the pixels the
JPEG gave, about a fifth smaller (as cjxl makes it, within a few bytes), and `JxlImage.ReconstructJpeg` writes the original
back byte for byte. Baseline and progressive JPEGs with any Huffman tables, restart markers, ICC, Exif and XMP are taken;
arithmetic-coded, lossless, hierarchical, 12-bit and CMYK JPEGs, and 4:1:1 sampling, are refused.

## Save a lossless JPEG XL file

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save)]

Every pixel comes back exactly, in the raster's own precision: 8 or 16-bit, half or single float, gray or RGB, with alpha
when it is not opaque everywhere. The effort follows cjxl's: 1 is its fast path, 7 learns a context tree and tries palettes
and color transforms, for files within a few bytes of cjxl's. The ICC profile (or an enumerated sRGB, linear or HDR space),
EXIF, XMP and orientation are kept.

## Save a lossy JPEG XL file

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy)]

Lossy files are VarDCT in XYB, as cjxl writes them for PNG input, and come out within 5% of cjxl's size and Butteraugli score at the
same settings (libjxl 0.12.0). Pick either a quality or a distance: `Quality` is cjxl's `-q` (90 by default, 100 lossless) and
`Distance` its `-d`, where 1.0 is visually lossless and each step up gives a smaller file with more visible loss; a distance,
when set, wins. `Effort` 1 to 7 is cjxl's `-e`: 6 adds the full block-size search and a per-block edge-preserving
filter strength, 7 patches for repeated shapes such as text. `Progressive` and `PhotonNoiseIso` do what cjxl's `-p` and `--photon_noise_iso` do.
Large images are coded in 2048-pixel chunks, as cjxl does, so memory stays bounded.

A JPEG source is better recompressed than re-encoded: [Recompress a JPEG into JPEG XL without
loss](#recompress-a-jpeg-into-jpeg-xl-without-loss) keeps its pixels exactly and the original file recoverable, where lossy
encoding its decoded pixels loses detail a second time.

## Keep alpha exact in a lossy JPEG XL file

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy-alpha)]

Alpha is an extra channel coded apart from the color, exact by default, so cut-outs keep their edges (below distance 10;
from there cjxl and Egami code the whole image at half size). `AlphaDistance` above 0
codes it lossy as cjxl's `--alpha_distance` does. Fully transparent pixels keep their color unless `KeepTransparentColors` is
false, which lets the encoder replace it, as libjxl does by default, for a smaller file.

## Read an HDR JPEG XL file as SDR

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#cookbook-sdr)]

PQ and HLG files keep their curve when they load, with intensity_target as `HdrMetadata.MaxContentLightLevel`; converting to
sRGB tone maps with BT.2390 from that peak. A file with a jhgm gain map loads as its SDR base, and `ReaderOptions.Hdr` renders
it as HDR, as for [Ultra HDR photos](#show-an-ultra-hdr-photo-as-hdr).

## Load an AVIF image

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-load)]

Stills, grids and overlays load through `Raster.Load`, converted to RGB as libavif converts them, the clean aperture cropped
and `irot` and `imir` kept as the orientation (`AutoOrient` applies it). Film grain is added unless
`AvifReaderOptions.FilmGrain` is false. See the [formats guide](../guides/formats.md).

## Save an AVIF file

Lossy at a chosen quality on avifenc's scale, or lossless; transparency is kept as an alpha item.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#write)]

## Get an AVIF file's YUV planes

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-yuv)]

`YuvRaster.Load` gives the decoded AV1 planes at 8, 10 or 12 bits, with the alpha image as a fourth plane, as dav1d decodes
them; see [Planar YUV](../guides/planar-yuv.md).

## Load an iPhone photo (HEIC)

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#load)]

HEIC needs the Egami.Heic package, registered with `WithHeic()`, because HEVC is covered by patents. The photo's tiles decode
in parallel, and the pixels equal libheif's heif-dec output. See the [HEIC guide](../guides/heic.md).

## Get a HEIC photo's YUV planes

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#yuv)]

`YuvRaster.Load` gives the decoded HEVC planes, as HM, the reference decoder, decodes them.

## Show an iPhone HDR photo as HDR

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#hdr)]

The gain map, Apple's or ISO 21496-1, loads with the SDR image; `ReaderOptions.Hdr` renders the HDR rendition for a display's
headroom. See the [HEIC guide](../guides/heic.md#hdr-photos).

## Read an iPhone photo's mattes and depth map

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#items)]

`HeicDetails.Items` lists the thumbnails, gain map, depth maps and mattes; `HeicImage.ReadItem` reads one.

## Read an HDR AVIF file as SDR

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sdr)]

PQ and HLG files keep their curve when they load, with clli and mdcv in `RasterMetadata.Hdr`; converting to sRGB tone maps
with BT.2390.

## Show an AVIF with a gain map as HDR

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-gainmap)]

An AVIF tone-mapped image (tmap) loads as its base rendition with the gain map in `RasterMetadata.GainMap`, and
`ReaderOptions.Hdr` renders the other rendition for a display's headroom, as for [Ultra HDR photos](#show-an-ultra-hdr-photo-as-hdr);
the base may be the SDR or the HDR one.

## Read an AVIF's thumbnail or depth map

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-items)]

`AvifDetails.Items` lists every item of the file with what it is to the primary image, and `AvifImage.ReadItem` decodes any
image item on its own, in its natural pixel type.

## Turn an AVIF image sequence into an animation

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sequence)]

A sequence (the avis brand) whose frames are all key frames loads every frame with its delay; one with inter frames loads its
first frame with a warning for now.

## Find a PDF font's glyph by name

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#names)]

A simple font's `/Differences` names glyphs; `TryGetGlyphByName` finds them in a CFF charset or a TrueType post table. See
[Font data for PDF](../guides/text.md#font-data-for-pdf).

## Find a CIDFontType0 glyph by CID

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cids)]

## Pick the cmap subtable a PDF font uses

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cmaps)]

A symbolic TrueType font looks codes up in (3,0), then (1,0); a non-symbolic one in (3,1). A Type1C font without `/Encoding`
uses the CFF program's own encoding, `BuiltInEncoding`.

## Decode a PDF's CCITT image

[!code-csharp[](../../../tests/Egami.DocSamples/Ccitt.cs#ccitt-pdf-image)]

`CcittFax.Decode` gives what the CCITTFaxDecode filter gives, byte-aligned Group 4 included; the image's `/Decode`, `/ImageMask`
and color space then apply as after any filter. To load the data straight to `Gray8`, use `CcittFormat`; see the
[CCITT guide](../guides/ccitt.md).

## Separations of a CMYK page with spot colours

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-separations)]

`InkCanvas` draws on plates with any `Canvas` call; RGB images and colours convert through the ink set's profile. See
[Printing inks](../guides/drawing.md#printing-inks).

## A spot-colour label with overprint

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-spot-label)]

`Overprint.On` paints only the inks a colour names; `NonZero` also leaves the plates of a CMYK colour's zero components alone,
as PDF's overprint mode 1.

## Check total ink coverage before printing

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-coverage)]
