"""Builds the derived test fonts in tests/fonts/derived and fontTools' references in tests/reference/fonts, in Docker.

The image tools/fontref/Dockerfile (egami-fontref:2.7.1) pins fontTools 4.55.0 and freetype-py 2.5.1. tools/fontref/derive.py
builds the derived fonts (a collection, bare CFF streams, a CID-keyed CJK subset when .cache/fonts/NotoSansCJKsc-Regular.otf
exists, and fonts with symbol, Mac Roman, format 12 and kern-table data); tools/fontref/dump.py writes one gzipped JSON per face.

Usage, from the repo root with Docker running:
  python tools/make-font-references.py
"""
import os
import subprocess

IMAGE = "egami-fontref:2.7.1"


def docker(*args):
    root = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, *args], check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/fontref"], check=True)
docker("python", "/w/tools/fontref/derive.py", "/w")
docker("python", "/w/tools/fontref/dump.py", "/w")
