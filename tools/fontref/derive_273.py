"""Builds 2.7.3's derived test fonts in tests/fonts/derived from earlier derived ones (run in egami-fontref:2.7.2).

  NotoSansCJK-fd2048.cff    NotoSansCJK-subset.cff with every Font DICT's FontMatrix set to 1/2048 and the glyphs unchanged,
                            as pdf.js's text_clip_cff_cid.pdf has it: the glyphs draw at 1000/2048 of their old size
  NotoSansCJK-fdmatrix.cff  the same with Font DICTs that differ: Generic keeps none, Ideographs 1/2048, Proportional a slant
  NotoSans-format2.ttf      NotoSans-mac.ttf with a (1,1) format 2 subtable added beside its (1,0) one: Shift-JIS single bytes
                            for ASCII and two-byte codes 0x8140 to 0x815F for 32 Latin-1 letters
  NotoSans-post1.ttf        NotoSans-mac.ttf with a version 1 post table (the Macintosh standard names for its glyphs)
  NotoSans-post25.ttf       NotoSans-mac.ttf with a version 2.5 post table: glyph g named by standard index g - 2 to g + 2
  SourceSans3-expert.cff    SourceSans3-custom.cff pointed at the predefined Expert charset and Expert Encoding (so its glyphs
                            take expert names), and SourceSans3-expertsubset.cff (Source Sans 3 cut to A to Z and digits) at the ExpertSubset charset and the Standard Encoding: these check
                            Egami's tables against FreeType's
  SourceSans3-custom.cff    Source Sans 3 cut to ASCII as a bare CFF with a custom encoding: Standard Encoding codes 0x21 to 0x7E moved down by one
"""
import io
import os
import struct
import sys
from types import SimpleNamespace

from fontTools.cffLib import CFFFontSet
from fontTools.encodings.StandardEncoding import StandardEncoding
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.ttLib.sfnt import SFNTWriter
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
    font = TTFont(os.path.join(OUT, "NotoSans-mac.ttf"), recalcTimestamp=False)
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


def cff_custom_encoding(text="".join(chr(c) for c in range(0x20, 0x7F)), target="SourceSans3-custom.cff"):
    options = subset.Options()
    options.notdef_outline = True
    options.name_IDs = ["*"]
    font = TTFont(os.path.join(ROOT, "tests/fonts/SourceSans3-Regular.otf"))
    sub = subset.Subsetter(options)
    sub.populate(text=text)
    sub.subset(font)
    fonts = font["CFF "].cff
    top = fonts[fonts.fontNames[0]]
    encoding = [".notdef"] * 256
    for code, name in enumerate(StandardEncoding):
        if name != ".notdef" and name in top.CharStrings:
            encoding[code - 1 if 0x21 <= code <= 0x7E else code] = name
    top.Encoding = encoding
    save_cff(fonts, target)


def post_versions():
    font = TTFont(os.path.join(OUT, "NotoSans-mac.ttf"), recalcTimestamp=False)
    font["post"].formatType = 1.0
    font.save(os.path.join(OUT, "NotoSans-post1.ttf"))

    # fontTools writes no version 2.5, so the table's bytes are put together here (the 32-byte header, the glyph count, then
    # one signed offset per glyph from its id to its index in the standard order) and the file is written table by table.
    font = TTFont(os.path.join(OUT, "NotoSans-mac.ttf"), recalcTimestamp=False)
    count = font["maxp"].numGlyphs
    tables = {tag: font.reader[tag] for tag in font.reader.keys()}
    offsets = bytes(((max(0, min(257, g + (g % 5) - 2)) - g) & 0xFF) for g in range(count))
    tables["post"] = struct.pack(">I", 0x00025000) + tables["post"][4:32] + struct.pack(">H", count) + offsets
    with open(os.path.join(OUT, "NotoSans-post25.ttf"), "wb") as f:
        writer = SFNTWriter(f, len(tables), font.sfntVersion)
        for tag in sorted(tables):
            writer[tag] = tables[tag]
        writer.close()


def predefined(source, target, charset, encoding):
    """Rewrites the Top DICT's charset and Encoding offsets (fontTools writes them as 3- or 5-byte integers) to predefined ids."""
    with open(os.path.join(OUT, source), "rb") as f:
        data = bytearray(f.read())
    header = data[2]

    def skip_index(at):
        count = int.from_bytes(data[at:at + 2], "big")
        if count == 0:
            return at + 2
        size = data[at + 2]
        last = int.from_bytes(data[at + 3 + count * size:at + 3 + (count + 1) * size], "big")
        return at + 3 + (count + 1) * size - 1 + last

    top = skip_index(header)
    size = data[top + 2]
    start = top + 3 + 2 * size - 1 + int.from_bytes(data[top + 3:top + 3 + size], "big")
    end = top + 3 + 2 * size - 1 + int.from_bytes(data[top + 3 + size:top + 3 + 2 * size], "big")
    i, operand = start, None
    while i < end:
        b0 = data[i]
        if b0 == 29:
            operand = i
            i += 5
        elif b0 == 28:
            operand = i
            i += 3
        elif b0 == 30:
            i += 1
            while (data[i] & 0x0F) != 0x0F and (data[i] >> 4) != 0x0F:
                i += 1
            i += 1
        elif b0 >= 247 and b0 <= 254:
            i += 2
        elif b0 >= 32:
            i += 1
        else:
            op = b0
            i += 2 if b0 == 12 else 1
            if op in (15, 16):
                value = charset if op == 15 else encoding
                width = 4 if data[operand] == 29 else 2
                data[operand + 1:operand + 1 + width] = value.to_bytes(width, "big")
            operand = None
    with open(os.path.join(OUT, target), "wb") as f:
        f.write(bytes(data))


def main():
    post_versions()
    cjk_matrices()
    cmap_format2()
    cff_custom_encoding()
    predefined("SourceSans3-custom.cff", "SourceSans3-expert.cff", 1, 1)
    # The ExpertSubset charset has 87 glyphs, so it gets a font with fewer (FreeType refuses a charset shorter than the font).
    cff_custom_encoding("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "SourceSans3-expertsubset.cff")
    predefined("SourceSans3-expertsubset.cff", "SourceSans3-expertsubset.cff", 2, 0)


if __name__ == "__main__":
    main()
