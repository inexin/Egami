"""Builds 2.7.3's derived test fonts in tests/fonts/derived from earlier derived ones (run in egami-fontref:2.7.2).

  NotoSansCJK-fd2048.cff    NotoSansCJK-subset.cff with every Font DICT's FontMatrix set to 1/2048 and the glyphs unchanged,
                            as pdf.js's text_clip_cff_cid.pdf has it: the glyphs draw at 1000/2048 of their old size
  NotoSansCJK-fdmatrix.cff  the same with Font DICTs that differ: Generic keeps none, Ideographs 1/2048, Proportional a slant
  NotoSans-format2.ttf      NotoSans-mac.ttf with a (1,1) format 2 subtable added beside its (1,0) one: Shift-JIS single bytes
                            for ASCII and two-byte codes 0x8140 to 0x815F for 32 Latin-1 letters
  SourceSans3-custom.cff    Source Sans 3 cut to ASCII as a bare CFF with a custom encoding: Standard Encoding codes 0x21 to 0x7E moved down by one
"""
import io
import os
import sys
from types import SimpleNamespace

from fontTools.cffLib import CFFFontSet
from fontTools.encodings.StandardEncoding import StandardEncoding
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._c_m_a_p import cmap_format_2

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
OUT = os.path.join(ROOT, "tests/fonts/derived")


def load_cff(name):
    fonts = CFFFontSet()
    with open(os.path.join(OUT, name), "rb") as f:
        fonts.decompile(io.BytesIO(f.read()), None)
    return fonts


def save_cff(fonts, name):
    out = io.BytesIO()
    fonts.compile(out, SimpleNamespace(recalcBBoxes=False))
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(out.getvalue())


def cjk_matrices():
    s = 1 / 2048
    fonts = load_cff("NotoSansCJK-subset.cff")
    top = fonts[fonts.fontNames[0]]
    for fd in top.FDArray:
        fd.FontMatrix = [s, 0, 0, s, 0, 0]
    save_cff(fonts, "NotoSansCJK-fd2048.cff")

    fonts = load_cff("NotoSansCJK-subset.cff")
    top = fonts[fonts.fontNames[0]]
    by_name = {fd.FontName.split("-")[-1]: fd for fd in top.FDArray}
    by_name["Ideographs"].FontMatrix = [s, 0, 0, s, 0, 0]
    by_name["Proportional"].FontMatrix = [0.001, 0, 0.0002, 0.001, 0, 0]
    save_cff(fonts, "NotoSansCJK-fdmatrix.cff")


def cmap_format2():
    font = TTFont(os.path.join(OUT, "NotoSans-mac.ttf"))
    mac = font["cmap"].getcmap(1, 0).cmap
    table = cmap_format_2(2)
    table.platformID, table.platEncID, table.language = 1, 1, 0
    mapping = {c: mac[c] for c in range(0x20, 0x7F) if c in mac}
    letters = [c for c in range(0xC0, 0x100) if c in mac][:32]
    for i, c in enumerate(letters):
        mapping[0x8140 + i] = mac[c]
    table.cmap = mapping
    font["cmap"].tables.append(table)
    font["cmap"].tables.sort(key=lambda t: (t.platformID, t.platEncID, t.language))
    font.save(os.path.join(OUT, "NotoSans-format2.ttf"))


def cff_custom_encoding():
    options = subset.Options()
    options.notdef_outline = True
    options.name_IDs = ["*"]
    font = TTFont(os.path.join(ROOT, "tests/fonts/SourceSans3-Regular.otf"))
    sub = subset.Subsetter(options)
    sub.populate(text="".join(chr(c) for c in range(0x20, 0x7F)))
    sub.subset(font)
    fonts = font["CFF "].cff
    top = fonts[fonts.fontNames[0]]
    encoding = [".notdef"] * 256
    for code, name in enumerate(StandardEncoding):
        if name != ".notdef" and name in top.CharStrings:
            encoding[code - 1 if 0x21 <= code <= 0x7E else code] = name
    top.Encoding = encoding
    save_cff(fonts, "SourceSans3-custom.cff")


def main():
    cjk_matrices()
    cmap_format2()
    cff_custom_encoding()


if __name__ == "__main__":
    main()
