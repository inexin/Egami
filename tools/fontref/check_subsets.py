"""Opens every subset Egami's tests wrote to .cache/font-subsets with fontTools and FreeType (run in egami-fontref:2.7.1).

fontTools loads each sfnt with checksum checks on, decompiles every table and draws every glyph; a bare CFF is decompiled and
drawn. FreeType opens each file and loads every glyph unhinted and unscaled. Any failure is printed and the exit code is 1.

Usage (from the repo root, after the Egami.Fonts tests ran):
  docker run --rm -v "$PWD:/w" egami-fontref:2.7.1 python /w/tools/fontref/check_subsets.py /w
"""
import glob
import io
import os
import sys

import freetype
from fontTools.cffLib import CFFFontSet
from fontTools.pens.recordingPen import DecomposingRecordingPen, RecordingPen
from fontTools.ttLib import TTFont

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
failed = 0
files = sorted(glob.glob(os.path.join(ROOT, ".cache/font-subsets/*")))
for path in files:
    name = os.path.basename(path)
    try:
        if path.endswith(".cff"):
            cff = CFFFontSet()
            cff.decompile(io.BytesIO(open(path, "rb").read()), None)
            top = cff[cff.fontNames[0]]
            for glyph in top.charset:
                top.CharStrings[glyph].draw(DecomposingRecordingPen(top.CharStrings))
            count = len(top.charset)
        else:
            font = TTFont(path, checkChecksums=2)
            font.ensureDecompiled()
            glyphs = font.getGlyphSet()
            for glyph in font.getGlyphOrder():
                glyphs[glyph].draw(RecordingPen())
            count = len(font.getGlyphOrder())
        face = freetype.Face(path)
        for g in range(face.num_glyphs):
            face.load_glyph(g, freetype.FT_LOAD_NO_SCALE | freetype.FT_LOAD_NO_HINTING)
        if face.num_glyphs != count:
            raise ValueError(f"FreeType sees {face.num_glyphs} glyphs, fontTools {count}")
        print(f"ok   {name}: {count} glyphs")
    except Exception as e:
        failed += 1
        print(f"FAIL {name}: {type(e).__name__}: {e}")
print(f"{len(files) - failed} of {len(files)} subsets open in fontTools and FreeType")
sys.exit(1 if failed else 0)
