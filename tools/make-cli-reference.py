"""Writes the command references from the tools themselves: docs/articles/cli/commands.md from every egami command's --help,
docs/articles/cli/steps.md from 'egami run --list', and docs/articles/video-cli/commands.md from every egami-video command's
--help, so the references never lag the tools. Rerun after changing a command, an option or a RasterOps method.

Usage: python tools/make-cli-reference.py          (from the repo root; builds src/Egami.Cli and src/Egami.Video.Cli in Release first)
       python tools/make-cli-reference.py --check  (exit 1 when the pages are out of date)
"""
import os
import re
import subprocess
import sys

PROJECT = "src/Egami.Cli/Egami.Cli.csproj"
TOOL = "src/Egami.Cli/bin/Release/net10.0/Egami.Cli.dll"
COMMANDS_PAGE = "docs/articles/cli/commands.md"
STEPS_PAGE = "docs/articles/cli/steps.md"
VIDEO_PROJECT = "src/Egami.Video.Cli/Egami.Video.Cli.csproj"
VIDEO_TOOL = "src/Egami.Video.Cli/bin/Release/net10.0/Egami.Video.Cli.dll"
VIDEO_COMMANDS_PAGE = "docs/articles/video-cli/commands.md"


def egami(*args):
    result = subprocess.run(["dotnet", TOOL, *args], capture_output=True, text=True, encoding="utf-8")
    return result.stdout.replace("\r\n", "\n")


def egami_video(*args):
    result = subprocess.run(["dotnet", VIDEO_TOOL, *args], capture_output=True, text=True, encoding="utf-8")
    return result.stdout.replace("\r\n", "\n")


def subcommands(help_text):
    """The command names listed under 'Commands:' in a help text."""
    names = []
    section = help_text.split("Commands:", 1)
    if len(section) < 2:
        return names
    for line in section[1].splitlines():
        match = re.match(r"^  (\S[^ ]*?)(,|\s)", line)
        if match:
            names.append(match.group(1).split(",")[0])
    return names


def commands_page():
    root = egami("--help")
    lines = [
        "# Command reference",
        "",
        "Every egami command with its arguments and options, as `egami <command> --help` prints them. The options of all commands",
        "(`-v`, `-vv`, `-q`, `--limit`, `--raw-*`) are listed once, at the top. [The egami command](index.md) explains the conventions they share.",
        "",
        "```text",
        root.rstrip(),
        "```",
        "",
    ]
    for name in subcommands(root):
        text = egami(name, "--help")
        lines += [f"## {name}", "", "```text", strip_globals(text).rstrip(), "```", ""]
        for child in subcommands(text):
            lines += [f"### {name} {child}", "", "```text", strip_globals(egami(name, child, "--help")).rstrip(), "```", ""]
    return "\n".join(lines)


def video_commands_page():
    root = egami_video("--help")
    lines = [
        "# egami-video command reference",
        "",
        "Every egami-video command with its arguments and options, as `egami-video <command> --help` prints them. The options of all",
        "commands (`-v`, `-vv`, `-q`, `--limit`, `--progress`, `--progress-format`) are listed once, at the top. [The egami-video",
        "command](index.md) explains the conventions they share; `run` takes [egami's steps](../cli/steps.md).",
        "",
        "```text",
        root.rstrip(),
        "```",
        "",
    ]
    for name in subcommands(root):
        text = egami_video(name, "--help")
        lines += [f"## {name}", "", "```text", strip_globals(text).rstrip(), "```", ""]
        for child in subcommands(text):
            lines += [f"### {name} {child}", "", "```text", strip_globals(egami_video(name, child, "--help")).rstrip(), "```", ""]
    return "\n".join(lines)


def strip_globals(text):
    """Leaves out the options every command shares, which the top of the page lists."""
    shared = ("-?, -h, --help", "-v, --verbose", "-vv, --debug", "-q, --quiet", "--limit <", "--progress <", "--progress-format <", "--raw-", "--preview-formats")
    return "\n".join(line for line in text.splitlines() if not line.strip().startswith(shared))


def steps_page():
    listing = egami("run", "--list")
    names = [line.split()[0] for line in listing.splitlines()[1:] if line.startswith("  ") and line.split()]
    lines = [
        "# Steps for run",
        "",
        "The steps `egami run` chains and workflow files list. Every public `RasterOps` method whose arguments a command line can",
        "spell is a step, named in kebab case (`ContrastStretch` is `contrast-stretch`); its options are the method's parameters.",
        "Positional values fill the parameters in order; `--name value` sets one by name. `egami run --list <step>` prints one.",
        "",
        "| Step | What it does |",
        "| --- | --- |",
    ]
    for line in listing.splitlines()[1:]:
        if not line.startswith("  ") or not line.split():
            continue
        name = line.split()[0]
        summary = line[len("  ") + 26:].strip() if len(line) > 28 else ""
        lines.append(f"| [{name}](#{name}) | {summary.replace('|', '\\|')} |")
    lines.append("")
    for name in names:
        if name == "save":
            continue
        text = egami("run", "--list", name).rstrip()
        lines += [f"## {name}", "", "```text", text, "```", ""]
    lines += [
        "## save",
        "",
        "```text",
        "save [path] [--format f] [--quality q] [--lossless] [--effort e] [--bit-depth b] [--strip] [--set fmt.key=value]",
        "```",
        "",
        "Writes the image as it is at that point. A plan may save more than once; a save without a path writes to `-o`.",
        "",
    ]
    return "\n".join(lines)


def main():
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "-v", "q", "-nologo"], check=True, capture_output=True)
    subprocess.run(["dotnet", "build", VIDEO_PROJECT, "-c", "Release", "-v", "q", "-nologo"], check=True, capture_output=True)
    pages = {COMMANDS_PAGE: commands_page(), STEPS_PAGE: steps_page(), VIDEO_COMMANDS_PAGE: video_commands_page()}
    stale = []
    for path, text in pages.items():
        old = open(path, encoding="utf-8").read() if os.path.exists(path) else None
        if old != text:
            stale.append(path)
            if "--check" not in sys.argv:
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
    if "--check" in sys.argv and stale:
        print("Out of date: " + ", ".join(stale) + ". Run python tools/make-cli-reference.py.")
        sys.exit(1)
    print("Wrote " + ", ".join(stale) if stale else "Up to date.")


if __name__ == "__main__":
    main()
