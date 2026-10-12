"""Dumps fontTools' (and Google's woff2 decoder's) reading of Phase 2.7.2's test fonts to tests/reference/fonts (run in
egami-fontref:2.7.2).

  web/<file>.json.gz       every table of a WOFF or WOFF2 file as fontTools decodes it (WOFF2 glyf and loca rebuilt), and as
                           woff2_decompress decodes it: the length and SHA-256 of each table, per face, and the glyphs with
                           overlap flags, which Debian's woff2 1.0.2 drops
  var/<font>.json.gz       a variable font at 25 locations: the normalized coordinates, every glyph's advance and outline from
                           fontTools' glyph set at that location, and fontTools' instancer's OS/2, hhea and post values and
                           pair kerning there
  var/<font>.instances.json.gz  fontTools' instancer output (saved and read back) at three positions of each glyf font: every
                           glyph's integer outline, advance and bearing, and OS/2's weight and width classes
  color/<font>.json.gz     palettes and every color glyph's paint tree, varied at the default and two other locations

Usage (from the repo root): docker run --rm -v "$PWD:/w" egami-fontref:2.7.2 python /w/tools/fontref/dump_272.py /w [web|var|instances|color]
"""
import glob
import gzip
import hashlib
import io
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
WHAT = sys.argv[2:] or ["web", "var", "instances", "color"]
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
            # Normalized coordinates are F2Dot14, as OpenType says; fontTools keeps avar 1's float result, so round it here.
            normalized = {k: round(v * 16384) / 16384 for k, v in font.normalizeLocation(loc).items()}
            glyph_set = font.getGlyphSet(location=normalized, normalized=True)
            glyphs = []
            for gid, gname in enumerate(order):
                pen = DecomposingRecordingPen(glyph_set)
                glyph_set[gname].draw(pen)
                glyphs.append({"advance": r4(glyph_set[gname].width), "outline": normalize(pen.value)})
            # Only the metrics and kerning are taken from the instancer; it fails on some subset CFF2 private dicts, so drop CFF2.
            source = TTFont(path)
            if "CFF2" in source:
                del source["CFF2"]
            entry = {"user": {k: r4(v) for k, v in loc.items()}, "normalized": {k: r4(v) for k, v in normalized.items()}, "glyphs": glyphs}
            if "avar" in source and getattr(source["avar"], "majorVersion", 1) >= 2:
                # The instancer reads no avar 2; outlines and advances are the check here.
                data["locations"].append(entry)
                continue
            static = instancer.instantiateVariableFont(source, loc, inplace=False)
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


def dump_instances():
    """fontTools' instancer output for the glyf fonts at three positions each: integer outlines, advances and bearings."""
    from fontTools.varLib import instancer

    for name in ["RobotoFlex-subset.ttf", "NotoSans-VF-subset.ttf", "NotoSerif-VF-subset.ttf"]:
        path = os.path.join(DERIVED, name)
        font = TTFont(path)
        axes = font["fvar"].axes
        picks = [
            {a.axisTag: font["fvar"].instances[-1].coordinates.get(a.axisTag, a.defaultValue) for a in axes},
            {a.axisTag: a.minValue for a in axes},
            {a.axisTag: round(a.minValue + (a.maxValue - a.minValue) * 0.37, 2) for a in axes},
        ]
        data = {"locations": []}
        for loc in picks:
            static = instancer.instantiateVariableFont(TTFont(path), loc, inplace=False)
            # Saved and read back, as a file holds it: glyf coordinates are integers only once compiled.
            buffer = io.BytesIO()
            static.save(buffer)
            buffer.seek(0)
            static = TTFont(buffer)
            order = static.getGlyphOrder()
            glyph_set = static.getGlyphSet()
            glyphs = []
            for gname in order:
                pen = DecomposingRecordingPen(glyph_set)
                glyph_set[gname].draw(pen)
                advance, lsb = static["hmtx"][gname]
                glyphs.append({"advance": advance, "lsb": lsb, "outline": normalize(pen.value)})
            os2 = static["OS/2"]
            data["locations"].append({"user": {k: r4(v) for k, v in loc.items()}, "glyphs": glyphs,
                                      "weightClass": os2.usWeightClass, "widthClass": os2.usWidthClass,
                                      "typoAscender": os2.sTypoAscender, "xHeight": os2.sxHeight})
        write(os.path.join(OUT, "var", os.path.splitext(name)[0] + ".instances.json.gz"), data)
        print("dumped instances of", name)


COLOR = ["NotoColorEmoji-COLRv1-subset.ttf", "NotoColorEmoji-CBDT-subset.ttf", "Twemoji-COLRv0-subset.ttf", "Twemoji-sbix.ttf",
         "Svg-test.ttf", "../test_glyphs-glyf_colr_1.ttf", "../test_glyphs-glyf_colr_1_variable.ttf"]


def color_of(palette, index, alpha):
    """A palette entry as straight RGBA floats with the alpha factor, or the foreground; the same rule Egami follows."""
    if index == 0xFFFF:
        return {"fg": r4(alpha)}
    if index >= len(palette):
        return {"rgba": [0, 0, 0, 0]}
    c = palette[index]
    return {"rgba": [r4(c.red / 255), r4(c.green / 255), r4(c.blue / 255), r4(c.alpha / 255 * alpha)]}


def paint_tree(font, paint, palette, ids, depth=0):
    """fontTools' paint at its default values in Egami's canonical form: transform paints as matrices (fontTools' getTransform)."""
    from fontTools.ttLib.tables.otTables import PaintFormat

    if depth > 64:
        return None
    f = paint.Format
    base = f - 1 if PaintFormat(f).is_variable() else f
    name = PaintFormat(base).name

    def line(cl):
        return {"extend": int(cl.Extend), "stops": [[r4(s.StopOffset), color_of(palette, s.PaletteIndex, s.Alpha)] for s in cl.ColorStop]}

    def child(p):
        return paint_tree(font, p, palette, ids, depth + 1)

    if name == "PaintColrLayers":
        layers = font["COLR"].table.LayerList.Paint[paint.FirstLayerIndex:paint.FirstLayerIndex + paint.NumLayers]
        return {"t": "layers", "c": [child(p) for p in layers]}
    if name == "PaintSolid":
        return {"t": "solid", "color": color_of(palette, paint.PaletteIndex, paint.Alpha)}
    if name == "PaintLinearGradient":
        return {"t": "linear", "p": [paint.x0, paint.y0, paint.x1, paint.y1, paint.x2, paint.y2], "line": line(paint.ColorLine)}
    if name == "PaintRadialGradient":
        return {"t": "radial", "p": [paint.x0, paint.y0, paint.r0, paint.x1, paint.y1, paint.r1], "line": line(paint.ColorLine)}
    if name == "PaintSweepGradient":
        return {"t": "sweep", "p": [paint.centerX, paint.centerY, r4(paint.startAngle), r4(paint.endAngle)], "line": line(paint.ColorLine)}
    if name == "PaintGlyph":
        return {"t": "glyph", "g": ids[paint.Glyph], "c": child(paint.Paint)}
    if name == "PaintColrGlyph":
        return {"t": "colr", "g": ids[paint.Glyph]}
    if name == "PaintComposite":
        return {"t": "comp", "m": int(paint.CompositeMode), "s": child(paint.SourcePaint), "b": child(paint.BackdropPaint)}
    # The transform family: fontTools' own matrix, read at the default values.
    saved = paint.Format
    paint.Format = base
    t = paint.getTransform()
    paint.Format = saved
    return {"t": "xf", "m": [r4(v) for v in tuple(t)], "c": child(paint.Paint)}


def dump_color():
    for name in COLOR:
        path = os.path.normpath(os.path.join(DERIVED, name))
        font = TTFont(path)
        order = font.getGlyphOrder()
        ids = {n: i for i, n in enumerate(order)}
        data = {"glyphCount": len(order), "palettes": [], "glyphs": {}}
        palette = []
        if "CPAL" in font:
            cpal = font["CPAL"]
            types = getattr(cpal, "paletteTypes", None) or [0] * len(cpal.palettes)
            for p, colors in enumerate(cpal.palettes):
                data["palettes"].append({"type": types[p], "colors": [[c.red, c.green, c.blue, c.alpha] for c in colors]})
            palette = cpal.palettes[0] if cpal.palettes else []
        if "COLR" in font:
            colr = font["COLR"]
            data["colrVersion"] = colr.version
            if colr.version >= 1 and colr.table.BaseGlyphList:
                clips = colr.table.ClipList.clips if colr.table.ClipList else {}
                for rec in colr.table.BaseGlyphList.BaseGlyphPaintRecord:
                    entry = {"v": 1, "tree": paint_tree(font, rec.Paint, palette, ids)}
                    if rec.BaseGlyph in clips:
                        b = clips[rec.BaseGlyph]
                        entry["clip"] = [b.xMin, b.yMin, b.xMax, b.yMax]
                    data["glyphs"][str(ids[rec.BaseGlyph])] = entry
            if colr.version == 0 or getattr(colr, "ColorLayers", None):
                for gname, layers in (getattr(colr, "ColorLayers", None) or {}).items():
                    key = str(ids[gname])
                    if key not in data["glyphs"]:
                        data["glyphs"][key] = {"v": 0, "tree": {"t": "layers", "c": [
                            {"t": "glyph", "g": ids[l.name], "c": {"t": "solid", "color": color_of(palette, l.colorID, 1)}} for l in layers]}}
        if "sbix" in font:
            sbix = font["sbix"]
            strikes = {}
            for ppem, strike in sbix.strikes.items():
                for gname, g in strike.glyphs.items():
                    if g.graphicType is None:
                        continue
                    strikes.setdefault(str(ids[gname]), {})[str(ppem)] = {
                        "type": g.graphicType, "x": g.originOffsetX, "y": g.originOffsetY,
                        "ref": ids[g.referenceGlyphName] if g.graphicType == "dupe" else None,
                        "sha256": hashlib.sha256(g.imageData).hexdigest() if g.graphicType != "dupe" else None}
            data["sbix"] = strikes
        if "CBDT" in font:
            cbdt = font["CBDT"]
            strikes = {}
            for s, strike in enumerate(cbdt.strikeData):
                ppem = font["CBLC"].strikes[s].bitmapSizeTable.ppemY
                for gname, g in strike.items():
                    m = g.metrics
                    strikes.setdefault(str(ids[gname]), {})[str(ppem)] = {
                        "format": g.getFormat(), "width": m.width, "height": m.height,
                        "bearingX": m.BearingX if hasattr(m, "BearingX") else m.horiBearingX,
                        "bearingY": m.BearingY if hasattr(m, "BearingY") else m.horiBearingY,
                        "sha256": hashlib.sha256(g.imageData).hexdigest()}
            data["cbdt"] = strikes
        if "SVG " in font:
            data["svg"] = [{"first": first, "last": last, "sha256": hashlib.sha256(doc.encode("utf-8")).hexdigest()} for doc, first, last in font["SVG "].docList]
        write(os.path.join(OUT, "color", os.path.splitext(os.path.basename(name))[0] + ".json.gz"), data)
        print("dumped", name)


def main():
    if "web" in WHAT:
        dump_web()
    if "var" in WHAT:
        dump_var()
    if "instances" in WHAT:
        dump_instances()
    if "color" in WHAT:
        dump_color()


if __name__ == "__main__":
    main()
