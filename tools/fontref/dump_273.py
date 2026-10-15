"""Dumps 2.7.3's font data references to tests/reference/fonts/fontdata/<file>.json.gz (run in egami-fontref:2.7.2).

For every test font file (each face of a collection), FreeType and fontTools give:
  names     FreeType's FT_Get_Glyph_Name for each glyph when the face has glyph names, else null; fontTools' post format
  cid       for a CID-keyed CFF: fontTools' ROS and the CID of each glyph (its charset)
  cmaps     every cmap subtable but format 14, as fontTools reads it: platform, encoding, format, language and code -> glyph
  builtin   FreeType's platform 7 charmaps (a CFF font's own encoding): encoding id and code -> glyph
  outlines  for bare CFF files: FreeType's unhinted outline of each glyph (loaded by CID for a CID-keyed font, as FreeType
            numbers them) and its advance (which FreeType runs through the Font DICT matrix, unlike its linear advance) at 1000
            pixels per em, so in thousandths of an em whatever the font's units; Egami's are scaled by 1000 / UnitsPerEm

Usage (from the repo root): docker run --rm -v "$PWD:/w" egami-fontref:2.7.2 python /w/tools/fontref/dump_273.py /w
"""
import glob
import gzip
import io
import json
import os
import sys

import freetype
from fontTools.cffLib import CFFFontSet
from fontTools.ttLib import TTCollection, TTFont

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
FONTS = os.path.join(ROOT, "tests/fonts")
OUT = os.path.join(ROOT, "tests/reference/fonts/fontdata")


def r3(v):
    v = round(float(v), 3)
    return int(v) if v == int(v) else v


def freetype_names(face):
    if not face.has_glyph_names:
        return None
    return [face.get_glyph_name(g).decode("latin-1") for g in range(face.num_glyphs)]


def freetype_builtin(face):
    maps = []
    for cm in face.charmaps:
        if cm.platform_id != 7:
            continue
        face.set_charmap(cm)
        codes = {}
        code, glyph = face.get_first_char()
        while glyph != 0:
            codes[code] = glyph
            code, glyph = face.get_next_char(code, glyph)
        maps.append({"encoding": cm.encoding_id, "map": sorted(codes.items())})
    return maps


def freetype_outlines(face, cid):
    # FreeType numbers a bare CID-keyed CFF font's glyphs by CID.
    face.set_pixel_sizes(1000, 1000)
    flags = freetype.FT_LOAD_NO_HINTING | freetype.FT_LOAD_NO_BITMAP
    glyphs = []
    for g in range(len(cid["cids"]) if cid else face.num_glyphs):
        face.load_glyph(cid["cids"][g] if cid else g, flags)
        outline = face.glyph.outline
        points = [[r3(x / 64), r3(y / 64)] for x, y in outline.points]
        glyphs.append({"advance": r3(face.glyph.advance.x / 64), "points": points, "contours": list(outline.contours)})
    return glyphs


def cff_cid(cff):
    top = cff[cff.fontNames[0]]
    if not hasattr(top, "ROS"):
        return None
    registry, ordering, supplement = top.ROS
    cids = [0 if n == ".notdef" else int(n[3:]) for n in top.charset]
    return {"registry": registry, "ordering": ordering, "supplement": supplement, "cids": cids}


def sfnt_data(font):
    data = {"post": font["post"].formatType if "post" in font else None, "cmaps": []}
    if "cmap" in font:
        order = font.getGlyphOrder()
        ids = {n: i for i, n in enumerate(order)}
        for t in font["cmap"].tables:
            if t.format == 14:
                continue
            data["cmaps"].append({
                "platform": t.platformID, "encoding": t.platEncID, "format": t.format, "language": t.language,
                "map": sorted((code, ids.get(name, 0)) for code, name in t.cmap.items()),
            })
    for tag in ("CFF ",):
        if tag in font:
            data["cid"] = cff_cid(font[tag].cff)
    return data


def dump(path, rel):
    with open(path, "rb") as f:
        raw = f.read()
    faces = []
    if raw[:4] == b"ttcf":
        count = len(TTCollection(io.BytesIO(raw)).fonts)
    else:
        count = 1
    for index in range(count):
        face = freetype.Face(path, index)
        entry = {"upem": face.units_per_EM, "names": freetype_names(face), "builtin": freetype_builtin(face)}
        if raw[:1] == b"\x01":
            cff = CFFFontSet()
            cff.decompile(io.BytesIO(raw), None)
            entry["cid"] = cff_cid(cff)
            entry["cmaps"] = []
            entry["outlines"] = freetype_outlines(face, entry["cid"])
        else:
            entry.update(sfnt_data(TTFont(io.BytesIO(raw), fontNumber=index, lazy=True)))
        faces.append(entry)
    os.makedirs(OUT, exist_ok=True)
    target = os.path.join(OUT, rel.replace("/", "_") + ".json.gz")
    with gzip.GzipFile(target, "wb", mtime=0) as f:
        f.write(json.dumps(faces, separators=(",", ":")).encode())


def main():
    files = sorted(glob.glob(os.path.join(FONTS, "*.*")) + glob.glob(os.path.join(FONTS, "derived", "*.*")))
    for path in files:
        if not path.endswith((".ttf", ".otf", ".cff", ".ttc")):
            continue
        dump(path, os.path.relpath(path, FONTS).replace(os.sep, "/"))


if __name__ == "__main__":
    main()
