"""Writes the Phase 2.5.4 filter references in tests/reference/filters, made by ImageMagick in Docker.

Inputs, in tests/images/filters (PNG, committed), made here from the 1.2 operation images in tests/images/ops:
  duck.png      240x180 crop of the middle of ops/duck.png, 8-bit RGBA, opaque.
  egret.png     180x240 crop of the middle of ops/egret.png, 8-bit RGBA, opaque.
  noisy.png     duck.png with ImageMagick's impulse noise (salt and pepper, seeded), for median and despeckle.
  duck16.png    duck.png softened and stored at 16 bits, so its values use all 16 bits.
  shapes.png    ops/shapes.png as it is: 200x150 with soft and hard alpha edges.
  logo.png      160x120, generated here: an opaque rounded badge and a soft disc on transparent black, for lighting and shadows.
  lights.png    160x120, generated here: small bright points on a dark gradient, for lens blur.
  steps.png     160x120, generated here: gray steps, a ramp and a disc, for edges and morphology.

Each reference is the filter's output as raw RGBA (8 or 16 bits, as the case says), row by row, gzipped:
<input>.<case>.rgba8.gz or .rgba16.gz. manifest.txt lists every case as "<input>.<case> <bits> <width> <height> <ImageMagick
arguments>", plus the ImageMagick version. tests/Egami.Tests/Operations/Filters/FilterReferenceTests.cs runs the matching Egami
chain and applies each case's gate (identity for filters that pick values, closeness for the rest).

Usage, from the repo root, with Docker running: python tools/make-filter-references.py
The script runs alpine:3.20 and installs ImageMagick inside it with apk, as tools/make-op-references.py does.
"""
import gzip
import math
import os
import shutil
import subprocess

IMAGE = "alpine:3.20"
OPS = "tests/images/ops"
IMAGES = "tests/images/filters"
TARGET = "tests/reference/filters"
CACHE = ".cache/filter-references"


def logo(width, height):
    data = bytearray()
    for y in range(height):
        for x in range(width):
            r, g, b, a = 0, 0, 0, 0
            # A rounded badge: an opaque rectangle with corners of radius 12, antialiased.
            cx, cy = min(max(x + 0.5, 22), 98), min(max(y + 0.5, 32), 88)
            d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
            cover = max(0.0, min(1.0, 12.5 - d)) if 10 <= x < 110 and 20 <= y < 100 else 0.0
            if cover > 0:
                r, g, b, a = 40, 110, 220, round(255 * cover)
            # A disc with a 10-pixel soft edge.
            d = math.hypot(x + 0.5 - 125, y + 0.5 - 60)
            if d < 30:
                edge = max(0.0, min(1.0, (30 - d) / 10))
                r, g, b, a = 240, 180, 30, round(255 * edge)
            data += bytes((r, g, b, a))
    return bytes(data)


def lights(width, height):
    points = [(20, 20, 255), (60, 35, 230), (110, 25, 255), (140, 80, 200), (35, 90, 255), (90, 70, 180), (125, 105, 255)]
    data = bytearray()
    for y in range(height):
        for x in range(width):
            v = 10 + (30 * x) // width
            r, g, b = v, v // 2 + 5, v + 20
            for px, py, level in points:
                if abs(x - px) <= 1 and abs(y - py) <= 1:
                    r, g, b = level, level, (level * 9) // 10
            data += bytes((r, g, b, 255))
    return bytes(data)


def steps(width, height):
    data = bytearray()
    for y in range(height):
        for x in range(width):
            if y < 40:
                v = (x // 32) * 60
            elif y < 80:
                v = (255 * x) // (width - 1)
            else:
                v = 230 if math.hypot(x + 0.5 - 80, y + 0.5 - 100) < 15 else 20
            data += bytes((v, v, v, 255))
    return bytes(data)


GENERATED = {"logo": logo, "lights": lights, "steps": steps}

# How each input is made, in ImageMagick arguments on the source named first.
DERIVED = {
    "duck": (f"{OPS}/duck.png", "-crop 240x180+200+150 +repage", "PNG32"),
    "egret": (f"{OPS}/egret.png", "-crop 180x240+150+200 +repage", "PNG32"),
    "noisy": (f"{IMAGES}/duck.png", "-seed 7 -attenuate 0.6 -channel RGB +noise Impulse +channel", "PNG32"),
    "duck16": (f"{IMAGES}/duck.png", "-depth 16 -blur 0x0.6", "PNG64"),
    "shapes": (f"{OPS}/shapes.png", "", "PNG32"),
}

# (input, case, bits, ImageMagick arguments). The tests name the Egami chain for each case.
CASES = [
    ("duck", "median-1", 8, "-statistic Median 3x3"),
    ("duck", "median-2", 8, "-statistic Median 5x5"),
    ("duck", "median-3", 8, "-statistic Median 7x7"),
    ("duck", "median-10", 8, "-statistic Median 21x21"),
    ("noisy", "median-2", 8, "-statistic Median 5x5"),
    ("noisy", "median-4", 8, "-statistic Median 9x9"),
    ("duck16", "median-2", 16, "-statistic Median 5x5"),
    ("duck16", "median-3", 16, "-statistic Median 7x7"),
    ("shapes", "median-2", 8, "-channel RGB -statistic Median 5x5"),
    ("shapes", "median-3-alpha", 8, "-channel RGBA -statistic Median 7x7"),
    ("duck", "bilateral-3-0.1", 8, "-bilateral-blur 13x13+25.5+3"),
    ("egret", "bilateral-2-0.2", 8, "-bilateral-blur 9x9+51+2"),
    ("noisy", "bilateral-noise-2-0.2", 8, "-bilateral-blur 9x9+51+2"),
    ("duck", "motion-6-2-30", 8, "-motion-blur 6x2+30"),
    ("duck", "motion-10-4-0", 8, "-motion-blur 10x4+0"),
    ("egret", "motion-8-3-110", 8, "-motion-blur 8x3+110"),
    ("duck", "motion-6-2-30-rounded", 8, "-motion-blur 6x2+30"),
    ("egret", "motion-8-3-110-rounded", 8, "-motion-blur 8x3+110"),
    ("duck", "rotational-10", 8, "-rotational-blur 10"),
    ("egret", "rotational-4", 8, "-rotational-blur 4"),
    ("duck", "despeckle", 8, "-channel RGB -despeckle"),
    ("noisy", "despeckle", 8, "-channel RGB -despeckle"),
    ("duck16", "despeckle", 16, "-channel RGB -despeckle"),
    ("steps", "bilateral-gray-3-0.1", 8, "-bilateral-blur 13x13+25.5+3"),
    # Shade keeps alpha in Egami, so ImageMagick shades only RGB; +shade needs the legacy parser (see LEGACY).
    ("duck", "shade-30x30", 8, "-channel RGB -shade 30x30 +channel"),
    ("duck", "shade-120x45", 8, "-channel RGB -shade 120x45 +channel"),
    ("egret", "shade-300x15", 8, "-channel RGB -shade 300x15 +channel"),
    ("shapes", "shade-30x30", 8, "-channel RGB -shade 30x30 +channel"),
    ("duck", "shade-colored-30x30", 8, "-channel RGB +shade 30x30 +channel"),
    ("egret", "shade-colored-210x60", 8, "-channel RGB +shade 210x60 +channel"),
    ("shapes", "shade-colored-30x30", 8, "-channel RGB +shade 30x30 +channel"),
    # ImageMagick's -emboss is a diagonal sharpening kernel followed by histogram equalization, not a directional relief, so
    # Egami's Emboss is only compared with it and the PSNR reported.
    ("duck", "emboss", 8, "-emboss 0x1"),
    ("egret", "emboss", 8, "-emboss 0x1"),
    # Edges and morphology (stage B). Canny's maps are gated by the edge-map rule in the tests, not by identity.
    ("duck", "canny-1", 8, "-canny 0x1+10%+30%"),
    ("egret", "canny-1", 8, "-canny 0x1+10%+30%"),
    ("steps", "canny-1", 8, "-canny 0x1+10%+30%"),
    ("duck", "canny-1.4", 8, "-canny 0x1.4+5%+20%"),
    ("noisy", "canny-2", 8, "-canny 0x2+10%+30%"),
    ("steps", "dilate-disk-3", 8, "-morphology Dilate Disk:3"),
    ("duck", "dilate-square-5", 8, "-morphology Dilate Square:5"),
    ("duck", "erode-square-5", 8, "-morphology Erode Square:5"),
    ("duck", "dilate-square-25", 8, "-morphology Dilate Square:25"),
    ("duck", "dilate-rect-4x2", 8, "-morphology Dilate Rectangle:9x5"),
    ("duck", "erode-rect-1x3", 8, "-morphology Erode Rectangle:3x7"),
    ("duck", "erode-disk-12", 8, "-morphology Erode Disk:12"),
    ("duck", "open-disk-2", 8, "-morphology Open Disk:2"),
    ("duck", "close-diamond-3", 8, "-morphology Close Diamond:3"),
    ("duck", "close-cross-2-x2", 8, "-morphology Close:2 Plus:2"),
    ("noisy", "open-square-1", 8, "-morphology Open Square:1"),
    ("duck", "gradient-disk-2", 8, "-channel RGB -morphology Edge Disk:2"),
    ("duck", "tophat-disk-4", 8, "-channel RGB -morphology TopHat Disk:4"),
    ("duck", "bottomhat-square-3", 8, "-channel RGB -morphology BottomHat Square:3"),
    ("shapes", "dilate-disk-4", 8, "-morphology Dilate Disk:4"),
    ("shapes", "erode-mask", 8, "-morphology Erode 3x3+0+1:1,1,-,-,1,1,1,-,-"),
    ("shapes", "dilate-mask", 8, "-morphology Dilate 3x3+0+1:1,1,-,-,1,1,1,-,-"),
    ("duck16", "dilate-disk-2", 16, "-morphology Dilate Disk:2"),
    ("duck16", "close-square-3", 16, "-morphology Close Square:3"),
]

# Options the magick command no longer runs, so their cases go through its legacy convert parser.
LEGACY = ("+shade",)


def main():
    for folder in (IMAGES, TARGET, CACHE):
        os.makedirs(folder, exist_ok=True)

    script = ["set -e", f"magick -version | head -1 > {CACHE}/version.txt"]
    for name, make in GENERATED.items():
        with open(os.path.join(CACHE, name + ".rgba8"), "wb") as f:
            f.write(make(160, 120))
        script.append(f"magick -size 160x120 -depth 8 RGBA:{CACHE}/{name}.rgba8 -define png:exclude-chunks=date,time PNG32:{IMAGES}/{name}.png")
    for name, (source, args, kind) in DERIVED.items():
        script.append(f"magick {source} {args} -alpha set -define png:exclude-chunks=date,time {kind}:{IMAGES}/{name}.png")

    outputs = []
    for name, case, bits, args in CASES:
        out = f"{CACHE}/{name}.{case}.rgba{bits}"
        tool = "magick convert" if any(option in args.split() for option in LEGACY) else "magick"
        script.append(f"{tool} {IMAGES}/{name}.png {args} -depth {bits} -endian MSB RGBA:{out}")
        script.append(f"echo \"{name}.{case} {bits} $({tool} {IMAGES}/{name}.png {args} -format '%w %h' info:) {args}\" >> {CACHE}/manifest.txt")
        outputs.append(out)

    with open(os.path.join(CACHE, "run.sh"), "w", newline="\n") as f:
        f.write("\n".join(script) + "\n")
    if os.path.exists(os.path.join(CACHE, "manifest.txt")):
        os.remove(os.path.join(CACHE, "manifest.txt"))

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache imagemagick > /dev/null && sh {CACHE}/run.sh"], check=True)

    for out in outputs:
        # No name and a zero time in the gzip header, so regenerating gives identical files.
        with open(out, "rb") as src, open(os.path.join(TARGET, os.path.basename(out) + ".gz"), "wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as dst:
                shutil.copyfileobj(src, dst)
    with open(os.path.join(CACHE, "version.txt")) as f:
        version = f.read().strip()
    with open(os.path.join(CACHE, "manifest.txt")) as f:
        lines = [line.rstrip("\n") for line in f if line.strip()]
    with open(os.path.join(TARGET, "manifest.txt"), "w", newline="\n") as f:
        f.write(f"# {version}, in {IMAGE}. Lines: <input>.<case> <bits> <width> <height> <ImageMagick arguments>\n")
        f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
