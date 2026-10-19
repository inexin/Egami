# HEIC

HEIC is the format iPhones and most Android phones save photos in: HEIF files whose images are coded with HEVC (H.265). HEVC is
covered by patents, so Egami reads HEIC through a separate package, Egami.Heic, that an app opts in to. The core Egami package
contains no HEVC code and never depends on Egami.Heic.

Egami.Heic is a work in progress: this version reads HEIC containers, and its managed HEVC decoder is not finished yet.

## Without Egami.Heic

Core Egami recognizes HEIC by its brands (heic, heix, hevc and the other HEVC brands) and reads the container, so
`Raster.Probe` gives the size, the natural pixel type and the orientation. `Raster.Load` throws an `UnsupportedFormatException`
that names the package.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#probe)]

The size is the image's after its clean aperture (`clap`) crop; its rotation and mirroring (`irot` and `imir`) are given as
`RasterInfo.Orientation`, as for AVIF. The natural pixel type follows the bit depth: `Rgb8` for 8-bit files, `Rgb16` for 10 and
12 bits, with alpha when the file has an alpha image, and gray for monochrome.

## Registering Egami.Heic

Nothing registers Egami.Heic on its own. `WithHeic()`, an extension method in `HeicContextExtensions`, returns a context with
Egami.Heic's `HeicFormat.Info` in place of core Egami's HEIC entry; pass it to the calls, or make it the default once at startup
with `EgamiContext.SetDefault(new EgamiContext().WithHeic())`.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#register)]

Egami.Heic ships with every Egami release, at the same version, and depends on exactly that version of Egami.

## Patents

Egami.Heic decodes HEVC (H.265), a format covered by patents. Egami's authors grant no patent rights and are not lawyers.
Whether you need a patent licence depends on your country and on how you use and distribute this package; commercial products
may need one from the HEVC patent pools or holders. The core Egami package contains no HEVC code and does not depend on this
package.
