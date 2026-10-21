# HDR images

HDR images hold light brighter than SDR white. Egami loads HDR pixels as linear light in floating point, unclamped, with 1.0 as
reference white (203 cd/m²), the same scale the [color management](color-management.md) guide's tone mapping and HDR metadata
use. Radiance (.hdr) and OpenEXR (.exr) files hold such pixels directly; see the [Radiance](../formats/radiance.md) and
[OpenEXR](../formats/openexr.md) pages. Phone photos carry HDR as a gain map beside an SDR picture, which this guide covers. PQ and
HLG images are covered with their formats: [AVIF](../formats/avif.md), [HEIC](../formats/heic.md) and [JPEG XL](../formats/jxl.md).

## Gain maps

An Ultra HDR JPEG, as Android phones, Chrome and Photoshop write it, is an SDR picture any viewer shows, followed (through
[MPF](../formats/jpeg.md#multi-picture-jpeg-files)) by a gain map: how much brighter each part of the picture is in the HDR rendition. ISO 21496-1, Adobe's hdrgm XMP (Ultra
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

### Show an Ultra HDR photo as HDR

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-ultrahdr-view)]

The gain map is applied for the display's headroom: 1 gives the SDR picture, and the map's own capacity (leave `Headroom` null)
the full HDR rendition.

### Make an Ultra HDR JPEG from an OpenEXR render

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-to-ultrahdr)]

The SDR picture is the render tone mapped with BT.2390; the gain map, half the size here, brings the HDR render back.
