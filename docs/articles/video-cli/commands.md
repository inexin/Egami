# egami-video command reference

Every egami-video command with its arguments and options, as `egami-video <command> --help` prints them. The options of all
commands (`-v`, `-vv`, `-q`, `--limit`, `--progress`, `--progress-format`) are listed once, at the top. [The egami-video
command](index.md) explains the conventions they share; `run` takes [egami's steps](../cli/steps.md).

```text
Description:
  Inspect, remux, cut, join and convert video and audio files, and export and make frames, with Egami.Video, a fully managed video library.

Usage:
  egami-video [command] [options]

Options:
  -v, --verbose                  Write information to standard error.
  -vv, --debug                   Write debugging detail to standard error.
  -q, --quiet                    Write only errors to standard error: no warnings and no progress line.
  --limit <name=value,...>       Safety limits for decoded frames, such as pixels=100M,memory=2GB (names: pixels, width, height, frames, memory, metadata). EGAMI_LIMITS sets them too.
  --progress <file|->            Write progress to a file, or - for standard output: key=value lines as ffmpeg -progress writes them, or JSON with --progress-format json.
  --progress-format <keys|json>  How --progress writes: keys (the default, ffmpeg's key=value blocks) or json (one object per line).
  -?, -h, --help                 Show help and usage information
  --version                      Show version information

Commands:
  info <inputs>                 Show what media files hold: container, duration, tracks and codecs, tags, chapters and attachments; with options, packets and frames. Like ffprobe.
  remux <inputs>                Copy tracks into another container without decoding: change AVI to Matroska, pick tracks, keep tags, chapters and attachments.
  trim <inputs>                 Cut a part out of files without decoding: every track from the video keyframe at or before --from up to --to, with timestamps starting at 0; subtitle cues shown across the cut are clipped to it. Like ffmpeg -ss -to -c copy.
  concat <inputs>               Join clips with the same tracks and codecs into one file without decoding, one after another in the order given (globs sorted by name).
  segment <inputs>              Split files into parts without decoding, at video keyframes: -o names the parts with {part} (from 1), such as out-{part:000}.mkv; a folder takes {name}-{part:000}.{ext}.
  timestamps <inputs>           Change packet timestamps without decoding: shift tracks, re-time video to a constant frame rate, repair broken timestamps or start at 0. With --track, --shift, --rate and --fix change only those tracks; every track is written.
  extract <inputs>              Copy a track into its own file without decoding (VP8, VP9 and AV1 to IVF, PCM to WAV, FLAC to FLAC, Opus and Vorbis to Ogg, the rest to Matroska), or write attachments and cover art as files.
  embed <inputs>                Add tracks from other files, attachments and cover art to a file without decoding, and set track languages, names and defaults. Track numbers count the output's tracks from 1.
  metadata <inputs>             Write tags, chapters, track names and languages and attachment names as JSON, or copy a file with them changed from such a JSON or with --set, --remove and --chapter.
  frames <inputs>               Write video frames as images: every frame, one every few seconds, at given times, keyframes or a few evenly spaced.
  encode <inputs>               Make a clip from images, an image sequence or a Y4M stream, with Motion JPEG, VP8 or uncompressed video, and audio from another file.
  run <inputs-and-steps>        Run egami's steps over every frame of a video, separated by +, and encode it again: 'egami-video run in.mkv crop 0 60 1920 960 + resize 1280x + save out.mkv'. 'egami-video run --list' lists the steps; audio and subtitles are copied, and frames keep their times.
  thumbnail <inputs>            Write a representative frame of each video as an image: a typical frame of its opening, not a black or empty one, or the frame at --at.
  scenes <inputs>               Find the cuts between shots: list where each scene starts (frame, time and how sharp the cut is), and optionally write a frame of each.
  analyze <inputs>              Look for black, frozen, silent, cropped, interlaced and duplicate stretches, scene cuts, signal levels and loudness in one pass, as ffmpeg's detection filters do.
  waveform <inputs>             Draw the waveform of each file's audio as an image: the min to max outline of each column with the RMS level inside, as editors and podcast players show it.
  spectrum <inputs>             Draw the spectrogram of each file's audio as an image: frequency up, time across, level as color.
  generate <source> <file>      Write a test clip or sound: moving pattern with frame number and timecode, color bars, a solid color or a gradient, with or without a tone; or a sine, silence, noise or sweep on its own.
  sheet <inputs>                Make a contact sheet: a grid of evenly spaced frames, with their times and a title if asked.
  animate <inputs>              Make an animated GIF, WebP or PNG (APNG) from a stretch of video, at a frame rate and size of your choice.
  hash <inputs>                 Hash every decoded frame or packet of each track, in the layout of ffmpeg -f framemd5, to check that decoding matches.
  compare <reference> <inputs>  Measure PSNR and SSIM per plane between a reference clip and others, frame by frame in presentation order, as ffmpeg's psnr and ssim filters do.
  audio <inputs>                Decode audio to WAV, FLAC, Ogg FLAC, Opus, AIFF, CAF or ALAC in .m4a, or encode a video file's audio again while copying its video unchanged: resample, remix and change the sample format.
  formats                       List the containers and codecs egami-video reads and writes, like ffmpeg -formats and -codecs.
  ffmpeg <arguments>            Run an ffmpeg command line with egami-video: 'egami-video ffmpeg -i in.avi -c copy out.mkv'. --explain prints the native command instead. Takes -i, -f, -y, -n, -ss, -to, -t, -itsoffset, -framerate, -map, -c, -codec, -c:v, -c:a, -c:s, -vcodec, -acodec, -scodec, -an, -vn, -sn, -dn, -r, -s, -frames:v, -vframes, -q:v, -qscale:v, -ar, -ac, -sample_fmt, -metadata, -map_metadata, -map_chapters, -vf, -filter:v, -af, -filter:a, -lavfi, -filter_complex (psnr, ssim, showwavespic, showspectrumpic), -segment_time, -segment_times, -update, -progress, -stats, -nostats, -stats_period, -hide_banner, -loglevel, -v, -nostdin, %d patterns and pipe:; filters scale, crop, pad, transpose, hflip, vflip, fps, thumbnail, select (scene), tile; to -f null the detection filters blackdetect, freezedetect, cropdetect, idet, signalstats, blurdetect, blockdetect, silencedetect, volumedetect, astats and ebur128; -f lavfi sources testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc; anything else stops with exit code 2.
  ffprobe <arguments>           Run an ffprobe command line with egami-video: 'egami-video ffprobe -of json -show_streams in.mkv'. --explain prints the native command instead. Takes -i, -show_streams, -show_format, -show_packets, -show_frames, -show_chapters, -select_streams, -show_entries, -of, -print_format, -output_format (json, csv, compact, default, flat, xml), -v, -loglevel, -hide_banner, -pretty; anything else stops with exit code 2.
  mcp                           Serve egami-video's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.
```

## info

```text
Description:
  Show what media files hold: container, duration, tracks and codecs, tags, chapters and attachments; with options, packets and frames. Like ffprobe.

Usage:
  egami-video info <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -t, --track <track>                       Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                           Leave out audio tracks.
  -sn, --no-subtitles                       Leave out subtitle tracks.
  -r, --recursive                           Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>                        How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                               Stop at the first file that fails.
  -a, --all                                 Also read every packet: packet and keyframe counts, start times, sizes and bit rates per track.
  --headers                                 Also show the codec headers of each track's first frame (profile, size, color and so on).
  --packets                                 List every packet: track, timestamps, duration, size and flags.
  --frames                                  Decode and list every frame: track, timestamp, duration, size or samples.
  -f, --format <text|json|csv|xml|ffprobe>  How to write: text (the default), json, csv (packet and frame lists), xml (the json fields), or ffprobe (JSON with ffprobe's field names).
  --json                                    Write JSON (schema 1); the same as --format json.
  --csv                                     Write CSV; the same as --format csv.
```

## remux

```text
Description:
  Copy tracks into another container without decoding: change AVI to Matroska, pick tracks, keep tags, chapters and attachments.

Usage:
  egami-video remux <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
```

## trim

```text
Description:
  Cut a part out of files without decoding: every track from the video keyframe at or before --from up to --to, with timestamps starting at 0; subtitle cues shown across the cut are clipped to it. Like ffmpeg -ss -to -c copy.

Usage:
  egami-video trim <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --from <time>                  Where the cut starts: seconds, [HH:]MM:SS[.mmm] or a frame number such as #250; the copy starts at the video keyframe at or before it.
  --to <time>                    Where the cut ends, written as for --from; nothing that starts at or after it is kept.
  --duration <time>              How long the cut is, counted from --from, instead of --to.
```

## concat

```text
Description:
  Join clips with the same tracks and codecs into one file without decoding, one after another in the order given (globs sorted by name).

Usage:
  egami-video concat <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
```

## segment

```text
Description:
  Split files into parts without decoding, at video keyframes: -o names the parts with {part} (from 1), such as out-{part:000}.mkv; a folder takes {name}-{part:000}.{ext}.

Usage:
  egami-video segment <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --every <time>                 Split every so often, such as 10 or 1:00; each part starts at the first video keyframe at or after its split point.
  --at <times>                   Split at these times, separated by commas, such as 1:00,2:30 or #250,#500; each part starts at the first video keyframe at or after its split point.
  --keep-timestamps              Keep the input's timestamps in every part instead of starting each part at 0.
  --scenes                       Split at scene changes, as the scenes command finds them; each part starts at the video keyframe at or before its scene's first frame, and the gap is reported.
  --threshold <0-1>              With --scenes: how sharp a change starts a scene, from 0 to 1. Default 0.15.
  --min-length <time>            With --scenes: the shortest scene. Default 0.6s.
  --exact                        With --scenes: start every part on its scene's first frame. Needs smart cut (Video hub step V5.6), which is not built yet.
```

## timestamps

```text
Description:
  Change packet timestamps without decoding: shift tracks, re-time video to a constant frame rate, repair broken timestamps or start at 0. With --track, --shift, --rate and --fix change only those tracks; every track is written.

Usage:
  egami-video timestamps <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --shift <time>                 Move timestamps later, or earlier with a minus sign: seconds or [HH:]MM:SS[.mmm], such as 1.5, -1.5 or --shift=-0:01.
  --rate <fps>                   Re-time video packets to a constant frame rate in presentation order, such as 25 or 30000/1001; audio stays as it is.
  --fix                          Repair duplicated or out-of-order timestamps and missing durations: decode order, strictly increasing times per track (subtitle cues may start together), durations from the frame rate or the next packet.
  --start-at-zero                Move every track by the same amount so the earliest packet starts at 0.
```

## extract

```text
Description:
  Copy a track into its own file without decoding (VP8, VP9 and AV1 to IVF, PCM to WAV, FLAC to FLAC, Opus and Vorbis to Ogg, the rest to Matroska), or write attachments and cover art as files.

Usage:
  egami-video extract <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --attachment <name|index|all>  Write attachments as files instead of a track: by name, by index counted from 0, or all.
  --cover                        Write the cover art as a file instead of a track.
```

## embed

```text
Description:
  Add tracks from other files, attachments and cover art to a file without decoding, and set track languages, names and defaults. Track numbers count the output's tracks from 1.

Usage:
  egami-video embed <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --add <file[:tracks]>          Add the tracks of another file, all of them or those a track spec after ':' picks (audio.flac, other.mkv:a:1).
  --attach <file>                Add a file as an attachment, such as a font for subtitles.
  --cover <image>                Add a JPEG, PNG or WebP image as cover art, replacing any cover art the input has.
  --language <track=language>    Set an output track's language, such as 2=eng or a:0=nor.
  --name <track=name>            Set an output track's name, such as s:0=Commentary.
  --default <track>              Make an output track the default one of its kind; the others of that kind are not.
```

## metadata

```text
Description:
  Write tags, chapters, track names and languages and attachment names as JSON, or copy a file with them changed from such a JSON or with --set, --remove and --chapter.

Usage:
  egami-video metadata <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>       The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --load <file.json>             Take tags, chapters, track fields and attachments from a JSON file as metadata writes it; keys it leaves out stay.
  --clear                        Drop the tags, chapters, track names, languages and attachments that --load's JSON leaves out (all of them without --load).
  --set <KEY=value>              Set a file tag, such as TITLE=Holiday; repeat it for more.
  --remove <KEY>                 Remove a file tag; repeat it for more.
  --chapter <start[-end]=title>  Add a chapter, such as 1:30-2:45=Intro or 90=Part two; repeat it for more.
```

## frames

```text
Description:
  Write video frames as images: every frame, one every few seconds, at given times, keyframes or a few evenly spaced.

Usage:
  egami-video frames <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                    Stop at the first file that fails.
  -o, --output <template>        Where to write: a template with {frame} (the frame number from 0, padded as in {frame:0000}), {name}, {dir}, {index} and {format}, a folder, or - for standard output (one frame, with --format).
  -f, --format <format>          The image format by name or extension (png, jpg, webp, ...), when -o's extension does not say; always for -o -.
  --quality <0-100>              Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL).
  --set <fmt.key=value>          Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  --every <time|#n>              One frame per length of time (10s, 1:00), or every nth frame with #n (#25).
  --at <t1,t2,...>               Only the frames shown at these times, separated by commas (5, 1:30, #100).
  --keyframes                    Only the keyframes.
  --count <n>                    This many frames, evenly spaced from the first frame to the last.
  --from <time>                  Start at this time, or at a frame number with #n.
  --to <time>                    Stop before this time, or before a frame number with #n.
  --auto-orient                  Turn the frames upright by the track's rotation, as players show them.
```

## encode

```text
Description:
  Make a clip from images, an image sequence or a Y4M stream, with Motion JPEG, VP8 or uncompressed video, and audio from another file.

Usage:
  egami-video encode <inputs>... [options]

Arguments:
  <inputs>  Images in order (files, or globs sorted by name), an image sequence such as shots/{frame:0000}.png or shots/%04d.png, a video such as a Y4M file, or - for Y4M or Matroska on standard input.

Options:
  -o, --output <path>            The clip to write, or - for standard output (with --format).
  -f, --format <container>       The container by name or extension (mkv, avi, mp4, y4m), when -o's extension does not say; always for -o -.
  --rate <fps>                   Frames per second, such as 25, 29.97 or 30000/1001; 25 for images, and a video's own rate for a video.
  -c, --codec <codec>            The video codec: mjpeg (the default), vp8 or vp9 (for webm, mkv and ivf), or raw (uncompressed, the default for y4m).
  --quality <0-100>              Quality from 0 to 100: 75 by default for Motion JPEG, 60 for VP8 and VP9.
  --effort <0-10>                VP8 and VP9: how hard to work, 0 (fastest) to 10 (smallest); 4 by default.
  -b, --bitrate <bits/s>         VP8 and VP9: aim at this average bitrate instead of a quality, such as 2000k or 1.5M.
  --size <WxH>                   Fit every frame into this size, keeping its shape, with black around it; images take the first image's size by default.
  --chroma <420|422|444>         Chroma subsampling for frames made from images: 420 (the default), 422 or 444.
  --audio <file>                 Add the first audio track of this file, copied without decoding, up to the end of the video.
```

## run

```text
Description:
  Run egami's steps over every frame of a video, separated by +, and encode it again: 'egami-video run in.mkv crop 0 60 1920 960 + resize 1280x + save out.mkv'. 'egami-video run --list' lists the steps; audio and subtitles are copied, and frames keep their times.

Usage:
  egami-video run [<inputs-and-steps>...] [options] [[--] <additional arguments>...]]

Arguments:
  <inputs-and-steps>  Videos (files, globs, -), then steps separated by +, ending in save out.mkv if -o is not given.

Options:
  -o, --output <path>            Where the video goes when save has no path: a file, a folder, a template with {name}, or - with --format.
  -f, --format <container>       The output container (mkv, avi …), when the output's extension does not say.
  --codec <codec>                The video codec to encode with: mjpeg, vp8, vp9 or raw. The input's codec when Egami.Video can encode it, else mjpeg.
  --quality <1-100>              The encoder's quality from 1 to 100.
  --crop <auto>                  auto finds the black bars of letterbox, pillarbox and windowbox video, as the analyze command's crop detector does, and crops them off before the other steps.
  --rate <fps>                   Frames per second of the output, dropping or repeating frames to match; frames keep their own times by default.
  -t, --track <track>            Which video track to run the steps over: v:1 for the second, or a track number; audio and subtitle tracks are copied.
  -an, --no-audio                Leave out the audio tracks.
  -sn, --no-subtitles            Leave out the subtitle tracks.
  -w, --workflow <file>          Take the steps from an egami JSON workflow file.
  --save-workflow <file>         Write the steps to a JSON workflow file (then run them, unless --dry-run).
  --dry-run                      Print the steps and the file each input would write, and stop.
  --list                         List every step with its arguments; --list <step> shows one.
  -r, --recursive                Take every media file in folders given as inputs.
  -j, --jobs <count>             How many files to work on at once; 0 for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.

Additional Arguments:
  Arguments passed to the application that is being run.
```

## thumbnail

```text
Description:
  Write a representative frame of each video as an image: a typical frame of its opening, not a black or empty one, or the frame at --at.

Usage:
  egami-video thumbnail <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - for standard output.
  -f, --format <format>          The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>            Which video track: v:1 for the second, or a track number as info shows it; the first video track by default.
  --quality <0-100>              Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>          Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  -s, --size <size>              The thumbnail's size in egami's size syntax: 320x, x180, 320x180 (fits inside), 50%.
  --at <time>                    Take the frame shown at this time (seconds, [HH:]MM:SS[.mmm] or #frame) instead of choosing one.
```

## scenes

```text
Description:
  Find the cuts between shots: list where each scene starts (frame, time and how sharp the cut is), and optionally write a frame of each.

Usage:
  egami-video scenes <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>                    Also write a frame of each scene (the first, or as --frame says): a folder, or a template with {scene} (from 1, padded as {scene:000}), {name}, {dir} and {index}.
  -f, --format <format>                  The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>                    Which video track: v:1 for the second, or a track number as info shows it; the first video track by default.
  --quality <0-100>                      Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>                  Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                        Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>                     How many files to work on at once; 0 (the default) for one per processor.
  --in-place                             Allow outputs to replace their inputs.
  --fail-fast                            Stop at the first file that fails.
  --method <adaptive|content|histogram>  How changes are scored: adaptive (the default; ignores fast motion), content (a fixed threshold, as ffmpeg's scene value) or histogram. [default: adaptive]
  --threshold <0-1>                      How sharp a change starts a scene, from 0 to 1; lower finds more cuts. Default 0.15 for adaptive, 0.4 for content and histogram.
  --min-gap, --min-length <time>         The shortest scene: a cut closer than this to the last one is not taken. [default: 0.6s]
  --fades                                Also find fades to and from black: a scene then starts in the middle of a fade, and the fades are listed.
  --flash <merge|suppress>               What a flash (a frame or two of a different picture, as a camera flash) does: merge (the default) keeps it in its scene; suppress also ignores the change it ends with. [default: merge]
  --frame <first|middle|last>            Which frame of each scene -o writes: first (the default), middle or last. Middle and last need a file, not standard input. [default: first]
  --series <file.csv>                    Also write every frame's change score to this CSV file.
  --json                                 Write the scenes as JSON.
  --csv                                  Write the scenes as CSV, one row per scene.
```

## analyze

```text
Description:
  Look for black, frozen, silent, cropped, interlaced and duplicate stretches, scene cuts, signal levels and loudness in one pass, as ffmpeg's detection filters do.

Usage:
  egami-video analyze <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                    Stop at the first file that fails.
  -d, --detect <detectors>       What to look for, separated by commas or repeated: scenes, black, freeze, crop, interlace, duplicates, signal, blur, blockiness, silence, levels, loudness.
  --all                          Run every detector the file has a track for; blur and blockiness come with signal.
  -ss, --start <time>            Start here instead of at the beginning.
  --duration <time>              Look at this much and stop.
  --series <file.csv>            Also write per-frame and per-100 ms values (scene scores, black share, signal levels, loudness) to this CSV file.
  --json                         Write the findings as JSON.
  --csv                          Write the findings as CSV, one row per range or value.
  --ffmpeg-log <filters>         Instead of the report, print the findings on standard error in the shape these ffmpeg filters log them, for scripts that read ffmpeg's output: blackdetect, freezedetect, cropdetect, idet, signalstats, blurdetect, blockdetect, silencedetect, volumedetect, astats, ebur128.
  --scene-method <method>        How scene changes are scored: adaptive (the default), content or histogram.
  --scene-threshold <0-1>        How sharp a change starts a scene, from 0 to 1. Default 0.15 for adaptive, 0.4 otherwise.
  --scene-min <time>             The shortest scene. Default 0.6s.
  --black-min <time>             The shortest black stretch reported, as blackdetect's d. Default 2s.
  --black-ratio <0-1>            The share of black pixels that makes a frame black, as blackdetect's pic_th. Default 0.98.
  --black-pixel <0-1>            How dark a pixel must be to count as black, as a share of the luma range, as blackdetect's pix_th. Default 0.10.
  --freeze-min <time>            The shortest frozen stretch reported, as freezedetect's d. Default 2s.
  --freeze-noise <level>         How much change still counts as frozen, as a ratio (0.001) or in dB (-60dB), as freezedetect's n. Default 0.001.
  --silence-min <time>           The shortest silence reported, as silencedetect's d. Default 2s.
  --silence-noise <level>        The level under which audio is silent, in dB (-50dB) or as a ratio (0.003), as silencedetect's n. Default -60dB.
  --silence-per-channel          Also report each channel's silences, as silencedetect's mono.
  --crop-limit <level>           The brightest a border may be, as a share of the range (0.094) or an 8-bit value (24), as cropdetect's limit. Default 24.
  --crop-round <n>               Round the width and height down to a multiple of this, as cropdetect's round. Default 2.
  --crop-skip <n>                Frames at the start not measured, as cropdetect's skip. Default 2.
  --dup-hi <n>                   The block difference no 8x8 block may pass, as mpdecimate's hi. Default 768.
  --dup-lo <n>                   The block difference only --dup-frac of blocks may pass, as mpdecimate's lo. Default 320.
  --dup-frac <0-1>               The share of blocks that may pass --dup-lo, as mpdecimate's frac. Default 0.33.
```

## waveform

```text
Description:
  Draw the waveform of each file's audio as an image: the min to max outline of each column with the RMS level inside, as editors and podcast players show it.

Usage:
  egami-video waveform <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - for standard output.
  -f, --format <format>          The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>            Which audio track: a:1 for the second, or a track number as info shows it; the first audio track by default.
  --quality <0-100>              Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>          Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  -s, --size <WxH>               The picture's size, width x height. Default 1800x280. [default: 1800x280]
  --channels <mixed|stacked>     mixed (the default) draws every channel in one strip; stacked gives each its own. [default: mixed]
  --scale <linear|db>            linear (the default) or db, which shows quiet passages; --floor sets the lowest level. [default: linear]
  --floor <dB>                   The lowest level --scale db shows, in dBFS. Default -60. [default: -60]
  --color <color>                The outline color as #rrggbb or #rrggbbaa. Default a mid blue.
  --rms-color <color|none>       The color of the RMS band inside the outline, or none. Default a light blue.
  --background <color>           The background color. Default transparent, or dark gray with --legend.
  --legend                       Add a time axis and level labels around the picture.
```

## spectrum

```text
Description:
  Draw the spectrogram of each file's audio as an image: frequency up, time across, level as color.

Usage:
  egami-video spectrum <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - for standard output.
  -f, --format <format>          The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>            Which audio track: a:1 for the second, or a track number as info shows it; the first audio track by default.
  --quality <0-100>              Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>          Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  -s, --size <WxH>               The picture's size, width x height. Default 1800x512. [default: 1800x512]
  --window <samples>             The FFT window in samples, a power of two from 256 to 32768; longer resolves frequency finer and time coarser. Default 2048. [default: 2048]
  --scale <linear|log>           The frequency axis: linear (the default) or log, which gives each octave the same height. [default: linear]
  --min-frequency <Hz>           The lowest frequency on the log axis, in hertz. Default 20. [default: 20]
  --floor <dB>                   The level drawn darkest, in dBFS. Default -120. [default: -120]
  --ceiling <dB>                 The level drawn brightest, in dBFS, where a full-scale sine reads 0. Default 0. [default: 0]
  --channels <mixed|stacked>     mixed (the default) shows the mix of every channel; stacked gives each its own strip. [default: mixed]
  --colors <magma|gray>          The color map: magma (the default) or gray. [default: magma]
  --legend                       Add a time axis and frequency labels around the picture.
```

## generate

```text
Description:
  Write a test clip or sound: moving pattern with frame number and timecode, color bars, a solid color or a gradient, with or without a tone; or a sine, silence, noise or sweep on its own.

Usage:
  egami-video generate <source> [<file>] [options]

Arguments:
  <source>  What to make: pattern, bars, solid, gradient (pictures) or sine, silence, noise, sweep (sound).
  <file>    The file to write; or give -o.

Options:
  -o, --output <path>            The file to write: the container comes from its extension or --format.
  -f, --format <container>       The container by name or extension, when the file's extension does not say.
  -t, --duration <time>          How long, in seconds or [HH:]MM:SS[.mmm]. Default 10. [default: 10]
  -s, --size <WxH>               The picture size. Default 1280x720. [default: 1280x720]
  -r, --rate <fps>               Frames per second, such as 25, 29.97 or 30000/1001. Default 30. [default: 30]
  --color <color>                solid's color, and gradient's first, as #rrggbb. Default black for solid, black to white for gradient.
  --to-color <color>             gradient's last color as #rrggbb.
  --vertical                     Run gradient from top to bottom instead of left to right.
  --bars <auto|hd|sd>            Which bars: auto (the default; SMPTE RP 219 for HD sizes, EG 1 for SD), hd or sd. [default: auto]
  --tone                         Add a 1 kHz tone at -18 dBFS to a picture source: the same as --audio sine.
  --audio <source>               Add a sound to a picture source: sine, silence, noise or sweep.
  --frequency <Hz>               sine's frequency in hertz. Default 1000. [default: 1000]
  --level <dB>                   The level of sine (its peak), noise (its RMS) and sweep in dBFS. Default -18. [default: -18]
  --noise <white|pink>           noise's color: white (the default) or pink. [default: white]
  --from <Hz>                    sweep's start frequency in hertz. Default 20. [default: 20]
  --to <Hz>                      sweep's end frequency in hertz. Default just below half the sample rate.
  -ar, --sample-rate <Hz>        The sample rate in hertz. Default 48000. [default: 48000]
  -ac, --channels <layout>       A channel count or a layout (mono, stereo, 5.1 …). Default stereo. [default: stereo]
  -c, --codec <codec>            The video codec: mjpeg (the default), vp8, vp9 or raw (the default for y4m).
  --audio-codec <codec>          The audio codec: flac, pcm or opus; by default Opus in WebM and .opus, PCM in WAV and AVI, and FLAC otherwise.
  --quality <0-100>              The video quality from 0 to 100.
```

## sheet

```text
Description:
  Make a contact sheet: a grid of evenly spaced frames, with their times and a title if asked.

Usage:
  egami-video sheet <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - for standard output.
  -f, --format <format>          The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>            Which video track: v:1 for the second, or a track number as info shows it; the first video track by default.
  --quality <0-100>              Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>          Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --grid <CxR>                   Columns and rows of frames, such as 4x4 or 5x3. [default: 4x4]
  --width <pixels>               The sheet's width in pixels; the tiles share it and keep the video's shape. [default: 1280]
  --timestamps                   Write each frame's time under it.
  --title                        Write the file's name, length, size and codec above the frames.
```

## animate

```text
Description:
  Make an animated GIF, WebP or PNG (APNG) from a stretch of video, at a frame rate and size of your choice.

Usage:
  egami-video animate <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>            The animation to write: .gif, .webp or .png (APNG); a folder, or a template with {name}, {dir} and {index}.
  -f, --format <format>          The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.
  -t, --track <track>            Which video track: v:1 for the second, or a track number as info shows it; the first video track by default.
  --quality <0-100>              Lossy quality from 0 to 100 for formats that have one.
  --set <fmt.key=value>          Writer settings as fmt.key=value, as egami takes them.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --in-place                     Allow outputs to replace their inputs.
  --fail-fast                    Stop at the first file that fails.
  --rate <fps>                   Frames per second of the animation; frames of the video are dropped or held to match. [default: 10]
  -s, --size <size>              The animation's size in egami's size syntax: 320x, x180, 320x180 (fits inside), 50%.
  --from <time>                  Start at this time (seconds, [HH:]MM:SS[.mmm] or #frame); the start of the video by default.
  --to <time>                    End at this time; the end of the video by default.
  --loop <count>                 How many times the animation plays; 0 (the default) loops forever.
  --lossless                     Write lossless WebP.
```

## hash

```text
Description:
  Hash every decoded frame or packet of each track, in the layout of ffmpeg -f framemd5, to check that decoding matches.

Usage:
  egami-video hash <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                    Stop at the first file that fails.
  --packets                      Hash every packet as stored instead of every decoded frame, as ffmpeg -c copy -f framemd5 does.
  --algorithm <md5|sha256>       The hash: md5 (the default, as framemd5) or sha256. [default: md5]
  --total                        End with a line per track that hashes all its frames or packets together.
  --sample-format <format>       Convert decoded audio to u8, s16, s32, f32 or f64 before hashing; s16 matches ffmpeg's framemd5 default.
  -f, --format <text|json|csv>   How to write: text (the default, framemd5's lines), json or csv.
  --json                         Write JSON; the same as --format json.
  --csv                          Write CSV; the same as --format csv.
  -o, --output <file>            Write the hashes to this file instead of standard output.
```

## compare

```text
Description:
  Measure PSNR and SSIM per plane between a reference clip and others, frame by frame in presentation order, as ffmpeg's psnr and ssim filters do.

Usage:
  egami-video compare <reference> <inputs>... [options]

Arguments:
  <reference>  The original clip, or - for standard input.
  <inputs>     The clips to measure against the reference: files, globs (*, ?, **), folders with --recursive, or - for standard input.

Options:
  -t, --track <track>            Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                Leave out audio tracks.
  -sn, --no-subtitles            Leave out subtitle tracks.
  -r, --recursive                Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>             How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                    Stop at the first file that fails.
  -m, --metric <psnr,ssim>       What to measure: psnr, ssim, or both separated by a comma (the default). [default: psnr,ssim]
  --per-frame                    Also list the scores of every frame.
  --scale                        Resize frames to the reference's size (bicubic) when the sizes differ.
  --min-psnr <dB>                Exit with 1 when a clip's average PSNR is below this many decibels.
  --min-ssim <value>             Exit with 1 when a clip's average SSIM is below this, from 0 to 1.
  -f, --format <text|json|csv>   How to write: text (the default), json or csv.
  --json                         Write JSON; the same as --format json.
  --csv                          Write CSV; the same as --format csv.
```

## audio

```text
Description:
  Decode audio to WAV, FLAC, Ogg FLAC, Opus, AIFF, CAF or ALAC in .m4a, or encode a video file's audio again while copying its video unchanged: resample, remix and change the sample format.

Usage:
  egami-video audio <inputs>... [options]

Arguments:
  <inputs>  Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>               Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).
  -f, --format <container>          The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.
  -t, --track <track>               Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.
  -an, --no-audio                   Leave out audio tracks.
  -sn, --no-subtitles               Leave out subtitle tracks.
  -r, --recursive                   Take every media file in folders given as inputs, at any depth.
  -j, --jobs <count>                How many files to work on at once; 0 (the default) for one per processor.
  --in-place                        Allow outputs to replace their inputs.
  --fail-fast                       Stop at the first file that fails.
  -c, --codec <flac|pcm|opus|alac>  The audio codec to write: flac, pcm, opus or alac (Apple Lossless); by default Opus in .opus and WebM, PCM in WAV, Wave64, AVI, AIFF and CAF, ALAC in .m4a, and FLAC otherwise.
  -ar, --rate <Hz>                  Resample to this many samples per second.
  -ac, --channels <count|layout>    Mix to this many channels or this layout: mono, stereo, 2.1, 3.0, quad, 5.0, 5.1, 5.1(side), 6.1, 7.1 or 7.1.4.
  --sample-format <format>          The samples to store: u8, s16, s24, s32, f32 or f64 (float only with pcm); by default the source's depth.
  --level <0-8>                     FLAC's compression level, from 0 (fastest) to 8 (smallest), 5 by default; ALAC's, 0 (fast) or 1 (the default).
  -b, --bitrate <bits/s>            Opus's bitrate for all channels together, such as 96k; 64k a channel by default.
  -vn, --no-video                   Leave out video tracks, keeping only audio (and subtitles) in a Matroska or MP4 output.
```

## formats

```text
Description:
  List the containers and codecs egami-video reads and writes, like ffmpeg -formats and -codecs.

Usage:
  egami-video formats [options]

Options:
  --json                         Write JSON instead of text.
```

## ffmpeg

```text
Description:
  Run an ffmpeg command line with egami-video: 'egami-video ffmpeg -i in.avi -c copy out.mkv'. --explain prints the native command instead. Takes -i, -f, -y, -n, -ss, -to, -t, -itsoffset, -framerate, -map, -c, -codec, -c:v, -c:a, -c:s, -vcodec, -acodec, -scodec, -an, -vn, -sn, -dn, -r, -s, -frames:v, -vframes, -q:v, -qscale:v, -ar, -ac, -sample_fmt, -metadata, -map_metadata, -map_chapters, -vf, -filter:v, -af, -filter:a, -lavfi, -filter_complex (psnr, ssim, showwavespic, showspectrumpic), -segment_time, -segment_times, -update, -progress, -stats, -nostats, -stats_period, -hide_banner, -loglevel, -v, -nostdin, %d patterns and pipe:; filters scale, crop, pad, transpose, hflip, vflip, fps, thumbnail, select (scene), tile; to -f null the detection filters blackdetect, freezedetect, cropdetect, idet, signalstats, blurdetect, blockdetect, silencedetect, volumedetect, astats and ebur128; -f lavfi sources testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc; anything else stops with exit code 2.

Usage:
  egami-video ffmpeg [<arguments>...] [options] [[--] <additional arguments>...]]

Arguments:
  <arguments>  ffmpeg's arguments, as ffmpeg takes them.

Options:
  --explain                      Print the egami-video command the line stands for, and stop.

Additional Arguments:
  Arguments passed to the application that is being run.
```

## ffprobe

```text
Description:
  Run an ffprobe command line with egami-video: 'egami-video ffprobe -of json -show_streams in.mkv'. --explain prints the native command instead. Takes -i, -show_streams, -show_format, -show_packets, -show_frames, -show_chapters, -select_streams, -show_entries, -of, -print_format, -output_format (json, csv, compact, default, flat, xml), -v, -loglevel, -hide_banner, -pretty; anything else stops with exit code 2.

Usage:
  egami-video ffprobe [<arguments>...] [options] [[--] <additional arguments>...]]

Arguments:
  <arguments>  ffprobe's arguments, as ffprobe takes them.

Options:
  --explain                      Print the egami-video command the line stands for, and stop.

Additional Arguments:
  Arguments passed to the application that is being run.
```

## mcp

```text
Description:
  Serve egami-video's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.

Usage:
  egami-video mcp [options]

Options:
  --allow <folder>               A folder the tools may read and write, besides the folders the assistant opened; give it once per folder. With neither, the working folder.
  --max-inputs <count>           The most files one tool call may take; 1000 by default. [default: 1000]
```
