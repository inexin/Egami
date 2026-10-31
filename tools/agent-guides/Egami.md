# Egami: guide for coding agents

Egami is a fully managed 2D imaging library for .NET 10: it loads, edits and saves images, with no native code and no
third-party dependencies. This package holds rasters, pixel types, the image formats, operations, color management and
metadata. Drawing, fonts, SVG, CSS, metafiles, barcodes, HEIC, the extra formats and video are separate packages that build on it.

Namespaces: `Egami` (rasters, `EgamiContext`), `Egami.Pixels` (pixel types), `Egami.Formats` and `Egami.Formats.<Format>`
(readers, writers and their options), `Egami.Operations` (what `Apply` and `Derive` take), `Egami.Metadata`.

{{shared}}

## Entry points

- `Raster.Load`, `Raster.LoadAsync`, `Raster.Probe`: from a path, a stream or bytes, with an optional `EgamiContext` and
  `ReaderOptions`.
- `Raster.Create<TPixel>(width, height, fill)`: a new image.
- `raster.Apply(ops => ...)` and `raster.Derive(ops => ...)`: `Fit`, `Resize`, `Crop`, `AutoOrient`, `Grayscale`, `Blur`,
  `Sharpen`, `Saturation` and the other operations, chained.
- `raster.Row(y)`: one row of pixels as a `Span<TPixel>`, for your own pixel loops.
- `raster.Save`, `raster.SaveAsync`: by extension, or with a `FormatInfo` such as `JpegFormat.Info` and writer options such as
  `JpegWriterOptions`.
- `Raster.Transcode(input, output, ops => ...)`: load, edit and save a large file without holding the whole image.

## Samples

Load, make a thumbnail and save:

{{sample GettingStarted.cs#load-edit-save}}

Work in a pixel type you choose:

{{sample GettingStarted.cs#pixel-types}}

Choose the output format and its options:

{{sample GettingStarted.cs#choosing-a-format}}

A photo thumbnail: smallest JPEG decode, upright, fitted:

{{sample Cookbook.cs#thumbnail}}

Files from users: probe first, with limits:

{{sample MemoryAndSecurity.cs#untrusted}}

Many files at once:

{{sample Cookbook.cs#batch-convert}}

## Common mistakes

- Not disposing rasters. Pixel memory is pooled, so a raster left to the garbage collector holds its memory until then. Put
  `using` on every `Load`, `Create`, `Derive`, `As` and `Copy` result.
- Expecting `Apply` to return a new image. `Apply` changes the raster it is called on; use `Derive` to keep the original.
- Forgetting `AutoOrient()` on photos. Camera JPEGs are often stored sideways with an Exif orientation; `Raster.Probe` reports
  it in `Orientation`.
- Resizing with both sizes when the aspect ratio should stay: `Fit(w, h)` keeps it inside a box, `Resize(w, 0)` keeps it from
  one side, `Resize(w, h)` stretches.
- Expecting `Raster.Load` to give a PSD's layers. Load gives the flattened image only; `PsdImage.ReadLayers` gives each layer
  with its pixels and mask, and `PsdDocument.Flatten` flattens them again after changing a layer's `Info`.
- Running many images in parallel with the default context. Each operation already uses several threads; when you run files
  side by side, give each a context with `MaxParallelism = 1`.
- Loading untrusted files without limits. Probe first and pass an `EgamiContext` with `SafetyLimits`; catch
  `UnsupportedFormatException`, `LimitExceededException` and `CorruptDataException` (all are `EgamiException`).
- Expecting HEIC, SVG, PCX and similar formats to load out of the box. HEIC needs Egami.Heic (`WithHeic()`), SVG needs Egami.Svg
  (`WithFormat(SvgFormat.Info)`), PCX, SGI and the other rare formats need Egami.Formats.Extended (`WithExtendedFormats()`).

## More

- Getting started: https://inexin.github.io/Egami/v1/articles/getting-started.html
- Guides (formats, resizing, color, filters, metadata, memory, security): https://inexin.github.io/Egami/v1/articles/guides/index.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.html
