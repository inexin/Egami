"""Cuts Noto Sans CJK SC down to the characters the Phase 2.8.2 layout corpus uses (plus ASCII and the ellipsis and hyphens),
keeping every layout feature (vert, vrt2, halt, palt, vpal, kern) and the vertical metrics, as
tests/fonts/derived/NotoSansCJK-layout-subset.otf. CID-keyed, as the source.

Usage (inside the egami-fontref image, from tools/make-layout-references.py): python /w/tools/layoutref/derive.py /w
"""
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont

sys.path.insert(0, os.path.dirname(__file__))
import corpus  # noqa: E402

ROOT = sys.argv[1]
SOURCE = os.path.join(ROOT, ".cache", "fonts", "NotoSansCJKsc-Regular.otf")
TARGET = os.path.join(ROOT, "tests", "fonts", "derived", "NotoSansCJK-layout-subset.otf")

text = set(corpus.all_text(ROOT)) | set(chr(c) for c in range(0x20, 0x7F)) | {chr(0x2026), chr(0x2010), chr(0x2011), chr(0x00AD)}
options = subset.Options()
options.layout_features = ["*"]
options.name_IDs = ["*"]
options.name_languages = ["*"]
options.notdef_outline = True
options.hinting = False
options.desubroutinize = False
font = TTFont(SOURCE)
subsetter = subset.Subsetter(options)
subsetter.populate(text="".join(sorted(text)))
subsetter.subset(font)
# The source's timestamps stay, so the subset's bytes are the same on every run.
font.recalcTimestamp = False
font.save(TARGET)
print(f"{TARGET}: {len(font.getGlyphOrder())} glyphs")
