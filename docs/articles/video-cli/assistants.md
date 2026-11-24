# egami-video for AI assistants

`egami-video mcp` serves egami-video's commands as tools to an AI assistant (Claude, GitHub Copilot, Cursor) over the Model
Context Protocol, on standard input and output. It runs on the same server code as [egami mcp](../cli/assistants.md): one tool per
command (ffmpeg and ffprobe mode aside), with the command's options as arguments.

```json
{
  "mcpServers": {
    "egami-video": { "command": "egami-video", "args": ["mcp"] }
  }
}
```

- Tools read and write only inside the folders the assistant opened, and those given with `--allow <folder>`. Without either they
  use the working folder.
- Outputs never replace inputs unless a call sets `in_place`. Standard input and output carry the protocol, so `-` is refused.
- Every result is an object: `exitCode`, `written` (the files written), `output` (the command's JSON or text) and `messages`.
  `info` answers in its JSON.
- Long jobs send progress notifications as they go, with the same line the terminal shows, and stop when the call is cancelled,
  leaving no partial file.
- A call may ask for a picture of the first file it wrote (`preview`): an image, or a video's first frame, fit to 1024 pixels.
- `--max-inputs` (1000 by default) bounds the files one call takes; `--limit` sets the safety limits for decoded frames.

## Prompts and resources

egami-video serves these documents as MCP resources:

| Resource | Holds |
| --- | --- |
| `egami-video://schema/info` | The JSON schema of what `info` returns |
| `egami-video://schema/metadata` | The JSON schema of what `metadata` writes and `--load` reads |
| `egami-video://formats` | The containers and codecs, as `egami-video formats --json` prints them |
| `egami-video://guide` | The [guide for coding agents](https://inexin.github.io/Egami/v1/agents/Egami.Video.Cli.md) |

and offers common jobs as MCP prompts, each one message that names the tools to call:

| Prompt | Arguments | Does |
| --- | --- | --- |
| `make_clip` | `file`, `start`, `end` | A clip cut out with `trim`, without encoding |
| `make_gif` | `file`, `start`, `length` | An animated GIF of a stretch of the video, with `animate` |
| `web_video` | `file` | An MP4 browsers play: H.264, HEVC and AV1 copied with `remux`, anything else encoded to AV1 |
| `extract_audio` | `file`, `format` | The sound as an audio file: copied with `extract` where it fits, else decoded with `audio` |

`length` and `format` may be left out. egami-video does not encode H.264 or HEVC, so `web_video` writes AV1 where it has to
encode.
