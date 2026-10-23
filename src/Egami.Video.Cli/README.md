# Egami.Video.Cli

The `egami-video` command: inspect, remux, cut, join and convert video and audio files from a shell or a build script with
[Egami.Video](https://github.com/inexin/Egami), a fully managed video library for .NET. It does the jobs people reach for
ffprobe and ffmpeg for, without native code.

```sh
dotnet tool install --global Egami.Video.Cli
egami-video info clip.mkv --packets --json
egami-video trim in.mp4 --from 1:30 --to 2:00 -o cut.mp4
egami-video frames in.webm --every 1s -o shots/{name}-{frame:0000}.png
egami-video run in.avi resize 1280x + sharpen 0.5 -o out.mkv
egami-video audio in.mkv --track a:0 -o sound.flac
egami-video ffmpeg -i in.avi -c copy out.mkv
```

- Commands: info, remux, trim, concat, segment, timestamps, extract, embed, metadata, frames, encode, run, thumbnail, scenes,
  sheet, animate, hash, compare, audio and formats.
- Reads AVI, Matroska and WebM, MP4 and QuickTime, IVF, YUV4MPEG2, raw AV1, WAV, FLAC and Ogg; decodes Motion JPEG, VP8, VP9,
  AV1, PCM, FLAC, Opus and Vorbis; copies any track as packets.
- `run` applies any of egami's image steps to every frame. `egami video ...` runs it from the `egami` tool.
- `egami-video ffmpeg` and `egami-video ffprobe` take a documented subset of FFmpeg's command lines and stop on anything else.
- `egami-video mcp` serves the commands as tools to AI assistants over the Model Context Protocol.
- Globs, batches, `-` for pipes, a progress line and `--progress` reports, Ctrl+C that leaves no partial file, and exit codes 0
  (done), 1 (a file failed), 2 (usage), 3 (a safety limit) and 130 (stopped).

The [documentation](https://inexin.github.io/Egami/v1/articles/video-cli/index.html) explains the commands. MIT licensed.
