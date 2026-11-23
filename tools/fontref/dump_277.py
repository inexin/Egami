"""Writes the references for Phase 2.7.7 (WOFF and WOFF2 writing) to tests/reference/fonts/encode (run in egami-fontref:2.7.2).

  encode/<font>.json.gz    for each test font WebFontWriterTests encodes: the sizes of Google's woff2_compress output and of
                           fontTools' WOFF (zlib level 9) and WOFF2 (glyf, loca and hmtx transformed) files, and every table
                           of woff2_decompress's decoding of woff2_compress's file (length and SHA-256, per face), whose glyf
                           and loca are the reference rebuild Egami's own encode-then-decode must match; plus the glyphs with
                           overlap flags, which Debian's woff2 1.0.2 drops

Usage (from the repo root): docker run --rm -v "$PWD:/w" egami-fontref:2.7.2 python /w/tools/fontref/dump_277.py /w
"""
import gzip
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys

from fontTools.ttLib import TTCollection, TTFont, sfnt

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
OUT = os.path.join(ROOT, "tests/reference/fonts/encode")
FONTS = [
    "NotoSans-Regular.ttf", "Roboto-Regular.ttf", "SourceSans3-Regular.otf", "DejaVuSans.ttf",
    "derived/RobotoFlex-subset.ttf", "derived/SourceSans3-VF-subset.otf", "derived/NotoSansSerif.ttc",
]


def table_hashes(reader):
    return {tag: [len(reader[tag]), hashlib.sha256(reader[tag]).hexdigest()] for tag in sorted(reader.keys())}


def overlap_glyphs(font):
    if "glyf" not in font:
        return []
    glyf = font["glyf"]
    return [gid for gid, name in enumerate(font.getGlyphOrder()) if glyf[name].numberOfContours > 0 and glyf[name].flags[0] & 0x40]


def fonttools_size(path, flavor, **kwargs):
    font = TTFont(path)
    font.flavor = flavor
    out = io.BytesIO()
    font.save(out, **kwargs)
    return len(out.getvalue())


def main():
    os.makedirs(OUT, exist_ok=True)
    sfnt.ZLIB_COMPRESSION_LEVEL = 9
    for name in FONTS:
        path = os.path.join(ROOT, "tests/fonts", name)
        base = os.path.basename(name)
        stem, ext = os.path.splitext(base)
        work = "/tmp/" + base
        shutil.copy(path, work)
        subprocess.run(["woff2_compress", work], check=True, stdout=subprocess.DEVNULL)
        woff2 = "/tmp/" + stem + ".woff2"
        data = {"woff2Compress": os.path.getsize(woff2)}
        decoded = "/tmp/" + stem + ".decoded.woff2"
        shutil.copy(woff2, decoded)
        subprocess.run(["woff2_decompress", decoded], check=True, stdout=subprocess.DEVNULL)
        ttf = "/tmp/" + stem + ".decoded.ttf"
        if ext == ".ttc":
            data["woff2Decompress"] = [table_hashes(font.reader) for font in TTCollection(ttf).fonts]
            data["overlapGlyphs"] = [overlap_glyphs(font) for font in TTCollection(path).fonts]
        else:
            data["woff2Decompress"] = [table_hashes(TTFont(ttf).reader)]
            data["overlapGlyphs"] = [overlap_glyphs(TTFont(path))]
            data["fontToolsWoff"] = fonttools_size(path, "woff")
            data["fontToolsWoff2"] = fonttools_size(path, "woff2")
        with gzip.open(os.path.join(OUT, base + ".json.gz"), "wt", encoding="utf-8", compresslevel=9) as f:
            json.dump(data, f, separators=(",", ":"))
        print(base, data["woff2Compress"], data.get("fontToolsWoff"), data.get("fontToolsWoff2"))


if __name__ == "__main__":
    main()
