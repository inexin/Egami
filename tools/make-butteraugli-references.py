"""Builds the Butteraugli references for CompareOptions.Butteraugli (JPEG XL extras, PR C) in tests/reference/analysis.

For each pair of the analysis corpus (tests/images/analysis, made by tools/make-analysis-references.py), libjxl 0.12.0's
butteraugli_main gives the distance (the map's largest value) and the 3-norm. The tool reads the PNGs as sRGB at 80 nits,
blends images with alpha over black and over white and keeps the larger distance, and gives 0 for images under 8 pixels
either way, as Egami does.

References (tests/reference/analysis):
  butteraugli.json   per pair "<a>~<b>", {"distance": ..., "norm3": ...}

Usage, from the repo root with Docker running:
  python tools/make-butteraugli-references.py
It builds the image egami-butteraugliref:2.9 from tools/butteraugliref when it is missing (libjxl from source, some minutes).
"""
import json
import os
import subprocess

IMAGE = "egami-butteraugliref:2.9"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGES = "tests/images/analysis"
TARGET = os.path.join(ROOT, "tests", "reference", "analysis", "butteraugli.json")

PAIRS = [
    ("duck.png", "duck-q75.png"),
    ("egret.png", "egret-q75.png"),
    ("grad16-a.png", "grad16-b.png"),
    ("alpha-a.png", "alpha-b.png"),
    ("tiny-a.png", "tiny-b.png"),
    ("duck.png", "duck.png"),
]


def main():
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "butteraugliref")], check=True)

    script = "; ".join(f"butteraugli_main {IMAGES}/{a} {IMAGES}/{b}" for a, b in PAIRS)
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/work", "-w", "/work", IMAGE, "sh", "-c", script],
                         capture_output=True, text=True, check=True).stdout.split("\n")
    lines = [line for line in out if line.strip()]
    result = {}
    for i, (a, b) in enumerate(PAIRS):
        distance = float(lines[2 * i])
        norm3 = float(lines[(2 * i) + 1].split(":")[1])
        result[f"{a}~{b}"] = {"distance": distance, "norm3": norm3}

    with open(TARGET, "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    print(f"wrote {TARGET}")


if __name__ == "__main__":
    main()
