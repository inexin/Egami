# ffmpeg command lines

`egami-video ffmpeg` and `egami-video ffprobe` take the command lines people already know, find in answers online or have in
scripts, and run them with egami-video's own commands. They accept a documented subset of FFmpeg's options. Anything outside it
stops with exit code 2 and names the option: nothing is ignored silently.

```sh
egami-video ffmpeg -i in.avi -c copy out.mkv
egami-video ffmpeg -ss 0.5 -i in.webm -t 1 -c copy cut.webm
egami-video ffmpeg -i in.mkv -vf fps=2 shot%03d.png
egami-video ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 in.mkv
```

`--explain` prints the native command a line stands for and stops, which is also the quickest way to learn the native syntax:

```sh
egami-video ffmpeg -i in.mkv -vf scale=640:-1,hflip -c:v mjpeg out.mkv --explain
```

prints `egami-video run in.mkv resize 640 + flip horizontal -o out.mkv --codec mjpeg`.

## What a line becomes

| ffmpeg line | Native command |
| --- | --- |
| `-i in -c copy out` (any container change, `-map`, `-an`, `-sn`) | `remux` |
| `-movflags frag_keyframe+empty_moov+default_base_moof` with `-c copy` or an image input | `remux --fragment=keyframe`, `encode --fragment=keyframe` |
| `-ss`, `-to`, `-t` with `-c copy` | `trim` |
| several `-i` with `-map 0 -map 1:a` | `embed` |
| `-f concat -i list.txt -c copy` | `concat` with the files the list names |
| `-f segment -segment_time` or `-segment_times` | `segment` |
| `-itsoffset`, or `-r` with `-c copy` | `timestamps` |
| `-metadata key=value`, `-map_metadata -1` | `metadata` |
| an image output, `%04d` patterns, `-vf fps=`, `-frames:v` | `frames` |
| image or Y4M input (`-framerate`, `%04d`, `-f yuv4mpegpipe`) | `encode` |
| `-vf` with scale, crop, pad, transpose, hflip, vflip; `-s`; `-c:v mjpeg` or `rawvideo` | `run` |
| `-vf thumbnail`, `select='gt(scene,0.4)'`, `tile=4x4` | `thumbnail`, `scenes`, `sheet` |
| `.gif`, `.apng` or `.webp` output | `animate` |
| `-vn`, `-c:a flac`, `-c:a alac`, `-c:a libopus`, `-b:a`, `-ar`, `-ac`, `-sample_fmt`, an .aiff or .caf output | `audio` |
| `-f framemd5`, `-f framehash` | `hash` |
| `-i distorted -i reference -lavfi psnr` or `ssim` | `compare` |
| `-vf blackdetect`, `freezedetect`, `cropdetect`, `idet`, `signalstats`, `blurdetect`, `blockdetect` or `-af silencedetect`, `volumedetect`, `astats`, `ebur128`, with `-f null -` | `analyze --ffmpeg-log` |
| `-f lavfi -i testsrc2`, `smptebars`, `smptehdbars`, `color`, `sine`, `anullsrc`, `anoisesrc` | `generate` |
| `-lavfi showwavespic` or `showspectrumpic` (with or without `-frames:v 1`) | `waveform`, `spectrum` |

## Detection filters

ffmpeg's detection filters write what they find to its log, and scripts read those lines back. With `-f null -` the filters
map to `analyze`, and `--ffmpeg-log` makes it print the same lines on standard error, in the shapes ffmpeg's documentation
shows: `black_start:1 black_end:3.4 black_duration:2.4`, `silence_start: 1` then `silence_end: 3.4 | silence_duration: 2.4`,
`lavfi.freezedetect.freeze_start`, cropdetect's `crop=w:h:x:y`, idet's field counts, volumedetect's `mean_volume` and
`max_volume` and the `histogram_<n>db` counts, astats's per-channel block and ebur128's summary with both gate thresholds. Each
line starts with `[Parsed_<filter>_<n> @ egami]`, the filter numbered within its `-vf` or `-af` chain as ffmpeg numbers it, so
the usual regular expressions still match; the ebur128 summary's later lines carry no prefix, as in ffmpeg's log. cropdetect
prints its line (`x1` to `y2`, `w`, `h`, `x`, `y`, `pts`, `t`, `limit` and `crop=`) once for each run of frames with the same
rectangle where ffmpeg prints it for every frame, and rounds to 16 by default here, as ffmpeg does (`analyze` itself rounds to 2).

```sh
egami-video ffmpeg -i in.mkv -vf blackdetect=d=0.5 -af silencedetect=noise=-50dB:d=1 -f null -
egami-video ffmpeg -i in.mkv -af ebur128=peak=true -f null -
```

The filters take their documented options by name: blackdetect `d`, `pic_th` and `pix_th`; freezedetect `n` and `d`;
cropdetect `limit`, `round` and `skip`; silencedetect `n`, `d` and `mono`; ebur128 `peak` (the summary always has the sample
and true peaks). signalstats, blurdetect and blockdetect have no log shape in ffmpeg's documentation, so they print the lines
`analyze` writes. crop runs print one cropdetect line each, not one a frame.

## Test sources and pictures of sound

`-f lavfi` sources become `generate`: testsrc and testsrc2 the moving pattern, smptebars and smptehdbars the SD and HD bars,
color a solid picture, and sine, anullsrc and anoisesrc (white or pink) a sound; a picture source and a sound source together
make one clip. The length comes from `d=` or `-t`, as ffmpeg's sources run until stopped.

```sh
egami-video ffmpeg -f lavfi -i smptehdbars=s=1920x1080 -f lavfi -i sine=frequency=1000 -t 10 bars.mkv
egami-video ffmpeg -i in.mkv -lavfi showwavespic=split_channels=1:s=1024x800 waveform.png
egami-video ffmpeg -i in.mkv -lavfi showspectrumpic=s=1024x512:fscale=log spectrogram.png
```

showwavespic takes `s`, `split_channels`, `colors` (the first) and `scale` (lin or log); showspectrumpic takes `s`, `mode`,
`color` (magma), `scale` (log), `fscale` and `legend`, and draws a legend unless `legend=0`, as ffmpeg does.

ffmpeg encodes again by default; egami-video copies streams where ffmpeg would encode with a codec it does not have, and says so
in a warning. An output that exists needs `-y`, as in ffmpeg.

## Editing filters

A line with an editing filter becomes one edit file, rendered by `egami-video edit`; `--explain` prints the command with
`edit.json` for the file, then the file. `-vf`, `-af` and `-filter_complex` graphs take fade (in from the start, out to the end),
setpts with a constant factor (0.1x to 10x), reverse, loop (whole clips), tpad (`stop_mode=clone`), fps, scale, xfade (with
V9's transitions), concat, overlay (numbers and the `W`, `H`, `w` and `h` placement forms), hstack, vstack, xstack (grids),
zoompan on an image (a constant zoom, or `zoom+k` with an optional cap, from the corner or the centre), chromakey, colorkey,
despill, and hflip and vflip with a time window; and of the sound afade, acrossfade (where an xfade joins the same inputs),
amix (of an overlay's inputs), atempo, volume, areverse and aloop. `enable` takes `between(t,a,b)`, `gte(t,a)` and `lte(t,b)`.
`-loop 1` before an image makes a still clip.

```sh
egami-video ffmpeg -i in.avi -i in.mkv -filter_complex "[0:v][1:v]xfade=transition=wipeleft:duration=0.5:offset=1.5[v];[0:a][1:a]acrossfade=d=0.5[a]" -map [v] -map [a] joined.mkv
egami-video ffmpeg -i in.avi -i in.mkv -filter_complex "[1:v]scale=64:-1[small];[0:v][small]overlay=W-w-10:10:enable='between(t,0.5,1.5)'" pip.mkv
egami-video ffmpeg -i in.avi -vf setpts=PTS/2 -af atempo=2 fast.mkv
```

An edit changes picture and sound together, so a line that changes one and not the other stops: setpts without the same
atempo, reverse without areverse and loop without aloop, unless the sound is dropped with `-an`. A fade fades the sound with
the picture. freezeframes stops, since it replaces frames where V9's freeze adds time (`tpad=stop_mode=clone` or
`egami-video freeze` hold a frame), and loudnorm stops too. `deshake`, `vidstabdetect` (to `-f null`, writing its `result`
file as Egami.Video's camera motion) and `vidstabtransform` (its `input` and `smoothing`) become `egami-video stabilize`.

## The options

ffmpeg mode takes `-i`, `-f`, `-y`, `-n`, `-ss`, `-to`, `-t`, `-itsoffset`, `-framerate`, `-safe`, `-map`, `-c`, `-codec`, `-c:v`,
`-c:a`, `-c:s`, `-vcodec`, `-acodec`, `-scodec`, `-an`, `-vn`, `-sn`, `-dn`, `-r`, `-s`, `-frames:v`, `-vframes`, `-q:v`,
`-qscale:v`, `-ar`, `-b:a`, `-ab`, `-ac`, `-sample_fmt`, `-metadata`, `-map_metadata`, `-map_chapters`, `-vf`, `-filter:v`, `-lavfi` and
`-filter_complex` (psnr, ssim, showwavespic, showspectrumpic and the editing filters), `-af` and `-filter:a` (detection and editing filters), `-loop 1` before an image, `-segment_time`, `-segment_times`, `-movflags` (faststart, frag_keyframe, empty_moov and default_base_moof), `-frag_duration` (with frag_keyframe), `-update`, `-progress`, `-stats`, `-nostats`,
`-stats_period`, `-hide_banner`, `-loglevel`, `-v` and `-nostdin`, with `pipe:` and `%d` patterns. The filters are scale, crop,
pad, transpose, hflip, vflip, fps, thumbnail, select (scene) and tile, and with `-f null` the detection filters above; the
`-f lavfi` sources are testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc. Codecs: `copy`, `mjpeg` and `rawvideo` for video, `flac`,
`alac`, `libopus` (or `opus`) and the `pcm_` codecs for audio.

ffprobe mode takes `-i`, `-show_streams`, `-show_format`, `-show_packets`, `-show_frames`, `-show_chapters`, `-select_streams`,
`-show_entries`, `-of` (also `-print_format`, `-output_format`) with the json, csv, compact, default, flat and xml writers and
their `nokey`, `noprint_wrappers`, `print_section` and `item_sep` options, `-v`, `-loglevel`, `-hide_banner` and `-pretty`. Its
JSON is `egami-video info --format ffprobe`.

ffmpeg starts a fragment at every video keyframe for frag_keyframe and for empty_moov alone, and egami-video writes the same
boxes in the same order. With frag_keyframe, `-frag_duration` also cuts where keyframes are further apart than it; egami-video
cuts only at keyframes, so the layouts differ only for such long keyframe intervals. `-frag_duration` without frag_keyframe
stops, since ffmpeg then cuts between keyframes.

The tests run more than a hundred common ffmpeg and ffprobe lines: each either maps to the native command it should or stops
naming its option. The subset was written from FFmpeg's published documentation only.
