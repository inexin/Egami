"""Writes docs/llms.txt (https://llmstxt.org) from the DocFX articles: a short summary of Egami, then every article in
docs/articles/toc.yml with its first sentence, linked on the docs site. The docs build publishes it next to the pages.

Also writes the agent guides: docs/agents/<package>.md for each tools/agent-guides/<package>.md, with every
{{sample File.cs#region}} line replaced by that region of tests/Egami.DocSamples and {{shared}} by tools/agent-guides/shared.md.
Each package packs its guide as AGENTS.md (Directory.Build.targets), so coding agents find it in the NuGet package, and the docs
build publishes them too.

Usage: python tools/make-llms-txt.py          (from the repo root; rerun after changing an article, a guide or a sample)
       python tools/make-llms-txt.py --check  (exit 1 when docs/llms.txt or an agent guide is out of date)
"""
import glob
import os
import re
import sys

SITE = "https://inexin.github.io/Egami/v1/"
TOC = "docs/articles/toc.yml"
OUT = "docs/llms.txt"
GUIDES = "tools/agent-guides"
GUIDES_OUT = "docs/agents"
SAMPLES = "tests/Egami.DocSamples"

# Articles that open with a list rather than a sentence.
DESCRIPTIONS = {
    "performance.md": "Tips that keep loading, editing and saving fast, how to run the benchmarks, and Egami's numbers.",
}

SUMMARY = """# Egami

> Egami is an MIT-licensed, fully managed 2D imaging library for .NET 10: it loads, edits, draws and saves images, with no
> native code and no third-party dependencies. The packages are Egami (rasters, pixel types, formats, operations, color
> management, metadata), Egami.Drawing (vector drawing on a canvas), Egami.Fonts (fonts, shaping and paragraph layout),
> Egami.Fonts.Substitutes (bundled metric-compatible fonts), Egami.Svg (SVG), Egami.Css (CSS) and Egami.Metafiles (WMF, EMF
> and EMF+).

Egami's own names: `Raster<TPixel>` and `Raster` hold images, pixel types such as `Rgba8` and `RgbaF32` say how pixels are
stored, `Raster.Load` and `Save` read and write files, `Raster.Probe` reads a file's size and format without decoding it,
`Apply` edits an image in place and `Derive` makes a new one, through a chain of operations, and `EgamiContext` holds the
settings, safety limits and registered formats a call uses.
"""


def toc_entries():
    """Yields (section, name, href) in the order of the table of contents."""
    section, name = "Docs", None
    for line in open(TOC, encoding="utf-8"):
        m = re.match(r"^(\s*)- name: (.+)$", line)
        if m:
            name = m.group(2).strip().strip("\"")
            if m.group(1) == "":
                section = "Docs"
            continue
        m = re.match(r"^\s*items:\s*$", line)
        if m and name:
            section = name
            continue
        m = re.match(r"^\s*href: (.+\.md)\s*$", line)
        if m and name:
            yield section, name, m.group(1).strip()


def first_sentence(path):
    """The first sentence of the article's first paragraph of prose, links reduced to their text."""
    text, started = [], False
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if not started:
            started = line.startswith("# ")
            continue
        if not line.strip():
            if text:
                break
            continue
        if line.startswith((" ", "\t")) and not text:
            continue
        if line.lstrip().startswith(("[!", ">", "|", "```", "#", "- ", "<")):
            if text:
                break
            continue
        text.append(line.strip())
    prose = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", " ".join(text))
    m = re.match(r"(.+?[.:])(\s|$)", prose)
    sentence = (m.group(1) if m else prose).rstrip(":.") + "."
    if len(sentence) > 200:
        cut = sentence[:200]
        sentence = cut[: cut.rfind(" ")].rstrip(",;") + " …"
    return sentence


def render():
    sections = {}
    for section, name, href in toc_entries():
        url = SITE + "articles/" + href[:-3] + ".html"
        about = DESCRIPTIONS.get(href) or first_sentence(os.path.join("docs/articles", href))
        sections.setdefault(section, []).append(f"- [{name}]({url}): {about}")
    out = [SUMMARY]
    for section, items in sections.items():
        out.append(f"\n## {section}\n")
        out.extend(items)
    out.append("\n## Optional\n")
    out.append(f"- [API reference]({SITE}api/Egami.html): every public type and member of every package.")
    for package in guide_packages():
        out.append(f"- [{package} agent guide]({SITE}agents/{package}.md): the names, entry points, samples and common mistakes"
                   f" of {package}, as packed in its NuGet package as AGENTS.md.")
    out.append("- [Source and changelog](https://github.com/inexin/Egami): the repository, CHANGELOG.md and CONTRIBUTING.md.")
    return "\n".join(out) + "\n"


def guide_packages():
    """The packages with an agent guide, core Egami first."""
    names = [os.path.basename(p)[:-3] for p in glob.glob(f"{GUIDES}/*.md") if os.path.basename(p) != "shared.md"]
    return sorted(names, key=lambda n: (n != "Egami", n))


def sample(reference):
    """The region's code, without its indent, nested region lines or test assertions."""
    file, name = reference.split("#")
    lines, inside = [], False
    for line in open(os.path.join(SAMPLES, file), encoding="utf-8"):
        stripped = line.strip()
        if stripped == f"#region {name}":
            inside = True
        elif inside and stripped.startswith("#endregion"):
            break
        elif inside and not stripped.startswith(("#region", "Assert.")):
            lines.append(line.rstrip())
    if not lines:
        sys.exit(f"{GUIDES}: region {name} not found in {SAMPLES}/{file}")
    indent = min(len(line) - len(line.lstrip()) for line in lines if line.strip())
    return "```csharp\n" + "\n".join(line[indent:] for line in lines) + "\n```"


def render_guide(package):
    shared = open(f"{GUIDES}/shared.md", encoding="utf-8").read().strip()
    out = [f"<!-- Generated by tools/make-llms-txt.py from tools/agent-guides/{package}.md and tests/Egami.DocSamples. -->"]
    for line in open(f"{GUIDES}/{package}.md", encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.fullmatch(r"\{\{sample (\S+)\}\}", line.strip())
        if m:
            out.append(sample(m.group(1)))
        elif line.strip() == "{{shared}}":
            out.append(shared)
        else:
            out.append(line)
    return "\n".join(out).rstrip() + "\n"


def outputs():
    """Every generated file and its text."""
    files = {OUT: render()}
    for package in guide_packages():
        files[f"{GUIDES_OUT}/{package}.md"] = render_guide(package)
    return files


def main():
    files = outputs()
    stale = [p.replace(os.sep, "/") for p in glob.glob(f"{GUIDES_OUT}/*.md")]
    stale = [p for p in stale if p not in files]
    if "--check" in sys.argv:
        bad = [p for p, text in files.items() if (open(p, encoding="utf-8").read() if os.path.exists(p) else "") != text]
        for path in bad + stale:
            print(f"{path} is out of date: run python tools/make-llms-txt.py")
        if bad or stale:
            sys.exit(1)
        print(f"{OUT} and {len(files) - 1} agent guides are up to date")
        return
    os.makedirs(GUIDES_OUT, exist_ok=True)
    for path in stale:
        os.remove(path)
    for path, text in files.items():
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    print(f"wrote {OUT} and {len(files) - 1} agent guides")


if __name__ == "__main__":
    main()
