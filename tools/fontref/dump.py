"""Dumps fontTools' reading of each test font to tests/reference/fonts/<name>[.<face>].json.gz (run in egami-fontref:2.7.1).

Each dump holds the head, hhea, maxp, OS/2 and post fields, every decoded name record, every cmap subtable, each glyph's advance,
bearing, bounds and outline (fontTools' RecordingPen output in Egami's sink form: M, L, Q, C and Z, quadratic runs split at their
implied on-curve points, a closing line back to the start dropped), and the pair kerning among the glyphs of printable ASCII and
Latin-1 (GPOS type 2 under kern for DFLT and latn, else the kern table). Bare .cff files get the outline and width parts.

Usage (from the repo root): docker run --rm -v "$PWD:/w" egami-fontref:2.7.1 python /w/tools/fontref/dump.py /w
"""
import glob
import io
import gzip
import json
import os
import sys

from fontTools.cffLib import CFFFontSet
from fontTools.cffLib import cffStandardStrings
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.recordingPen import DecomposingRecordingPen, RecordingPen
from fontTools.ttLib import TTCollection, TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
OUT = os.path.join(ROOT, "tests/reference/fonts")
SAMPLE = [chr(c) for c in range(0x20, 0x7F)] + [chr(c) for c in range(0xA0, 0x100)] + list("ŒœŠšŸŽž“”‘’–—…«»")


def r4(v):
    v = round(float(v), 4)
    return int(v) if v == int(v) else v


def mid(a, b):
    return ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)


def normalize(recording):
    out, start = [], None
    for op, args in recording:
        if op == "moveTo":
            start = args[0]
            out.append(["M", r4(start[0]), r4(start[1])])
        elif op == "lineTo":
            out.append(["L", r4(args[0][0]), r4(args[0][1])])
        elif op == "qCurveTo":
            pts = list(args)
            if pts[-1] is None:
                pts = pts[:-1]
                n = len(pts)
                start = mid(pts[-1], pts[0])
                out.append(["M", r4(start[0]), r4(start[1])])
                for i in range(n):
                    c, e = pts[i], mid(pts[i], pts[(i + 1) % n])
                    out.append(["Q", r4(c[0]), r4(c[1]), r4(e[0]), r4(e[1])])
            else:
                offs, end = pts[:-1], pts[-1]
                for i, c in enumerate(offs):
                    e = end if i == len(offs) - 1 else mid(c, offs[i + 1])
                    out.append(["Q", r4(c[0]), r4(c[1]), r4(e[0]), r4(e[1])])
        elif op == "curveTo":
            c1, c2, e = args
            out.append(["C", r4(c1[0]), r4(c1[1]), r4(c2[0]), r4(c2[1]), r4(e[0]), r4(e[1])])
        elif op in ("closePath", "endPath"):
            if out and out[-1][0] == "L" and start is not None and out[-1][1:] == [r4(start[0]), r4(start[1])]:
                out.pop()
            out.append(["Z"])
    return out


def glyf_outline(font, name):
    glyf = font["glyf"]
    g = glyf[name]
    if g.numberOfContours == 0:
        return []
    coords, ends, flags = g.getCoordinates(glyf)
    flat = Glyph()
    flat.numberOfContours = len(ends)
    flat.coordinates = coords
    flat.endPtsOfContours = ends
    flat.flags = flags
    pen = RecordingPen()
    flat.draw(pen, glyf)
    return normalize(pen.value)


def gpos_kerning(font, glyph_ids):
    """Pair kerning per Egami's 2.7.1 rule, over every ordered pair of glyph_ids; returns {(l, r): value} for nonzero pairs."""
    if "GPOS" not in font:
        return {}
    gpos = font["GPOS"].table
    features = set()
    for rec in gpos.ScriptList.ScriptRecord if gpos.ScriptList else []:
        if rec.ScriptTag not in ("DFLT", "latn") or rec.Script.DefaultLangSys is None:
            continue
        ls = rec.Script.DefaultLangSys
        if ls.ReqFeatureIndex != 0xFFFF:
            features.add(ls.ReqFeatureIndex)
        features.update(ls.FeatureIndex)
    lookups = set()
    for fi in features:
        rec = gpos.FeatureList.FeatureRecord[fi]
        if rec.FeatureTag == "kern":
            lookups.update(rec.Feature.LookupListIndex)
    order = font.getGlyphOrder()
    ids = {n: i for i, n in enumerate(order)}
    tables = []
    for li in sorted(lookups):
        lookup = gpos.LookupList.Lookup[li]
        subs = []
        for st in lookup.SubTable:
            if lookup.LookupType == 9:
                if st.ExtensionLookupType != 2:
                    continue
                st = st.ExtSubTable
            elif lookup.LookupType != 2:
                continue
            cov = {ids[n]: i for i, n in enumerate(st.Coverage.glyphs)}
            if st.Format == 1:
                sets = []
                for ps in st.PairSet:
                    d = {}
                    for rec in ps.PairValueRecord:
                        r = ids[rec.SecondGlyph]
                        if r not in d:
                            d[r] = getattr(rec.Value1, "XAdvance", 0) or 0 if rec.Value1 is not None else 0
                    sets.append(d)
                subs.append((1, cov, sets))
            else:
                cd1 = {ids[n]: c for n, c in st.ClassDef1.classDefs.items()} if st.ClassDef1 else {}
                cd2 = {ids[n]: c for n, c in st.ClassDef2.classDefs.items()} if st.ClassDef2 else {}
                matrix = [[(getattr(c2.Value1, "XAdvance", 0) or 0) if c2.Value1 is not None else 0 for c2 in c1.Class2Record] for c1 in st.Class1Record]
                subs.append((2, cov, (cd1, cd2, matrix, st.Class2Count)))
        tables.append(subs)
    result = {}
    for l in glyph_ids:
        for r in glyph_ids:
            total = 0
            for subs in tables:
                for fmt, cov, data in subs:
                    if l not in cov:
                        continue
                    if fmt == 1:
                        idx = cov[l]
                        if idx >= len(data) or r not in data[idx]:
                            continue
                        total += data[idx][r]
                        break
                    cd1, cd2, matrix, count2 = data
                    c1, c2 = cd1.get(l, 0), cd2.get(r, 0)
                    if c2 >= count2 or c1 >= len(matrix):
                        continue
                    total += matrix[c1][c2]
                    break
            if total:
                result[(l, r)] = total
    return result


def kern_table_pairs(font):
    if "kern" not in font:
        return {}
    ids = {n: i for i, n in enumerate(font.getGlyphOrder())}
    pairs = {}
    for t in font["kern"].kernTables:
        if getattr(t, "format", 0) != 0:
            continue
        for (l, r), v in t.kernTable.items():
            key = (ids[l], ids[r])
            pairs[key] = pairs.get(key, 0) + v
    return pairs


def fields(table, names):
    return {n: r4(getattr(table, n)) if isinstance(getattr(table, n, None), (int, float)) else str(getattr(table, n, "")) for n in names if hasattr(table, n)}


def dump_font(font, target):
    order = font.getGlyphOrder()
    data = {"glyphCount": len(order)}
    data["head"] = fields(font["head"], ["unitsPerEm", "xMin", "yMin", "xMax", "yMax", "macStyle", "indexToLocFormat", "flags", "lowestRecPPEM"])
    if "hhea" in font:
        data["hhea"] = fields(font["hhea"], ["ascent", "descent", "lineGap", "advanceWidthMax", "minLeftSideBearing", "minRightSideBearing", "xMaxExtent", "caretSlopeRise", "caretSlopeRun", "caretOffset", "numberOfHMetrics"])
    if "OS/2" in font:
        os2 = font["OS/2"]
        data["os2"] = fields(os2, ["version", "xAvgCharWidth", "usWeightClass", "usWidthClass", "fsType", "ySubscriptXSize", "ySubscriptYSize",
                                   "ySubscriptXOffset", "ySubscriptYOffset", "ySuperscriptXSize", "ySuperscriptYSize", "ySuperscriptXOffset",
                                   "ySuperscriptYOffset", "yStrikeoutSize", "yStrikeoutPosition", "sFamilyClass", "ulUnicodeRange1", "ulUnicodeRange2",
                                   "ulUnicodeRange3", "ulUnicodeRange4", "achVendID", "fsSelection", "usFirstCharIndex", "usLastCharIndex",
                                   "sTypoAscender", "sTypoDescender", "sTypoLineGap", "usWinAscent", "usWinDescent", "ulCodePageRange1",
                                   "ulCodePageRange2", "sxHeight", "sCapHeight", "usDefaultChar", "usBreakChar", "usMaxContext"])
    if "post" in font:
        data["post"] = fields(font["post"], ["formatType", "italicAngle", "underlinePosition", "underlineThickness", "isFixedPitch"])
    if "name" in font:
        names = []
        for rec in font["name"].names:
            try:
                names.append([rec.nameID, rec.platformID, rec.platEncID, rec.langID, rec.toUnicode()])
            except Exception:
                pass
        data["names"] = names
    if "cmap" in font:
        ids = {n: i for i, n in enumerate(order)}
        tables = []
        for t in font["cmap"].tables:
            entry = {"platform": t.platformID, "encoding": t.platEncID, "format": t.format}
            if t.format == 14:
                entry["uvs"] = [[sel, [[cp, ids[n] if n is not None else -1] for cp, n in items]] for sel, items in t.uvsDict.items()]
            else:
                entry["map"] = [[cp, ids[n]] for cp, n in sorted(t.cmap.items()) if ids.get(n, 0) != 0]
            tables.append(entry)
        data["cmap"] = tables
    glyphs = []
    glyph_set = font.getGlyphSet()
    cff = "CFF " in font
    for gid, name in enumerate(order):
        adv, lsb = font["hmtx"][name] if "hmtx" in font else (0, 0)
        if cff:
            pen = DecomposingRecordingPen(glyph_set)
            glyph_set[name].draw(pen)
            outline = normalize(pen.value)
            bp = BoundsPen(glyph_set)
            glyph_set[name].draw(bp)
            bounds = [r4(v) for v in bp.bounds] if bp.bounds else None
        else:
            outline = glyf_outline(font, name)
            g = font["glyf"][name]
            bounds = [g.xMin, g.yMin, g.xMax, g.yMax] if g.numberOfContours != 0 and hasattr(g, "xMin") else None
        glyphs.append({"advance": adv, "lsb": lsb, "bounds": bounds, "outline": outline})
    data["glyphs"] = glyphs
    best = font.getBestCmap() or {}
    ids = {n: i for i, n in enumerate(order)}
    sample = sorted({ids[best[ord(c)]] for c in SAMPLE if ord(c) in best})
    pairs = gpos_kerning(font, sample)
    data["kernSource"] = "GPOS" if pairs else "kern"
    if not pairs:
        allowed = set(sample)
        pairs = {k: v for k, v in kern_table_pairs(font).items() if k[0] in allowed and k[1] in allowed}
    data["kerningSample"] = sample
    data["kerning"] = [[l, r, v] for (l, r), v in sorted(pairs.items())]
    write(target, data)


def dump_cff(path, target):
    with open(path, "rb") as f:
        stream = io.BytesIO(f.read())
    cff = CFFFontSet()
    cff.decompile(stream, None)
    top = cff[cff.fontNames[0]]
    charstrings = top.CharStrings
    order = top.charset
    glyphs = []
    for name in order:
        cs = charstrings[name]
        pen = DecomposingRecordingPen(charstrings)
        cs.draw(pen)
        private = cs.private
        width = cs.width if cs.width is not None else getattr(private, "defaultWidthX", 0)
        glyphs.append({"advance": r4(width), "outline": normalize(pen.value)})
    data = {"glyphCount": len(order), "name": cff.fontNames[0], "cid": hasattr(top, "ROS"), "glyphs": glyphs}
    write(target, data)


def write(target, data):
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with gzip.open(target, "wt", encoding="utf-8", compresslevel=9) as f:
        json.dump(data, f, separators=(",", ":"), ensure_ascii=False)


# Phase 2.7.2's fonts have their own references (dump_272.py).
PHASE_272 = {
    "RobotoFlex-subset.ttf", "NotoSans-VF-subset.ttf", "NotoSerif-VF-subset.ttf", "SourceSerif4-VF-subset.otf", "SourceSans3-VF-subset.otf",
    "NotoSans-avar2.ttf", "NotoColorEmoji-COLRv1-subset.ttf", "NotoColorEmoji-CBDT-subset.ttf", "Twemoji-COLRv0-subset.ttf",
    "Twemoji-sbix.ttf", "Svg-test.ttf", "test_glyphs-glyf_colr_1.ttf", "test_glyphs-glyf_colr_1_variable.ttf",
}


def main():
    write(os.path.join(OUT, "cff-standard-strings.json.gz"), {"strings": list(cffStandardStrings)})
    fonts = sorted(glob.glob(os.path.join(ROOT, "tests/fonts/*.*")) + glob.glob(os.path.join(ROOT, "tests/fonts/derived/*.*")))
    for path in fonts:
        if os.path.basename(path) in PHASE_272:
            continue
        stem, ext = os.path.splitext(os.path.basename(path))
        ext = ext.lower()
        if ext in (".ttf", ".otf"):
            dump_font(TTFont(path, lazy=False), os.path.join(OUT, stem + ".json.gz"))
        elif ext in (".ttc", ".otc"):
            for i, font in enumerate(TTCollection(path).fonts):
                dump_font(font, os.path.join(OUT, f"{stem}.{i}.json.gz"))
        elif ext == ".cff":
            dump_cff(path, os.path.join(OUT, stem + ".cff.json.gz"))
        else:
            continue
        print("dumped", os.path.basename(path))


if __name__ == "__main__":
    main()
