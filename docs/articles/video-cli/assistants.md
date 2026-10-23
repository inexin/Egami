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
