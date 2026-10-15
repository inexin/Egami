"""Builds the derived test fonts in tests/fonts/derived and fontTools' references in tests/reference/fonts, in Docker.

The image tools/fontref/Dockerfile (egami-fontref:2.7.2) pins fontTools 4.55.0, freetype-py 2.5.1, Brotli, Pillow, lxml and
Debian's woff2 tools. tools/fontref/derive.py builds 2.7.1's derived fonts (a collection, bare CFF streams, a CID-keyed CJK subset
when .cache/fonts/NotoSansCJKsc-Regular.otf exists, and fonts with symbol, Mac Roman, format 12 and kern-table data) and
tools/fontref/dump.py writes one gzipped JSON per face of them; tools/fontref/derive_272.py builds 2.7.2's variable, color and web
fonts from the downloads in .cache/fonts (tests/fonts/README.md lists them) and tools/fontref/dump_272.py their references;
tools/fontref/derive_273.py builds 2.7.3's fonts (Font DICT matrices, a format 2 cmap, a custom CFF encoding) and
tools/fontref/dump_273.py writes FreeType's and fontTools' font data (names, CIDs, cmaps, built-in encodings, CFF outlines).

Usage, from the repo root with Docker running:
  python tools/make-font-references.py
"""
import os
import subprocess

IMAGE = "egami-fontref:2.7.2"


def docker(*args):
    root = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, *args], check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/fontref"], check=True)
docker("python", "/w/tools/fontref/derive.py", "/w")
docker("python", "/w/tools/fontref/dump.py", "/w")
docker("python", "/w/tools/fontref/derive_272.py", "/w")
docker("python", "/w/tools/fontref/dump_272.py", "/w")
docker("python", "/w/tools/fontref/derive_273.py", "/w")
docker("python", "/w/tools/fontref/dump_273.py", "/w")
