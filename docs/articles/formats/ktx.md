# KTX and KTX 2

KTX (Khronos Texture) is the texture container of OpenGL, Vulkan and glTF: a header that names the GPU format, then every mip
level of every array layer, cube face and depth slice. KTX 1.1 (.ktx) names OpenGL formats; KTX 2.0 (.ktx2) names Vulkan formats,
describes its colour in a data format descriptor and can compress each mip level again with Zstandard or zlib. Both read through
the Egami.Formats.Extended package: register them with `WithKtx()` and `WithKtx2()`, or `WithExtendedFormats()`
([Extended formats](extended.md)). They are two formats, `KtxFormat` and `Ktx2Format`, with one set of options and details.

| | |
| --- | --- |
| Reads | KTX 1.1 in both byte orders and KTX 2.0, uncompressed or Zstandard and zlib supercompressed: the 8, 16 and 32-bit UNORM, SNORM, sRGB, UINT, SINT and float formats, BGRA, the 565, 5551 and 4444 packed formats, A2B10G10R10, B10G11R11 and E5B9G9R9 floats, BC1 to BC7 with BC6H unsigned and signed, ETC1, ETC2 (RGB8, RGB8A1, RGBA8, UNORM and sRGB), EAC R11 and RG11 (unsigned and signed), and ASTC in all 14 2D footprints, UNORM, sRGB and HDR (SFLOAT). 1D and 2D textures, arrays, cube maps and volume textures, with their mips |
| Writes | No |
| Natural pixel type | As [DDS](dds.md): `Rgba8` for BC1, BC2, BC3, BC7, ETC2 RGB8A1 and RGBA8, ASTC LDR and 8-bit RGBA; `Gray8` for BC4 and R8; `Rgb8` for BC5, ETC1, ETC2 RGB8, RG8 and RGB8; `Gray16` for EAC R11; `Rgb16` for EAC RG11; `Rgba16` for 16-bit UNORM; `RgbaF16` for BC6H, ASTC HDR, half floats and the packed floats; `RgbaF32` for 32-bit floats; `Bgr565`, `Bgra5551` and `Bgra4444` for the packed formats. A swizzle that moves channels widens the type to its RGBA form |
| `FormatInfo` | `KtxFormat.Info`: name `KTX`, extension `.ktx`, MIME type `image/ktx`. `Ktx2Format.Info`: name `KTX2`, extension `.ktx2`, MIME type `image/ktx2` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#ktx-load)]

3D ASTC, PVRTC and Basis Universal (ETC1S with BasisLZ, and UASTC) are not read: a load or probe throws `UnsupportedFormatException`
that names the family, such as "KTX 2 textures in Basis Universal UASTC compression are not supported yet".

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

## Streaming and damaged files

Warnings start `Ktx.` for a KTX 1 file and `Ktx2.` for a KTX 2 file. A lenient read of a file that ends early keeps the rows
it has and leaves the rest black with `LevelShort` or `DataMissing`; a supercompressed level that does not inflate reads as
black with `LevelDamaged`; a header that claims more mips than its size allows is read with the mips it can have and
`MipCountRepaired`; a KTX 1 `imageSize` word that disagrees with the format gets `ImageSizeWrong`, and the format's size is
used; damaged key/value data or descriptor is skipped with `KeyValueDamaged` or `DescriptorDamaged`. A strict read throws
`CorruptDataException` for each. Probe reads the headers, the level index, the descriptor and the key/value data, never the
pixels.
