# Egami.Cli.Host

The command host that Egami's command-line tools share, as source: `egami` builds on it, and so does Egap's `egap`. A tool
references the package as a development dependency, and its files compile into the tool with internal types only, so the
package adds no public API and no assembly.

- Exit codes 0 (done), 1 (a file failed or a check found a problem), 2 (usage) and 3 (a safety limit).
- Glob expansion that behaves the same in every shell (`*`, `?`, `[abc]`, `**`), `--recursive` folders, and output templates
  with `{name}`, `{ext}`, `{dir}`, `{index}`, a part number and `{format}`.
- Parallel batches that carry on past a failed file and end with a summary; outputs written through a temporary name.
- `<tool> <name>` runs a `<tool>-<name>` program from the PATH, as git and dotnet do.
- An MCP server over standard input and output: one tool per command of a System.CommandLine tree, the folders the assistant
  opened or `--allow` names, progress and cancellation.

A tool derives its session from `CliHostSession` and describes its MCP tools with an `McpToolSet`. The sources use implicit
usings and nullable reference types, and need System.CommandLine and ModelContextProtocol.Core: reference both in the tool, as Egami.Cli does.
MIT licensed.
