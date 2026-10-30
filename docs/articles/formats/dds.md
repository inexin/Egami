# DDS

DDS (DirectDraw Surface, .dds) is the texture container of Direct3D and most game engines: a header, then every mip level of
every face, array slice or depth slice back to back, uncompressed or in 4 × 4 blocks of BC1 to BC7. It reads and writes through the
Egami.Formats.Extended package: register it with `WithDds()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | The legacy header (DXT1 to DXT5, ATI1 and ATI2, bit masks, luminance, alpha, P8 and A8P8 palettes, YUY2 and UYVY, the D3D9 float and bump formats) and the DX10 header's DXGI formats: BC1 to BC7 with BC6H unsigned and signed, UNORM, SNORM, sRGB, float, UINT and SINT formats, R10G10B10A2, R11G11B10 and R9G9B9E5 floats, the 565, 5551 and 4444 packed formats, depth and stencil, and the packed and planar video formats (AYUV, Y410, Y416, YUY2, Y210, Y216, NV12, P010, P016, NV11, P208, V208, V408). 2D textures, arrays, cube maps and volume textures, with their mips |
| Writes | Uncompressed formats (8-bit RGBA, BGRA and BGRX, R8, R8G8, A8, 16-bit R, RG and RGBA, half and float RGBA, R32 float, float RGB, R10G10B10A2, R11G11B10 and R9G9B9E5 floats, 565, 5551 and 4444) and BC1 to BC7 with BC6H unsigned and signed, with mips, texture arrays and cube maps (`DdsWriterOptions`) |
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

## Writing

Save writes one texture with its full mip chain; `DdsWriterOptions` picks the rest.

- `Format` (a `DdsTextureFormat`) is the format to write. Left `null`, a raster loaded from DDS keeps its own format when it is
  writable, and any other raster gets the uncompressed format nearest its pixel type: `Rgba8` for 8-bit colour, `R8` for 8-bit
  gray, `RgbaF16` for half floats and so on. Block formats are never picked on their own.
- `MipLevels`: 0 (the default) writes the full chain down to 1 × 1, 1 mip 0 alone, any other number that many. Mips average
  2 × 2 blocks of premultiplied pixels (`MipChain`), in linear light when the format is sRGB; an odd size takes a box resize to
  the size DDS gives that mip. A raster loaded at mip 0 and unchanged by operations since writes the file's own mips 1 and on.
- `Effort` (0 to 6, default 4, WebP's scale) is how hard the block encoders search: endpoints along the colours' principal
  axis, then least-squares refits and neighbouring endpoints as the effort rises; BC7 tries modes 6 and 1 at the lowest efforts
  and every mode, more partitions and rotations above, and BC6H adds its two-region modes from effort 3. The bytes are the
  same for any thread count.
- `Srgb` writes the _SRGB form of `Rgba8`, `Bgra8`, `Bgrx8` and BC1 to BC3; left `null`, a source that was sRGB stays sRGB.
- `PremultipliedAlpha` writes colour premultiplied by alpha: DXT2 and DXT4 for BC2 and BC3, the DX10 alpha mode otherwise.
- The legacy header, which old tools read, is written whenever the format has one and nothing needs the DX10 header (BC6H,
  BC7, sRGB, floats, R8G8, R10G10B10A2, texture arrays, premultiplied alpha outside DXT2 and DXT4); `Dx10Header` forces it.
- SaveAll writes its rasters as a texture array, or with `Cube` as cube maps, six faces a cube. Every raster has the same
  size, and cube faces are square.

BC1 keeps alpha as on or off: a pixel below half alpha becomes transparent black. BC4 and the R formats take the raster's gray,
BC5 and R8G8 its red and green. BC6H keeps no alpha; the unsigned form writes negative values as 0. The [Textures](../guides/textures.md)
guide says which format suits which content.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dds-save)]

## Streaming and damaged files

The reader skips to the surface it wants and decodes its rows in order, a row of blocks at a time, so `RasterDecoder` streams
it; planar video surfaces keep chroma after luma and are read whole. A lenient read of a file that ends early keeps the rows it
has and leaves the rest transparent black with `Dds.RowsMissing`; a header that claims more mips than its size allows is read
with the mips it can have and `Dds.MipCountRepaired`. A strict read throws `CorruptDataException` for both. Xbox tiled DDS (the
XBOX header) and the palette DXGI formats in a DX10 header are not read and throw `UnsupportedFormatException`.
