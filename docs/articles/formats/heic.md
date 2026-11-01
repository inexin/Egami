# HEIC

HEIC is the format iPhones and most Android phones save photos in: HEIF files whose images are coded with HEVC (H.265). HEVC is
covered by patents, so Egami reads and writes HEIC through a separate package, Egami.Heic, that an app opts in to. The core
Egami package contains no HEVC code and never depends on Egami.Heic.

Egami.Heic decodes HEIC images with a managed HEVC decoder: intra and inter pictures of the Main, Main 10, Main Still Picture
and format range extensions profiles, up to 12 bits and from 4:0:0 to 4:4:4. Gain maps, the photo's other images (thumbnails,
depth maps, mattes) and image sequences are read too, and `HevcStreamDecoder` decodes HEVC video streams. Saving to HEIC
codes the image with a managed HEVC intra encoder.

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
[Planar YUV](../guides/planar-yuv.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#yuv)]

Every picture of the HEVC conformance streams (JCT-VC HEVC_v1 and RExt) in these profiles decodes bit-exact to HM, the reference
decoder, and matches its decoded picture hash.

## HDR photos

iPhones store an HDR photo as an SDR image with a gain map: Apple's HDRGainMap auxiliary image (its headroom in the maker note
or the map's XMP), and on newer iPhones an ISO 21496-1 tone-map item as well, which is preferred. Load gives the SDR image with
the map in `RasterMetadata.GainMap`; `ReaderOptions.Hdr` gives the HDR rendition for a display's headroom, as for
[Ultra HDR JPEGs](../guides/hdr.md).

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#hdr)]

A gain map that cannot be read (Apple's without a headroom, a damaged tone-map item) never fails the load: the image loads as
SDR with a `Heic.GainMapDropped` warning.

## Thumbnails, depth maps and mattes

`HeicDetails.Items` lists every item of the file with its `HeicItemRole`, what it is to the photo: the primary image, thumbnails, the gain map,
depth maps and other auxiliary images (an iPhone's portrait, skin and sky mattes), Exif and XMP. `HeicImage.ReadItem` reads an
image item on its own, with its own color and crop, and its own rotation as the orientation.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#items)]

## Image sequences

A HEIC image sequence (the hevc and hevx brands), such as a burst or an animation, loads every frame, as an animation: the
track's pictures in output order, inter-coded ones too, equal to libheif's `heif-dec -S`.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#sequence)]

## HEVC streams

`HevcStreamDecoder` decodes an HEVC video stream an access unit at a time, as Egami.Video's HEVC support does: an Annex B byte
stream, or an MP4 or Matroska track's samples after `SetConfiguration` with its hvcC record. Pictures come out in output order
into a `YuvRaster`, cropped to the stream's conformance window, with their order count, NAL unit type and color in
`HevcFrameInfo` (with its `HevcPictureKind`); `Flush` lets the last ones out at the end of the stream. Decoding starts at an IRAP picture; leading pictures of
a CRA picture that starts the stream are left out, and only the base layer of a layered stream decodes. In `HevcDecoderOptions`,
`ConcealErrors` keeps a
picture with damaged slice data as far as it decoded, and `CheckPictureHashes` checks each picture against its decoded picture
hash.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#stream)]

## Writing

With Egami.Heic registered, `Save` writes HEIC for the `.heic` and `.heif` extensions and the `image/heic` MIME type, through
`HeicWriterOptions`. Core Egami alone cannot write HEIC: its `UnsupportedFormatException` names the package.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#write)]

- **Quality.** `Quality` runs from 0 to 100 on heif-enc's scale, 50 by default, so the same number gives about the file size
  heif-enc gives with x265. Files are about as small as x265's at the same SSIMULACRA2 score. A built-in perceptual tuning gives
  busy areas a coarser quantizer and flat ones a finer one.
- **Effort.** `Effort` runs from 0 (fastest) to 10 (smallest files), 4 by default: about the size and the time of x265's medium
  preset. Higher efforts search more intra modes, block sizes and transform splits.
- **Format.** Lossy images are 4:2:0 at 8 bits (Main) or 10 bits for deeper pixels and HDR (Main 10), which every phone, PC and
  HEVC decoder reads. `Chroma` asks for 4:4:4, 4:2:2 or 4:0:0 and `BitDepth` for 12 bits; those use HEVC's format range
  extensions, which some phones and operating systems cannot decode. `Lossless` keeps every sample: 4:4:4 RGB with the identity
  matrix, coded without transform or quantization, which needs a range extensions decoder too.
- **Color.** RGB is converted with the BT.601 matrix for SDR images (as iPhones and libheif write) and BT.2020 for PQ and HLG,
  full range, with sharp chroma downsampling from effort 5. The `colr` box gives the code points; an ICC profile they cannot
  describe is written too. PQ and HLG images get `clli` and `mdcv` from `RasterMetadata.Hdr`.
- **Alpha.** Transparency is an auxiliary image coded as Main 4:2:0, which every decoder reads, straight and exact by default;
  `AlphaQuality` below 100 codes it lossy.
- **Size.** An image up to 8,912,896 samples (HEVC level 5.1, such as 4096 x 2176) is one picture; a larger one is a grid of 512 x
  512 cells, as iPhones write. `GridTileSize` asks for a grid of other cells. Cells and the picture's CTB rows (wavefronts) code
  in parallel; the file is the same on any number of threads.
- **Metadata and extras.** Exif and XMP are kept as items, the orientation as `irot` and `imir` (the pixels are not rotated), a
  gain map as an ISO 21496-1 tone-map item, and `ThumbnailSize` adds a thumbnail. `TargetSize` searches the quality for the
  largest file not above a size.
- **Not written.** Image sequences: a raster of several frames writes its first, with a `Save.FramesDropped` warning.

[!code-csharp[](../../../tests/Egami.DocSamples/Heic.cs#write-options)]

Every stream Egami.Heic writes decodes to the encoder's own reconstruction in Egami's decoder, and libheif's heif-dec gives the
same pixels.

## Patents

Egami.Heic decodes and encodes HEVC (H.265), a format covered by patents. Egami's authors grant no patent rights and are not lawyers.
Whether you need a patent licence depends on your country and on how you use and distribute this package; commercial products
may need one from the HEVC patent pools or holders. The core Egami package contains no HEVC code and does not depend on this
package.
