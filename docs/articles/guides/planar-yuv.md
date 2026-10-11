# Planar YUV

A `YuvRaster` holds a picture as separate luma (Y) and chroma (U, V) planes, plus an optional alpha plane: the way video codecs,
AVIF, lossy WebP and JPEG store it. Use it to take frames from a decoder or a camera, to scale video before converting it, and to
read and write JPEG without the RGB step. Everything else is done on a `Raster` after conversion.

## Creating and wrapping

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#create)]

Each plane is a `YuvPlane<TSample>`. `YuvRaster<byte>` holds 8-bit samples; `YuvRaster<ushort>` holds 10-, 12- or 16-bit samples, stored low-bit aligned (a 10-bit
sample is 0 to 1023), as libvpx, dav1d and libavif hand them out. Chroma planes are half the luma width (4:2:0 and 4:2:2) and half
the height (4:2:0), rounded up; 4:0:0 has no chroma at all. The planes come from the context's memory source in one block, so
pooling, `SafetyLimits` and the usage counters cover them.

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#planes)]

Frames a decoder or the operating system already holds are wrapped without a copy:

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#wrap)]

## What the samples mean

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#color)]

| Part | Values | Notes |
| --- | --- | --- |
| `YuvMatrix` | `Bt601`, `Bt709`, `Bt2020Ncl`, `Identity` | Kr and Kb from ITU-T H.273. `Identity` stores G, B, R as Y, U, V (AV1 lossless) |
| `YuvRange` | `Full`, `Limited` | Limited is luma 16 to 235 and chroma 16 to 240 at 8 bits, scaled for more |
| `ChromaSiting` | `Center`, `Left`, `TopLeft` | JPEG and WebP center chroma; BT.601 and BT.709 video put it on the left column; BT.2020 on the top-left sample |

There is no default guess: every load, wrap and conversion states a `YuvColor`. `YuvColor.TryFromH273` reads the
`matrix_coefficients` code and full-range flag that AV1, VP9, HEVC and CICP boxes carry, and refuses codes Egami does not know.

## Converting

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#convert)]

Conversion is within one level of an exact double-precision implementation of H.273, at every depth, and identical for at least
99% of samples. RGB comes out in the picture's own transfer function: BT.709 video gives BT.709-encoded RGB, PQ gives PQ-encoded
RGB. Linearizing or tone mapping is a separate step (see [color management](color-management.md)).

Chroma is filtered by its siting both ways: bilinear upsampling (the 3:1 triangle filter for centered chroma, the average between
co-sited samples otherwise), and averaging that matches the siting on the way back. The fast options repeat or pick samples instead:

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#options)]

A player converts every frame into the same raster; after the first frame nothing is allocated:

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#reuse)]

## Interleaved layouts

Media Foundation, MediaCodec, GPU decoders and webcams hand out NV12, NV21, P010, P016, YUYV or UYVY. One SIMD pass copies them
to and from the planes:

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#packed)]

## JPEG without the RGB step

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#jpeg)]

`YuvRaster.Load` gives a YCbCr JPEG's planes exactly as libjpeg-turbo's raw output does: 4:2:0, 4:2:2 or 4:4:4 at the file's own
sampling (other samplings are replicated to 4:4:4), gray as 4:0:0, also at reduced scale. CMYK, YCCK and RGB JPEGs, and formats
without planes, throw `NotSupportedException`: load those with `Raster.Load`. Saving 8-bit BT.601 full-range planes as JPEG
encodes them straight, with the same bytes as libjpeg-turbo's encoder from planes.

Converting loaded planes with `ToRaster` may differ by one level, in a small share of pixels, from `Raster.Load`, which keeps the
JPEG decoder's own libjpeg-turbo color code.

## Crop and resize

[!code-csharp[](../../../tests/Egami.DocSamples/PlanarYuv.cs#operations)]

`Resize` runs each plane through the same filters as `RasterOps.Resize` and clamps to the bit depth; the chroma format, siting and
color stay. `Crop` must start on a chroma sample, so chroma is copied exactly.
