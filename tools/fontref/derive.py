"""Builds the derived test fonts in tests/fonts/derived from the downloaded ones (run in egami-fontref:2.7.1).

  NotoSansSerif.ttc       a collection of Noto Sans and Noto Serif with shared tables
  SourceSans3.cff         Source Sans 3's CFF table as a bare stream (PDF FontFile3/Type1C)
  NotoSansCJK-subset.otf  Noto Sans CJK SC cut to ASCII and 200 common hanzi, still CID-keyed (needs .cache/fonts)
  NotoSansCJK-subset.cff  its CFF table as a bare CID-keyed stream (PDF FontFile3/CIDFontType0C)
  NotoSans-symbol.ttf     Noto Sans cut to ASCII with only a (3,0) symbol cmap at U+F020 to U+F07E
  NotoSans-mac.ttf        Noto Sans cut to ASCII and Latin-1 with only a (1,0) Mac Roman cmap
  NotoSans-format12.ttf   Noto Sans cut to Latin-1 with only a (3,10) format 12 cmap
  NotoSans-kern.ttf       Noto Sans cut to ASCII without GPOS and with a kern table built from its GPOS pairs
"""
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables._c_m_a_p import cmap_format_0, cmap_format_4, cmap_format_12
from fontTools.ttLib.tables._k_e_r_n import KernTable_format_0
from fontTools.ttLib.ttCollection import TTCollection

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
FONTS = os.path.join(ROOT, "tests/fonts")
OUT = os.path.join(FONTS, "derived")
os.makedirs(OUT, exist_ok=True)

ASCII = "".join(chr(c) for c in range(0x20, 0x7F))
LATIN1 = ASCII + "".join(chr(c) for c in range(0xA0, 0x100))
HANZI = ("的一是不了在人有我他这个们中来上大为和国地到以说时要就出会可也你对生能而子那得于着下自之年过发后作里用道行所然家"
         "种事成方多经么去法学如都同现当没动面起看定天分还进好小部其些主样理心她本前开但因只从想实日军者意无力它与长把机十民第公此"
         "已工使情明性知全三又关点正业外将两高间由问很最重并物手应战向头文体政美相见被利什二等产或新己制身果加西斯月话合回特代内信"
         "表化老给世位次度门任常先海通教儿原东声提立及比员解水名真论处走义各入几口认条平系气题活尔更别打女变四神总何电数安少报才结")


def sub(source, text, target, keep_cff=True, drop=()):
    options = subset.Options()
    options.layout_features = ["kern"]
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.notdef_outline = True
    options.drop_tables += list(drop)
    font = TTFont(source)
    subsetter = subset.Subsetter(options)
    subsetter.populate(text=text)
    subsetter.subset(font)
    return font


def raw_table(path, tag):
    font = TTFont(path)
    return font.reader[tag]


def main():
    # Collection with shared tables.
    collection = TTCollection()
    collection.fonts = [TTFont(os.path.join(FONTS, "NotoSans-Regular.ttf")), TTFont(os.path.join(FONTS, "NotoSerif-Regular.ttf"))]
    collection.save(os.path.join(OUT, "NotoSansSerif.ttc"), shareTables=True)

    # Bare CFF.
    with open(os.path.join(OUT, "SourceSans3.cff"), "wb") as f:
        f.write(raw_table(os.path.join(FONTS, "SourceSans3-Regular.otf"), "CFF "))

    # CID-keyed subset of the CJK font, when it was fetched.
    cjk = os.path.join(ROOT, ".cache/fonts/NotoSansCJKsc-Regular.otf")
    if os.path.exists(cjk):
        font = sub(cjk, ASCII + HANZI, None)
        path = os.path.join(OUT, "NotoSansCJK-subset.otf")
        font.save(path)
        with open(os.path.join(OUT, "NotoSansCJK-subset.cff"), "wb") as f:
            f.write(raw_table(path, "CFF "))

    # Symbol cmap.
    font = sub(os.path.join(FONTS, "NotoSans-Regular.ttf"), ASCII, None)
    best = font.getBestCmap()
    table = cmap_format_4(4)
    table.platformID, table.platEncID, table.language = 3, 0, 0
    table.cmap = {0xF000 + cp: name for cp, name in best.items() if cp < 0x100}
    font["cmap"].tables = [table]
    font.save(os.path.join(OUT, "NotoSans-symbol.ttf"))

    # Mac Roman cmap.
    font = sub(os.path.join(FONTS, "NotoSans-Regular.ttf"), LATIN1, None)
    best = font.getBestCmap()
    table = cmap_format_0(0)
    table.platformID, table.platEncID, table.language = 1, 0, 0
    mac = {}
    for code in range(256):
        cp = ord(bytes([code]).decode("mac_roman"))
        if cp in best:
            mac[code] = best[cp]
    table.cmap = mac
    font["cmap"].tables = [table]
    font.save(os.path.join(OUT, "NotoSans-mac.ttf"))

    # Format 12 only.
    font = sub(os.path.join(FONTS, "NotoSans-Regular.ttf"), LATIN1, None)
    best = font.getBestCmap()
    table = cmap_format_12(12)
    table.platformID, table.platEncID, table.language = 3, 10, 0
    table.cmap = dict(best)
    font["cmap"].tables = [table]
    font.save(os.path.join(OUT, "NotoSans-format12.ttf"))

    # kern table instead of GPOS.
    font = sub(os.path.join(FONTS, "NotoSans-Regular.ttf"), ASCII, None)
    sys.path.insert(0, os.path.dirname(__file__))
    from dump import gpos_kerning
    order = font.getGlyphOrder()
    glyphs = list(range(len(order)))
    pairs = gpos_kerning(font, glyphs)
    del font["GPOS"]
    kern = newTable("kern")
    kern.version = 0
    sub_table = KernTable_format_0()
    sub_table.version = 0
    sub_table.coverage = 1
    sub_table.kernTable = {(order[l], order[r]): v for (l, r), v in pairs.items()}
    kern.kernTables = [sub_table]
    font["kern"] = kern
    font.save(os.path.join(OUT, "NotoSans-kern.ttf"))
    print("derived fonts written to", OUT)


if __name__ == "__main__":
    main()
