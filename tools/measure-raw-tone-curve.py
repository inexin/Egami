"""Measures the default tone curve RawReaderOptions.DefaultToneCurve uses (RawLook.DefaultCurve), from Adobe Camera Raw run as a
black box.

RAW Build Plan task 12 (Lars 2026-10-31: measure Adobe's default tone curve, ship it opt-in). The script writes a LinearRaw DNG
whose camera RGB is linear sRGB, with a neutral ramp of 256 steps from 2^-14 to 1 and no profile tone curve, has Adobe DNG
Converter (free) write it again with a full-size preview, which Camera Raw renders with its defaults, and reads the ramp back
from that preview. The curve is the preview's linear output against the ramp's linear input, smoothed over 7 steps, sampled at
log-spaced inputs; below 0.4 % the 8-bit preview cannot resolve it, so the C# table runs a straight line through black there.
Needs Pillow. Prints the (input, output) pairs to paste into RawLook.DefaultCurve.

Usage, from the repo root on a PC with Adobe DNG Converter installed:
  python tools/measure-raw-tone-curve.py
"""
import io
import math
import os
import struct
import subprocess
import sys
import tempfile

from PIL import Image

CONVERTER = r"C:\Program Files\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe"
W, H, STEPS = 2048, 64, 256


def ramp_dng(path):
    """A LinearRaw DNG: ColorMatrix1 is XYZ to linear sRGB under D65, as-shot neutral 1, no profile, the ramp in 8-pixel steps."""
    vals = [min(65535, round(65535 * 2 ** (-14 + 14 * i / (STEPS - 1)))) for i in range(STEPS)]
    row = b"".join(struct.pack("<HHH", vals[x * STEPS // W], vals[x * STEPS // W], vals[x * STEPS // W]) for x in range(W))
    data = row * H
    cm = [3.2404542, -1.5371385, -0.4985314, -0.9692660, 1.8760108, 0.0415560, 0.0556434, -0.2040259, 1.0572252]

    def rationals(values, fmt):
        return b"".join(struct.pack("<" + fmt + fmt, round(v * 10000), 10000) for v in values)

    tags = [
        (254, 4, 1, 0), (256, 4, 1, W), (257, 4, 1, H), (258, 3, 3, b"\x10\x00\x10\x00\x10\x00"), (259, 3, 1, 1),
        (262, 3, 1, 34892), (271, 2, 6, b"Egami\0"), (272, 2, 12, b"Ramp Camera\0"), (273, 4, 1, "DATA"), (277, 3, 1, 3),
        (278, 4, 1, H), (279, 4, 1, len(data)), (284, 3, 1, 1), (50706, 1, 4, b"\x01\x04\x00\x00"), (50707, 1, 4, b"\x01\x01\x00\x00"),
        (50708, 2, 18, b"Egami Ramp Camera\0"), (50714, 3, 1, 0), (50717, 3, 1, 65535), (50721, 10, 9, rationals(cm, "i")),
        (50728, 5, 3, rationals([1, 1, 1], "I")), (50730, 10, 1, rationals([0], "i")), (50778, 3, 1, 21),
    ]
    blobs = bytearray()
    start = 8 + 2 + 12 * len(tags) + 4
    entries = []
    for tag, kind, count, value in tags:
        if value == "DATA":
            entries.append((tag, kind, count, None))
        elif isinstance(value, bytes) and len(value) > 4:
            entries.append((tag, kind, count, struct.pack("<I", start + len(blobs))))
            blobs += value + (b"\0" if len(value) % 2 else b"")
        elif isinstance(value, bytes):
            entries.append((tag, kind, count, value.ljust(4, b"\0")))
        else:
            entries.append((tag, kind, count, struct.pack("<H", value) + b"\0\0" if kind == 3 else struct.pack("<I", value)))
    ifd = struct.pack("<H", len(tags)) + b"".join(
        struct.pack("<HHI", t, k, c) + (v if v is not None else struct.pack("<I", start + len(blobs))) for t, k, c, v in entries) + b"\0\0\0\0"
    with open(path, "wb") as f:
        f.write(b"II*\0" + struct.pack("<I", 8) + ifd + bytes(blobs) + data)


def largest_jpeg(data):
    best = None
    at = data.find(b"\xff\xd8\xff")
    while at >= 0:
        try:
            image = Image.open(io.BytesIO(data[at:]))
            image.load()
            if best is None or image.size[0] * image.size[1] > best.size[0] * best.size[1]:
                best = image
        except Exception:  # noqa: BLE001 - not every FFD8FF starts a JPEG
            pass
        at = data.find(b"\xff\xd8\xff", at + 3)
    return best


def linear(c):
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def main():
    if not os.path.exists(CONVERTER):
        sys.exit(f"Adobe DNG Converter is not at {CONVERTER}; install it (free) from Adobe.")
    with tempfile.TemporaryDirectory() as folder:
        source, out = os.path.join(folder, "ramp.dng"), os.path.join(folder, "out")
        os.makedirs(out)
        ramp_dng(source)
        subprocess.run([CONVERTER, "-p2", "-d", out, source], check=True, capture_output=True, timeout=600)
        preview = largest_jpeg(open(os.path.join(out, "ramp.dng"), "rb").read()).convert("RGB")
    px = preview.load()
    steps = []
    for i in range(STEPS):
        values = [sum(px[x, y]) / 3 for x in range(i * 8 + 2, i * 8 + 6) for y in range(16, 48)]
        steps.append((round(65535 * 2 ** (-14 + 14 * i / (STEPS - 1))) / 65535, sum(values) / len(values)))
    smooth = [(steps[i][0], sum(s[1] for s in steps[max(0, i - 3):i + 4]) / len(steps[max(0, i - 3):i + 4])) for i in range(STEPS)]
    for i in range(1, STEPS):
        smooth[i] = (smooth[i][0], max(smooth[i][1], smooth[i - 1][1]))
    points = [(0.0, 0.0)]
    for k in range(27):
        target = 2 ** (-11 + 11 * k / 26)
        x, y = min(smooth, key=lambda s: abs(math.log(s[0]) - math.log(target)))
        if 0.0035 < x < 1 and x > points[-1][0] + 1e-6:
            points.append((x, linear(y)))
    points.append((1.0, 1.0))
    print(",\n".join(f"{x:.6f}f, {y:.6f}f" for x, y in points))


if __name__ == "__main__":
    main()
