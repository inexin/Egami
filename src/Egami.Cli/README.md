# Egami.Cli

The `egami` command: convert, inspect, resize and edit images from a shell or a build script with
[Egami](https://github.com/inexin/Egami), a fully managed imaging library for .NET.

```sh
dotnet tool install --global Egami.Cli
egami info photo.jpg
egami convert in.png -o out.webp --quality 80
egami thumbnail "photos/*.jpg" -o thumbs/{name}.webp --size 256
egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82
egami run "photos/**/*.jpg" --workflow web.json -o web/{dir}/{name}.webp --jobs 8
```

- Single commands for everyday jobs: info, convert, resize, thumbnail, crop, rotate, flip, auto-orient, compare, stats, frames,
  layers, metadata, color, text, overlay, montage and formats.
- `run` chains any of Egami's operations over each image, and JSON workflow files keep a recipe.
- Every command takes many files and globs, and works on several at once.
- Reads every format Egami reads, SVG and WMF/EMF included; writes PNG, JPEG, WebP, AVIF, JPEG XL, TIFF, GIF and more.
- `egami mcp` serves the commands as tools to AI assistants (Claude, GitHub Copilot, Cursor) over the Model Context Protocol,
  reading and writing only inside the folders you allow.
- Exit codes 0 (done), 1 (a file failed), 2 (usage) and 3 (a safety limit), with Egami's diagnostics on standard error.

The [documentation](https://inexin.github.io/Egami/v1/articles/cli/index.html) explains the commands, the size syntax, output
templates, writer settings, workflow files and batches. MIT licensed.
