# Egami.Heic: guide for coding agents

Egami.Heic reads HEIC, the HEIF files with HEVC-coded images that iPhones and most Android phones save photos in: grids, alpha,
color profiles, orientation, Exif and XMP, Main, Main 10 and the range extensions up to 12 bits. It is a plugin for core Egami's
`Raster.Load`. HEVC is covered by patents; read the package README before shipping it.

Namespaces: `Egami.Heic` (`WithHeic`, `HeicFormat`), with `Egami` and `Egami.Formats`.

{{shared}}

## Entry points

- `new EgamiContext().WithHeic()`: a context that decodes HEIC. Make it the default once at startup with
  `EgamiContext.SetDefault(new EgamiContext().WithHeic())`, or pass it to each call.
- `Raster.Load`, `Raster.Probe` and the other core calls, with that context. Bursts and animated files load as frames.
- `HeicDetails.Items` (from `raster.Metadata.FormatDetails`) and `HeicImage.ReadItem`: the thumbnails, gain maps, depth maps and
  mattes a photo holds.

## Samples

Register it:

{{sample Heic.cs#register}}

Load an iPhone photo:

{{sample Heic.cs#load}}

The other images a photo holds:

{{sample Heic.cs#items}}

Without the package, core Egami still probes HEIC:

{{sample Heic.cs#probe}}

## Common mistakes

- Referencing the package and expecting `Raster.Load` to read HEIC. Nothing registers it on its own: call `WithHeic()`.
- Forgetting `AutoOrient()`. iPhone photos often carry a rotation, which comes as the orientation.
- Expecting to save HEIC. The package reads only; save as JPEG, PNG, WebP or AVIF.
- Looking for the depth map or a matte in the main raster. They are separate items: read them with `HeicImage.ReadItem`.

## More

- HEIC: https://inexin.github.io/Egami/v1/articles/formats/heic.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Heic.html
