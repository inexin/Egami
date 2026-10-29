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
| `segment` | Split every `--every`, at `--at` times or at `--scenes`, at keyframes; `--hls` and `--dash` for streaming | `-f segment`, `-f hls`, `-f dash` |
| `timestamps` | Shift, set a frame rate, repair timestamps | `-itsoffset`, `-r`, `setts` |
| `extract` | One track, attachments or cover art to their own files | `-map 0:a:0 -c copy`, `-dump_attachment` |
| `embed` | Add tracks, attachments and cover art; set languages and names | `-i a -i b -map 0 -map 1` |
| `metadata` | Write tags and chapters as JSON, or change them | `-f ffmetadata`, `-metadata` |
| `frames` | Frames as images; Motion JPEG frames as JPEG without decoding | `ffmpeg -i in out-%04d.png` |
| `encode` | Images, an image sequence or Y4M into a clip | `ffmpeg -framerate 30 -i %04d.png` |
| `run` | egami's steps on every frame, then encode | `-vf scale,crop,pad,transpose` |
| `thumbnail` | A representative frame | `-vf thumbnail` |
| `scenes` | Where the scenes start, and a frame of each | `select='gt(scene,0.4)'` |
| `analyze` | Black, frozen, silent, cropped, interlaced and duplicate stretches, scenes, signal levels and loudness, in one pass | `blackdetect`, `silencedetect`, `ebur128` … with `-f null -` |
| `waveform` | A picture of the sound's waveform | `-lavfi showwavespic` |
| `spectrum` | A spectrogram picture | `-lavfi showspectrumpic` |
| `generate` | Test clips and sounds: bars, a pattern, solid colors, gradients, tones, noise, sweeps | `-f lavfi -i testsrc2`, `smptebars`, `sine` |
| `sheet` | A contact sheet of frames with their times | `-vf tile=4x4` |
| `animate` | Animated GIF, WebP or APNG | `ffmpeg -i in.mp4 out.gif` |
| `hash` | Hashes of every frame or packet | `-f framemd5` |
| `compare` | PSNR and SSIM between clips | `-lavfi psnr`, `-lavfi ssim` |
| `audio` | Audio to WAV, FLAC, Opus, AIFF, CAF or ALAC; audio again while copying video | `-vn -c:a flac`, `-c:a libopus -b:a`, `-ar`, `-ac` |
| `join` | Clips of any size, rate and audio fitted to one canvas and joined, with transitions | `concat` filter with `scale`, `pad`, `fps`; `xfade` and `acrossfade` |
| `fade` | Fade in from and out to a colour, picture and sound | `-vf fade -af afade` |
| `overlay` | One clip over another, with `--key` for a chroma key | `overlay`, `chromakey`, `despill` |
| `pip` | A second clip small in a corner | `scale` and `overlay` |
| `stack` | Clips side by side, above each other or in a grid | `hstack`, `vstack`, `xstack` |
| `speed` | Faster or slower, the sound's pitch kept | `setpts` with `atempo` |
| `reverse` | Backwards, picture and sound | `reverse`, `areverse` |
| `loop` | A clip several times in a row | `loop`, `aloop` |
| `freeze` | One frame held for a while, or the last one at the end | `tpad`, `freezeframes` |
| `slideshow` | Images one after another, with transitions and pan and zoom | `loop=1`, `zoompan`, `xfade` |
| `stabilize` | Take the shake out of a clip, in two passes | `vidstabdetect`, `vidstabtransform` |
| `edit` | Render an edit file: clips, transitions, layers and effects | `-filter_complex` |
| `formats` | The containers and codecs this build reads and writes | `ffmpeg -formats` |

Commands that copy packets (`remux`, `trim`, `concat`, `segment`, `timestamps`, `extract`, `embed`, `metadata`) never decode, so
they work on any codec, H.264 and AAC included. Commands that decode need a codec Egami.Video decodes: Motion JPEG, VP8, VP9, AV1
and uncompressed video, and PCM, FLAC, Opus and Vorbis audio. `encode` and `run` write Motion JPEG, VP8, VP9, AV1 or uncompressed video; `encode --codec vp8`, `vp9` or `av1` takes
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
and says on standard error how far the start moved. `segment` starts each part at the first keyframe at or after its split point;
`segment --scenes` starts each part at the keyframe at or before its scene's first frame and says on standard error how far
before, with a warning when that is over a second. `trim --smart` starts on the exact frame: it encodes again only the frames
from `--from` up to the next keyframe, matching the stream at its own quantizer, and copies the rest; `trim --reencode` encodes
the whole cut again. `segment --scenes --exact` cuts every part that way, so each starts on its scene's first frame.

```sh
egami-video trim in.webm --from 0.5 --duration 1 -o cut.webm
egami-video trim in.webm --from 0.5 --duration 1 --smart -o exact.webm
egami-video segment in.mkv --every 1 -o parts/{part:000}.mkv
egami-video segment in.mkv --scenes -o shots/{part:000}.mkv -v
egami-video concat parts/001.mkv parts/002.mkv -o joined.mkv
egami-video timestamps in.mkv --shift 0.5 --track a -o later.mkv
```

## Streaming

`remux --fragment` and `encode --fragment` write fragmented MP4: a moov with the setup only, then the media as moof and mdat
pairs, each starting at the first video keyframe after 2 seconds (`--fragment=4s` for another length, `--fragment=keyframe` for
every keyframe). Such a file is written as it goes, so it suits uploads, cloud storage and browsers' Media Source Extensions.

`segment --hls --dash` writes the audio and video tracks as CMAF segments into the folder `-o` names, cut at the first video
keyframe after `--every` (6 seconds by default), with `master.m3u8` and a playlist a track for HLS and `manifest.mpd` for DASH;
give one of the two for its playlists only. Subtitle and data tracks are left out.

```sh
egami-video remux in.mp4 --fragment -o fragmented.mp4
egami-video segment in.mp4 --hls --dash --every 2 -o stream/
```

## Frames and pictures

`frames` writes decoded frames in any format egami writes; `{frame}` in the output is the frame's number in the video. Motion
JPEG frames written as JPEG are the stored frames, byte for byte, without decoding.

```sh
egami-video frames in.mkv --count 4 -o shots/{frame:000}.jpg
egami-video frames in.webm --at 0.5,1.5 -o at-{frame}.png
egami-video thumbnail in.webm -o thumb.jpg --size 320x
egami-video scenes in.mkv --json
egami-video scenes in.mkv --fades --frame middle --series scores.csv -o shots/{scene:000}.jpg
egami-video sheet in.mkv --grid 3x2 --timestamps -o sheet.png
egami-video animate in.webm --rate 10 --size 120x -o clip.gif
```

`scenes` finds cuts with the adaptive method by default (`--method content` or `histogram`, `--threshold`); `--min-length`
(once `--min-gap`) sets the shortest scene, `--fades` names scenes that start inside a fade and lists the fades, `--flash
suppress` drops flashes instead of merging them, `--frame middle` or `last` picks the frame written for each scene, and
`--series` writes every frame's score to a CSV file.

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
egami-video run in.mkv --crop auto resize 80x -o cropped.mkv
```

`--crop auto` reads the video once to find its black bars, as `analyze -d crop` does, and crops them off before the other
steps.

## Analysis

`analyze` reads a file once and runs the detectors `--detect` (`-d`) names, or every one with `--all`: `scenes`, `black`,
`freeze`, `crop`, `interlace`, `duplicates`, `signal`, `blur`, `blockiness`, `silence`, `levels` and `loudness`. Each keeps the
measure and defaults of the ffmpeg filter it stands for, and its settings take that filter's names: `--black-min` is
blackdetect's `d`, `--silence-noise -50dB` silencedetect's `n`, `--crop-limit` cropdetect's `limit`. `--start` and `--duration`
look at part of the file. The report is text, `--json` or `--csv` (one row per range or value), and `--series file.csv` adds
per-frame and per-100 ms values: scene scores, black share, signal levels, momentary and short-term loudness.

```sh
egami-video analyze in.mkv --all
egami-video analyze in.mkv -d black,silence --black-min 0.5 --json
egami-video analyze in.mkv -d loudness --series loudness.csv
egami-video analyze in.mkv -d black --ffmpeg-log blackdetect
```

In JSON each file has `start`, `end` and `frames`, and one entry per detector: `scenes` (scene, frame, start, end, score, kind)
and `fades`; lists of `{start, end, duration}` ranges with a value for `black`, `freeze`, `duplicates` and `silence` (and
`silence.1`, `silence.2` … per channel); and objects for `crop`, `interlace`, `signal`, `levels` and `loudness`. Levels and
loudness that do not exist (silence has no peak) are `null`. `--ffmpeg-log` prints the lines ffmpeg's filters log instead,
for scripts that read them ([ffmpeg command lines](ffmpeg.md)).

`waveform` and `spectrum` draw the audio as a picture: `--size`, `--channels mixed|stacked`, a linear or dB `--scale` for the
waveform and a linear or log frequency `--scale` for the spectrum, colors, and `--legend` for a time axis and level or
frequency labels. `generate` makes test files: `pattern`, `bars`, `solid` and `gradient` pictures, with `--tone` or `--audio`
for sound, and `sine`, `silence`, `noise` and `sweep` sounds.

```sh
egami-video waveform in.mkv --legend --scale db -o wave.png
egami-video spectrum in.mkv --scale log --legend --size 1200x400 -o spectrum.png
egami-video generate bars --tone -t 10 bars.mkv
egami-video generate sweep -t 5 sweep.flac
```

## Audio

`audio` decodes the picked audio track to WAV, Wave64, FLAC, Ogg FLAC, Opus, AIFF or CAF, resampling with `--rate`, mixing with `--channels`
and converting with `--sample-format`. With a video container as output it copies the video unchanged and encodes the audio
again. `--codec` picks FLAC, PCM, Opus or ALAC (Apple Lossless); .opus and WebM outputs get Opus, WAV, Wave64, AVI, AIFF
and CAF get PCM, .m4a gets ALAC, and the rest FLAC. ALAC also goes in .mov, .mka and .caf. `--bitrate` sets Opus's bitrate
for all channels together (64k a channel by default); `--level` sets FLAC's compression (0 to 8) or ALAC's (0 fast, 1 the
default). `extract` writes ALAC tracks to .m4a and AC-3 and E-AC-3 tracks to raw .ac3 and .eac3 files.

```sh
egami-video audio in.mkv -o sound.flac
egami-video audio in.webm --rate 44100 --channels stereo --sample-format s16 -o sound.wav
egami-video audio in.mkv --codec flac -o flac-audio.mkv
egami-video audio sound.flac --bitrate 96k -o sound.opus
egami-video audio in.mp4 --bitrate 64k -o opus-audio.webm
egami-video audio sound.flac -o sound.m4a
egami-video audio sound.wav --sample-format s24 -o sound.aiff
```

## Editing

`join`, `fade`, `overlay`, `pip` and `stack` build an edit with Egami.Video's editing library (see
[Video editing](../guides/video-editing.md)) and encode it again, so their clips may differ in size, frame rate, pixel format
and audio: every clip is fitted to one canvas (`--size`, `--rate` and `--fit`, from the first clip by default) and its sound
resampled. `--transition` joins clips with `fade`, `fadeblack`, `dissolve`, `wipeleft`, `slideup`, `circleopen`, `clock`,
`zoom`, `pixelize`, `blur` and others, as `kind:duration`, with an equal-power crossfade of the sound. `overlay --key green`
keys out a green screen and takes the spill out of its edges. `--save-edit` writes the edit as JSON, which `edit` renders,
and which can be changed by hand: effects in it are egami run steps, such as `"blur 8"`.

```sh
egami-video join in.avi in.webm in.mkv --transition dissolve:0.5 --size 320x240 -o joined.mkv
egami-video fade in.webm --in 0.5 --out 1 -o faded.webm
egami-video overlay in.avi in.webm --at right-8,top-8 --scale 0.3 --opacity 0.8 --from 0.5 --to 1.5 -o overlay.mkv
egami-video overlay in.avi in.mkv --key green --similarity 0.12 --despill 0.6 -o keyed.mkv
egami-video pip in.avi in.webm --corner bottom-left --scale 0.25 --fade-in 0.2 -o pip.mkv
egami-video stack in.avi in.mkv --gap 4 -o side-by-side.mkv --save-edit side-by-side.json
egami-video edit side-by-side.json --codec raw -o again.avi
egami-video speed in.webm 1.5 -o fast.webm
egami-video reverse in.avi -o backwards.mkv
egami-video loop in.avi --count 3 -o looped.mkv
egami-video freeze in.avi --at 0.5 --for 1 -o frozen.mkv
egami-video slideshow shots/*.jpg --each 2 --transition fade:0.5 --pan-zoom --size 320x240 -o show.mkv
```

`speed`, `reverse`, `loop` and `freeze` change a clip's timing, picture and sound together; `freeze --at end` holds the
last frame. `slideshow` takes images in order (globs sort by name) and moves each with a slow pan and zoom drawn at sub-pixel
accuracy. A step of `run` can take a time window, `@start-end` right after it: `blur 8 @0:05-0:12` blurs only that part.

```sh
egami-video run in.avi grayscale @0.2-0.6 + blur 2 @0.4- + save windowed.mkv
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
