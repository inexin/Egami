# Egami.Cli: guide for coding agents

Egami.Cli is the `egami` command: it converts, inspects, resizes and edits image files from a shell, a build script or an AI
assistant, with Egami, a fully managed .NET imaging library. It installs as a .NET tool (`dotnet tool install --global
Egami.Cli`) or runs once without installing (`dnx Egami.Cli info photo.jpg`); native binaries named `egami` come with each release.
It reads and writes every format of Egami, Egami.Svg, Egami.Metafiles and Egami.Formats.Extended; `egami formats` lists them.

## Commands

- `info`: format, size, frames, color and (with `--all`) metadata. `--json` for scripts.
- `convert`, `resize`, `thumbnail`, `crop`, `rotate`, `flip`, `auto-orient`: one operation per command, on any number of files.
- `run`: a chain of steps (`resize 800x + sharpen 0.5 + save out.webp`), or a JSON workflow file with `--workflow`.
- `compare`, `stats`: differences and channel statistics; `compare` exits 1 when the images differ.
- `frames split`, `frames join`, `layers`, `metadata show|strip|copy`, `color`, `text`, `overlay`, `montage`, `trace`, `ocr`.
- `font convert|subset|info`: fonts (TTF, OTF, TTC, WOFF, WOFF2) to WOFF2, WOFF or back to TTF/OTF/TTC; subsets for the web;
  names, tables and Unicode coverage.
- `formats` lists the formats; `formats <format>` lists a writer's settings for `--set fmt.key=value`.
- `mcp` serves the commands to an AI assistant (see below).

## Calling egami from a script

- Inputs are files, globs (`*`, `?`, `**`, expanded by egami the same way in every shell; quote them) or folders with
  `--recursive`. `-` reads standard input.
- `-o` is a file, a folder or a template with `{name}`, `{ext}`, `{dir}`, `{index}` and `{format}`. The extension picks the
  format; `-f` names it when the extension does not say.
- Outputs never replace inputs unless `--in-place` is given.
- Results (`info`, `stats`, `compare`) go to standard output; messages go to standard error. Add `--json` where a script reads
  the result, and `-q` to keep only errors.
- Batches run one file per processor (`--jobs` changes that) and keep going past a failed file; `--fail-fast` stops at the
  first.

Exit codes:

| Code | Means |
| --- | --- |
| 0 | Everything worked |
| 1 | A file failed, or `compare` found a difference |
| 2 | The command line is wrong |
| 3 | An input exceeded a safety limit (`--limit`, `EGAMI_LIMITS`) |

## Samples

Look at a file, then convert and resize it:

```sh
egami info photo.jpg --json
egami convert photo.jpg -o photo.webp --quality 82
egami resize "photos/*.jpg" --size 800x -o small/{name}.jpg
egami thumbnail "photos/*.jpg" --size 160x160 --fit cover -o thumbs/{name}.png
```

Chain steps in one pass, and keep them as a workflow file:

```sh
egami run photo.jpg auto-orient + resize 1200x + sharpen 0.6 + save web.webp --quality 82
egami run photo.jpg resize 1200x + sharpen 0.6 -o web/{name}.webp --save-workflow web.json --dry-run
egami run "photos/*.jpg" --workflow web.json -o web/{name}.webp
```

A workflow file is the steps of `run` as JSON, checked against the schema
`https://raw.githubusercontent.com/inexin/Egami/main/docs/schemas/egami-workflow-1.json`; `egami run --list` lists the steps
and their options. Branching and conditions stay out: a script is the right tool for logic.

`web.json`:

```json
{
  "egami": 1,
  "steps": [
    { "op": "auto-orient" },
    { "op": "resize", "size": "1200x" },
    { "op": "save", "format": "webp", "quality": 82 }
  ]
}
```

Metadata, writer settings and a check:

```sh
egami metadata show photo.jpg
egami metadata strip photo.jpg -o clean.jpg
egami convert logo.png -o logo-small.png --set png.compression-level=9
egami compare logo.png logo-small.png
```

Fonts for the web: WOFF2 of a whole font, a subset with only the characters of some pages (shaped as the full font shapes
them), a description:

```sh
egami font convert fonts/NotoSans-Regular.ttf -o web/
egami font subset fonts/NotoSans-Regular.ttf --text-file "pages/*.html" -o web/NotoSans-subset.woff2
egami font subset fonts/NotoSans-Regular.ttf --unicodes U+0020-007E --no-layout -o web/ascii.woff2
egami font info fonts/NotoSans-Regular.ttf --json
```

## The MCP server

`egami mcp` serves every command as a tool to an AI assistant over the Model Context Protocol, on standard input and output.
Tools are the command names in snake case (`metadata_strip`), their arguments the options in snake case (`bit_depth`). `ocr`
is not a tool. Calls use only the folders the assistant opened and those given with `--allow`; `-` is refused. Every result
is an object with `exitCode`, `written`, `output` and `messages`; tools that write take `preview` for a picture of the first
file written.

Resources: `egami://schema/workflow` (the workflow schema), `egami://formats` (what `egami formats --json` prints) and
`egami://guide` (this guide). Prompts: `thumbnails`, `convert_folder`, `web_images`, `strip_metadata` and `make_workflow`;
each returns one message naming the tools to call.

## Common mistakes

- Leaving a glob unquoted: the shell expands it first, differently in each shell. Quote it and egami expands it the same way
  everywhere.
- Writing `-o -` without `-f`: standard output has no extension, so name the format.
- Writing over the originals: egami refuses unless `--in-place`. Write to a new folder with `-o out/{name}.{ext}`.
- Re-encoding a JPEG to strip metadata: `metadata strip` without `--keep` rewrites a JPEG without decoding it.
- Treating exit code 1 from `compare` as an error: it means the images differ.
- Reading stdout for messages: they go to standard error; stdout carries only results.
- Calling `run` steps with option names: steps take the step's words (`resize 800x`), or the JSON workflow's properties.
- Calling `font subset` with no `--text`, `--text-file` or `--unicodes`: it needs to know what to keep (exit code 2). Its
  default keeps the layout tables, cut down and renumbered, so pass `--no-layout` only when the smallest file matters more than
  ligatures and kerning, and `--retain-gids` only when something refers to the glyphs by id.

## More

- The egami command: https://inexin.github.io/Egami/v1/articles/cli/index.html
- Commands: https://inexin.github.io/Egami/v1/articles/cli/commands.html
- Steps of run: https://inexin.github.io/Egami/v1/articles/cli/steps.html
- egami for AI assistants: https://inexin.github.io/Egami/v1/articles/cli/assistants.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
