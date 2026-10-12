"""Builds the GSUB closure references of Phase 2.8.1 (question 17): the glyphs hb-subset's layout closure keeps, from the same
pinned HarfBuzz 14.5.1 as the shaping references.

Runs tools/shaperef/closure.py in the egami-shaperef image, which compiles tools/shaperef/hb-closure.c against the image's
HarfBuzz and writes tests/reference/shaping/closure. The image and the fonts come from tools/make-shaping-references.py, which
must have run first (it checks the HarfBuzz tarball's SHA-256 and the fonts' before the build).

Usage, from the repo root with Docker running:
  python tools/make-closure-references.py
"""
import os
import subprocess

IMAGE = "egami-shaperef:2.8.1"

if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
    raise SystemExit(f"The {IMAGE} image is missing; run python tools/make-shaping-references.py first.")

root = os.getcwd()
subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python", "/w/tools/shaperef/closure.py", "/w"], check=True,
               env={**os.environ, "MSYS_NO_PATHCONV": "1"})
