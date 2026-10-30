# DDS

DDS (DirectDraw Surface, .dds) is the texture container of Direct3D and most game engines: a header, then every mip level of
every face, array slice or depth slice back to back, uncompressed or in 4 × 4 blocks of BC1 to BC7. It reads through the
Egami.Formats.Extended package: register it with `WithDds()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | The legacy header (DXT1 to DXT5, ATI1 and ATI2, bit masks, luminance, alpha, P8 and A8P8 palettes, YUY2 and UYVY, the D3D9 float and bump formats) and the DX10 header's DXGI formats: BC1 to BC7 with BC6H unsigned and signed, UNORM, SNORM, sRGB, float, UINT and SINT formats, R10G10B10A2, R11G11B10 and R9G9B9E5 floats, the 565, 5551 and 4444 packed formats, depth and stencil, and the packed and planar video formats (AYUV, Y410, Y416, YUY2, Y210, Y216, NV12, P010, P016, NV11, P208, V208, V408). 2D textures, arrays, cube maps and volume textures, with their mips |
| Writes | No, not yet |
| Natural pixel type | The nearest to the stored format: `Rgba8` for BC1, BC2, BC3, BC7 and 8-bit RGBA; `Gray8` for BC4 and R8; `Rgb8` for BC5 and R8G8; `Rgba16` for 16-bit UNORM; `RgbaF16` for BC6H, half floats and the packed floats; `RgbaF32`, `RgbF32` or `GrayF32` for 32-bit floats; `Rgb10A2`; `Bgr565`, `Bgra5551` and `Bgra4444` for the packed formats |
| `FormatInfo` | `DdsFormat.Info`: name `DDS`, extension `.dds`, MIME types `image/vnd-ms.dds` and `image/x-dds` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-load)]

## Surfaces and mips

Load reads mip 0 of the first surface. The surfaces are the faces of a cube map (in the order +X, −X, +Y, −Y, +Z, −Z, each cube
after the one before), the slices of a texture array, or the depth slices of a volume texture; `RasterInfo.ImageCount` counts
them. LoadAll gives each at mip 0, and with `DdsReaderOptions.IncludeMipLevels` every mip after the surface it belongs to.
`DdsReaderOptions.Surface` and `DdsReaderOptions.MipLevel` pick one; for a volume, depth slice `Surface` at mip `m` is slice
`Surface >> m` of that mip. `ReaderOptions.MinimumSize` (without a `Region`) picks the smallest mip that still covers the size,
so a thumbnail reads a fraction of the file.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-surfaces)]

## Values

Block formats decode as the Direct3D 11 specification defines them: BC4, BC5, BC6H and BC7 exactly, and BC1 to BC3 colours
from the exact interpolation of their 5- and 6-bit endpoints, rounded once. A BC6H or BC7 block with a reserved mode decodes
to black (BC7 transparent black).

- Two-channel formats (BC5, R8G8, R16G16 and their float forms) load with blue 0; `DdsReaderOptions.ReconstructNormalZ`
  rebuilds blue as a normal map's Z, sqrt(1 − x² − y²).
- SNORM formats map −1 to 1 onto 0 to the type's maximum; SINT formats are offset by half the range, so their order is kept.
  UINT formats keep their values; 32-bit integers load as floats holding the integer.
- BC6H signed keeps negative values in `RgbaF16`. R11G11B10 and R9G9B9E5 floats convert to half floats exactly.
- Depth formats load the depth as `GrayF32` (D24 scaled to 0 to 1); the stencil-only views (X24_G8, X32_G8X24) load the
  stencil as `Gray8`.
- YUV formats convert to RGB with the BT.601 studio-range integer formulas, as DirectXTex does; chroma is repeated, not
  interpolated.
- Legacy bit masks scale each field to 8 or 16 bits, rounding to nearest. The 10:10:10:2 masks are read with red and blue
  swapped, as D3DX wrote them.

## Colour, alpha and details

DDS has no ICC profile. Values load as stored; an `_SRGB` format (or a legacy header NVTT marked sRGB) is tagged
`ColorSpaceHint.Srgb`, and anything else is untagged. Premultiplied files (DXT2, DXT4, or `DdsAlphaMode.Premultiplied` in the
DX10 header) are un-premultiplied on load, as TGA's are.

`DdsDetails`, in `RasterMetadata.FormatDetails`, keeps what the header says: the DXGI format (`DxgiFormat`, `FormatName`), the
legacy FourCC, the `DdsDimension`, cube flag, array size and depth, the mip count, the `DdsAlphaMode`, whether the format is
sRGB, and which surface and mip the raster is.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-details)]

## Streaming and damaged files

The reader skips to the surface it wants and decodes its rows in order, a row of blocks at a time, so `RasterDecoder` streams
it; planar video surfaces keep chroma after luma and are read whole. A lenient read of a file that ends early keeps the rows it
has and leaves the rest transparent black with `Dds.RowsMissing`; a header that claims more mips than its size allows is read
with the mips it can have and `Dds.MipCountRepaired`. A strict read throws `CorruptDataException` for both. Xbox tiled DDS (the
XBOX header) and the palette DXGI formats in a DX10 header are not read and throw `UnsupportedFormatException`.
