# HEIC

HEIC is the format iPhones and most Android phones save photos in: HEIF files whose images are coded with HEVC (H.265). HEVC is
covered by patents, so Egami reads HEIC through a separate package, Egami.Heic, that an app opts in to. The core Egami package
contains no HEVC code and never depends on Egami.Heic.

Egami.Heic decodes HEIC images with a managed HEVC decoder: the intra pictures of the Main, Main 10, Main Still Picture and
format range extensions profiles, up to 12 bits and from 4:0:0 to 4:4:4. Gain maps, the photo's other images (thumbnails,
depth maps, mattes) and image sequences whose frames are all intra are read too.

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

## Loading

With Egami.Heic registered, HEIC loads like any format, in its natural pixel type.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#load)]

- **Grids.** Phone photos are grids of tiles (an iPhone photo has 48 or more). The tiles decode in parallel, on up to
  `EgamiContext.MaxParallelism` threads, and a `ReaderOptions.Region` decodes only the tiles it touches. Tiles and wavefront rows
  inside one HEVC picture decode in parallel too. The pixels are the same at any number of threads.
- **Color.** The planes are converted to RGB as libheif's heif-dec converts them, so `Raster.Load` gives the same samples.
  The phone photos and HEVC-coded files of Egami's test corpus equal heif-dec's output sample for sample, except a clean
  aperture at half-sample offsets, which Egami crops at whole samples. The colors come from the `colr` property, or else from
  the HEVC stream's own color description. An ICC profile is kept in `RasterMetadata.IccProfile`.
- **Geometry and alpha.** The clean aperture (`clap`) is cropped; rotation and mirroring (`irot` and `imir`) are kept as the
  orientation, which `AutoOrient` applies. An alpha image loads as straight alpha. Overlays (`iovl`) are composed.
- **Metadata.** Exif and XMP items are read into `RasterMetadata.Exif` and `RasterMetadata.Xmp`.

The decoded HEVC planes are available without the conversion to RGB, as for AVIF and JPEG (see
[Planar YUV](planar-yuv.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#yuv)]

Every intra picture of the HEVC conformance streams in these profiles decodes bit-exact to HM, the reference decoder.

## HDR photos

iPhones store an HDR photo as an SDR image with a gain map: Apple's HDRGainMap auxiliary image (its headroom in the maker note
or the map's XMP), and on newer iPhones an ISO 21496-1 tone-map item as well, which is preferred. Load gives the SDR image with
the map in `RasterMetadata.GainMap`; `ReaderOptions.Hdr` gives the HDR rendition for a display's headroom, as for
[Ultra HDR JPEGs](hdr.md).

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#hdr)]

A gain map that cannot be read (Apple's without a headroom, a damaged tone-map item) never fails the load: the image loads as
SDR with a `Heic.GainMapDropped` warning.

## Thumbnails, depth maps and mattes

`HeicDetails.Items` lists every item of the file with its `HeicItemRole`, what it is to the photo: the primary image, thumbnails, the gain map,
depth maps and other auxiliary images (an iPhone's portrait, skin and sky mattes), Exif and XMP. `HeicImage.ReadItem` reads an
image item on its own, with its own color and crop, and its own rotation as the orientation.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#items)]

## Image sequences

A HEIC image sequence (the hevc and hevx brands) whose frames are all intra loads every frame, as an animation; Egami.Heic does
not decode inter frames. A sequence with inter frames gives its still image when the file has one, and otherwise its first frame
with a `Heic.SequenceFirstFrame` warning.

## Patents

Egami.Heic decodes HEVC (H.265), a format covered by patents. Egami's authors grant no patent rights and are not lawyers.
Whether you need a patent licence depends on your country and on how you use and distribute this package; commercial products
may need one from the HEVC patent pools or holders. The core Egami package contains no HEVC code and does not depend on this
package.
