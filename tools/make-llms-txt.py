"""Writes docs/llms.txt (https://llmstxt.org) from the DocFX articles: a short summary of Egami, then every article in
docs/articles/toc.yml with its first sentence, linked on the docs site. The docs build publishes it next to the pages.

Usage: python tools/make-llms-txt.py          (from the repo root; rerun after adding or renaming an article)
       python tools/make-llms-txt.py --check  (exit 1 when docs/llms.txt is out of date)
"""
import os
import re
import sys

SITE = "https://inexin.github.io/Egami/v1/"
TOC = "docs/articles/toc.yml"
OUT = "docs/llms.txt"

# Articles that open with a list rather than a sentence.
DESCRIPTIONS = {
    "performance.md": "Tips that keep loading, editing and saving fast, how to run the benchmarks, and Egami's numbers.",
}

SUMMARY = """# Egami

> Egami is an MIT-licensed, fully managed 2D imaging library for .NET 10: it loads, edits, draws and saves images, with no
> native code and no third-party dependencies. The packages are Egami (rasters, pixel types, formats, operations, color
> management, metadata), Egami.Drawing (vector drawing on a canvas), Egami.Fonts (fonts, shaping and paragraph layout),
> Egami.Svg (SVG), Egami.Css (CSS) and Egami.Metafiles (WMF, EMF and EMF+).

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
            name = m.group(2).strip()
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
    out.append("- [Source and changelog](https://github.com/inexin/Egami): the repository, CHANGELOG.md and CONTRIBUTING.md.")
    return "\n".join(out) + "\n"


def main():
    text = render()
    if "--check" in sys.argv:
        current = open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
        if current != text:
            print(f"{OUT} is out of date: run python tools/make-llms-txt.py")
            sys.exit(1)
        print(f"{OUT} is up to date")
        return
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
