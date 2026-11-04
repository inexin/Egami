# KTX and KTX 2

KTX (Khronos Texture) is the texture container of OpenGL, Vulkan and glTF: a header that names the GPU format, then every mip
level of every array layer, cube face and depth slice. KTX 1.1 (.ktx) names OpenGL formats; KTX 2.0 (.ktx2) names Vulkan formats,
describes its colour in a data format descriptor and can compress each mip level again with Zstandard or zlib. Both read, and KTX 2 writes, through
the Egami.Formats.Extended package: register them with `WithKtx()` and `WithKtx2()`, or `WithExtendedFormats()`
([Extended formats](extended.md)). They are two formats, `KtxFormat` and `Ktx2Format`, with one set of options and details.

| | |
| --- | --- |
| Reads | KTX 1.1 in both byte orders and KTX 2.0, uncompressed or Zstandard and zlib supercompressed: the 8, 16 and 32-bit UNORM, SNORM, sRGB, UINT, SINT and float formats, BGRA, the 565, 5551 and 4444 packed formats, A2B10G10R10, B10G11R11 and E5B9G9R9 floats, BC1 to BC7 with BC6H unsigned and signed, ETC1, ETC2 (RGB8, RGB8A1, RGBA8, UNORM and sRGB), EAC R11 and RG11 (unsigned and signed), ASTC in all 14 2D footprints, UNORM, sRGB and HDR (SFLOAT), and Basis Universal: ETC1S (BasisLZ, with or without an alpha slice, video included), UASTC LDR 4 × 4 and UASTC HDR 4 × 4. 1D and 2D textures, arrays, cube maps and volume textures, with their mips |
| Writes | KTX 2.0 only: 8-bit R, RG, RGB, RGBA and BGRA (UNORM and sRGB), 16-bit UNORM R, RG, RGB and RGBA, half-float RGBA, float R, RGB and RGBA, UINT and SINT in 8, 16 and 32 bits, D16 and D32 depth, A2B10G10R10, B10G11R11, E5B9G9R9 and the 565, 1555 and 4444 packed formats (not SNORM), BC1 to BC7 with BC6H, ETC1, ETC2, EAC (unsigned and signed), ASTC LDR in all 14 2D footprints, and Basis Universal ETC1S and UASTC LDR 4 × 4; mips, arrays, cube maps, Zstandard and zlib. [Texture](#textures) also writes volumes |
| Natural pixel type | As [DDS](dds.md): `Rgba8` for BC1, BC2, BC3, BC7, ETC2 RGB8A1 and RGBA8, ASTC LDR, Basis Universal with alpha and 8-bit RGBA; `Gray8` for BC4 and R8; `Rgb8` for BC5, ETC1, ETC2 RGB8, Basis Universal without alpha, RG8 and RGB8; `Gray16` for EAC R11; `Rgb16` for EAC RG11; `Rgba16` for 16-bit UNORM; `RgbaF16` for BC6H, ASTC HDR, UASTC HDR, half floats and the packed floats; `RgbaF32` for 32-bit floats; `Bgr565`, `Bgra5551` and `Bgra4444` for the packed formats. A swizzle that moves channels widens the type to its RGBA form |
| `FormatInfo` | `KtxFormat.Info`: name `KTX`, extension `.ktx`, MIME type `image/ktx`. `Ktx2Format.Info`: name `KTX2`, extension `.ktx2`, MIME type `image/ktx2` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#ktx-load)]

3D ASTC, PVRTC and the newer Basis Universal families (UASTC HDR 6 × 6, XUASTC LDR and XUBC7) are not read: a load or probe
throws `UnsupportedFormatException` that names the family, such as "KTX 2 textures in Basis Universal UASTC HDR 6x6 compression
are not supported yet (supercompression scheme 4)".

## Surfaces and mips

Load reads mip 0 of the first surface. The surfaces are the array layers, each with its cube faces (in the order +X, −X, +Y,
−Y, +Z, −Z) or depth slices; `RasterInfo.ImageCount` counts them. LoadAll gives each at mip 0, and with
`KtxReaderOptions.IncludeMipLevels` every mip after the surface it belongs to. `KtxReaderOptions.Surface` and
`KtxReaderOptions.MipLevel` pick one; for a volume, depth slice `Surface` at mip `m` is slice `Surface >> m` of that mip.
`ReaderOptions.MinimumSize` (without a `Region`) picks the smallest mip that still covers the size.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#ktx-surfaces)]

A KTX 2 file stores its smallest mip first, and a supercompressed level is inflated whole into a pooled buffer before its rows
are read; an uncompressed level streams a row of pixels or blocks at a time, as DDS does.

## Orientation and swizzle

The `KTXorientation` key says which way the rows and columns run: `rd` (right, down) is the default, and OpenGL tools often
write `ru`, bottom row first. Load turns the texture to `rd`, so it comes out upright as other formats do. The `KTXswizzle` key
says where each channel comes from (`rgba`, `rrr1` for a gray texture stored as red, `rg01`); load applies it. Set
`KtxReaderOptions.KeepStoredLayout` to read the pixels as stored. `KtxDetails.Orientation` and `KtxDetails.Swizzle` keep the
keys either way, so a renderer can apply them itself. A malformed key is ignored with an `OrientationIgnored` or
`SwizzleIgnored` warning.

## Values

Block formats decode as DDS's do, with the same shared decoders: BC4, BC5, BC6H and BC7 exactly, BC1 to BC3 from the exact
interpolation of their endpoints. `BC1_RGB` formats load opaque. ETC1, ETC2 and EAC decode exactly as their specification
(the Khronos Data Format Specification) says; an ETC1 file decodes with the ETC2 decoder, which reads every valid ETC1 block the
same. EAC's 11-bit values widen to 16 bits by bit replication, and signed EAC maps −1 to 1 onto 0 to 65535. ASTC decodes as ARM's
astcenc does, bit for bit: LDR blocks to 8 bits per channel (sRGB files interpolate their 8-bit endpoints as the sRGB decode mode
says), HDR blocks to half floats, an illegal block to magenta (LDR) or NaN (HDR), and blocks of any of the 14 2D footprints. Two-channel formats load with blue 0;
`KtxReaderOptions.ReconstructNormalZ` rebuilds blue as a normal map's Z. SNORM, SINT and UINT formats map as DDS's do. A KTX 1
file written big-endian is swapped by its `glTypeSize`, and the 4-byte row padding of KTX 1 uncompressed data is skipped.

## Basis Universal

Basis Universal textures decode straight to pixels, bit for bit as basisu's own unpacker does, with no detour through a GPU
format. An ETC1S file (Vulkan format 0, BasisLZ supercompression) keeps its endpoint and selector codebooks and Huffman tables
in the file's global data, and each image is a slice of codebook references; a second slice per image, when the descriptor has
one, is the alpha (its green channel). An ETC1S video (the `KTXanimData` key) codes later frames against the one before, so a
P-frame surface decodes from the last I-frame up. A UASTC LDR file (descriptor colour model 166) is 4 × 4 blocks of 16 bytes in
any of UASTC's 19 modes, often supercompressed with Zstandard; the reserved mode decodes to magenta, as basisu's does. A UASTC
HDR 4 × 4 file (colour model 167) holds ASTC HDR blocks and decodes as ASTC HDR does, to `RgbaF16`.

The descriptor says whether there is alpha: ETC1S with an alpha slice, and UASTC whose sample is RGBA or RRRG, load as
`Rgba8`; the rest load as `Rgb8`. `KtxDetails.FormatName` names the family, the channels and the transfer, such as
`ETC1S_RGBA_SRGB` or `UASTC_4x4_RGB_UNORM`, and `KtxDetails.VkFormat` is 0 for ETC1S and UASTC LDR. A lenient read of a BasisLZ level that does
not decode keeps the blocks decoded so far and leaves the rest black, with `LevelDamaged`; a strict read throws.

## Colour, alpha and details

A KTX 2 file's data format descriptor names its colour primaries and transfer function. sRGB transfer with BT.709 primaries is
tagged `ColorSpaceHint.Srgb`, linear with BT.709 `ColorSpaceHint.Linear`, and other pairs (Display P3, BT.2020 with PQ or HLG
and the rest) set `RasterMetadata.CodePoints` to their H.273 values. In a KTX 1 file only an `_SRGB` format says anything, and
is tagged sRGB. Colours the descriptor marks premultiplied are un-premultiplied on load.

`KtxDetails`, in `RasterMetadata.FormatDetails`, keeps what the file says: the Vulkan format (`VkFormat`, `FormatName`) or KTX 1's
`GlInternalFormat`, the `KtxSupercompression`, the `KtxDimension`, cube flag, layers, faces and depth, the mip count, sRGB, the
`KtxAlphaMode`, the orientation and swizzle, the key/value pairs that hold text (`KTXwriter`, `KTXglFormat` and your own), and
which surface and mip the raster is.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#ktx-details)]

## Writing

`Ktx2WriterOptions` mirrors `DdsWriterOptions`. With no options a raster is written in the uncompressed format nearest its
pixel type (`R8G8B8A8` for `Rgba8`, `R8` for `Gray8`, `R16G16B16A16_SFLOAT` for half floats and so on), with the full mip chain
and no supercompression. A raster loaded from KTX, KTX 2 or DDS keeps its file's format when the writer has it, ETC1S and UASTC
included; a block format is otherwise only chosen with `Ktx2WriterOptions.Format`, a `KtxTextureFormat`:

| Family | Formats | Encoder |
| --- | --- | --- |
| BC | `Bc1` to `Bc5`, `Bc6h`, `Bc6hSigned`, `Bc7` | The DDS writer's encoders |
| ETC | `Etc1`, `Etc2Rgb`, `Etc2RgbA1`, `Etc2Rgba`, `EacR11`, `EacRg11` and their signed forms | Mode search for ETC1, plus T, H and planar for ETC2 from effort 3 up; EAC is a table search. Effort 0 is a fast tier (about 30 megapixels a second on one thread); effort 4 matches etc2comp's quality at its effort 40 |
| ASTC | `Astc4x4` to `Astc12x12`, LDR | Partition, block mode and weight search; ASTC HDR is not encoded |
| Basis Universal | `Etc1s`, `Uastc` | ETC1S codebooks (`BasisQuality`, 1 to 255) with BasisLZ; UASTC's 19 modes with BC1 and ETC1 transcoder hints and optional RDO (`UastcRdo`) |

`Effort` runs 0 (fastest) to 6 (best) and defaults to 4. `Srgb` picks a format's _SRGB form; left `null`, it follows the
source file, or else the raster's tag: an untagged raster is sRGB, as Egami reads it, and a linear one is not. Formats with no
sRGB form write UNORM. `MipLevels` is 0 for the full chain, made with `MipChain` (premultiplied, in linear light for sRGB), or
that many; integer and depth formats write mip 0 alone unless a count is given. `Supercompression` is `Zstandard` or `Zlib` with an optional `CompressionLevel`; ETC1S is always BasisLZ. `Cube` makes
SaveAll write cube maps (six faces a cube, a cube map array for more); without it SaveAll writes a texture array.
`PremultipliedAlpha` premultiplies the colour and flags it so.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#ktx-save)]

The data format descriptor is the one KTX-Software writes for the format, with the primaries and transfer function of the
raster's tag (`ColorCodePoints` such as Display P3 or BT.2100 PQ go in as KHR_DF values). The key/value data holds
`KTXorientation` (`rd`, or `rdi` for a volume) and `KTXwriter`. Every file the writer makes in the test corpus passes
`ktx validate`, and libktx reads back the same level data; `tools/make-ktx-references.py --written` checks them again. The
output is the same for any number of threads: blocks encode on their own, ETC1S builds its codebooks deterministically and
UASTC's rate-distortion pass works in fixed bands.

Recipes:

`BasisQuality` is not basisu's `-q` scale: Egami's ETC1S at about 170 makes files the size of basisu's at 128, with slightly
higher quality, and lower qualities also trade some quality for size in the endpoints. A UASTC block's BC1 hint is set only when
the shortcut it allows stays close to a full BC1 encode, so a transcoder never takes a bad shortcut.

- **glTF and the web:** `Uastc` with `UastcRdo` 1 to 4 and `Zstandard` for colour that must look right; `Etc1s` at
  `BasisQuality` 64 to 128 for the smallest downloads. Viewers transcode both to the GPU's own format.
- **Desktop:** `Bc7` for colour, `Bc5` for normal maps, `Bc4` for masks, `Bc6h` for HDR.
- **Mobile:** `Astc6x6` (or `Astc4x4` for more quality, `Astc8x8` for less space) on recent GPUs, `Etc2Rgba` or `Etc2Rgb` where
  ASTC is missing, `EacRg11` for normal maps.

## Textures

`Texture` (namespace `Egami.Formats.Extended.Textures`) holds a whole texture: every layer, cube face and depth slice at every
mip, loaded with `Texture.Load` from DDS, KTX or KTX 2 whether or not the formats are registered. `GetImage` and `SetImage` reach
one image by mip, layer, face and slice; `GenerateMips` makes the mips again from mip 0 (2D textures and cube maps) and
`TrimMips` drops the smallest. `Save` writes DDS or KTX 2 with exactly the mips the texture holds, so a texture with hand-made
mips, or a volume, survives a round trip; the writer options choose the format as for a raster. A volume saves to either
format, an array of volumes only to KTX 2. `Texture.FromSurfaces` and `Texture.FromSlices` build one from rasters.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#texture)]

## Streaming and damaged files

Warnings start `Ktx.` for a KTX 1 file and `Ktx2.` for a KTX 2 file. A lenient read of a file that ends early keeps the rows
it has and leaves the rest black with `LevelShort` or `DataMissing`; a supercompressed level that does not inflate reads as
black with `LevelDamaged`; a header that claims more mips than its size allows is read with the mips it can have and
`MipCountRepaired`; a KTX 1 `imageSize` word that disagrees with the format gets `ImageSizeWrong`, and the format's size is
used; damaged key/value data or descriptor is skipped with `KeyValueDamaged` or `DescriptorDamaged`. A strict read throws
`CorruptDataException` for each. Probe reads the headers, the level index, the descriptor and the key/value data, never the
pixels.
