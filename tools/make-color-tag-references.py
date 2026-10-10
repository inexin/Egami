"""Records what Pillow reports about color for every PngSuite file: the cHRM chromaticities and the gAMA value.

Writes tests/reference/pngsuite/color-tags.json: {file: {"chromaticity": [wx, wy, rx, ry, gx, gy, bx, by], "gamma": g}}, with a key
only where Pillow reports it. Pillow's "gamma" is the file's value (0.45455 for 1/2.2), whose reciprocal Egami keeps as the
decoding exponent.

Usage, from the repo root: python tools/make-color-tag-references.py
"""
import glob
import json
import os

import PIL
from PIL import Image

OUT = "tests/reference/pngsuite/color-tags.json"


def main():
    tags = {}
    for path in sorted(glob.glob("tests/images/pngsuite/*.png")):
        name = os.path.basename(path)
        if name.startswith("x"):
            continue  # the corrupt files
        try:
            with Image.open(path) as image:
                info = image.info
        except Exception:
            continue
        entry = {}
        if "chromaticity" in info:
            entry["chromaticity"] = [round(v, 5) for v in info["chromaticity"]]
        if "gamma" in info:
            entry["gamma"] = info["gamma"]
        if entry:
            tags[name] = entry
    with open(OUT, "w", newline="\n") as f:
        json.dump({"tool": f"Pillow {PIL.__version__}", "files": tags}, f, indent=1)
    print("Wrote", len(tags), "files to", OUT)


if __name__ == "__main__":
    main()
