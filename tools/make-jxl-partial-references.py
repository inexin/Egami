"""Builds the references for JPEG XL partial display (JPEG XL extras PR D) in tests/reference/jxl/partial.

Each file below, from the JPEG XL corpus in tests/images/jxl, is cut at 10, 50 and 90 % of its bytes, and libjxl 0.12.0's
djxl --allow_partial_files writes what its progressive flush shows of each cut. The tests feed Egami's incremental decoder the
same bytes and compare RenderPreview with these pictures.

References (tests/reference/jxl/partial):
  <name>.cut<pct>.png   djxl's picture of the cut, as 8-bit RGB(A) PNG; missing when djxl shows nothing yet
  partial.json          per file, each cut's byte count and whether djxl shows it

Usage, from the repo root with Docker running:
  python tools/make-jxl-partial-references.py
It uses the image egami-jxlref:2.9.3 that tools/make-jxl-references.py builds from tools/jxlref.
"""
import json
import os
import subprocess

IMAGE = "egami-jxlref:2.9.3"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FILES = ["duck-d1", "duck-d1-progressive", "duck-lossless-e7", "duck-small-modular-progressive", "duck-alpha-d1"]
CUTS = [10, 50, 90]
TARGET = os.path.join(ROOT, "tests", "reference", "jxl", "partial")


def main():
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "jxlref")], check=True)

    os.makedirs(TARGET, exist_ok=True)
    for name in os.listdir(TARGET):
        os.remove(os.path.join(TARGET, name))

    manifest = {}
    for name in FILES:
        size = os.path.getsize(os.path.join(ROOT, "tests", "images", "jxl", f"{name}.jxl"))
        cuts = {}
        for pct in CUTS:
            count = size * pct // 100
            out = f"tests/reference/jxl/partial/{name}.cut{pct}.png"
            subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/w", "-w", "/w", IMAGE, "sh", "-c",
                            f"head -c {count} tests/images/jxl/{name}.jxl > /tmp/cut.jxl && "
                            f"(djxl --allow_partial_files --bits_per_sample=8 /tmp/cut.jxl {out} > /dev/null 2>&1 || true)"], check=True)
            cuts[str(pct)] = {"bytes": count, "djxl": os.path.exists(os.path.join(ROOT, out))}
        manifest[name] = cuts

    with open(os.path.join(TARGET, "partial.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    shown = sum(c["djxl"] for cuts in manifest.values() for c in cuts.values())
    print(f"{len(manifest)} files, {len(manifest) * len(CUTS)} cuts, djxl shows {shown}")


if __name__ == "__main__":
    main()
