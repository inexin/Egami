# Egami.Video

A fully managed, MIT-licensed video library for .NET 10, built on [Egami](https://github.com/inexin/Egami). Video frames are
Egami rasters, so resizing, color conversion and drawing from Egami work on video with no copying.

- Containers: MP4 and QuickTime (fragmented MP4 too), Matroska and WebM, AVI (OpenDML over 1 GB), IVF, YUV4MPEG2, Ogg, and the
  audio files WAV, FLAC, MP3, AIFF and CAF.
- Video codecs: AV1, VP9 and VP8 decode and encode (AV1 decodes equal to libaom, VP9 to libvpx), FFV1 for lossless
  archives and Motion JPEG. H.264 and HEVC decode through the opt-in packages Egami.Video.H264 and Egami.Video.Hevc.
- Audio codecs: PCM, FLAC, Opus and ALAC decode and encode; Vorbis, MP3, AC-3, AAC LC and G.711 decode; audio filters.
- Remuxing, trimming with smart cut and seeking without decoding what need not be; SRT, WebVTT and ASS subtitles.
- Editing (`VideoEdit`, `Clip`): joins, transitions, overlays, keying, layouts and titles, rendered to a file.
- Deinterlacing (`Deinterlacer`), HDR metadata, scene detection, waveforms and spectrograms.

Royalty-free codecs only in this package (the MP3 and AC-3 patents have expired). No native code, no wrapped C or C++
libraries.

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
