"""Writes the GSUB closure references of Phase 2.8.1 with HarfBuzz 14.5.1 (hb-closure.c) in the egami-shaperef image.

  closure/closure.json.gz   per case: the font, the layout features ("default" for hb-subset's defaults, "*" for all, "-" for
                            none, or tags), the input glyph ids and the closure hb-subset computes for GSUB, as ranges "a-b,c"

The inputs always hold glyph 0, which hb-subset always keeps. Each closure is also checked against the glyphs hb-subset's plan
keeps with --retain-gids, which add composite components and other tables' closures, so the closure must be a part of them.

Usage (inside the image, from tools/make-closure-references.py): python /w/tools/shaperef/closure.py /w
"""
import glob
import gzip
import json
import os
import struct
import subprocess
import sys

ROOT = sys.argv[1]
OUT = os.path.join(ROOT, "tests", "reference", "shaping", "closure", "closure.json.gz")
TEXTS = os.path.join(ROOT, "tests", "reference", "shaping", "texts")
VERSION = subprocess.run(["hb-shape", "--version"], capture_output=True, text=True, check=True).stdout.split()[2]
TOOL = "/tmp/hb-closure"

# (font, corpus texts, extra feature lists besides the defaults and all)
CORPUS = [
    ("tests/fonts/NotoSans-Regular.ttf", ["english", "greek", "russian"], ["liga", "-", "frac,numr,dnom", "smcp,c2sc"]),
    ("tests/fonts/SourceSans3-Regular.otf", ["english"], ["liga"]),
    ("tests/fonts/DejaVuSans.ttf", ["english", "arabic"], []),
    ("tests/fonts/Roboto-Regular.ttf", ["english"], []),
    ("tests/fonts/shaping/Amiri-Regular.ttf", ["arabic", "persian", "urdu"], ["init,medi,fina", "calt"]),
    ("tests/fonts/shaping/NotoSansArabic-Regular.ttf", ["arabic"], []),
    ("tests/fonts/shaping/ScheherazadeNew-Regular.ttf", ["arabic", "urdu"], []),
    ("tests/fonts/shaping/NotoSansHebrew-Regular.ttf", ["hebrew"], []),
    ("tests/fonts/shaping/NotoSansDevanagari-Regular.ttf", ["hindi"], ["half,blwf", "akhn,rphf,pres"]),
    ("tests/fonts/shaping/Mukta-Regular.ttf", ["hindi"], []),
    ("tests/fonts/shaping/NotoSansBengali-Regular.ttf", ["bengali"], []),
    ("tests/fonts/shaping/NotoSansTamil-Regular.ttf", ["tamil"], []),
    ("tests/fonts/shaping/NotoSansThai-Regular.ttf", ["thai"], []),
    ("tests/fonts/shaping/NotoSansLao-Regular.ttf", ["lao"], []),
    ("tests/fonts/shaping/NotoSansKhmer-Regular.ttf", ["khmer"], ["pres,abvs,blws,psts"]),
    ("tests/fonts/shaping/NotoSansMyanmar-Regular.ttf", ["burmese"], []),
    ("tests/fonts/shaping/NotoSansSinhala-Regular.ttf", ["sinhala"], []),
]
LINES = 3


class Closure:
    def __init__(self, font):
        self.process = subprocess.Popen([TOOL, os.path.join(ROOT, font)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        text=True, encoding="utf-8")

    def ask(self, request):
        self.process.stdin.write(request + "\n")
        self.process.stdin.flush()
        return self.process.stdout.readline().rstrip("\n")

    def close(self):
        self.process.stdin.close()
        self.process.wait()


def ids(text):
    return [int(v) for v in text.split(",") if v]


def ranges(values):
    values = sorted(set(values))
    parts = []
    i = 0
    while i < len(values):
        j = i
        while j + 1 < len(values) and values[j + 1] == values[j] + 1:
            j += 1
        parts.append(str(values[i]) if i == j else f"{values[i]}-{values[j]}")
        i = j + 1
    return ",".join(parts)


def has_gsub(path):
    with open(path, "rb") as f:
        data = f.read(12)
        if len(data) < 12 or data[:4] in (b"ttcf", b"wOFF", b"wOF2"):
            return False
        count = struct.unpack(">H", data[4:6])[0]
        records = f.read(count * 16)
    return any(records[i * 16:i * 16 + 4] == b"GSUB" for i in range(count))


def picked(count, seed):
    """A deterministic third of the glyphs, different per seed."""
    return [g for g in range(count) if (g * 2654435761 + seed * 40503) % 1000003 % 3 == 0]


cases = []
checked = equal = 0


def add(tool, font, features, glyphs):
    global checked, equal
    glyphs = sorted(set(glyphs) | {0})
    closure_text, plan_text = tool.ask(f"C {features} {','.join(map(str, glyphs))}").split("|")
    closure, plan = ids(closure_text), ids(plan_text)
    if not set(closure) <= set(plan):
        raise SystemExit(f"{font} {features}: the closure is not a part of hb-subset's plan")
    checked += 1
    equal += closure == plan
    cases.append({"font": font, "features": features, "input": ranges(glyphs), "closure": ranges(closure)})


subprocess.run(["cc", "-O2", "-o", TOOL, os.path.join(ROOT, "tools", "shaperef", "hb-closure.c"),
                *subprocess.run(["pkg-config", "--cflags", "--libs", "harfbuzz-subset", "harfbuzz"], capture_output=True, text=True,
                                check=True).stdout.split()], check=True)

for font, texts, extra in CORPUS:
    tool = Closure(font)
    for text in texts:
        with open(os.path.join(TEXTS, f"{text}.txt"), encoding="utf-8") as f:
            lines = f.read().splitlines()[:LINES]
        glyphs = []
        for line in lines:
            glyphs += ids(tool.ask(f"M {line}"))
        for features in ["default", "*"] + (extra if text == texts[0] else []):
            add(tool, font, features, glyphs)
    tool.close()

for path in sorted(glob.glob(os.path.join(ROOT, "tests", "fonts", "harfbuzz", "*", "fonts", "*"))):
    if not path.lower().endswith((".ttf", ".otf")) or not has_gsub(path):
        continue
    font = os.path.relpath(path, ROOT).replace(os.sep, "/")
    tool = Closure(font)
    count = int(tool.ask("N"))
    if count == 0:
        tool.close()
        continue
    for seed in range(3):
        add(tool, font, "*", picked(count, seed))
    add(tool, font, "default", picked(count, 0))
    tool.close()

os.makedirs(os.path.dirname(OUT), exist_ok=True)
text = json.dumps({"harfbuzz": VERSION, "cases": cases}, ensure_ascii=False, separators=(",", ":"))
with open(OUT, "wb") as raw:
    with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as f:
        f.write(text.encode("utf-8"))
print(f"{len(cases)} cases; the closure equals hb-subset's whole plan in {equal} of {checked}")
