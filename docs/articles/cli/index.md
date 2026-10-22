# The egami command

`egami` converts, inspects, resizes and edits images from a shell or a build script, with no C# to write. It is the
`Egami.Cli` package, a .NET tool built on Egami's public API, and reads every format Egami and its packages read: SVG,
WMF and EMF included. It has three layers:

- **Single commands** for everyday jobs: `info`, `convert`, `resize`, `thumbnail`, `crop`, `rotate`, `flip`, `auto-orient`,
  `compare`, `stats`, `frames`, `metadata`, `color`, `text`, `overlay`, `montage` and `formats`.
- **`run`**, which chains any number of steps over each image. Every Egami operation is a step, and the same steps can be
  saved as a JSON workflow file.
- **Batches**: every command takes many files, globs and folders, and works on several at once.

The [command reference](commands.md) lists every option, and [steps for run](steps.md) every step. `egami mcp` serves the same
commands to an AI assistant: see [egami for AI assistants](assistants.md).

```sh
egami info photo.jpg
egami convert in.png -o out.webp --quality 80
egami resize in.jpg -o out.jpg --size 800x600
egami thumbnail "photos/*.jpg" -o thumbs/{name}.webp --size 256
egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82
cat in.png | egami convert - -o - --format qoi > out.qoi
```

## Installing

Egami.Cli is a .NET tool, so it needs the .NET 10 runtime:

```sh
dotnet tool install --global Egami.Cli
```

`dnx Egami.Cli info photo.jpg` runs it once without installing. The tool is built free of reflection, with the trimming and
Native AOT analyzers on, so it also publishes as a standalone native program (see [Native binaries](#native-binaries)).

## Inputs

Inputs are files, globs or `-` for standard input. egami expands globs itself, so they work the same in bash, zsh, cmd and
PowerShell: `*` and `?` match within a name, `[abc]` a set, and `**` any depth of folders. Quote them so the shell leaves them
alone. A folder needs `--recursive`, and then means every image below it. Files come in sorted order, so `{index}` and the
order of a batch never depend on the file system.

## Outputs

`-o` names where to write:

| `-o` | Writes |
| --- | --- |
| `out.png` | That file. The format comes from the extension, or `--format`. |
| `out/` or an existing folder | `out/<name>.<ext>`, keeping each input's name. |
| `web/{dir}/{name}.webp` | A template: `{name}` (the input's name without extension), `{ext}` (its extension), `{dir}` (its folder below the glob's start), `{index}` (its position from 0, padded with `{index:000}`), `{format}` (the output format's extension). `{{` and `}}` write braces. |
| `-` | Standard output. The format is `--format`, or the input's. |

With several inputs, `-o` must be a folder or a template that names each input. An output never replaces its input unless
`--in-place` is given; `--in-place` without `-o` writes each file over itself. Files are written to a temporary name first and
replace the target only when complete.

## Formats and writer settings

The output format comes from the extension or `--format`. The common settings have flags: `--quality`, `--lossless`, `--effort`,
`--bit-depth` and `--strip`, which leaves out EXIF, XMP, IPTC, text and resolution but keeps the color profile, so colors
stay right (ImageMagick's `-strip` drops the profile too). A flag the format does not have is skipped with a warning. Every other writer
setting is `--set fmt.key=value`, where the keys are the format's `WriterOptions` properties in kebab case:

```sh
egami formats png        # lists png.compression-level, png.filter, png.palette.max-colors, ...
egami convert in.tif -o out.png --set png.compression-level=9 --set png.filter=paeth
```

`egami formats` lists what egami reads and writes. The keys come straight from the library, so the tool never lags it. HEIC is
not part of the tool, since HEVC is covered by patents.

## Sizes

`resize`, `thumbnail` and the `resize` step take the size syntax, the common subset of ImageMagick's geometry:

| Size | Means |
| --- | --- |
| `800` | 800 wide, height in proportion |
| `x600` | 600 high, width in proportion |
| `800x600` | Fit inside 800 × 600, aspect ratio kept |
| `50%`, `50%x25%` | A percentage of the size |
| `800x600!` | Exactly 800 × 600, aspect ratio ignored |
| `800x600^` | At least 800 × 600, aspect ratio kept (fills the box, nothing cropped) |
| `800x600>` | Only shrink: images already smaller are left alone |
| `800x600<` | Only enlarge |

`--fit` says how a box is filled: `contain` (inside it, the default), `cover` (filled and cropped, with `--gravity` choosing what
stays), `fill` (stretched) or `pad` (inside, then padded with `--background`). `--filter` picks the resampling kernel (`bicubic`,
Catmull-Rom, by default, as in the library) and `--linear` blends in linear light.

## Defaults

Every command keeps the library's defaults, except `thumbnail`, which is the web-image shortcut: it turns the image upright from
its EXIF orientation, shrinks it (never enlarges) to fit `--size` (256 by default), converts it to sRGB and leaves out
metadata. The other commands do those only when asked: `--auto-orient`, `--to srgb` and `--strip`.

`rotate`, `flip` and `auto-orient` turn a JPEG written as JPEG without decoding it, so no quality is lost, when the angle
allows; `--reencode` decodes instead. `flip` has no default direction: give `horizontal`, `vertical` or `both`, since
ImageMagick's `-flip` and a photo app's flip mirror opposite ways. `metadata strip` without `--keep` rewrites a JPEG without
decoding it too; any other metadata edit that would write a lossy format again (JPEG, lossy WebP, AVIF, JPEG XL) asks for
`--reencode`.

## Chaining steps with run

```sh
egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82
egami run in.jpg save full.png + resize 256x256 + save thumb.webp
egami run "photos/**/*.jpg" grayscale + contrast 1.2 -o gray/{dir}/{name}.jpg --jobs 8
```

Steps are separated by a bare `+`, which no shell treats specially. Each step's options follow its name: positional values fill
the parameters in order, `--name value` sets one by name. `save` writes the image as it is at that point, and may come more than
once; a plan without a save writes to `-o`. Writer flags outside a `save` apply to every save. `--dry-run` prints the steps and
the files each input would write.

Every single command is turned into the same kind of plan, so `egami resize --size 800x` and `egami run resize 800x` give the
same bytes. A plan of steps that can stream (crop, resize and the per-pixel color steps) runs through `Raster.Transcode`, a
band of rows at a time; anything else loads the image, runs the steps and saves. When a plan starts with a resize, the image
is decoded at no less than one and a half times the result (`ReaderOptions.MinimumSize`), so a large JPEG is decoded at a half,
a quarter or an eighth of its size and the resize filter still has real work to do. Either way the result is what the same
`RasterOps` chain with the same reader options gives in C#.

## Workflow files

A workflow file holds the steps of `run` as JSON, one object per step with the step's options as properties. It is checked
against [the published schema](https://raw.githubusercontent.com/inexin/Egami/main/docs/schemas/egami-workflow-1.json):

```json
{
  "egami": 1,
  "steps": [
    { "op": "auto-orient" },
    { "op": "resize", "size": "1200x", "filter": "lanczos3" },
    { "op": "sharpen", "sigma": 0.6 },
    { "op": "save", "format": "webp", "quality": 82, "set": { "webp.effort": 5 } }
  ]
}
```

```sh
egami run in.jpg resize 1200x + sharpen 0.6 --save-workflow web.json --dry-run   # write the file from a command line
egami run "photos/*.jpg" --workflow web.json -o web/{name}.webp
```

A `save` in a workflow may give a `path`; one without writes to `-o`. Branching and conditions stay out: a script is the right
tool for logic.

## Batches

Every command takes many inputs. `--jobs` sets how many files run at once (one per processor by default); each image is then
worked on by one core, and since Egami gives the same bytes at any core count, `--jobs` never changes an output. A file that
fails is reported and the rest carry on; `--fail-fast` stops at the first. A summary goes to standard error when anything failed.

## Exit codes, messages and limits

| Code | Means |
| --- | --- |
| 0 | Everything worked |
| 1 | A file failed (missing, corrupt, unwritable), or `compare` found a difference above `--threshold` |
| 2 | The command line is wrong: an unknown command, option or value |
| 3 | An input exceeded a safety limit |

Messages and Egami's own diagnostics go to standard error: warnings by default, `-v` adds information, `-vv` debugging detail,
and `-q` keeps only errors. Results (`info`, `stats`, `compare`) go to standard output, so they can be piped.

The library's safety limits apply to every input. `--limit` raises or lowers them, and the `EGAMI_LIMITS` environment variable
sets them for a service:

```sh
egami convert big.tif -o big.png --limit pixels=400M,memory=4GB
EGAMI_LIMITS=pixels=20M,frames=100 egami thumbnail uploads/* -o thumbs/
```

Names: `pixels` (a count such as `50M`, or `WxH`), `width`, `height`, `frames`, `memory` and `metadata` (bytes, such as `512MB`).

## JSON output

`info --json`, `stats --json`, `compare --json`, `metadata show --json` and `formats --json` write JSON for scripts. `info`'s
has a version, `"schema": 1`, which changes only when a field changes meaning or goes away; new fields may be added:

```json
{
  "schema": 1,
  "files": [
    {
      "path": "photo.jpg", "format": "JPEG", "mimeType": "image/jpeg", "width": 4032, "height": 3024,
      "frames": 1, "images": 1, "loopCount": null, "orientation": "rotate90",
      "pixels": { "channels": "rgb", "channelCount": 3, "bitsPerChannel": 8, "channelType": "unorm8", "alpha": "none" },
      "hasMetadata": true, "estimatedBytes": 36578304, "streams": true
    }
  ]
}
```

With `--all`, each file also has `metadata`: the blocks it holds (`icc`, `exif`, `xmp`, `iptc`, `text`, `gain-map`), the color
profile's name and size, resolution, gamma, EXIF's camera, date, orientation and location, the XMP size, IPTC datasets and
text entries. `info` never decodes pixels; `--all` reads the metadata, which may decode a pixel.

## Other tools

A command egami does not know runs the program `egami-<name>` from the PATH with the rest of the arguments, as git does, so
`egami video info clip.webm` starts `egami-video` once Egami.Video's tool is installed. Egap (documents) has its own `egap` tool
with the same conventions.

## Native binaries

Egami.Cli publishes as a single native program that needs no .NET runtime. It answers `egami info` in about 14 ms, where the
dotnet tool takes about 140 ms, most of it starting the runtime:

```sh
dotnet publish src/Egami.Cli -c Release -r linux-x64 -p:EgamiNativeAot=true
```

The `cli-native` workflow builds and checks these for win-x64, linux-x64 and osx-arm64 on every change to the tool, and
keeps them as the run's artifacts.
