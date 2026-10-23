# Egami.Video

A fully managed, MIT-licensed video library for .NET 10, built on [Egami](https://github.com/inexin/Egami). Video frames are
Egami rasters, so resizing, color conversion and drawing from Egami work on video with no copying.

Version 0.1 (V1) covers:

- AVI read and write, including files over 1 GB (OpenDML).
- Matroska read and write; the reader also accepts WebM.
- Motion JPEG decode and encode on top of Egami's JPEG codec, with planar YUV frames (`YuvRaster`) or RGB rasters.
- Audio and other tracks copied through untouched, so remuxing keeps the sound.
- Seeking to the nearest keyframe, from any `Stream`.

Version 0.2 (V2) adds VP8 decode, WebM alpha, IVF and exact seeking. Version 0.3 (V3) adds VP9 decode in all four profiles
(8, 10 and 12 bit; 4:2:0, 4:2:2, 4:4:4 and RGB), equal to libvpx frame for frame, from WebM, Matroska and IVF. Version 0.4
(V4.1) adds MP4 and QuickTime. Version 0.5 (V4.2) adds AV1 decode (8, 10 and 12 bit, 4:0:0 to 4:4:4, film grain), equal to
libaom frame for frame, from WebM, Matroska, MP4 and IVF.

Audio (V5) adds PCM and FLAC decode and encode, Opus, Vorbis and MP3 decode, and WAV, FLAC, Ogg and MP3 files.

Royalty-free codecs only (MP3's patents have expired). No native code, no wrapped C or C++ libraries.

## Read frames

```csharp
using var reader = VideoReader.Open("camera.avi");
foreach (VideoFrame<Rgb8> frame in reader.ReadFrames<Rgb8>())
{
    using (frame)
        frame.Raster.Save($"frame-{frame.Timestamp.Ticks}.png");
}
```

## Write a clip

```csharp
using var writer = VideoWriter.Create("out.mkv",
    new VideoWriterOptions { Codec = VideoCodecs.MotionJpeg, FrameRate = new Rational(30, 1), Quality = 85 });
foreach (Raster<Rgb8> image in images)
    writer.WriteFrame(image);
```

## Remux without decoding

```csharp
using var demuxer = MediaFile.OpenDemuxer("in.avi");
using var muxer = MediaFile.CreateMuxer("out.mkv", demuxer.Tracks);
while (demuxer.ReadPacket() is { } packet)
    using (packet) muxer.WritePacket(packet);
```

## Documentation

The [Video guide](https://inexin.github.io/Egami/v1/articles/guides/video.html) and the API reference are on Egami's site.
Egami.Video is built and tested in the [Egami repository](https://github.com/inexin/Egami).

## License

MIT. See [LICENSE](https://github.com/inexin/Egami/blob/main/LICENSE) and
[THIRD-PARTY-NOTICES.md](https://github.com/inexin/Egami/blob/main/THIRD-PARTY-NOTICES.md).
