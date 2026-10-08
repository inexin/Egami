"""Writes the PngSuite reference decodes in tests/reference/pngsuite.

Each valid PngSuite file gets a .rgba8 file: its pixels as 8-bit RGBA, row by row, decoded by Pillow
(an independent decoder). Pillow keeps only the high byte of 16-bit samples, so for 16-bit files the
reference is the high byte of each sample, and the test compares Egami's 16-bit decode shifted right by 8.

Pillow's RGBA conversion ignores a tRNS key on gray images, so this script applies it per the PNG
specification (section 11.3.2.1): a pixel whose raw gray sample equals the key is fully transparent.

Usage: python tools/make-png-references.py   (from the repo root; needs Pillow)
"""
import glob
import os

from PIL import Image

SOURCE = "tests/images/pngsuite"
TARGET = "tests/reference/pngsuite"

os.makedirs(TARGET, exist_ok=True)
count = 0
for path in sorted(glob.glob(os.path.join(SOURCE, "*.png"))):
    name = os.path.basename(path)
    if name.startswith("x"):
        continue
    with open(path, "rb") as f:
        header = f.read(26)
    depth, color_type = header[24], header[25]
    with Image.open(path) as image:
        image.load()
        key = image.info.get("transparency") if color_type == 0 else None
        if color_type == 0:
            # Gray: Pillow gives 16-bit samples as-is and scales lower depths to 8 bits.
            raw = list(image.get_flattened_data() if hasattr(image, "get_flattened_data") else image.getdata())
            scale = 255 // ((1 << depth) - 1) if depth < 8 else 1
            data = bytearray()
            for v in raw:
                sample = v // scale if depth < 8 else v
                gray = v >> 8 if depth == 16 else v
                alpha = 0 if key is not None and sample == key else 255
                data += bytes((gray, gray, gray, alpha))
            data = bytes(data)
        else:
            data = image.convert("RGBA").tobytes()
    with open(os.path.join(TARGET, name[:-4] + ".rgba8"), "wb") as f:
        f.write(data)
    count += 1
print(f"Wrote {count} references.")
