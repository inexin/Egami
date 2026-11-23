# Hardware video

Egami.Video.Hardware decodes and encodes video on the GPU's video engine, the fixed-function block that NVIDIA, AMD and
Intel GPUs carry next to their shader cores. It reaches the engine through Vulkan Video, the cross-vendor video API of the
Vulkan driver, on Windows and Linux. A hardware encoder runs many times faster than a software one and leaves the CPU almost
idle; the price is bigger files at the same quality than Egami's own encoders at their default effort, and output that
differs between GPU models and driver versions.

The package is a preview. It finds the hardware, reports what it can do, and decodes and encodes H.264; HEVC follows in
the next previews. Egami.Video's software codecs stay the default: hardware is used only when an app
asks for it.

## What it needs

- A GPU and driver with Vulkan 1.3 and the Vulkan Video extensions (`VK_KHR_video_queue` and the decode or encode queue
  and codec extensions). Current NVIDIA, AMD and Intel drivers on Windows and Linux have them.
- A 64-bit process on Windows or Linux.
- Nothing to install with the app. Egami ships no native code: the package calls the Vulkan loader the graphics driver
  installs (`vulkan-1.dll` on Windows, `libvulkan.so.1` on Linux) through one exported function and the function pointers it
  hands back. On a machine without that loader, such as a server without a GPU or a CI runner, hardware video is simply not
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

## Errors

When the driver refuses or fails an operation, the package throws `HardwareVideoException`, an `EgamiException` whose
`ResultCode` holds the driver's result (a negative `VkResult`, such as -4 when the driver reset the GPU). The probe itself
never throws.

## Troubleshooting

- On Windows, overlay layers that games install (Steam, Epic, GOG Galaxy) hook every Vulkan app. If the probe is slow or
  fails, set the environment variable `VK_LOADER_LAYERS_DISABLE=~implicit~` to turn them off for the process.
- Set `EGAMI_VULKAN_VALIDATION=1` to turn on the Khronos validation layer when the Vulkan SDK is installed: it reports misuse
  of the API on the console or in the debugger's output.
