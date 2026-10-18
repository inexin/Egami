# AVIF

Egami reads and writes AVIF still images, written from the AV1 and HEIF specifications with no native code: the HEIF container,
a managed AV1 intra decoder and a managed AV1 intra encoder. `AvifFormat.Info` is registered in `EgamiContext.Default`, so
`Raster.Load` recognizes an ftyp box with the avif or avis brand and `Save` writes a file whose name ends in .avif.

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

## Writing

`Save` codes the image with Egami's own AV1 encoder. Lossy images use avifenc's quality scale, so `AvifWriterOptions.Quality`
gives about the same file as `avifenc -q` with the same number; the default is 60. `Lossless` keeps every sample: the image is
written as 4:4:4 RGB with the identity matrix, at its own bit depth. A file read from a lossless AVIF is written lossless again
unless the options say otherwise (`AvifDetails.Lossless`).

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#write)]

Lossy color is 4:2:0 below Quality 90 and 4:4:4 from 90, or the `Chroma` you choose; gray images are written 4:0:0. 8-bit pixels
are written at 8 bits and deeper pixels at 10, or at the `BitDepth` you choose (8, 10 or 12). Transparency goes in an alpha item,
a second AV1 image, kept exactly unless `AlphaQuality` is below 100.

`Effort`, 0 to 10, trades encoding time for size: it is 10 minus avifenc's speed, and the default 4 works like avifenc's
default speed 6. The encoder weighs partitions, prediction modes and transforms by the bits they cost and the error they leave,
spends finer quantizer steps on flat areas, where banding shows, and picks the deblocking and CDEF filters. Screenshots, text and
graphics are recognized: their few colors are coded as palettes, and repeated content as copies of what came before (intra block
copy).

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-write)]

From effort 5, 8-bit 4:2:0 images are converted with sharp RGB to YUV, as libavif does: luma and chroma are refined together so
sharp colored edges keep their color. A `YuvRaster` is written as its planes are, at 8, 10 or 12 bits, with no conversion or
chroma resampling; its matrix, range and chroma siting go into the file, and `Lossless` keeps every sample of the planes.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#write-yuv)]

Metadata goes with the image: EXIF and XMP as items, the EXIF orientation as irot and imir (which readers apply), PQ and HLG
code points with the HDR luminances as clli and mdcv, and a gain map as a tmap item that readers without gain maps skip. The ICC
profile is written only when the colors have no code points and it is not plain sRGB, which the nclx box always says.
`StripMetadata` leaves EXIF, XMP, the profile and the gain map out.

An image up to 8192 x 4352 is one AV1 frame, split into tiles every 4096 samples across that are coded in parallel; a larger
image is written as a grid of equal cells, or of the `GridTileSize` you choose, coded in parallel too. The file is the same
whatever the number of threads.

Every file Egami writes decodes in dav1d and libavif to the same pixels as in Egami; `tools/check-avif-interop.py` checks this.

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
