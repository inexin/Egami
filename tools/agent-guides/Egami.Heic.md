# Egami.Heic: guide for coding agents

Egami.Heic reads and writes HEIC, the HEIF files with HEVC-coded images that iPhones and most Android phones save photos in:
grids, alpha, color profiles, orientation, Exif and XMP, Main, Main 10 and the range extensions up to 12 bits. It is a plugin for
core Egami's `Raster.Load` and `Save`. HEVC is covered by patents, encoding as well as decoding; read the package README before
shipping it.

Namespaces: `Egami.Heic` (`WithHeic`, `HeicFormat`, `HeicWriterOptions`), with `Egami` and `Egami.Formats`.

{{shared}}

## Entry points

- `new EgamiContext().WithHeic()`: a context that decodes HEIC. Make it the default once at startup with
  `EgamiContext.SetDefault(new EgamiContext().WithHeic())`, or pass it to each call.
- `Raster.Load`, `Raster.Probe` and the other core calls, with that context. Bursts and animated files load as frames.
- `HeicDetails.Items` (from `raster.Metadata.FormatDetails`) and `HeicImage.ReadItem`: the thumbnails, gain maps, depth maps and
  mattes a photo holds.
- `raster.Save("photo.heic", new HeicWriterOptions { ... })` with that context: `Quality` (0 to 100, heif-enc's scale, 50),
  `Effort` (0 to 10, 4), `Lossless`, `Chroma`, `BitDepth`, `AlphaQuality`, `GridTileSize`, `TargetSize`, `GainMapQuality`,
  `ThumbnailSize`.

## Samples

Register it:

{{sample Heic.cs#register}}

Load an iPhone photo:

{{sample Heic.cs#load}}

The other images a photo holds:

{{sample Heic.cs#items}}

Save as HEIC:

{{sample Heic.cs#write}}

Without the package, core Egami still probes HEIC:

{{sample Heic.cs#probe}}

## Common mistakes

- Referencing the package and expecting `Raster.Load` to read HEIC. Nothing registers it on its own: call `WithHeic()`.
- Forgetting `AutoOrient()`. iPhone photos often carry a rotation, which comes as the orientation.
- Saving HEIC with a context that lacks `WithHeic()`. Core Egami cannot write HEIC; the exception names the package.
- Asking for 4:4:4, 12 bits or `Lossless` for files meant for phones. Those use HEVC's range extensions, which some phones and
  operating systems cannot decode; the default 4:2:0 Main or Main 10 reads everywhere.
- Looking for the depth map or a matte in the main raster. They are separate items: read them with `HeicImage.ReadItem`.

## More

- HEIC: https://inexin.github.io/Egami/v1/articles/formats/heic.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Heic.html
