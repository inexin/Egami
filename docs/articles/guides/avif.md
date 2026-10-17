# AVIF

Egami reads AVIF still images, written from the AV1 and HEIF specifications with no native code: the HEIF container and a
managed AV1 intra decoder. `AvifFormat.Info` is registered in `EgamiContext.Default`, so `Raster.Load` recognizes an ftyp box with
the avif or avis brand. Egami does not write AVIF yet.

`Raster.Probe` reads the container alone: size, natural pixel type and orientation, without decoding any AV1.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#probe)]

## Reading

AV1 intra frames decode with every coding tool, in-loop filter and film grain, equal to dav1d's decode, and are converted to RGB
as libavif converts them, so `Raster.Load` equals avifdec's output. Alpha (straight or premultiplied), grids, overlays, the
clean aperture (cropped), `irot` and `imir` (as the orientation), ICC, CICP, HDR metadata, Exif and XMP are all read.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-load)]

The natural pixel type follows the bit depth: `Rgb8` or `Rgba8` for 8-bit files, `Rgb16` or `Rgba16` for 10 and 12 bits, and
`Gray8` or `Gray16` (with alpha, `GrayAlpha8` or `GrayAlpha16`) for monochrome.

`AvifReaderOptions.FilmGrain` set to `false` leaves out the grain the AV1 stream asks the decoder to add, giving the image as
coded; the grain's parameters are in `AvifDetails.FilmGrain` as an `AvifFilmGrain` either way.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#options)]

`YuvRaster.Load` gives the AV1 planes themselves, without the conversion to RGB (see [Planar YUV](planar-yuv.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-yuv)]

## HDR

PQ and HLG files keep their curve; converting to sRGB as they load tone maps them, the peak taken from the file's clli box:

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sdr)]

Gain maps (tmap) load into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr` renders the HDR rendition from them, as for Ultra HDR
JPEGs (see the [HDR guide](hdr.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-gainmap)]

## Items

`AvifDetails` describes the AV1 configuration, color, alpha, grid and brands, and lists every item of the file as an `AvifItem`
with its `AvifItemRole` to the image `Load` gives: the primary image, alpha, thumbnails, depth maps, gain maps, grid cells and
metadata. `AvifImage.ReadItem` decodes any image item on its own.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#items)]

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-items)]

## Sequences and layers

Image sequences (avis) load every frame when all are key frames, and the first frame otherwise. Layered images load their base
layer, until Egami decodes AV1 inter frames.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sequence)]
