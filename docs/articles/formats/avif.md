# AVIF

Egami reads and writes AVIF still images, written from the AV1 and HEIF specifications with no native code: the HEIF container,
a managed AV1 intra decoder and a managed AV1 intra encoder. `AvifFormat.Info` is registered in `EgamiContext.Default`, so
`Raster.Load` recognizes an ftyp box with the avif or avis brand (or a mini file's mif3 brand with avif as its minor version) and
`Save` writes a file whose name ends in .avif.

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

`YuvRaster.Load` gives the AV1 planes themselves, without the conversion to RGB (see [Planar YUV](../guides/planar-yuv.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-yuv)]

Files with a sample transform (a sato item, AVIF 1.2), such as the 16-bit files `avifenc --depth 16` writes from a 12-bit image
and an 8-bit residual, load the transform's output at its own depth, as `Rgb16` or `Rgba16`: every expression the specification
allows is evaluated over the input images' planes. `AvifReaderOptions.SampleTransform` set to `false` loads the file's fallback
image instead, the 8- or 12-bit primary item that readers without sample transforms show. Files with the compact mini box (the
mif3 brand of the HEIF amendment draft) load like any other, with their alpha, gain map, ICC, Exif and XMP.

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

`BitDepth = 16` writes 16 bits a sample through a sample transform: lossless images as an 8-bit image of the high bytes and a
hidden 8-bit image of the low bytes, lossy ones as a 12-bit image and a hidden 8-bit residual that overlaps it by 4 bits, as
libavif writes them. Readers without sample transforms show the first image.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#sato)]

`TargetSize` searches the quality for the largest file not above a number of bytes, as `avifenc --target-size` does; when even
quality 0 is larger, that file is written and an `Avif.TargetSizeExceeded` warning says so. `GainMapQuality` codes a gain map
at its own quality, as `avifenc --qgain-map`.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#target-size)]

`FilmGrainDenoise`, 1 to 50, takes film grain out before coding and writes AV1 film grain parameters that decoders use to put it
back, so the grain costs a few bytes rather than many, as libaom's denoise-noise-level does for still images: on a picture with
measurable noise, each plane is denoised by a Wiener filter and the noise modeled by its autoregressive pattern and its strength at
each brightness. A clean picture gets no grain. A raster loaded with `AvifReaderOptions.FilmGrain` set to `false` keeps its
`AvifDetails.FilmGrain`, and saving it as AVIF writes the parameters back, so the look survives a round trip.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#grain)]

### Progressive AVIF

`Progressive` writes two layers, as `avifenc --progressive` does: a small first layer at quality 2 that a viewer can show while
the rest arrives, then the image at `Quality`. `Layers` gives up to four, each with its own quality and scale; the last is the
image at full size. The layers are one AV1 stream with a spatial layer each, indexed by an a1lx property; each later layer is
predicted from the one before when it is the same size, else coded on its own, as is the second of two after a near-empty first.
Images larger than one AV1 frame are written in one layer.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#layers)]

`Load` gives the best layer, or the one an lsel property or `AvifReaderOptions.Layer` picks, at the image's full size, and
`AvifDetails.Layers` lists each layer's size. While a progressive file arrives, `RasterDecoder.RenderPreview` draws the best layer
received so far at full size, as it draws a progressive JPEG's scans:

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#avif-preview)]

### Animated AVIF

A raster with more than one frame saves as an image sequence (the avis brand), as libavif writes one: the frames as one AV1 stream of
a key frame and inter frames in a track, each with its delay, and the loop count in the edit list. When any frame has transparency
the alpha goes into a second, monochrome track; it is exact unless `AlphaQuality` is below 100. The first frame is also the file's
primary image, so readers of stills show it. `Quality`, `Effort`, `Lossless`, `Chroma` and `BitDepth` apply to every frame; gain
maps and grids are for stills only. avifdec decodes every frame exactly as Egami does.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#write-animation)]

## HDR

PQ and HLG files keep their curve; converting to sRGB as they load tone maps them, the peak taken from the file's clli box:

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sdr)]

Gain maps (tmap) load into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr` renders the HDR rendition from them, as for Ultra HDR
JPEGs (see the [HDR guide](../guides/hdr.md)):

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-gainmap)]

## Items

`AvifDetails` describes the AV1 configuration, color, alpha, grid and brands, and lists every item of the file as an `AvifItem`
with its `AvifItemRole` to the image `Load` gives: the primary image, alpha, thumbnails, depth maps, gain maps, grid cells and
metadata. `AvifImage.ReadItem` decodes any image item on its own.

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#items)]

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-items)]

## Sequences and layers

Image sequences (avis) load every frame, inter frames included, each with its duration from the sample table, and the loop
count from the edit list; an alpha track gives each frame its alpha. Every frame equals avifdec's. A range of frames
(`ReaderOptions.Frames`) decodes from the key frame before its first. Layered images load their best layer (see
[Progressive AVIF](#progressive-avif)).

[!code-csharp[](../../../tests/Egami.DocSamples/Avif.cs#cookbook-sequence)]
