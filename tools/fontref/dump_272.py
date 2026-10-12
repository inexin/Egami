"""Dumps fontTools' (and Google's woff2 decoder's) reading of Phase 2.7.2's test fonts to tests/reference/fonts (run in
egami-fontref:2.7.2).

  web/<file>.json.gz       every table of a WOFF or WOFF2 file as fontTools decodes it (WOFF2 glyf and loca rebuilt), and as
                           woff2_decompress decodes it: the length and SHA-256 of each table, per face, and the glyphs with
                           overlap flags, which Debian's woff2 1.0.2 drops
  var/<font>.json.gz       a variable font at 25 locations: the normalized coordinates, every glyph's advance and outline from
                           fontTools' glyph set at that location, and fontTools' instancer's OS/2, hhea and post values and
                           pair kerning there
  color/<font>.json.gz     palettes and every color glyph's paint tree, varied at the default and two other locations

Usage (from the repo root): docker run --rm -v "$PWD:/w" egami-fontref:2.7.2 python /w/tools/fontref/dump_272.py /w [web|var|color]
"""
import glob
import gzip
import hashlib
import json
import math
import os
import random
import shutil
import subprocess
import sys

from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.ttLib import TTCollection, TTFont

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
WHAT = sys.argv[2:] or ["web", "var", "color"]
OUT = os.path.join(ROOT, "tests/reference/fonts")
DERIVED = os.path.join(ROOT, "tests/fonts/derived")
sys.path.insert(0, os.path.dirname(__file__))
from dump import gpos_kerning, normalize, r4  # noqa: E402


def write(target, data):
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with gzip.open(target, "wt", encoding="utf-8", compresslevel=9) as f:
        json.dump(data, f, separators=(",", ":"), ensure_ascii=False)


def table_hashes(reader):
    return {tag: [len(reader[tag]), hashlib.sha256(reader[tag]).hexdigest()] for tag in sorted(reader.keys())}


def overlap_glyphs(font):
    """The glyphs whose first flag has OVERLAP_SIMPLE, which Debian's woff2 1.0.2 drops (it predates the overlap bitmap)."""
    decoded = font["glyf"]
    return [gid for gid, name in enumerate(font.getGlyphOrder()) if decoded[name].numberOfContours > 0 and decoded[name].flags[0] & 0x40]


def dump_web():
    for path in sorted(glob.glob(os.path.join(DERIVED, "*.woff")) + glob.glob(os.path.join(DERIVED, "*.woff2"))):
        name = os.path.basename(path)
        data = {"faces": []}
        if path.endswith(".woff2"):
            work = "/tmp/" + name
            shutil.copy(path, work)
            subprocess.run(["woff2_decompress", work], check=True, stdout=subprocess.DEVNULL)
            ttf = work[:-6] + ".ttf"
            with open(ttf, "rb") as f:
                head = f.read(4)
            if head == b"ttcf":
                ttc = work[:-6] + ".ttc"
                os.replace(ttf, ttc)
                faces = [table_hashes(font.reader) for font in TTCollection(ttc).fonts]
            else:
                reader = TTFont(ttf).reader
                faces = [table_hashes(reader)]
                if "glyf" in reader:
                    data["overlapGlyphs"] = overlap_glyphs(TTFont(path))
            data["woff2Decompress"] = faces
        try:
            font = TTFont(path)
            data["faces"] = [table_hashes(font.reader)]
            if path.endswith(".woff") and font.flavorData is not None:
                data["metadata"] = (font.flavorData.metaData or b"").decode("utf-8")
                data["private"] = (font.flavorData.privData or b"").hex()
        except Exception as e:  # fontTools reads no WOFF2 collections
            data["fontToolsError"] = str(e)
        write(os.path.join(OUT, "web", name + ".json.gz"), data)
        print("dumped", name)


VARIABLE = ["RobotoFlex-subset.ttf", "NotoSans-VF-subset.ttf", "NotoSerif-VF-subset.ttf", "SourceSerif4-VF-subset.otf",
            "SourceSans3-VF-subset.otf", "NotoSans-avar2.ttf"]


def locations(font, count=25):
    """The default, each axis's ends (up to 12), the named instances and seeded random points, 25 in all."""
    axes = font["fvar"].axes
    default = {a.axisTag: a.defaultValue for a in axes}
    result = [dict(default)]
    for a in axes:
        for v in (a.minValue, a.maxValue):
            if len(result) >= 13:
                break
            loc = dict(default)
            loc[a.axisTag] = v
            if loc not in result:
                result.append(loc)
    for inst in font["fvar"].instances:
        if len(result) >= 19:
            break
        loc = {a.axisTag: inst.coordinates.get(a.axisTag, a.defaultValue) for a in axes}
        if loc not in result:
            result.append(loc)
    rng = random.Random(272)
    while len(result) < count:
        loc = {a.axisTag: round(rng.uniform(a.minValue, a.maxValue), 2) for a in axes}
        result.append(loc)
    return result


def dump_var():
    from fontTools.varLib import instancer

    for name in VARIABLE:
        path = os.path.join(DERIVED, name)
        font = TTFont(path)
        order = font.getGlyphOrder()
        best = font.getBestCmap() or {}
        ids = {n: i for i, n in enumerate(order)}
        sample = sorted({ids[g] for g in best.values()})
        axes = [{"tag": a.axisTag, "min": r4(a.minValue), "default": r4(a.defaultValue), "max": r4(a.maxValue),
                 "flags": a.flags, "nameId": a.axisNameID} for a in font["fvar"].axes]
        instances = [{"nameId": i.subfamilyNameID, "psNameId": i.postscriptNameID,
                      "coordinates": {k: r4(v) for k, v in i.coordinates.items()}} for i in font["fvar"].instances]
        data = {"axes": axes, "instances": instances, "glyphCount": len(order), "kerningSample": sample, "locations": []}
        for loc in locations(font):
            normalized = font.normalizeLocation(loc)
            glyph_set = font.getGlyphSet(location=loc)
            glyphs = []
            for gid, gname in enumerate(order):
                pen = DecomposingRecordingPen(glyph_set)
                glyph_set[gname].draw(pen)
                glyphs.append({"advance": r4(glyph_set[gname].width), "outline": normalize(pen.value)})
            static = instancer.instantiateVariableFont(TTFont(path), loc, inplace=False)
            entry = {"user": {k: r4(v) for k, v in loc.items()}, "normalized": {k: r4(v) for k, v in normalized.items()}, "glyphs": glyphs}
            os2, hhea, post = static["OS/2"], static["hhea"], static["post"]
            entry["metrics"] = {
                "hheaAscent": hhea.ascent, "hheaDescent": hhea.descent, "hheaLineGap": hhea.lineGap,
                "typoAscender": os2.sTypoAscender, "typoDescender": os2.sTypoDescender, "typoLineGap": os2.sTypoLineGap,
                "winAscent": os2.usWinAscent, "winDescent": os2.usWinDescent, "xHeight": getattr(os2, "sxHeight", 0),
                "capHeight": getattr(os2, "sCapHeight", 0), "strikeoutSize": os2.yStrikeoutSize, "strikeoutPosition": os2.yStrikeoutPosition,
                "underlinePosition": post.underlinePosition, "underlineThickness": post.underlineThickness,
            }
            pairs = gpos_kerning(static, sample)
            entry["kerning"] = [[l, r, v] for (l, r), v in sorted(pairs.items())]
            data["locations"].append(entry)
        write(os.path.join(OUT, "var", os.path.splitext(name)[0] + ".json.gz"), data)
        print("dumped", name)


def main():
    if "web" in WHAT:
        dump_web()
    if "var" in WHAT:
        dump_var()


if __name__ == "__main__":
    main()
