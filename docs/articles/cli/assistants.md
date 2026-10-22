# egami for AI assistants

`egami mcp` serves egami's commands to an AI assistant, such as Claude, GitHub Copilot or Cursor, over the
[Model Context Protocol](https://modelcontextprotocol.io). The assistant can then look at, convert, resize and edit images on
your computer when you ask it to, with the same commands, options and results as the shell. It needs nothing besides the
egami tool: no network, no service and no key.

```sh
dotnet tool install --global Egami.Cli
egami mcp --allow ~/Pictures
```

You don't run `egami mcp` yourself: the assistant starts it and talks to it on standard input and output. The sections below
show where each assistant takes the command.

## The tools

Every command is a tool with the command's name, words joined by `_`: `info`, `convert`, `resize`, `thumbnail`, `crop`,
`rotate`, `flip`, `auto_orient`, `run`, `compare`, `stats`, `frames_split`, `frames_join`, `metadata_show`, `metadata_strip`,
`metadata_copy`, `color`, `text`, `overlay`, `montage` and `formats`. Their arguments are the command's arguments and options
in snake case (`--bit-depth` is `bit_depth`), with the same descriptions as `egami <command> --help`, so the
[command reference](commands.md) covers them too. `--jobs` and `--fail-fast` are left out: a call works on one
file per processor and keeps going past a file that fails, reporting each one.

- `run` takes its steps as a workflow object, the same JSON as a [workflow file](index.md#workflow-files), and
  `list_steps` lists the steps and their options.
- Tools that write take `preview`: when it is true, the result also holds the first file written as a PNG picture, at most
  1024 pixels on its long side, so the assistant can look at what it made.
- Every result is a JSON object with `exitCode` ([as in the shell](index.md#exit-codes-messages-and-limits)), `written` (the
  files written), `output` (the command's JSON, for commands with `--json`, or its text) and `messages` (warnings and errors).
  A usage mistake, a failed file or a safety limit marks the call as failed, so the assistant sees it and can try again;
  `compare` finding a difference is a result, not a failure.

## The folders egami may use

A tool reads and writes only inside the folders egami may use, and refuses any other path, including one reached through a
symbolic link or `..`:

- the folders given with `--allow`, once per folder, and the folders the assistant has open (for example the project
  open in VS Code), when the assistant tells the server about them;
- when there are neither, the folder egami was started in. egami refuses to start in the root of a disk without `--allow`,
  because some assistants start servers there.

Outputs never replace an input unless the call sets `in_place`. Inputs and outputs must be files: standard input and output
carry the protocol, so `-` is refused. One call takes at most 1,000 files; `--max-inputs` changes that. The safety limits of
`--limit` and `EGAMI_LIMITS` apply to every call, as in the shell.

## Claude Desktop

Add egami to `claude_desktop_config.json` (Settings, Developer, Edit Config) and restart Claude:

```json
{
  "mcpServers": {
    "egami": {
      "command": "egami",
      "args": ["mcp", "--allow", "/Users/me/Pictures"]
    }
  }
}
```

On Windows, write the folder as `C:\\Users\\me\\Pictures`. Claude Desktop starts servers outside your folders, so give each
folder egami may use with `--allow`.

## Claude Code

```sh
claude mcp add egami -- egami mcp
```

Claude Code starts egami in the project's folder, which egami then uses. Add `--allow <folder>` for others.

## VS Code

Add egami to `.vscode/mcp.json` in your project. `dnx` runs the tool straight from NuGet, so it needs no install:

```json
{
  "servers": {
    "egami": {
      "type": "stdio",
      "command": "dnx",
      "args": ["Egami.Cli", "--yes", "--", "mcp"]
    }
  }
}
```

VS Code tells egami about the folders of the workspace. Other assistants take the same command and arguments in their own
settings.

## Logging

`-v` and `-vv` write what the server does (the folders it uses, each call) to standard error, which most assistants show in
their server log. Standard output carries only the protocol.
