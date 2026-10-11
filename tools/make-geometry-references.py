"""Writes the Phase 2.5.2 geometry references in tests/reference/geometry, made by ImageMagick in Docker.

Inputs, generated here into tests/images/geometry (8-bit RGBA PNG, committed):
  coded.png     200x150: each pixel stores its own position (red = x, green = y, blue = 128), opaque, for point mapping.
  dmap.png      200x150: a smooth displacement map (red and green waves around mid-gray).
  bordered.png  200x150: a picture on a near-white margin with faint noise in it, for -trim with -fuzz.
  bordered16.png 120x90: the same idea at 16 bits, with differences of a few 16-bit levels.
  zone.png and duck.png come from tests/images/ops.

Each reference is ImageMagick's output as raw 8-bit RGBA, row by row, gzipped: <input>.<case>.rgba8.gz. Trim cases write no
pixels; their line in the manifest holds the rectangle ImageMagick's %@ prints.
manifest.txt lists every case as "<input>.<case> <width> <height> <ImageMagick arguments>", plus the ImageMagick version.
The tests in tests/Egami.Tests/Operations/GeometryReferenceTests.cs run the matching Egami chain:
  - point cases use -filter Point -interpolate Nearest, and must give the same source pixel away from ties at half pixels;
  - closeness cases use ImageMagick's EWA with Catrom, against Egami's radial Bicubic, and must reach the closeness gate.

Usage, from the repo root, with Docker running:  python tools/make-geometry-references.py
The script runs alpine:3.20 and installs ImageMagick inside it with apk, so Docker downloads both on first use.
"""
import gzip
import math
import os
import random
import shutil
import struct
import subprocess
import zlib

IMAGE = "alpine:3.20"
IMAGES = "tests/images/geometry"
OPS = "tests/images/ops"
TARGET = "tests/reference/geometry"
CACHE = ".cache/geometry"

FILTERS = ("Lanczos2", "Hermite", "Gaussian", "Spline")
POINT = "-filter Point -interpolate Nearest -virtual-pixel Transparent -background none"
EWA = "-filter Catrom -virtual-pixel Transparent -background none"


def coded(width, height):
    return b"".join(bytes((x, y, 128, 255)) for y in range(height) for x in range(width))


def dmap(width, height):
    data = bytearray()
    for y in range(height):
        for x in range(width):
            r = round(127.5 + 110 * math.sin(x * 0.07 + y * 0.02))
            g = round(127.5 + 110 * math.cos(y * 0.09 - x * 0.03))
            data += bytes((r, g, 128, 255))
    return bytes(data)


def bordered(width, height):
    rng = random.Random(25)
    data = bytearray()
    for y in range(height):
        for x in range(width):
            v = 250 - rng.randrange(0, 4)
            p = (v, v, v, 255)
            if 37 <= x < 151 and 22 <= y < 119:
                p = ((x * 3) % 256, (y * 5) % 256, 90, 255)
            elif (x, y) in ((20, 70), (170, 30)):
                p = (225, 225, 225, 255)
            data += bytes(p)
    return bytes(data)


def bordered16(width, height):
    rng = random.Random(7)
    data = bytearray()
    for y in range(height):
        for x in range(width):
            v = 40000 + rng.randrange(0, 3)
            p = (v, v, v, 65535)
            if 30 <= x < 80 and 20 <= y < 61:
                p = (x * 500, y * 700, 9000, 65535)
            elif (x, y) == (100, 10):
                p = (40040, 40000, 40000, 65535)
            data += struct.pack(">4H", *p)
    return bytes(data)


def page(width, height):
    """A page of text-like ink: lines of words made of letter-sized blocks, black on white, with a heading and a picture."""
    rng = random.Random(1986)
    pixels = bytearray(b"\xff" * (width * height))

    def block(x0, y0, x1, y1, value=0):
        for y in range(max(0, y0), min(height, y1)):
            pixels[y * width + max(0, x0):y * width + min(width, x1)] = bytes([value]) * (min(width, x1) - max(0, x0))

    margin = width // 10
    y = margin
    block(margin, y, width // 2, y + 22)
    y += 60
    while y < height - margin:
        if height // 2 < y < height // 2 + 140:
            block(margin, y, width - margin, y + 120, 140)
            y += 160
            continue
        x = margin + (40 if rng.random() < 0.15 else 0)
        end = width - margin - (rng.randrange(0, width // 3) if rng.random() < 0.2 else 0)
        while x < end:
            for _ in range(rng.randrange(2, 9)):
                w = rng.randrange(5, 10)
                if x + w > end:
                    break
                top = y + (0 if rng.random() < 0.3 else 4)
                bottom = y + 14 + (4 if rng.random() < 0.15 else 0)
                block(x, top, x + w, bottom)
                x += w + 2
            x += 9
        y += 26
    return bytes(pixels)


def png(path, width, height, data, depth=8, color=6):
    """A plain RGBA (color 6) or gray (color 0) PNG, written here so the inputs need no image library."""
    stride = width * (4 if color == 6 else 1) * depth // 8
    raw = b"".join(b"\0" + data[y * stride:(y + 1) * stride] for y in range(height))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


def cases():
    """(input, case, ImageMagick arguments placed after the input). The tests hold the matching Egami chains."""
    c = []
    # Point mapping of the warps and transforms on the coordinate-coded image.
    c += [
        ("coded", "barrel", f"{POINT} -distort Barrel '0.1 -0.2 0.05'"),
        ("coded", "barrel-center", f"{POINT} -distort Barrel '0.05 0 -0.1 1.1 70,60'"),
        ("coded", "barrel-inverse", f"{POINT} -distort BarrelInverse '0.05 -0.05 0.02'"),
        ("coded", "polar", f"{POINT} +distort Polar '0'"),
        ("coded", "polar-args", f"{POINT} +distort Polar '60 10 100,75 -90,180'"),
        ("coded", "depolar", f"{POINT} +distort DePolar '0'"),
        ("coded", "depolar-args", f"{POINT} +distort DePolar '70 20 90,80'"),
        ("coded", "arc", f"{POINT} +distort Arc '60'"),
        ("coded", "arc-args", f"{POINT} +distort Arc '180 30 120 40'"),
        ("coded", "swirl", f"{POINT} -swirl 120"),
        ("coded", "implode", f"{POINT} -implode 0.6"),
        ("coded", "explode", f"{POINT} -implode -0.6"),
        ("coded", "wave", f"{POINT} -wave 8x40"),
        ("coded", "roll", "-roll +13+7"),
        ("coded", "affine", f"{POINT} -define distort:viewport=260x220-30-20 -distort AffineProjection '1.1037,0.4113,-0.2971,0.9089,12.31,-5.17'"),
        ("coded", "quad", f"{POINT} -define distort:viewport=230x180+0+0 -distort Perspective '0,0 20,10  200,0 210,0  200,150 230,170  0,150 0,160'"),
        ("coded", "displace", f"{POINT} dmap.png -compose Displace -define compose:args=10x10 -composite"),
    ]
    # Closeness of EWA sampling where the image shrinks or tilts away.
    c += [
        ("zone", "shrink", f"{EWA} -define distort:viewport=110x100+0+0 -distort AffineProjection '0.383,0.117,-0.117,0.383,25,15'"),
        ("duck", "shrink", f"{EWA} -define distort:viewport=300x260+0+0 -distort AffineProjection '0.383,0.117,-0.117,0.383,60,10'"),
        ("duck", "quad", f"{EWA} -define distort:viewport=640x480+0+0 -distort Perspective '0,0 200,40  640,0 440,40  640,480 640,440  0,480 0,440'"),
        ("duck", "skew", f"{EWA} -define distort:viewport=740x480+0+0 -distort AffineProjection '1,0,0.2,1,0,0'"),
        ("duck", "barrel", f"{EWA} -distort Barrel '0.0 -0.06 0.0'"),
    ]
    # The four resize filters added in 2.5.2, reducing and enlarging.
    for name in FILTERS:
        c += [
            ("zone", f"down-{name.lower()}", f"-filter {name} -resize 70x50!"),
            ("duck", f"down-{name.lower()}", f"-filter {name} -resize 160x120!"),
            ("duck", f"up-{name.lower()}", f"-crop 120x90+260+200 +repage -filter {name} -resize 300x225!"),
        ]
    return c


def trims():
    """(input, case, -trim arguments): the rectangle ImageMagick's %@ prints for each fuzz."""
    return [
        ("bordered", "trim-0", "-fuzz 0%"),
        ("bordered", "trim-2", "-fuzz 2%"),
        ("bordered", "trim-5", "-fuzz 5%"),
        ("bordered", "trim-15", "-fuzz 15%"),
        ("bordered16", "trim-0", "-fuzz 0%"),
        ("bordered16", "trim-001", "-fuzz 0.01%"),
        ("bordered16", "trim-01", "-fuzz 0.1%"),
    ]


# Tilts for the deskew pages, degrees clockwise as ImageMagick's -rotate turns.
DESKEW = (-12.0, -3.3, -0.7, 0.0, 0.45, 2.0, 7.5, 14.0)


def main():
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    os.makedirs(CACHE, exist_ok=True)
    png(os.path.join(IMAGES, "coded.png"), 200, 150, coded(200, 150))
    png(os.path.join(IMAGES, "dmap.png"), 200, 150, dmap(200, 150))
    png(os.path.join(IMAGES, "bordered.png"), 200, 150, bordered(200, 150))
    png(os.path.join(IMAGES, "bordered16.png"), 120, 90, bordered16(120, 90), depth=16)
    png(os.path.join(CACHE, "page.png"), 827, 1169, page(827, 1169), color=0)
    for name in ("zone", "duck"):
        shutil.copyfile(os.path.join(OPS, name + ".png"), os.path.join(CACHE, name + ".png"))
    for name in ("coded", "dmap", "bordered", "bordered16"):
        shutil.copyfile(os.path.join(IMAGES, name + ".png"), os.path.join(CACHE, name + ".png"))

    script = ["set -e", f"cd {CACHE}", "magick -version | head -1 > version.txt", "rm -f manifest.txt"]
    outputs = []
    for name, case, args in cases():
        out = f"{name}.{case}.rgba8"
        script.append(f"magick {name}.png {args} -depth 8 RGBA:{out}")
        script.append(f"echo \"{name}.{case} $(magick {name}.png {args} -format '%w %h' info: | head -1) {args}\" >> manifest.txt")
        outputs.append(out)
    for name in FILTERS:
        # The kernel as ImageMagick tabulates it, x from 0 in steps of 0.01, to six significant digits.
        script.append(f"magick -size 10x10 xc: -filter {name} -define filter:verbose=1 -resize 5x5 null: > filter-{name.lower()}.txt")
        outputs.append(f"filter-{name.lower()}.txt")
    for angle in DESKEW:
        # A page turned by a known angle, kept at its size as a scanner would; the deskew reference is ImageMagick's own angle.
        out = f"page{angle:+.2f}.png"
        script.append(f"magick page.png -background white -virtual-pixel background +distort SRT {angle} -gravity center -extent 827x1169 -type Grayscale -depth 8 PNG8:{out}")
        script.append(f"echo \"page.deskew{angle:+.2f} $(magick {out} -deskew 40% -format '%[deskew:angle]' info:) -deskew 40%\" >> manifest.txt")
        outputs.append(out)
    for name, case, args in trims():
        script.append(f"echo \"{name}.{case} $(magick {name}.png {args} -format '%@' info:) {args} -trim\" >> manifest.txt")

    with open(os.path.join(CACHE, "run.sh"), "w", newline="\n") as f:
        f.write("\n".join(script) + "\n")
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache imagemagick > /dev/null && sh {CACHE}/run.sh"], check=True)

    for out in [o for o in outputs if o.endswith(".txt")]:
        shutil.copyfile(os.path.join(CACHE, out), os.path.join(TARGET, out))
    for out in [o for o in outputs if o.endswith(".png")]:
        shutil.copyfile(os.path.join(CACHE, out), os.path.join(IMAGES, out))
    for out in [o for o in outputs if o.endswith(".rgba8")]:
        # No name and a zero time in the gzip header, so regenerating gives identical files.
        with open(os.path.join(CACHE, out), "rb") as src, open(os.path.join(TARGET, out + ".gz"), "wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as dst:
                shutil.copyfileobj(src, dst)
    with open(os.path.join(CACHE, "version.txt")) as f:
        version = f.read().strip()
    with open(os.path.join(CACHE, "manifest.txt")) as f:
        lines = [line.rstrip("\n") for line in f if line.strip()]
    with open(os.path.join(TARGET, "manifest.txt"), "w", newline="\n") as f:
        f.write(f"# {version}, in {IMAGE}. Lines: <input>.<case> <width> <height> <ImageMagick arguments>; trims: <input>.<case> <WxH+X+Y> <arguments>\n")
        f.write("\n".join(lines) + "\n")
    print(f"Wrote {len(outputs)} references and kernel tables and {len(trims())} trim rectangles with {version}.")


if __name__ == "__main__":
    main()
