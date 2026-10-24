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

## The options

ffmpeg mode takes `-i`, `-f`, `-y`, `-n`, `-ss`, `-to`, `-t`, `-itsoffset`, `-framerate`, `-safe`, `-map`, `-c`, `-codec`, `-c:v`,
`-c:a`, `-c:s`, `-vcodec`, `-acodec`, `-scodec`, `-an`, `-vn`, `-sn`, `-dn`, `-r`, `-s`, `-frames:v`, `-vframes`, `-q:v`,
`-qscale:v`, `-ar`, `-b:a`, `-ab`, `-ac`, `-sample_fmt`, `-metadata`, `-map_metadata`, `-map_chapters`, `-vf`, `-filter:v`, `-lavfi` and
`-filter_complex` (psnr, ssim, showwavespic and showspectrumpic), `-af` and `-filter:a` (detection filters), `-segment_time`, `-segment_times`, `-update`, `-progress`, `-stats`, `-nostats`,
`-stats_period`, `-hide_banner`, `-loglevel`, `-v` and `-nostdin`, with `pipe:` and `%d` patterns. The filters are scale, crop,
pad, transpose, hflip, vflip, fps, thumbnail, select (scene) and tile, and with `-f null` the detection filters above; the
`-f lavfi` sources are testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc. Codecs: `copy`, `mjpeg` and `rawvideo` for video, `flac`,
`alac`, `libopus` (or `opus`) and the `pcm_` codecs for audio.

ffprobe mode takes `-i`, `-show_streams`, `-show_format`, `-show_packets`, `-show_frames`, `-show_chapters`, `-select_streams`,
`-show_entries`, `-of` (also `-print_format`, `-output_format`) with the json, csv, compact, default, flat and xml writers and
their `nokey`, `noprint_wrappers`, `print_section` and `item_sep` options, `-v`, `-loglevel`, `-hide_banner` and `-pretty`. Its
JSON is `egami-video info --format ffprobe`.

The tests run more than a hundred common ffmpeg and ffprobe lines: each either maps to the native command it should or stops
naming its option. The subset was written from FFmpeg's published documentation only.
