"""Writes the operation references in tests/reference/ops, made by ImageMagick in Docker.

Inputs, in tests/images/ops (all 8-bit RGBA PNG, committed):
  shapes.png  200x150, generated here: a soft-edged disc, a hard-edged bar and a gradient on transparent black.
  zone.png    200x150, generated here: an opaque zone plate, which shows any difference in how filters treat fine detail.
  duck.png    640x480, from Adrian Pingstone's public-domain "Mandarin.duck.arp.jpg" (Wikimedia Commons).
  egret.png   480x640, from David Hall's public-domain USFWS "Egretta_thula1.jpg" (Wikimedia Commons).

Each reference is the operation's output as raw 8-bit RGBA, row by row, gzipped: <input>.<case>.rgba8.gz. Rotation references
hold premultiplied color; see rotate().
manifest.txt lists every case as "<input>.<case> <width> <height> <ImageMagick arguments>", plus the ImageMagick version.
The tests in tests/Egami.Tests/Operations/ReferenceTests.cs run the matching Egami chain and require PSNR >= 45 dB.

Usage, from the repo root, with Docker running:
  1. Download the two photos into .cache/op-sources (the commands are in the docstring of SOURCES below).
  2. python tools/make-op-references.py
The script runs alpine:3.20 and installs ImageMagick (with its JPEG codec) inside it with apk, so Docker downloads both on first use.
"""
import gzip
import math
import os
import shutil
import subprocess
import sys

IMAGE = "alpine:3.20"
IMAGES = "tests/images/ops"
TARGET = "tests/reference/ops"
CACHE = ".cache/op-sources"

# Downloaded by hand (see the module docstring); cropped to 4:3 and reduced with Lanczos into tests/images/ops.
SOURCES = {
    # curl -L -o .cache/op-sources/Mandarin.duck.arp.jpg https://upload.wikimedia.org/wikipedia/commons/5/51/Mandarin.duck.arp.jpg
    "duck": ("Mandarin.duck.arp.jpg", "-gravity center -crop 1680x1260+0+0 +repage -filter Lanczos -resize 640x480!"),
    # curl -L -o .cache/op-sources/Egretta_thula1.jpg https://upload.wikimedia.org/wikipedia/commons/8/81/Egretta_thula1.jpg
    "egret": ("Egretta_thula1.jpg", "-gravity center -crop 750x1000+0+0 +repage -filter Lanczos -resize 480x640!"),
}

FILTERS = {"nearest": "Point", "box": "Box", "bilinear": "Triangle", "bicubic": "Catrom", "mitchell": "Mitchell", "lanczos3": "Lanczos"}


def shapes(width, height):
    data = bytearray()
    for y in range(height):
        for x in range(width):
            r, g, b, a = 0, 0, 0, 0
            # A gradient band along the bottom, half transparent.
            if y >= 110:
                r, g, b, a = int(255 * x / (width - 1)), 64, 255 - int(255 * x / (width - 1)), 160
            # A hard-edged opaque bar.
            if 20 <= x < 70 and 15 <= y < 95:
                r, g, b, a = 30, 200, 60, 255
            # A disc with an 8-pixel soft edge.
            d = math.hypot(x + 0.5 - 130, y + 0.5 - 55)
            if d < 45:
                edge = max(0.0, min(1.0, (45 - d) / 8))
                r, g, b, a = 230, 40, 40, round(255 * edge)
            data += bytes((r, g, b, a))
    return bytes(data)


def zone(width, height):
    data = bytearray()
    for y in range(height):
        for x in range(width):
            d2 = (x - width / 2) ** 2 + (y - height / 2) ** 2
            v = round(127.5 + 127.5 * math.cos(d2 * math.pi / 220))
            data += bytes((v, 255 - v, (v + 85) % 256, 255))
    return bytes(data)


def cases(name, width, height):
    """
    Returns (case, ImageMagick arguments) for one input. Reductions use the whole image; every other operation on a photo uses a
    crop of its middle, which the tests take with Crop first, to keep the references small. Rotation needs Egami's canvas; see
    rotate().
    """
    small = (width // 4, height // 4) if width >= 400 else (64, 48)
    result = []
    for case, im in FILTERS.items():
        result.append((f"down-{case}", f"-filter {im} -resize {small[0]}x{small[1]}!"))
    result.append(("down-bicubic-linear", f"-colorspace RGB -filter Catrom -resize {small[0]}x{small[1]}! -colorspace sRGB"))
    crop, w, h = "", width, height
    if width >= 400:
        w, h = (240, 180) if width > height else (180, 240)
        crop = f"-crop {w}x{h}+{(width - w) // 2}+{(height - h) // 2} +repage "
    result.append(("up-bicubic", f"{crop}-filter Catrom -resize {w * 3 // 2}x{h * 3 // 2}!"))
    result.append(("up-lanczos3", f"{crop}-filter Lanczos -resize {w * 3 // 2}x{h * 3 // 2}!"))
    result.append(("rotate-15", crop + rotate(w, h, 15)))
    result.append(("rotate-n33", crop + rotate(w, h, -33)))
    result.append(("blur-2", f"{crop}-gaussian-blur 0x2"))
    result.append(("sharpen", f"{crop}-unsharp 0x1+1+0"))
    if width < 400:
        result.append(("blur-25", "-gaussian-blur 0x25"))
    return result


def rotate(width, height, degrees):
    """
    Bicubic (Catmull-Rom) rotation about the center onto Egami's canvas: the rotated bounding box rounded to whole pixels.

    ImageMagick's distort interpolates straight color with the transparent background, which darkens edges. To get the
    premultiplied result instead, color is rotated after flattening onto black (which premultiplies it) with a black background,
    and alpha is rotated separately; the reference then holds premultiplied color, which the test compares as such.
    """
    c, s = abs(math.cos(math.radians(degrees))), abs(math.sin(math.radians(degrees)))
    w = max(1, math.floor(width * c + height * s + 0.5))
    h = max(1, math.floor(width * s + height * c + 0.5))
    distort = (f"-virtual-pixel black -interpolate Catrom -filter Point -define distort:viewport={w}x{h}+0+0 "
               f"+distort SRT {width / 2},{height / 2},1,{degrees},{w / 2},{h / 2}")
    return (f"\\( +clone -background black -alpha remove -alpha off {distort} \\) \\( -clone 0 -alpha extract {distort} \\) "
            f"-delete 0 -alpha off -compose CopyOpacity -composite")


def main():
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    os.makedirs(CACHE, exist_ok=True)
    for name, (file, _) in SOURCES.items():
        if not os.path.exists(os.path.join(CACHE, file)):
            sys.exit(f"Missing {CACHE}/{file}; download it as the comment in SOURCES says.")

    script = ["set -e", "magick -version | head -1 > .cache/op-sources/version.txt"]
    for name, data, size in (("shapes", shapes(200, 150), (200, 150)), ("zone", zone(200, 150), (200, 150))):
        with open(os.path.join(CACHE, name + ".rgba8"), "wb") as f:
            f.write(data)
        script.append(f"magick -size {size[0]}x{size[1]} -depth 8 RGBA:{CACHE}/{name}.rgba8 PNG32:{IMAGES}/{name}.png")
    for name, (file, prep) in SOURCES.items():
        script.append(f"magick {CACHE}/{file} -auto-orient -strip -colorspace sRGB {prep} -alpha set PNG32:{IMAGES}/{name}.png")

    inputs = {"shapes": (200, 150), "zone": (200, 150), "duck": (640, 480), "egret": (480, 640)}
    manifest = []
    for name, (width, height) in inputs.items():
        for case, args in cases(name, width, height):
            out = f"{CACHE}/{name}.{case}.rgba8"
            script.append(f"magick {IMAGES}/{name}.png {args} -depth 8 RGBA:{out}")
            script.append(f"echo \"{name}.{case} $(magick {IMAGES}/{name}.png {args} -format '%w %h' info:) {args}\" >> {CACHE}/manifest.txt")
            manifest.append(out)

    with open(os.path.join(CACHE, "run.sh"), "w", newline="\n") as f:
        f.write("\n".join(script) + "\n")
    if os.path.exists(os.path.join(CACHE, "manifest.txt")):
        os.remove(os.path.join(CACHE, "manifest.txt"))

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache imagemagick imagemagick-jpeg > /dev/null && sh {CACHE}/run.sh"], check=True)

    for out in manifest:
        # No name and a zero time in the gzip header, so regenerating gives identical files.
        with open(out, "rb") as src, open(os.path.join(TARGET, os.path.basename(out) + ".gz"), "wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as dst:
                shutil.copyfileobj(src, dst)
    with open(os.path.join(CACHE, "version.txt")) as f:
        version = f.read().strip()
    with open(os.path.join(CACHE, "manifest.txt")) as f:
        lines = [line.rstrip("\n") for line in f if line.strip()]
    with open(os.path.join(TARGET, "manifest.txt"), "w", newline="\n") as f:
        f.write(f"# {version}, in {IMAGE}. Lines: <input>.<case> <width> <height> <ImageMagick arguments>\n")
        f.write("\n".join(lines) + "\n")
    print(f"Wrote {len(manifest)} references with {version}.")


if __name__ == "__main__":
    main()
