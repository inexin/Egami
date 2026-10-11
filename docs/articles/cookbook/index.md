# Cookbook

Task-sized recipes. Each one compiles and runs in Egami's test run.

## Thumbnails for the web

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#thumbnail)]

The JPEG decodes at 1/2, 1/4 or 1/8 scale, `AutoOrient` turns only the small result, and the metadata is left out of the file.

## Safe handling of untrusted uploads

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#untrusted)]

See the [security guide](../guides/security.md) for every limit.

## Use from ASP.NET Core

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#upload)]

Egami has no ASP.NET Core dependency: it reads and writes any `Stream`. The async calls read the upload into a pooled buffer,
counted against `MaxTotalBytes`, so a slow client never holds a thread.

## Batch convert a folder

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#batch-convert)]

## Make an animated GIF from frames

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#gif-from-frames)]

## Show video frames

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#reuse)]

Decoder frames in NV12 or P010 copy into the planes first; see [planar YUV](../guides/planar-yuv.md#interleaved-layouts).

## Read and write JPEG planes

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#jpeg)]

## Read a CMYK JPEG's inks

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#cmyk-inks)]

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

## Watermark with Multiply

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#watermark-multiply)]

A dark logo drawn with `Multiply` reads as ink on the photo: light areas of the logo leave the photo unchanged.

## Tint a photo

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#tint)]

## Blend a row in a custom operation

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#blend-row)]

`Compositor.Blend` works on any pixel type, so one custom step serves every raster.

## Blur a face

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-rect)]

The blur reads the real pixels around the rectangle, so its edge has no seam.

## Edit only the sky

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-mask)]

A soft mask gives a soft transition; blur the mask raster first to feather a hard selection.

## Swap red and blue

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#shuffle)]

## Alpha from a gray image

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#alpha)]

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
