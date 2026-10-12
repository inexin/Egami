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
