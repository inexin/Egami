# HDR files

Egami reads and writes Radiance pictures (.hdr) and OpenEXR images (.exr), lists the images of Multi-Picture JPEG files, and
reads, keeps and writes the gain maps of Ultra HDR, ISO 21496-1 and Apple JPEGs. HDR pixels load as linear light in floating
point, unclamped, with 1.0 as reference white (203 cd/m²), the same scale the [color management](color-management.md) guide's
tone mapping and HDR metadata use.

## Radiance

Radiance pictures hold three 8-bit mantissas and a shared exponent per pixel (RGBE), or CIE X, Y and Z the same way (XYZE).
`RadianceFormat` reads both, every scan order a resolution line can name (`-Y 512 +X 768` and the other seven), and flat,
old-style repeat-pixel and per-channel run-length scanlines. Pixels load as `RgbF32`, bit for bit what stb_image decodes; XYZE
converts to RGB in Radiance's standard primaries, which `RasterMetadata.Primaries` then names. A `PRIMARIES` line sets the
primaries of an RGBE file the same way; without one, Rec. 709 is assumed.

`RadianceDetails` keeps the header: the `RadiancePixelFormat`, `Exposure`, `ColorCorrection`, `Primaries`, `Software`, `View`, `PixelAspectRatio`, the scan
order as stored and every header line. The reader also measures the brightest value and stores it as
`HdrMetadata.MaxContentLightLevel`, so `ToneMap` fits the image rather than a fixed 1,000 cd/m² peak.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#radiance)]

The writer writes RGBE, top row first, with per-channel run-length scanlines (`RadianceWriterOptions.Compress = false` writes them
flat) and an `EXPOSURE` line only when `RadianceWriterOptions.Exposure` is set. Reading a file and writing it again gives the same
floats, and the same bytes once written by Egami.

## OpenEXR

`ExrFormat` reads scanline and tiled files, single and multi-part (`ExrPartKind`), with every compression of OpenEXR 3
(`ExrCompression`: NONE, RLE, ZIPS, ZIP, PIZ, PXR24, B44, B44A, DWAA and DWAB; HTJ2K is refused), half, float and uint channels
(`ExrPixelType`), and luminance-chroma images. Pixels match OpenEXR's own decode bit for bit, DWA within one half-float step.
A load gives the first part's R, G, B and A (or Y, YA or the luminance-chroma channels) as linear light; a tiled part loads its
full-resolution level (`ExrLevelMode` says which levels it has). Deep parts throw `UnsupportedFormatException`.

`ExrDetails` lists every part as an `ExrPart`: its name, channels (`ExrChannel`), layers, data and display windows, tile size,
chromaticities and every header attribute as an `ExrHeaderEntry`. `ExrReaderOptions` picks the `Part`, a `Layer` (such as
`diffuse` for `diffuse.R`, `diffuse.G` and `diffuse.B`) and the `ExrWindow` to load: the data window, the pixels the file
stores (the default), or the display window, padded with transparent black where there is no data. `ExrImage.ReadChannel` loads any one channel, such as depth
or an object id, as 32-bit float.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#exr)]

The writer writes one scanline part with R, G, B and A when the raster has alpha (`ExrWriterOptions`): ZIP by default, or NONE,
RLE, ZIPS or PIZ, in half or float (`ExrWriterOptions.PixelType`; by default float for float rasters other than `RgbaF16`,
half otherwise). A
non-linear raster, such as sRGB, is converted to linear light first, and primaries other than Rec. 709 are written as
chromaticities. OpenEXR reads every file Egami writes to the same values.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#exr-write)]

## Multi-Picture JPEG files

Phones store more than one image in a JPEG: a gain map for HDR, large previews, the second view of a stereo pair. The CIPA
Multi-Picture Format (MPF) lists them in an APP2 segment. `JpegDetails.MultiPicture` gives that index as `MpfData`: an
`MpfImage` for each image with its `MpfImageType`, flags, length, and offset from the start of the file (the same numbers
ExifTool prints). `Raster.Probe` counts them in `RasterInfo.ImageCount`, and `Raster.LoadAll` loads each JPEG one, the primary
first. A damaged index never fails a load: the primary loads with a `Jpeg.MpfDamaged` warning.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#mpf)]

## Gain maps

An Ultra HDR JPEG, as Android phones, Chrome and Photoshop write it, is an SDR picture any viewer shows, followed (through
MPF) by a gain map: how much brighter each part of the picture is in the HDR rendition. ISO 21496-1, Adobe's hdrgm XMP (Ultra
HDR 1.0) and Apple's HDRGainMap are three ways of writing the same idea down; `GainMapFormat` says which a file used, and
`GainMapInfo` holds its values, in log2 units as the standards give them. A load gives the SDR picture as stored, with the map in
`RasterMetadata.GainMap` as a `GainMap` (`GainMapValues` per channel); a damaged map is dropped with `Jpeg.GainMapDropped`.

`ReaderOptions.Hdr` asks for the HDR rendition instead: `HdrDecode.Headroom` is how much brighter than SDR white the display can
go (or the map's full capacity), and the load gives linear `RgbaF16`, within about 1% of libultrahdr's decode.
`GainMap.Apply` does the same for a raster in hand.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#gain-map)]

Saving as JPEG writes the map back, in both metadata forms, as Skia and libultrahdr write them. Crops, turns, flips and resizes
move the map with the picture; operations that change pixel values drop it (`Operation.GainMapDropped`), as do other formats
(`Save.GainMapDropped`) and `JpegWriterOptions.KeepGainMap = false`.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#gain-map-keep)]

To write an HDR raster as Ultra HDR, set `JpegWriterOptions.GainMap`: the raster is tone mapped to the SDR picture and
`GainMap.Create` makes the map that brings it back. `GainMapOptions` has libultrahdr's defaults (a full-size, color map, gamma 1,
quality 95) and each can be changed; `Create` also takes an SDR rendition you graded yourself.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#gain-map-write)]

## Saving HDR to an SDR format

PNG, JPEG and the other 8 and 16-bit formats hold 0 to 1. Saving a floating-point raster to them clamps, as other libraries do;
when the raster holds light above reference white, the save also reports `Save.HdrClipped`, naming `ToneMap`, which rolls the
highlights off instead of cutting them.

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### A render pass from OpenEXR as JPEG

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-layer)]

`ExrReaderOptions.Layer` picks a pass by name; `ExrDetails` lists the parts and layers a file has. `ToneMap` rolls off what is
brighter than white instead of clipping it.

### Show an Ultra HDR photo as HDR

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-ultrahdr-view)]

The gain map is applied for the display's headroom: 1 gives the SDR picture, and the map's own capacity (leave `Headroom` null)
the full HDR rendition.

### Make an Ultra HDR JPEG from an OpenEXR render

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-to-ultrahdr)]

The SDR picture is the render tone mapped with BT.2390; the gain map, half the size here, brings the HDR render back.
