# Textures

Game engines and 3D tools keep their textures in DDS files: block-compressed so the GPU reads them as they are, with a chain of
mips for distance, and with cube maps for skies and reflections. Egami.Formats.Extended reads every DDS format DirectXTex reads
and writes the uncompressed formats and BC1 to BC7, so a pipeline can turn PNGs and EXRs into textures, or textures back into
images, without a native tool. The [DDS format page](../formats/dds.md) lists every format, option and detail; this guide is
about choosing them.

Register the format once with `WithDds()` or `WithExtendedFormats()` ([Extended formats](../formats/extended.md)).

## Choosing a block format

| Content | Format | Bits a pixel | Notes |
| --- | --- | --- | --- |
| Colour with alpha, best quality | `Bc7` | 8 | Eight modes and partitions; the default choice for colour on any GPU from Direct3D 11 on |
| Colour, smallest | `Bc1` | 4 | 5-6-5 colour, alpha only on or off |
| Colour with smooth alpha, old hardware | `Bc3` | 8 | BC1 colour and an interpolated alpha block |
| One channel (height, roughness, masks) | `Bc4` | 4 | The raster's gray |
| Normal maps | `Bc5` | 8 | Red and green; the shader rebuilds blue |
| HDR (light maps, skies) | `Bc6h`, `Bc6hSigned` | 8 | Half-float RGB without alpha |

Colour textures are usually sRGB: set `DdsWriterOptions.Srgb` so the GPU decodes them to linear light when it samples, and the
mips are averaged in linear light as they should be. Normal, height and other data maps stay plain UNORM.

`Effort` trades encoding time for quality, from 0 to 6 like WebP's. The default 4 is meant for shipping textures; 0 to 2 suit
quick previews and iteration. The bytes are the same for any number of threads.

## Mips

Save writes the full mip chain by default. Set `MipLevels = 1` for user-interface images and anything always drawn at its own
size, or a number for a shorter chain. A texture loaded from DDS and saved again unchanged keeps the mips it had, so a tool that
edits only metadata, or converts between headers, does not blur hand-made mips.

## Recipes

### Turn a PNG into a texture

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-save)]

### Read every face, slice and mip

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-surfaces)]

### Make a normal map texture and read it back with Z

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-normal-map)]

### Compress an HDR image

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-hdr)]
