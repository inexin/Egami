"""Writes the Phase 2.5.3 tone and color references in tests/reference/tone, made by ImageMagick in Docker.

Inputs, in tests/images/tone (committed): generated here, or made here by ImageMagick from tests/images/ops/duck.png.
  ramp.png    256x48, generated: rows of a gray ramp, red, green and blue ramps, and a ramp of mixed colors, opaque.
  ramp16.png  256x256 16-bit gray, generated: every 16-bit value once, row by row.
  page.png    600x848 8-bit gray, generated: a text-like page with noise and light that falls off across it.
  dull.png    duck.png at low contrast (+level 25%,70%), for the auto steps; this and the next two have no alpha channel, since
              ImageMagick's synced auto steps count one as a fourth color.
  cast.png    duck.png with an orange cast (-colorize 20%), for white balance.
  foggy.png   duck.png flattened toward light gray (+level 35%,90%), for CLAHE and local contrast.
  hald.png    a graded level-8 Hald CLUT (hald:8 with contrast and saturation), for -hald-clut.

Each reference is ImageMagick's output as raw RGBA, row by row, gzipped: <input>.<case>.rgba8.gz, or .rgba16.gz (little-endian)
for ramp16. manifest.txt lists every case as "<input>.<case> <width> <height> <ImageMagick arguments>", plus the version; the
auto-threshold cases add the level ImageMagick chose. The tests in tests/Egami.Tests/Operations/Tone/ToneReferenceTests.cs run
the matching Egami chains.

Usage, from the repo root, with Docker running:  python tools/make-tone-references.py
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
IMAGES = "tests/images/tone"
OPS = "tests/images/ops"
TARGET = "tests/reference/tone"
CACHE = ".cache/tone"


def ramp(width, height):
    data = bytearray()
    for y in range(height):
        band = y // 8
        for x in range(width):
            v = x
            p = [(v, v, v), (v, 0, 0), (0, v, 0), (0, 0, v), (v, 255 - v, (v * 3) % 256), ((v * 7) % 256, v, 128)][band]
            data += bytes((*p, 255))
    return bytes(data)


def ramp16(width, height):
    return b"".join(struct.pack(">H", (y * width + x) & 0xFFFF) for y in range(height) for x in range(width))


def page(width, height):
    """Text-like ink on paper whose light falls from 235 to 150 across the page, with noise."""
    rng = random.Random(253)
    ink = bytearray(width * height)
    margin = width // 10
    y = margin
    while y < height - margin:
        x = margin
        end = width - margin - (rng.randrange(0, width // 3) if rng.random() < 0.2 else 0)
        while x < end:
            for _ in range(rng.randrange(2, 9)):
                w = rng.randrange(4, 8)
                if x + w > end:
                    break
                top = y + (0 if rng.random() < 0.3 else 3)
                for yy in range(top, y + 11):
                    ink[yy * width + x:yy * width + x + w] = b"\x01" * w
                x += w + 2
            x += 7
        y += 20
    data = bytearray()
    for yy in range(height):
        for xx in range(width):
            paper = 235 - 85 * xx / width
            v = paper * 0.3 if ink[yy * width + xx] else paper
            data.append(max(0, min(255, round(v + rng.gauss(0, 4)))))
    return bytes(data)


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


# Inputs ImageMagick makes from the duck.
# They are opaque and have no alpha channel: ImageMagick's synced steps count an alpha channel as a fourth color.
MADE = {
    "dull": "duck.png -alpha off +level 25%,70%",
    "cast": "duck.png -alpha off -fill 'rgb(255,170,90)' -colorize 20%",
    "foggy": "duck.png -alpha off +level 35%,90%",
    "hald": "hald:8 -sigmoidal-contrast 4x50% -modulate 100,140,100",
}


def cases():
    """(input, case, ImageMagick arguments placed after the input). The tests hold the matching Egami chains."""
    c = []
    for name in ("duck", "ramp"):
        c += [
            (name, "level", "-level 10%,85%,1.3"),
            (name, "plus-level", "+level 20%,70%"),
            (name, "gamma", "-gamma 2.2"),
            (name, "sigmoidal", "-sigmoidal-contrast 5x40%"),
            (name, "sigmoidal-down", "+sigmoidal-contrast 5x40%"),
            (name, "posterize", "+dither -posterize 5"),
            (name, "solarize", "-solarize 60%"),
            (name, "colorize", "-fill 'rgb(255,128,0)' -colorize 30%"),
            (name, "tint", "-fill 'rgb(255,200,150)' -tint 40"),
            (name, "tint-full", "-fill 'rgb(40,90,200)' -tint 100"),
            (name, "duotone", "-grayscale Rec709Luma +level-colors '#1b2a49','#f2c14e'"),
            (name, "threshold", "-threshold 45%"),
            (name, "threshold-channels", "-channel RGB -threshold 40%"),
        ]
    c += [
        ("ramp16", "level", "-level 10%,85%,1.3"),
        ("ramp16", "gamma", "-gamma 2.2"),
        ("ramp16", "sigmoidal", "-sigmoidal-contrast 5x40%"),
        ("ramp16", "solarize", "-solarize 60%"),
    ]
    c += [
        ("dull", "auto-level", "-auto-level"),
        ("dull", "auto-level-channels", "-channel RGB -auto-level"),
        ("dull", "contrast-stretch", "-contrast-stretch 2%x1%"),
        ("dull", "normalize", "-normalize"),
        ("dull", "auto-gamma", "-auto-gamma"),
        ("dull", "auto-gamma-channels", "-channel RGB -auto-gamma"),
        ("dull", "equalize-channels", "-channel RGB -equalize"),
        ("cast", "white-balance", "-white-balance"),
        ("foggy", "clahe", "-clahe 80x60+256+2"),
        ("foggy", "local-contrast", "-local-contrast 10x12.5"),
        ("foggy", "local-contrast-strong", "-local-contrast 30x60"),
        ("duck", "hald-clut", "hald.png -hald-clut"),
    ]
    return c


def thresholds():
    return [("page", f"auto-threshold-{m.lower()}", f"-auto-threshold {m}") for m in ("OTSU", "Triangle", "Kapur")]


def main():
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    os.makedirs(CACHE, exist_ok=True)
    png(os.path.join(IMAGES, "ramp.png"), 256, 48, ramp(256, 48))
    png(os.path.join(IMAGES, "ramp16.png"), 256, 256, ramp16(256, 256), depth=16, color=0)
    png(os.path.join(IMAGES, "page.png"), 600, 848, page(600, 848), color=0)
    shutil.copyfile(os.path.join(OPS, "duck.png"), os.path.join(CACHE, "duck.png"))
    for name in ("ramp", "ramp16", "page"):
        shutil.copyfile(os.path.join(IMAGES, name + ".png"), os.path.join(CACHE, name + ".png"))

    script = ["set -e", f"cd {CACHE}", "magick -version | head -1 > version.txt", "rm -f manifest.txt"]
    for name, args in MADE.items():
        script.append(f"magick {args} -depth 8 PNG24:{name}.png")
    outputs = [f"{name}.png" for name in MADE]
    for name, case, args in cases():
        sixteen = name == "ramp16"
        out = f"{name}.{case}.rgba{16 if sixteen else 8}"
        depth = "-depth 16 -endian LSB" if sixteen else "-depth 8"
        script.append(f"magick {name}.png {args} {depth} RGBA:{out}")
        script.append(f"echo \"{name}.{case} $(magick {name}.png {args} -format '%w %h' info: | head -1) {args}\" >> manifest.txt")
        outputs.append(out)
    for name, case, args in thresholds():
        out = f"{name}.{case}.rgba8"
        script.append(f"magick {name}.png {args} -depth 8 RGBA:{out}")
        script.append(f"echo \"{name}.{case} $(magick {name}.png {args} -format '%w %h %[auto-threshold:threshold]' info: | head -1) {args}\" >> manifest.txt")
        outputs.append(out)

    with open(os.path.join(CACHE, "run.sh"), "w", newline="\n") as f:
        f.write("\n".join(script) + "\n")
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache imagemagick > /dev/null && sh {CACHE}/run.sh"], check=True)

    for out in [o for o in outputs if o.endswith(".png")]:
        shutil.copyfile(os.path.join(CACHE, out), os.path.join(IMAGES, out))
    for out in [o for o in outputs if ".rgba" in o]:
        # No name and a zero time in the gzip header, so regenerating gives identical files.
        with open(os.path.join(CACHE, out), "rb") as src, open(os.path.join(TARGET, out + ".gz"), "wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as dst:
                shutil.copyfileobj(src, dst)
    with open(os.path.join(CACHE, "version.txt")) as f:
        version = f.read().strip()
    with open(os.path.join(CACHE, "manifest.txt")) as f:
        lines = [line.rstrip("\n") for line in f if line.strip()]
    with open(os.path.join(TARGET, "manifest.txt"), "w", newline="\n") as f:
        f.write(f"# {version}, in {IMAGE}. Lines: <input>.<case> <width> <height> [<auto-threshold level>] <ImageMagick arguments>\n")
        f.write("\n".join(lines) + "\n")
    print(f"Wrote {len(outputs)} references and inputs with {version}.")


if __name__ == "__main__":
    main()
