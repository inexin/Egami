# Hardware video

Egami.Video.Hardware decodes and encodes video on the GPU's video engine, the fixed-function block that NVIDIA, AMD and
Intel GPUs carry next to their shader cores. It reaches the engine through Vulkan Video, the cross-vendor video API of the
Vulkan driver, on Windows and Linux. A hardware encoder runs many times faster than a software one and leaves the CPU almost
idle; the price is bigger files at the same quality than Egami's own encoders at their default effort, and output that
differs between GPU models and driver versions.

The package is a preview. It finds the hardware, reports what it can do, decodes and encodes H.264 and HEVC, and plugs into
`MediaContext` so that readers use the GPU where it can and the software codecs elsewhere. Egami.Video's software codecs
stay the default: hardware is used only when an app asks for it.

## What it needs

- A GPU and driver with Vulkan 1.3 and the Vulkan Video extensions (`VK_KHR_video_queue` and the decode or encode queue
  and codec extensions). Current NVIDIA, AMD and Intel drivers on Windows and Linux have them.
- A 64-bit process on Windows or Linux.
- Nothing to install with the app. Egami ships no native code: the package calls the Vulkan loader the graphics driver
  installs (`vulkan-1.dll` on Windows, `libvulkan.so.1` on Linux) through one exported function and the function pointers it
  hands back, with the managed Vortice.Vulkan bindings that NuGet brings along. On a machine without that loader, such as a server without a GPU or a CI runner, hardware video is simply not
  available.

## Is hardware video available?

`HardwareVideoCodecs.IsAvailable` answers for a codec, by its `CodecIds` value, and a `HardwareDirection`. The first call asks
the driver and keeps the answer for the life of the process. It never throws: without a GPU, a loader or Vulkan Video it
returns false.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#available)]

## Devices and profiles

`HardwareVideoCodecs.GetDevices` lists every GPU with video hardware Egami can use, as `HardwareVideoDevice`s: the name,
the `HardwareDeviceKind` (integrated, discrete, virtual), the PCI vendor and device ids, the Vulkan version and the driver.
Each device lists its `HardwareVideoProfile`s: the codec, the direction, the profile (H.264 High to decode; High, Main and
Baseline to encode; HEVC Main and Main 10), the bit depth, the smallest and largest picture, the highest level and how many
reference pictures a picture can use. Encoder profiles add the `HardwareRateControl` modes (constant quantiser, constant
bit rate, variable bit rate), the number of quality levels, the highest bit rate and how many references P and B pictures
can use: an encoder whose `MaxBackwardReferences` is 0 writes no B pictures. When the list is empty,
`HardwareVideoCodecs.UnavailableReason` says why.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#devices)]

On a GeForce GTX 1070 with driver 582.66 it prints, among others:

```text
NVIDIA GeForce GTX 1070 (Vulkan 1.4.312, NVIDIA 582.66)
  H.264 decode High 8-bit, 48x16 to 4096x4096, level 5.2, 16 references
  HEVC decode Main 10 10-bit, 144x144 to 8192x8192, level 6.0, 16 references
  H.264 encode High 8-bit, 48x32 to 4096x4096, level 5.2, 16 references, rate control ConstantQp, ConstantBitrate, VariableBitrate, 5 quality levels, P 1 / B 1
  HEVC encode Main 8-bit, 96x32 to 4096x4096, level 6.2, 15 references, rate control ConstantQp, ConstantBitrate, VariableBitrate, 5 quality levels, P 1 / B 0
```

AV1 hardware (newer GPUs) is not listed yet. Every profile is 4:2:0.

## Encoding H.264

`HardwareVideoCodecs.H264` is a `VideoCodec` like Egami's software H.264 codec: hand it to `VideoWriterOptions.Codec`, or
call its `CreateEncoder`, with the same `VideoEncoderOptions` and `H264EncoderOptions`. It writes 8-bit 4:2:0 in the
Constrained Baseline, Main and High profiles, in MP4, Matroska or a raw `.h264` stream, and takes frames as Egami's encoders
do: YUV frames as they are, RGB frames converted to BT.601.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#encode)]

How the options map to the hardware:

- `Quality` sets a constant quantiser on the software encoder's scale (Quality 60 is QP 17), with I and B pictures spaced
  from P pictures as the driver prefers. `Quantizer` sets the QP directly.
- `RateControl` `Bitrate` (variable) and `ConstantBitrate` hand `Bitrate`, `MaxBitrate` and `BufferSize` to the driver's
  own rate control. `FirstPassStats` is refused: the hardware has no two-pass mode.
- `Effort` picks the driver's quality level: effort 4, the default, its best; lower efforts its faster levels.
- `KeyframeInterval` and `EncodeFrameOptions.ForceKeyframe` place IDR pictures; scene cuts don't start one.
- B-frames are off unless `H264EncoderOptions.BFrames` asks for them. Some GPUs predict B pictures from the earlier
  reference only, so that they cost more than P pictures: on a GeForce GTX 1070 two B-frames make files 10 to 40 % bigger at
  the same quality.
- `LatencyMode.Realtime` tunes the driver for low latency and writes no B pictures.
- `Level`, `Entropy` and `Slices` work as on the software encoder; the driver writes the SPS and PPS and may change the
  entropy coder.

What to expect, measured on a GeForce GTX 1070 (driver 582.66) with 720p and 1080p test clips at constant QP: the GPU encodes
about 300 frames per second at 1080p and 550 to 600 at 720p, with the CPU mostly idle, against 12 to 55 frames per second
for Egami's software encoder. At the same quality its files are 8 to 17 % smaller than the software encoder's at effort 0
and 15 to 70 % bigger than at effort 4, its default. Other GPUs differ: newer video engines compress better.

## Decoding H.264

`HardwareVideoCodecs.H264` also decodes. Add it to a `MediaContext` with `WithCodec`, after `WithH264`: it takes the place
of the software H.264 codec, and `VideoReader` then decodes on the GPU. Keep the software codec where `IsAvailable` says
there is no hardware decoder.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#decode)]

The stream is read with Egami's own H.264 parser, which works out picture order, reference marking and output order; the GPU
decodes each picture and it is copied back to memory. The frames are the same, sample for sample, as those of Egami's
software decoder: every progressive 8-bit 4:2:0 clip of Egami's H.264 test set matches it. Frames come out cropped as
`YuvRaster<byte>`, in output order, with the timestamps and color the software decoder gives.

The hardware decoder takes progressive 8-bit 4:2:0 streams in the Constrained Baseline, Main and High profiles. Field
pictures, MBAFF frames, monochrome, 4:2:2 and 10-bit streams throw `UnsupportedFormatException` at their first picture; use the
software decoder for those.

What to expect on a GeForce GTX 1070: about 200 frames per second at 1080p and 450 at 720p, on streams of 60 to 220 Mbit/s,
against 9 frames per second at 1080p for Egami's software decoder on one thread and 18 to 21 on all of a 12-core CPU.

## Encoding HEVC

`HardwareVideoCodecs.Hevc` encodes HEVC the same way, with `VideoEncoderOptions` and `HevcEncoderOptions` as Egami's
software HEVC encoder takes them. It writes 4:2:0 in the Main and Main 10 profiles, in MP4, Matroska or a raw `.h265`
stream. As with the software encoder, `HevcProfile` picks the profile, and without one frames of 16-bit planes are coded as
Main 10 and the rest as Main; 8-bit frames coded as Main 10 are shifted up. A GPU without Main 10 encoding throws
`NotSupportedException`.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#encodehevc)]

The options map as for H.264: `Quality` is a constant QP on the software HEVC encoder's scale (Quality 60 is QP 17), the
bitrate modes use the driver's rate control, `MinQuantizer` and `MaxQuantizer` bound the QP, and `Effort` picks the
driver's quality level. B-frames come only where `HevcEncoderOptions.BFrames` asks and the GPU codes B pictures; a GeForce
GTX 1070 codes none. The driver writes the VPS, SPS and PPS from Egami's values, with coding tree blocks of the largest
size the GPU codes, AMP, SAO and temporal motion vector prediction on. Pictures smaller than the GPU's smallest are coded
at its smallest size and cropped.

What to expect on a GeForce GTX 1070 at constant QP: about 150 frames per second at 1080p and 300 to 570 at 720p at the
default effort 4, and two to three times that at effort 0, against 6 to 33 frames per second for Egami's software HEVC
encoder. Its files are 25 to 67 % smaller than the software encoder's at effort 0 at the same quality, and from 17 %
smaller to 25 % bigger than at effort 4. Main 10 runs about 10 % slower, with files 23 to 39 % bigger than the software
encoder's Main 10 at effort 4.

## Decoding HEVC

`HardwareVideoCodecs.Hevc` decodes 4:2:0 streams in the Main and Main 10 profiles. Add it after `WithHevc`, as for H.264:

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#decodehevc)]

Egami's own HEVC parser works out picture order, the reference picture sets and output order; the GPU decodes each
picture. The frames are the same, sample for sample, as those of Egami's software decoder, on every 4:2:0 Main and Main 10
clip of Egami's HEVC test set, wavefronts, slices, open GOPs, lossless and weighted prediction among them: 8-bit
streams as `YuvRaster<byte>`, Main 10 as `YuvRaster<ushort>` holding 10 bits, with the timestamps, color and HDR metadata
the software decoder gives. Monochrome, 4:2:2, 4:4:4 and 12-bit streams throw `UnsupportedFormatException` at their first
picture.

What to expect on a GeForce GTX 1070: about 430 frames per second for a 720p 8-bit stream and 335 for Main 10, against
about 21 for Egami's software decoder on one thread and 80 on all of a 12-core CPU.

## Using the GPU wherever it can

`WithHardwareCodecs`, an extension method in `HardwareMediaContextExtensions`, registers the hardware codecs in a `MediaContext`
in front of the software ones, so that readers decode H.264 and HEVC on the GPU without any other change. Call it after
`WithH264` and `WithHevc`: it wraps the codecs registered for those ids and keeps them for whatever the GPU cannot do. A track
the hardware turns down up front (a bit depth or chroma format it does not decode), scaled decoding, and a stream the hardware
refuses at its first picture go to the software decoder; in the last case the packets sent so far are sent to it again. The
frames are the same either way. On a machine without a GPU or driver the context is returned unchanged.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#withhardware)]

Encoding stays in software unless `HardwareUse.All` (or `Encode`) asks for the GPU, since hardware output is bigger at the
same quality and differs between GPUs. With it, the context's codec encodes on the GPU and moves to the software encoder for
first-pass statistics and for profiles, sizes, levels or rate control the GPU cannot code.

## The hwprobe command

`egami-video hwprobe` prints the devices and profiles `GetDevices` lists, or why there are none; `--json` writes them as JSON.

## Errors

When the driver refuses or fails an operation, the package throws `HardwareVideoException`, an `EgamiException` whose
`ResultCode` holds the driver's result (a negative `VkResult`, such as -4 when the driver reset the GPU). The probe itself
never throws.

## Troubleshooting

- On Windows, overlay layers that games install (Steam, Epic, GOG Galaxy) hook every Vulkan app. If the probe is slow or
  fails, set the environment variable `VK_LOADER_LAYERS_DISABLE=~implicit~` to turn them off for the process.
- Set `EGAMI_VULKAN_VALIDATION=1` to turn on the Khronos validation layer when the Vulkan SDK is installed: it reports misuse
  of the API on the console or in the debugger's output.
