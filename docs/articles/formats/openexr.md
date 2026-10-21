# OpenEXR

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

Pixels are linear light with 1.0 as reference white; the [HDR guide](../guides/hdr.md) explains the scale, and `ToneMap` in
[color management](../guides/color-management.md) turns them into SDR.

## Recipes

### A render pass from OpenEXR as JPEG

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#cookbook-exr-layer)]

`ExrReaderOptions.Layer` picks a pass by name; `ExrDetails` lists the parts and layers a file has. `ToneMap` rolls off what is
brighter than white instead of clipping it.
