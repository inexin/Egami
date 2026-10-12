"""Builds Phase 2.7.2's derived test fonts in tests/fonts/derived (run in egami-fontref:2.7.2).

From the downloads in .cache/fonts (see tests/fonts/README.md for where each comes from):

  RobotoFlex-subset.ttf          Roboto Flex cut to Latin-1, every axis and avar kept
  NotoSans-VF-subset.ttf         Noto Sans variable (wdth, wght) cut to Latin-1, with GPOS kerning and its GDEF deltas
  NotoSerif-VF-subset.ttf        Noto Serif variable cut to Latin-1
  SourceSerif4-VF-subset.otf     Source Serif 4 variable (CFF2, opsz and wght) cut to Latin-1
  SourceSans3-VF-subset.otf      Source Sans 3 variable (CFF2, wght) cut to Latin-1
  NotoSans-avar2.ttf             NotoSans-VF-subset with an avar version 2 that narrows wdth as wght grows
  NotoColorEmoji-COLRv1-subset.ttf, NotoColorEmoji-CBDT-subset.ttf, Twemoji-COLRv0-subset.ttf
                                 the emoji fonts cut to EMOJI below
  Twemoji-sbix.ttf               an sbix font of eight Twemoji images at 72 and 36 pixels per em, one 'dupe' glyph
  Svg-test.ttf                   an OpenType SVG font: one gzipped document for two glyphs, one plain document

And from the 2.7.1 test fonts, the web fonts:

  NotoSans-Regular.woff          zlib tables, with a metadata block and a private block
  SourceSans3-Regular.woff       a CFF font in WOFF
  NotoSans-Regular.woff2         the glyf and loca transform
  NotoSans-Regular-null.woff2    glyf and loca stored untransformed (null transform)
  Roboto-Regular-hmtx.woff2      the glyf, loca and hmtx transforms
  SourceSans3-Regular.woff2      a CFF font in WOFF2
  RobotoFlex-subset.woff2        a variable font in WOFF2
  NotoSansSerif.woff2            the 2.7.1 collection, compressed by Google's woff2_compress

And small seeds for the woff2, gvar, cff2, colr and instance fuzz targets in tests/images/fuzz.
"""
import gzip
import io
import os
import shutil
import struct
import subprocess
import sys

from fontTools import subset
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables.DefaultTable import DefaultTable
from fontTools.ttLib.tables import otTables as ot
from fontTools.ttLib.tables.sbixGlyph import Glyph as SbixGlyph
from fontTools.ttLib.tables.sbixStrike import Strike
from fontTools.ttLib.woff2 import WOFF2FlavorData
from fontTools.ttLib.sfnt import WOFFFlavorData
from fontTools.varLib.builder import buildDeltaSetIndexMap
from fontTools.varLib.varStore import OnlineVarStoreBuilder
from PIL import Image

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
CACHE = os.path.join(ROOT, ".cache/fonts")
FONTS = os.path.join(ROOT, "tests/fonts")
OUT = os.path.join(FONTS, "derived")
os.makedirs(OUT, exist_ok=True)

ASCII = "".join(chr(c) for c in range(0x20, 0x7F))
LATIN1 = ASCII + "".join(chr(c) for c in range(0xA0, 0x100)) + "ŒœŠšŸŽž“”‘’–—…«»€"
EMOJI = ["😀", "😂", "😍", "❤", "👍", "🎉", "🚀", "⭐", "🌈", "🔥", "🍕", "🐱", "🌍", "✅", "❌", "💡", "🎨", "📷", "🧪", "🦊",
         "👩‍💻", "🏳️‍🌈", "🇳🇴", "👍🏽", "1️⃣"]


def cut(source, text, target, features=("kern",)):
    options = subset.Options()
    options.layout_features = list(features)
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.notdef_outline = True
    options.glyph_names = False
    font = TTFont(source)
    subsetter = subset.Subsetter(options)
    subsetter.populate(text=text)
    subsetter.subset(font)
    font.save(target)
    return target


def emoji_cut(source, target):
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.notdef_outline = True
    font = TTFont(source)
    subsetter = subset.Subsetter(options)
    subsetter.populate(text="".join(EMOJI) + "‍️\U0001F3FD")
    subsetter.subset(font)
    font.save(target)


def avar2(source, target):
    """Adds an avar version 2 whose ItemVariationStore moves wdth by -0.25 (normalized) as wght reaches its maximum."""
    font = TTFont(source)
    axes = [a.axisTag for a in font["fvar"].axes]
    builder = OnlineVarStoreBuilder(axes)
    builder.setSupports([{"wght": (0, 1, 1)}])
    indices = []
    for tag in axes:
        if tag == "wdth":
            indices.append(builder.storeDeltas([-4096]))
        else:
            indices.append(0xFFFFFFFF)
    avar = font["avar"] if "avar" in font else newTable("avar")
    if not avar.segments:
        avar.segments = {tag: {-1.0: -1.0, 0.0: 0.0, 1.0: 1.0} for tag in axes}
    avar.majorVersion = 2
    avar.minorVersion = 0
    avar.table = ot.avar()
    avar.table.Reserved = 0
    avar.table.VarStore = builder.finish()
    avar.table.VarIdxMap = buildDeltaSetIndexMap(indices)
    font["avar"] = avar
    font.save(target)


def sbix(target):
    """Eight Twemoji images in a 72 and a 36 pixel strike; U+2B50 is a 'dupe' of U+1F600's image, U+1F680 has an origin offset."""
    codes = [0x1F600, 0x1F602, 0x1F60D, 0x2764, 0x1F44D, 0x1F389, 0x1F680, 0x2B50]
    names = [".notdef"] + [f"u{c:04X}" for c in codes]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({c: f"u{c:04X}" for c in codes})
    pen = TTGlyphPen(None)
    pen.moveTo((100, 0))
    pen.lineTo((100, 800))
    pen.lineTo((900, 800))
    pen.lineTo((900, 0))
    pen.closePath()
    box = pen.glyph()
    fb.setupGlyf({n: box for n in names})
    fb.setupHorizontalMetrics({n: (1000, 100) for n in names})
    fb.setupHorizontalHeader(ascent=900, descent=-200)
    fb.setupNameTable({"familyName": "Egami Sbix Test", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200)
    fb.setupPost()
    font = fb.font
    table = newTable("sbix")
    table.version = 1
    table.flags = 1
    for ppem in (72, 36):
        strike = Strike(ppem=ppem, resolution=72)
        for name, code in zip(names[1:], codes):
            if code == 0x2B50:
                glyph = SbixGlyph(glyphName=name, graphicType="dupe", referenceGlyphName="u1F600")
            else:
                image = Image.open(os.path.join(CACHE, "twemoji72", f"{code:x}.png")).convert("RGBA")
                if ppem != 72:
                    image = image.resize((ppem, ppem), Image.LANCZOS)
                data = io.BytesIO()
                image.save(data, "PNG")
                offset = (10, -8) if code == 0x1F680 else (0, 0)
                glyph = SbixGlyph(glyphName=name, graphicType="png ", imageData=data.getvalue(), originOffsetX=offset[0], originOffsetY=offset[1])
            strike.glyphs[name] = glyph
        table.strikes[ppem] = strike
    font["sbix"] = table
    font.save(target)


def svg(target):
    """Two SVG documents: a gzipped one holding glyphs 1 and 2, and a plain one for glyph 3."""
    names = [".notdef", "a", "b", "c"]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({ord("a"): "a", ord("b"): "b", ord("c"): "c"})
    pen = TTGlyphPen(None)
    pen.moveTo((0, 0))
    pen.lineTo((0, 700))
    pen.lineTo((500, 700))
    pen.closePath()
    tri = pen.glyph()
    fb.setupGlyf({n: tri for n in names})
    fb.setupHorizontalMetrics({n: (600, 0) for n in names})
    fb.setupHorizontalHeader(ascent=800, descent=-200)
    fb.setupNameTable({"familyName": "Egami SVG Test", "styleName": "Regular"})
    fb.setupOS2()
    fb.setupPost()
    doc1 = ('<svg xmlns="http://www.w3.org/2000/svg"><g id="glyph1"><rect x="0" y="-700" width="500" height="700" fill="red"/></g>'
            '<g id="glyph2"><circle cx="250" cy="-350" r="250" fill="blue"/></g></svg>')
    doc2 = '<svg xmlns="http://www.w3.org/2000/svg" id="glyph3"><path d="M0 0L500 -700L0 -700Z" fill="green"/></svg>'
    # The table by hand, so one document is gzipped and the other plain.
    docs = [gzip.compress(doc1.encode(), mtime=0), doc2.encode()]
    entries = [(1, 2), (3, 3)]
    head = 2 + (12 * len(docs))
    raw = bytearray(struct.pack(">HII", 0, 10, 0))
    raw += struct.pack(">H", len(docs))
    at = head
    for (first, last), doc in zip(entries, docs):
        raw += struct.pack(">HHII", first, last, at, len(doc))
        at += len(doc)
    for doc in docs:
        raw += doc
    table = DefaultTable("SVG ")
    table.data = bytes(raw)
    font = fb.font
    font["SVG "] = table
    font.save(target)


def web():
    noto = os.path.join(FONTS, "NotoSans-Regular.ttf")
    source = os.path.join(FONTS, "SourceSans3-Regular.otf")
    roboto = os.path.join(FONTS, "Roboto-Regular.ttf")

    font = TTFont(noto)
    font.flavor = "woff"
    font.flavorData = WOFFFlavorData()
    font.flavorData.majorVersion, font.flavorData.minorVersion = 1, 2
    font.flavorData.metaData = b'<?xml version="1.0" encoding="UTF-8"?><metadata version="1.0"><uniqueid id="egami.test.notosans"/></metadata>'
    font.flavorData.privData = b"Egami private block"
    font.save(os.path.join(OUT, "NotoSans-Regular.woff"))

    font = TTFont(source)
    font.flavor = "woff"
    font.save(os.path.join(OUT, "SourceSans3-Regular.woff"))

    for path, name, transforms in [
        (noto, "NotoSans-Regular.woff2", None),
        (noto, "NotoSans-Regular-null.woff2", set()),
        (roboto, "Roboto-Regular-hmtx.woff2", {"glyf", "loca", "hmtx"}),
        (source, "SourceSans3-Regular.woff2", {"hmtx"}),
        (os.path.join(OUT, "RobotoFlex-subset.ttf"), "RobotoFlex-subset.woff2", None),
    ]:
        font = TTFont(path)
        font.flavor = "woff2"
        if transforms is not None:
            font.flavorData = WOFF2FlavorData(transformedTables=transforms)
        font.save(os.path.join(OUT, name))

    collection = os.path.join(OUT, "NotoSansSerif.ttc")
    work = "/tmp/NotoSansSerif.ttc"
    shutil.copy(collection, work)
    subprocess.run(["woff2_compress", work], check=True, stdout=subprocess.DEVNULL)
    shutil.copy("/tmp/NotoSansSerif.woff2", os.path.join(OUT, "NotoSansSerif.woff2"))


def fuzz_seeds():
    """Small seeds for the 2.7.2 fuzz targets, cut to a few glyphs so AFL++ mutates their structure, not their bulk."""
    seeds = os.path.join(ROOT, "tests/images/fuzz")
    for target in ("woff2", "gvar", "cff2", "colr", "instance"):
        os.makedirs(os.path.join(seeds, target + "-seeds"), exist_ok=True)

    def small(source, text, target, flavor=None, transforms=None):
        options = subset.Options()
        options.layout_features = ["kern"]
        options.name_IDs = [1, 2, 6]
        options.notdef_outline = True
        options.glyph_names = False
        font = TTFont(source)
        sub = subset.Subsetter(options)
        sub.populate(text=text)
        sub.subset(font)
        if flavor:
            font.flavor = flavor
        if transforms is not None:
            font.flavorData = WOFF2FlavorData(transformedTables=transforms)
        font.save(target)

    small(os.path.join(FONTS, "NotoSans-Regular.ttf"), "AVo", os.path.join(seeds, "woff2-seeds/noto.woff2"), "woff2")
    small(os.path.join(FONTS, "Roboto-Regular.ttf"), "Ag%", os.path.join(seeds, "woff2-seeds/roboto-hmtx.woff2"), "woff2", {"glyf", "loca", "hmtx"})
    small(os.path.join(FONTS, "SourceSans3-Regular.otf"), "AVo", os.path.join(seeds, "woff2-seeds/source.woff2"), "woff2")
    small(os.path.join(FONTS, "NotoSans-Regular.ttf"), "AVo", os.path.join(seeds, "woff2-seeds/noto.woff"), "woff")
    small(os.path.join(OUT, "NotoSans-VF-subset.ttf"), "AVo", os.path.join(seeds, "gvar-seeds/noto-vf.ttf"))
    small(os.path.join(OUT, "RobotoFlex-subset.ttf"), "Ag", os.path.join(seeds, "gvar-seeds/flex.ttf"))
    small(os.path.join(OUT, "NotoSans-avar2.ttf"), "Ao", os.path.join(seeds, "gvar-seeds/avar2.ttf"))
    small(os.path.join(OUT, "SourceSerif4-VF-subset.otf"), "AVo", os.path.join(seeds, "cff2-seeds/serif-vf.otf"))
    small(os.path.join(OUT, "SourceSans3-VF-subset.otf"), "Ag", os.path.join(seeds, "cff2-seeds/sans-vf.otf"))
    shutil.copy(os.path.join(FONTS, "test_glyphs-glyf_colr_1.ttf"), os.path.join(seeds, "colr-seeds/test-glyphs.ttf"))
    shutil.copy(os.path.join(OUT, "Twemoji-COLRv0-subset.ttf"), os.path.join(seeds, "colr-seeds/twemoji-v0.ttf"))
    shutil.copy(os.path.join(OUT, "Twemoji-sbix.ttf"), os.path.join(seeds, "colr-seeds/sbix.ttf"))
    options = subset.Options()
    options.layout_features = []
    font = TTFont(os.path.join(OUT, "NotoColorEmoji-CBDT-subset.ttf"))
    sub = subset.Subsetter(options)
    sub.populate(text="\U0001F600")
    sub.subset(font)
    font.save(os.path.join(seeds, "colr-seeds/noto-cbdt.ttf"))
    shutil.copy(os.path.join(seeds, "gvar-seeds/noto-vf.ttf"), os.path.join(seeds, "instance-seeds/noto-vf.ttf"))
    shutil.copy(os.path.join(seeds, "cff2-seeds/sans-vf.otf"), os.path.join(seeds, "instance-seeds/sans-vf.otf"))


def main():
    cut(os.path.join(CACHE, "RobotoFlex-VF.ttf"), LATIN1, os.path.join(OUT, "RobotoFlex-subset.ttf"))
    cut(os.path.join(CACHE, "NotoSans-VF.ttf"), LATIN1, os.path.join(OUT, "NotoSans-VF-subset.ttf"))
    cut(os.path.join(CACHE, "NotoSerif-VF.ttf"), LATIN1, os.path.join(OUT, "NotoSerif-VF-subset.ttf"))
    cut(os.path.join(CACHE, "SourceSerif4-VF.otf"), LATIN1, os.path.join(OUT, "SourceSerif4-VF-subset.otf"))
    cut(os.path.join(CACHE, "SourceSans3-VF.otf"), LATIN1, os.path.join(OUT, "SourceSans3-VF-subset.otf"))
    avar2(os.path.join(OUT, "NotoSans-VF-subset.ttf"), os.path.join(OUT, "NotoSans-avar2.ttf"))
    emoji_cut(os.path.join(CACHE, "NotoColorEmoji-COLRv1.ttf"), os.path.join(OUT, "NotoColorEmoji-COLRv1-subset.ttf"))
    emoji_cut(os.path.join(CACHE, "NotoColorEmoji-CBDT.ttf"), os.path.join(OUT, "NotoColorEmoji-CBDT-subset.ttf"))
    emoji_cut(os.path.join(CACHE, "Twemoji.Mozilla.ttf"), os.path.join(OUT, "Twemoji-COLRv0-subset.ttf"))
    sbix(os.path.join(OUT, "Twemoji-sbix.ttf"))
    svg(os.path.join(OUT, "Svg-test.ttf"))
    web()
    fuzz_seeds()
    print("2.7.2 derived fonts written to", OUT)


if __name__ == "__main__":
    main()
