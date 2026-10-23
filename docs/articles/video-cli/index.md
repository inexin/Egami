# The egami-video command

`egami-video` inspects, remuxes, cuts, joins and converts video and audio files from a shell or a build script, with no C# to
write. It does the jobs people use ffprobe and ffmpeg for, with Egami.Video's fully managed code. It is the `Egami.Video.Cli`
package, a .NET tool that carries Egami.Video's version.

```sh
egami-video info in.mkv
egami-video remux in.avi -o out.mkv
egami-video trim in.webm --from 0.5 --to 1.5 -o cut.webm
egami-video frames in.mkv --every 0.5 -o shots/{name}-{frame:0000}.png
egami-video run in.avi resize 80x + sharpen 0.5 -o small.mkv
egami-video audio in.mkv -o sound.flac
```

The [command reference](commands.md) lists every option. [ffmpeg command lines](ffmpeg.md) explains `egami-video ffmpeg` and
`egami-video ffprobe`, and [egami-video for AI assistants](assistants.md) the `mcp` server.

## Installing

```sh
dotnet tool install --global Egami.Video.Cli
```

With the `egami` tool installed too, `egami video info clip.mkv` runs `egami-video info clip.mkv`: egami runs any
`egami-<name>` program on the PATH. The tool is free of reflection and builds with the trimming and Native AOT analyzers on.

## Commands

| Command | What it does | Like |
| --- | --- | --- |
| `info` | Container, duration, tracks, codecs, tags, chapters, attachments; `--packets`, `--frames`, `--headers` | `ffprobe` |
| `remux` | Copy tracks into another container | `ffmpeg -c copy` |
| `trim` | Cut from the keyframe at or before `--from` to `--to` | `ffmpeg -ss -to -c copy` |
| `concat` | Join clips with the same tracks and codecs | the concat demuxer |
| `segment` | Split every `--every` or at `--at` times, at keyframes | `-f segment` |
| `timestamps` | Shift, set a frame rate, repair timestamps | `-itsoffset`, `-r`, `setts` |
| `extract` | One track, attachments or cover art to their own files | `-map 0:a:0 -c copy`, `-dump_attachment` |
| `embed` | Add tracks, attachments and cover art; set languages and names | `-i a -i b -map 0 -map 1` |
| `metadata` | Write tags and chapters as JSON, or change them | `-f ffmetadata`, `-metadata` |
| `frames` | Frames as images; Motion JPEG frames as JPEG without decoding | `ffmpeg -i in out-%04d.png` |
| `encode` | Images, an image sequence or Y4M into a clip | `ffmpeg -framerate 30 -i %04d.png` |
| `run` | egami's steps on every frame, then encode | `-vf scale,crop,pad,transpose` |
| `thumbnail` | A representative frame | `-vf thumbnail` |
| `scenes` | Where the scenes start, and a frame of each | `select='gt(scene,0.4)'` |
| `sheet` | A contact sheet of frames with their times | `-vf tile=4x4` |
| `animate` | Animated GIF, WebP or APNG | `ffmpeg -i in.mp4 out.gif` |
| `hash` | Hashes of every frame or packet | `-f framemd5` |
| `compare` | PSNR and SSIM between clips | `-lavfi psnr`, `-lavfi ssim` |
| `audio` | Audio to WAV, FLAC or Opus; audio again while copying video | `-vn -c:a flac`, `-c:a libopus -b:a`, `-ar`, `-ac` |
| `formats` | The containers and codecs this build reads and writes | `ffmpeg -formats` |

Commands that copy packets (`remux`, `trim`, `concat`, `segment`, `timestamps`, `extract`, `embed`, `metadata`) never decode, so
they work on any codec, H.264 and AAC included. Commands that decode need a codec Egami.Video decodes: Motion JPEG, VP8, VP9, AV1
and uncompressed video, and PCM, FLAC, Opus and Vorbis audio. `encode` and `run` write Motion JPEG, VP8 or uncompressed video; `encode --codec vp8` takes
`--effort` and `--bitrate` (see [Encoding video](../guides/video-encoding.md)).

## Inspecting

`info` reads the headers without decoding. `--all` reads every packet too, for packet and keyframe counts and bit rates;
`--headers` shows the codec headers of each track's first frame; `--packets` and `--frames` list every packet and every decoded
frame.

```sh
egami-video info in.mkv --all
egami-video info in.webm --packets --track v
egami-video info in.mkv --json
egami-video info in.mp4 --format ffprobe
```

`--format` picks how it writes: `text` (the default), `json` (Egami's own schema, `docs/schemas/egami-video-info-1.json`), `csv`
for packet and frame lists, `xml` with the same fields as JSON, or `ffprobe`: JSON with the field names `ffprobe -of json` uses,
so scripts written for ffprobe read it unchanged. ffprobe lists Matroska cover art as a video stream; egami-video lists it as an
attachment.

## Tracks and times

`--track` (`-t`) picks tracks by kind and position, language or number:

| Value | Picks |
| --- | --- |
| `v`, `a`, `s`, `d` | Every video, audio, subtitle or data track |
| `a:1` | The second audio track (counted from 0) |
| `s:eng` | Subtitle tracks in English (`eng` and `en` both match `en-US`) |
| `3` | Track 3, as `info` numbers it |

Repeat it, or separate values with commas, for several. `--no-audio` (`-an`) and `--no-subtitles` (`-sn`) leave those out.

Times are seconds (`90.5`, `500ms`, `2m`), `[HH:]MM:SS[.mmm]` (`1:30`, `01:02:03.250`) or a frame number in presentation order
(`#1234`, counted from 0).

A stream copy can only start on a keyframe. `trim` starts at the video keyframe at or before `--from`, ends exactly at `--to`,
and says on standard error how far the start moved. `segment` starts each part at the first keyframe at or after its split point.

```sh
egami-video trim in.webm --from 0.5 --duration 1 -o cut.webm
egami-video segment in.mkv --every 1 -o parts/{part:000}.mkv
egami-video concat parts/001.mkv parts/002.mkv -o joined.mkv
egami-video timestamps in.mkv --shift 0.5 --track a -o later.mkv
```

## Frames and pictures

`frames` writes decoded frames in any format egami writes; `{frame}` in the output is the frame's number in the video. Motion
JPEG frames written as JPEG are the stored frames, byte for byte, without decoding.

```sh
egami-video frames in.mkv --count 4 -o shots/{frame:000}.jpg
egami-video frames in.webm --at 0.5,1.5 -o at-{frame}.png
egami-video thumbnail in.webm -o thumb.jpg --size 320x
egami-video scenes in.mkv --json
egami-video sheet in.mkv --grid 3x2 --timestamps -o sheet.png
egami-video animate in.webm --rate 10 --size 120x -o clip.gif
```

`encode` goes the other way: images in name order, an image sequence (`shots/{frame:000}.jpg` reads frame 0, 1, 2 until one is
missing), or a Y4M file or stream.

```sh
egami-video encode "shots/*.jpg" --rate 2 -o slideshow.mkv
```

## Steps on every frame

`run` takes the steps and `+` syntax of `egami run` ([steps for run](../cli/steps.md)) and applies them to every frame. The
output keeps the source codec when Egami.Video can encode it, else Motion JPEG; `--codec` chooses. Audio and subtitles are copied,
and every frame keeps its time.

```sh
egami-video run in.avi resize 80x + sharpen 0.5 -o small.mkv
egami-video run in.mkv crop 0 10 160 100 + grayscale + save gray.avi --quality 90
```

## Audio

`audio` decodes the picked audio track to WAV, Wave64, FLAC, Ogg FLAC or Opus, resampling with `--rate`, mixing with `--channels`
and converting with `--sample-format`. With a video container as output it copies the video unchanged and encodes the audio
again. `--codec` picks FLAC, PCM or Opus; .opus and WebM outputs get Opus, WAV, Wave64 and AVI get PCM, and the rest FLAC.
`--bitrate` sets Opus's bitrate for all channels together (64k a channel by default); `--level` sets FLAC's compression.

```sh
egami-video audio in.mkv -o sound.flac
egami-video audio in.webm --rate 44100 --channels stereo --sample-format s16 -o sound.wav
egami-video audio in.mkv --codec flac -o flac-audio.mkv
egami-video audio sound.flac --bitrate 96k -o sound.opus
egami-video audio in.mp4 --bitrate 64k -o opus-audio.webm
```

## Checking

`hash` writes a hash of every decoded frame (or every packet with `--packets`) in the layout of `ffmpeg -f framemd5`, and the
hashes equal ffmpeg's for the same decoded planes. `compare` measures PSNR and SSIM per plane between a reference and other
clips, and `--min-psnr` or `--min-ssim` turn it into a check that exits with 1.

```sh
egami-video hash in.webm --total
egami-video compare in.mkv small.mkv --scale --json
```

## Files, batches and pipes

Inputs are files, globs (`*`, `?`, `[abc]`, `**`, expanded the same way in every shell), folders with `--recursive`, or `-` for
standard input. Outputs are a file, a folder, a template with `{name}`, `{ext}`, `{dir}`, `{index}`, `{format}` and the
command's part (`{frame}`, `{part}`, `{scene}`, `{track}`), or `-` for standard output with `--format`. Several inputs run at
once (`--jobs`), a failed file is reported and the rest go on unless `--fail-fast`, and an output never replaces its input
without `--in-place`.

Standard input is read as it comes and standard output written as it goes, so egami-video sits between other programs without
temporary files. Matroska, WebM, IVF, Y4M and WAV stream; AVI needs a file.

```sh
egami-video remux - -o - --format mkv < in.avi > piped.mkv
```

## Progress and stopping

A long job shows a line on standard error when it is a terminal: time, percent, frames, frames per second, size, speed and time
left. `--progress <file|->` writes the same reports as ffmpeg `-progress` does, as `key=value` blocks ending in
`progress=continue` or `progress=end`, or one JSON object per line with `--progress-format json`. Batches report each file.
`--quiet` turns the line off.

Ctrl+C stops the job between packets: the output it was writing is deleted, and the exit code is 130. Outputs are written
under a temporary name and get their own name only when complete.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Done |
| 1 | A file failed (unreadable, corrupt, unwritable), or `compare` missed `--min-psnr` or `--min-ssim` |
| 2 | The command line is wrong; the message says what to change |
| 3 | An input exceeded a safety limit (`--limit`, `EGAMI_LIMITS`) |
| 130 | Stopped with Ctrl+C |
