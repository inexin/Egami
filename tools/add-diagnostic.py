"""Adds or replaces rows in docs/articles/diagnostics.md's catalog, at its sorted place (other rows keep their order).

Usage: python tools/add-diagnostic.py "Svg.NoNamespace" Warning "Meaning text"
"""
import re
import sys

PATH = "docs/articles/diagnostics.md"
ident, level, meaning = sys.argv[1], sys.argv[2], sys.argv[3]
lines = open(PATH, encoding="utf-8").read().split("\n")
row = f"| `{ident}` | {level} | {meaning} |"
catalog = lines.index("## Catalog")
start = next(i for i, l in enumerate(lines) if i > catalog and l.startswith("| `"))
end = start
while end < len(lines) and lines[end].startswith("| `"):
    end += 1
rows = lines[start:end]
existing = [i for i, l in enumerate(rows) if l.startswith(f"| `{ident}` |")]
if existing:
    rows[existing[0]] = row
else:
    at = next((i for i, l in enumerate(rows) if re.match(r"\| `([^`]+)`", l).group(1) > ident), len(rows))
    rows.insert(at, row)
lines[start:end] = rows
open(PATH, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
