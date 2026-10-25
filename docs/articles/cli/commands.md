# Command reference

Every egami command with its arguments and options, as `egami <command> --help` prints them. The options of all commands
(`-v`, `-vv`, `-q`, `--limit`) are listed once, at the top. [The egami command](index.md) explains the conventions they share.

```text
Description:
  Convert, inspect, resize and edit images with Egami, a fully managed imaging library.

Usage:
  egami [command] [options]

Options:
  -v, --verbose             Write information to standard error.
  -vv, --debug              Write debugging detail to standard error.
  -q, --quiet               Write only errors to standard error.
  --limit <name=value,...>  Safety limits, such as pixels=100M,memory=2GB,frames=500 (names: pixels, width, height, frames, memory, metadata). EGAMI_LIMITS sets them too.
  -?, -h, --help            Show help and usage information
  --version                 Show version information

Commands:
  info <inputs>                     Show what images are: format, size, frames, pixel layout, orientation; with --all, their metadata. Pixels are not decoded.
  convert <inputs>                  Convert images to another format, or write them again with other settings.
  resize <inputs>                   Resize images: to a width, a height, a box, a percentage, filling or padding a box.
  thumbnail <inputs>                Make thumbnails for the web: turned upright, shrunk (never enlarged) to fit --size (256 by default), converted to sRGB, metadata left out.
  crop <inputs>                     Crop images to a rectangle.
  rotate <inputs>                   Rotate images clockwise. A JPEG turned by a multiple of 90 degrees into a JPEG is rotated losslessly.
  flip <inputs>                     Mirror images left to right (horizontal), top to bottom (vertical) or both. A JPEG flipped into a JPEG is flipped losslessly.
  auto-orient, autoorient <inputs>  Turn images upright from their EXIF orientation and reset it. A JPEG into a JPEG is turned losslessly.
  run <inputs-and-steps>            Run steps over images, separated by +: 'egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82'. Every Egami operation is a step ('egami run --list'); save writes, and may come more than once.
  compare <image> <other>           Compare two images: print a metric and exit with 1 when they differ more than --threshold.
  stats <inputs>                    Measure images: per-channel minimum, maximum, mean, deviation, skewness, kurtosis and entropy, as ImageMagick's identify -verbose.
  ocr <inputs>                      Read the text of images with an OCR program (Tesseract or any that writes hOCR or ALTO), as plain text, hOCR or ALTO.
  frames                            Split an animation (GIF, APNG, WebP) into still images, or join stills into one.
  metadata                          Show, strip or copy metadata: color profile, EXIF, XMP, IPTC, text and resolution.
  color <inputs>                    Convert images to another color profile (ICC, 2.1.2), tone mapping HDR to SDR where needed (2.9.1).
  text <inputs>                     Draw a caption or watermark on images, with any font and script, wrapped to fit (2.8.2 layout).
  overlay <inputs>                  Lay an image (a logo, a watermark, an SVG) on images, placed by anchor, with opacity and a blend mode.
  montage <inputs>                  Lay images out on a contact sheet: a grid of tiles, optionally labeled with their file names.
  formats <format>                  List the formats egami reads and writes, or one format's writer settings for --set.
  mcp                               Serve egami's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.
```

## info

```text
Description:
  Show what images are: format, size, frames, pixel layout, orientation; with --all, their metadata. Pixels are not decoded.

Usage:
  egami info <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast               Stop at the first file that fails.
  -a, --all                 Also read the metadata: color profile, EXIF, XMP, IPTC, text, resolution.
  --json                    Write JSON (schema 1, see the command reference) instead of text.
```

## convert

```text
Description:
  Convert images to another format, or write them again with other settings.

Usage:
  egami convert <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient             Turn the image upright from its EXIF orientation first.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
```

## resize

```text
Description:
  Resize images: to a width, a height, a box, a percentage, filling or padding a box.

Usage:
  egami resize <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>           Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>         The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>             Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                    Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>              Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>            Bits per channel to write (PNG, AVIF).
  --strip                       Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>         Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient                 Turn the image upright from its EXIF orientation first.
  --to <profile>                Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive               Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>            How many files to work on at once; 0 (the default) for one per processor.
  --in-place                    Allow outputs to replace their inputs.
  --fail-fast                   Stop at the first file that fails.
  -s, --size <size> (REQUIRED)  The size: W, xH, WxH or P%, then optionally ! (ignore the aspect ratio), ^ (fill the box), > (only shrink) or < (only enlarge).
  --fit <mode>                  How a WxH box is filled: contain (inside, the default), cover (filled and cropped), fill (stretched) or pad.
  --gravity <anchor>            What cover keeps and where pad places the image: center (the default), top, top-left, ...
  --filter <filter>             The resampling kernel: bicubic (Catmull-Rom, the default), lanczos3, mitchell, bilinear, nearest, ...
  --linear                      Blend in linear light rather than in the encoded values.
  --background <color>          The color --fit pad fills with (transparent by default).
```

## thumbnail

```text
Description:
  Make thumbnails for the web: turned upright, shrunk (never enlarged) to fit --size (256 by default), converted to sRGB, metadata left out.

Usage:
  egami thumbnail <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient             Turn the image upright from its EXIF orientation first.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  -s, --size <size>         The size: W, xH, WxH or P%, then optionally ! (ignore the aspect ratio), ^ (fill the box), > (only shrink) or < (only enlarge).
  --fit <mode>              How a WxH box is filled: contain (inside, the default), cover (filled and cropped), fill (stretched) or pad.
  --gravity <anchor>        What cover keeps and where pad places the image: center (the default), top, top-left, ...
  --filter <filter>         The resampling kernel: bicubic (Catmull-Rom, the default), lanczos3, mitchell, bilinear, nearest, ...
  --linear                  Blend in linear light rather than in the encoded values.
  --background <color>      The color --fit pad fills with (transparent by default).
  --keep-metadata           Keep EXIF, XMP, IPTC and text, which thumbnails leave out by default.
```

## crop

```text
Description:
  Crop images to a rectangle.

Usage:
  egami crop <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>          Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>        The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>            Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                   Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>             Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>           Bits per channel to write (PNG, AVIF).
  --strip                      Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>        Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient                Turn the image upright from its EXIF orientation first.
  --to <profile>               Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive              Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>           How many files to work on at once; 0 (the default) for one per processor.
  --in-place                   Allow outputs to replace their inputs.
  --fail-fast                  Stop at the first file that fails.
  --rect <x,y,w,h> (REQUIRED)  The area to keep: x,y,width,height in pixels.
```

## rotate

```text
Description:
  Rotate images clockwise. A JPEG turned by a multiple of 90 degrees into a JPEG is rotated losslessly.

Usage:
  egami rotate <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient             Turn the image upright from its EXIF orientation first.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  -a, --angle <degrees>     Degrees clockwise; the last input may give it instead, as in 'egami rotate in.jpg 90 -o out.jpg'.
  --filter <filter>         The resampling kernel for angles that are not a multiple of 90.
  --background <color>      The color of the corners an angle uncovers (transparent by default).
  --reencode                Decode and encode a JPEG even when a lossless rotation is possible.
```

## flip

```text
Description:
  Mirror images left to right (horizontal), top to bottom (vertical) or both. A JPEG flipped into a JPEG is flipped losslessly.

Usage:
  egami flip <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>          Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>        The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>            Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                   Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>             Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>           Bits per channel to write (PNG, AVIF).
  --strip                      Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>        Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient                Turn the image upright from its EXIF orientation first.
  --to <profile>               Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive              Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>           How many files to work on at once; 0 (the default) for one per processor.
  --in-place                   Allow outputs to replace their inputs.
  --fail-fast                  Stop at the first file that fails.
  -d, --direction <direction>  horizontal (mirror left to right), vertical (top to bottom) or both; the last input may give it instead.
  --reencode                   Decode and encode a JPEG even when a lossless flip is possible.
```

## auto-orient

```text
Description:
  Turn images upright from their EXIF orientation and reset it. A JPEG into a JPEG is turned losslessly.

Usage:
  egami auto-orient <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  --reencode                Decode and encode a JPEG even when a lossless turn is possible.
```

## run

```text
Description:
  Run steps over images, separated by +: 'egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82'. Every Egami operation is a step ('egami run --list'); save writes, and may come more than once.

Usage:
  egami run [<inputs-and-steps>...] [options] [[--] <additional arguments>...]]

Arguments:
  <inputs-and-steps>  Inputs (files, globs, -), then steps separated by +.

Options:
  -o, --output <path>       Where a save without a path writes, and where the plan writes when it has no save: a file, folder, template or -.
  -w, --workflow <file>     Take the steps from a JSON workflow file.
  --save-workflow <file>    Write the steps to a JSON workflow file (then run them, unless --dry-run).
  --dry-run                 Print the steps and the files each input would write, and stop.
  --list                    List every step with its arguments.
  --to <profile>            Convert colors to this profile while reading.
  -r, --recursive           Take every image in folders given as inputs.
  -j, --jobs <count>        How many files to work on at once; 0 for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.

Additional Arguments:
  Arguments passed to the application that is being run.
```

## compare

```text
Description:
  Compare two images: print a metric and exit with 1 when they differ more than --threshold.

Usage:
  egami compare <image> <other> [options]

Arguments:
  <image>  The first image.
  <other>  The image to compare it with, the same size.

Options:
  -m, --metric <metric>     The number to print and test: ae (pixels that differ), mae, mse, rmse (the default), peak, psnr, ssim or dssim. [default: rmse]
  -t, --threshold <value>   Exit with 1 when the metric is worse than this (above it, or below it for psnr and ssim); without it, any difference exits with 1.
  --fuzz <0-1>              How far apart two values may be and still count as equal, from 0 to 1, for ae and the exit code.
  -d, --diff <path>         Write a picture of the differences.
  --diff-style <style>      highlight (differing pixels in red, as ImageMagick's compare) or absolute (|a - b|). [default: highlight]
  --json                    Write every metric per channel as JSON.
```

## stats

```text
Description:
  Measure images: per-channel minimum, maximum, mean, deviation, skewness, kurtosis and entropy, as ImageMagick's identify -verbose.

Usage:
  egami stats <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast               Stop at the first file that fails.
  --json                    Write JSON instead of text.
  --frame <index>           The frame to measure, from 0.
  -a, --all                 Also count the colors and give perceptual hashes and the pixel signature.
```

## ocr

```text
Description:
  Read the text of images with an OCR program (Tesseract or any that writes hOCR or ALTO), as plain text, hOCR or ALTO.

Usage:
  egami ocr <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  --engine-command <command> (REQUIRED)  The OCR program and its arguments, run once per image: {image} is the image (PNG), {output} the result file without extension, {langs} the languages, {dpi} the resolution. It must write hOCR or ALTO, such as "tesseract {image} {output} -l {langs} --dpi {dpi} hocr".
  -o, --output <path>                    Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - (the default) for standard output.
  --as <text|hocr|alto>                  text, hocr or alto; by default from -o's extension (.txt, .hocr or .html, .xml), else text.
  -l, --lang <languages>                 The languages to read, as the program names them, joined by + or commas, such as eng+nor.
  --dpi <dpi>                            The image's resolution, which programs use to judge text size; by default the image's own, else 300.
  --engine-timeout <seconds>             Seconds one run of the program may take; 300 by default.
  -r, --recursive                        Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>                     How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast                            Stop at the first file that fails.
```

## frames

```text
Description:
  Split an animation (GIF, APNG, WebP) into still images, or join stills into one.

Usage:
  egami frames [command] [options]

Options:

Commands:
  split <input>  Write every frame of an animation as its own image, each a full picture as the animation shows it.
  join <inputs>  Join still images, all the same size, into an animation.
```

### frames split

```text
Description:
  Write every frame of an animation as its own image, each a full picture as the animation shows it.

Usage:
  egami frames split <input> [options]

Arguments:
  <input>  The animation, or - for standard input.

Options:
  -o, --output <template> (REQUIRED)  A template for each frame's file: {frame} or {index} is the frame number from 0, padded as in {frame:000}; {name} is the input's name.
  -f, --format <format>               The output format, when the template's extension does not say.
  --set <fmt.key=value>               Writer settings as fmt.key=value.
  --delays <path>                     Also write each frame's delay in milliseconds, one per line, to this file, so 'frames join --delays' can restore the timing.
```

### frames join

```text
Description:
  Join still images, all the same size, into an animation.

Usage:
  egami frames join <inputs>... [options]

Arguments:
  <inputs>  The frames in order: files or globs (sorted by name).

Options:
  -o, --output <path> (REQUIRED)  The animation to write: .gif, .png (APNG) or .webp.
  -f, --format <format>           The output format, when the extension does not say.
  --delay <time>                  How long each frame shows: 40ms, 0.04s or 40 (milliseconds). 100ms by default.
  --delays <path>                 A file of delays in milliseconds, one per frame, as 'frames split --delays' writes.
  --fps <rate>                    Frames per second, instead of --delay.
  --loop <count>                  How many times the animation plays; 0 (the default) loops forever.
  --set <fmt.key=value>           Writer settings as fmt.key=value.
  --quality <0-100>               Lossy quality for WebP.
  --lossless                      Lossless WebP.
```

## metadata

```text
Description:
  Show, strip or copy metadata: color profile, EXIF, XMP, IPTC, text and resolution.

Usage:
  egami metadata [command] [options]

Options:

Commands:
  show <inputs>   Show every piece of metadata an image holds.
  strip <inputs>  Remove metadata. A JPEG losing everything is rewritten without decoding; otherwise the image is written again, which a lossy format allows only with --reencode.
  copy <inputs>   Copy metadata from one image to others, which are written again with it; a lossy format only with --reencode.
```

### metadata show

```text
Description:
  Show every piece of metadata an image holds.

Usage:
  egami metadata show <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --fail-fast               Stop at the first file that fails.
  --json                    Write JSON, with every EXIF entry, IPTC dataset, text entry and the XMP packet.
```

### metadata strip

```text
Description:
  Remove metadata. A JPEG losing everything is rewritten without decoding; otherwise the image is written again, which a lossy format allows only with --reencode.

Usage:
  egami metadata strip <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  --keep <blocks>           Blocks to keep, comma-separated: icc, exif, xmp, iptc, text, resolution.
  --reencode                Allow writing a lossy format (JPEG, lossy WebP, AVIF, JPEG XL) again, which costs a generation.
```

### metadata copy

```text
Description:
  Copy metadata from one image to others, which are written again with it; a lossy format only with --reencode.

Usage:
  egami metadata copy <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>        Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>      The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>          Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                 Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>           Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>         Bits per channel to write (PNG, AVIF).
  --strip                    Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>      Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  -r, --recursive            Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>         How many files to work on at once; 0 (the default) for one per processor.
  --in-place                 Allow outputs to replace their inputs.
  --fail-fast                Stop at the first file that fails.
  --from <image> (REQUIRED)  The image to copy metadata from.
  --only <blocks>            Blocks to copy, comma-separated: icc, exif, xmp, iptc, text, resolution; all by default.
  --reencode                 Allow writing a lossy format (JPEG, lossy WebP, AVIF, JPEG XL) again, which costs a generation.
```

## color

```text
Description:
  Convert images to another color profile (ICC, 2.1.2), tone mapping HDR to SDR where needed (2.9.1).

Usage:
  egami color <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>         Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>       The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>           Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                  Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>            Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>          Bits per channel to write (PNG, AVIF).
  --strip                     Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>       Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  -r, --recursive             Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>          How many files to work on at once; 0 (the default) for one per processor.
  --in-place                  Allow outputs to replace their inputs.
  --fail-fast                 Stop at the first file that fails.
  --auto-orient               Turn the image upright from its EXIF orientation first.
  --to <profile> (REQUIRED)   The profile to convert to: srgb, display-p3, adobe-rgb, rec2020, rec2100-pq, ... or an ICC file.
  --intent <intent>           perceptual (the default), relative-colorimetric, saturation or absolute-colorimetric.
  --black-point-compensation  Map black to black between profiles with different black points.
  --tone-map <method>         How HDR is brought into a standard range: bt2390 (the default), reinhard or clip.
  --peak <nits>               The HDR source's peak brightness in nits, when the file does not say.
  --reference-white <nits>    The brightness of SDR white in nits (203 by default).
```

## text

```text
Description:
  Draw a caption or watermark on images, with any font and script, wrapped to fit (2.8.2 layout).

Usage:
  egami text <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient             Turn the image upright from its EXIF orientation first.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  -t, --text <text>         The text; the last input may give it instead: egami text in.png "(c) 2026" -o out.png.
  --font <font>             A font file, an installed family name, or sans-serif, serif or monospace. Liberation Sans (bundled) by default.
  --font-size <pixels>      The font size in pixels. [default: 24]
  --color <color>           The text color, a CSS color (white by default).
  --anchor <anchor>         Where the text sits: bottom-right (the default), bottom, center, top-left, ...
  --margin <pixels>         The distance from the image's edges in pixels. [default: 16]
  --offset <x,y>            A further shift of the text in pixels, x,y.
  --rotate <degrees>        Degrees clockwise to turn the text around its center.
  --opacity <0-1>           From 0 (invisible) to 1 (the default). [default: 1]
  --width <pixels>          The width to wrap lines at, in pixels; the image's width less the margins by default.
  --shadow <color>          A drop shadow in this color, offset by a tenth of the size.
```

## overlay

```text
Description:
  Lay an image (a logo, a watermark, an SVG) on images, placed by anchor, with opacity and a blend mode.

Usage:
  egami overlay <inputs>... [options]

Arguments:
  <inputs>  Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.

Options:
  -o, --output <path>       Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.
  -f, --format <format>     The output format by name or extension, when -o's extension does not say (and always for -o -).
  --quality <0-100>         Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).
  --lossless                Lossless encoding (WebP, AVIF, JPEG XL).
  --effort <level>          Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).
  --bit-depth <bits>        Bits per channel to write (PNG, AVIF).
  --strip                   Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right.
  --set <fmt.key=value>     Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.
  --auto-orient             Turn the image upright from its EXIF orientation first.
  --to <profile>            Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.
  -r, --recursive           Take every image in folders given as inputs, at any depth.
  -j, --jobs <count>        How many files to work on at once; 0 (the default) for one per processor.
  --in-place                Allow outputs to replace their inputs.
  --fail-fast               Stop at the first file that fails.
  -i, --image <image>       The image to lay on top (SVG too); the last input may give it instead: egami overlay in.png logo.svg -o out.png.
  --anchor <anchor>         Where it sits: bottom-right (the default), center, top-left, ...
  --margin <pixels>         The distance from the image's edges in pixels.
  --offset <x,y>            A further shift in pixels, x,y.
  --opacity <0-1>           From 0 (invisible) to 1 (the default). [default: 1]
  --blend <mode>            The blend mode: normal (the default), multiply, screen, overlay, darken, lighten, ...
  -s, --size <size>         Resize the overlay first, in the size syntax (W, xH, WxH, P%).
```

## montage

```text
Description:
  Lay images out on a contact sheet: a grid of tiles, optionally labeled with their file names.

Usage:
  egami montage <inputs>... [options]

Arguments:
  <inputs>  The images, in order: files or globs (sorted by name).

Options:
  -o, --output <path> (REQUIRED)  The contact sheet to write.
  -f, --format <format>           The output format, when the extension does not say.
  -c, --columns <count>           Images per row; enough for a square sheet by default.
  --tile <WxH>                    Each image's box; images are shrunk to fit it, never enlarged. [default: 200x200]
  --spacing <pixels>              The gap between tiles, in pixels. [default: 8]
  --background <color>            The sheet's color (white by default).
  --labels                        Write each file's name under its image.
  --font <font>                   The label font: a file, an installed family, or sans-serif, serif or monospace.
  --label-size <pixels>           The label size in pixels. [default: 13]
  --set <fmt.key=value>           Writer settings as fmt.key=value.
  --quality <0-100>               Lossy quality from 0 to 100.
```

## formats

```text
Description:
  List the formats egami reads and writes, or one format's writer settings for --set.

Usage:
  egami formats [<format>] [options]

Arguments:
  <format>  A format name or extension, to list its writer settings.

Options:
  --json                    Write JSON instead of text.
```

## mcp

```text
Description:
  Serve egami's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.

Usage:
  egami mcp [options]

Options:
  --allow <folder>          A folder the tools may read and write, besides the folders the assistant opened; give it once per folder. With neither, the working folder.
  --max-inputs <count>      The most files one tool call may take; 1000 by default. [default: 1000]
```
