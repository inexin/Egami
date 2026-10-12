"""Builds the Phase 2.4.4 lossless-transform corpus in tests/images/jpeg/transform and jpegtran's results in
tests/reference/jpeg/transform, with libjpeg-turbo 3.x in Docker. jpegtran is the oracle only: it runs in the container, and
nothing from it is linked into Egami.

Corpus (tests/images/jpeg/transform; the source photo is tests/images/ops/duck.png, public domain), written by cjpeg (and
ImageMagick for CMYK) at sizes that leave partial MCUs on both edges, plus one of whole MCUs:
  t420, t422, t440, t411, t444, tgray   every chroma sampling cjpeg writes, and gray
  tprog, tari, tari-prog, t12           progressive, arithmetic (sequential and progressive) and 12-bit sources
  tcmyk                                 Adobe CMYK from ImageMagick
  t420-whole                            64 × 48, so -perfect succeeds for every operation

References (tests/reference/jpeg/transform): jpegtran -copy none's output for each source and
  - every operation (none, flips, rotations, transpose, transverse) with its default edges (Keep in Egami) and with -trim
  - -perfect, recorded as whether jpegtran accepted it (its output equals -trim's when it does)
  - crops, aligned and not, inside the image and over its partial edge, with and without -trim
  - -grayscale, alone and with operations and a crop
manifest.json lists each result with the switches that made it. The C# test runs JpegTransform with the matching options and
requires the same decoded pixels.

Usage, from the repo root with Docker running:
  python tools/make-jpeg-transform-references.py
It uses the egami-jpeg-depth image of tools/make-jpeg-depth-references.py (building it when missing) and runs itself inside it.
"""
import importlib.util
import json
import os
import subprocess
import sys

IMAGE = "egami-jpeg-depth"
CORPUS = "tests/images/jpeg/transform"
TARGET = "tests/reference/jpeg/transform"
OPS = "tests/images/ops"

# Egami's operation name -> jpegtran's switches.
OPERATIONS = {
    "None": [],
    "FlipHorizontal": ["-flip", "horizontal"],
    "FlipVertical": ["-flip", "vertical"],
    "Rotate90": ["-rotate", "90"],
    "Rotate180": ["-rotate", "180"],
    "Rotate270": ["-rotate", "270"],
    "Transpose": ["-transpose"],
    "Transverse": ["-transverse"],
}


def run(*args):
    subprocess.run(list(args), check=True, stdout=subprocess.DEVNULL)


def size(path):
    """Returns a JPEG's width and height from its frame header."""
    with open(path, "rb") as f:
        data = f.read()
    i = 2
    while True:
        marker, length = data[i + 1], int.from_bytes(data[i + 2:i + 4], "big")
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            return int.from_bytes(data[i + 7:i + 9], "big"), int.from_bytes(data[i + 5:i + 7], "big")
        i += 2 + length


def inside():
    os.makedirs(CORPUS, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    work = "/tmp/src"
    os.makedirs(work, exist_ok=True)

    def source(name, size, *, gray=False, depth=8):
        path = os.path.join(work, name)
        args = ["magick", f"{OPS}/duck.png", "-background", "white", "-alpha", "remove", "-alpha", "off",
                "-crop", "220x150+120+80", "+repage", "-filter", "Lanczos", "-resize", size + "!"]
        if gray:
            args += ["-colorspace", "Gray"]
        run(*args, "-depth", str(depth), path)
        return path

    def cjpeg(src, name, *options):
        out = os.path.join(CORPUS, name)
        run("cjpeg", "-dct", "int", "-quality", "85", *options, "-outfile", out, src)
        return name

    sources = [
        cjpeg(source("a.ppm", "45x29"), "t420.jpg", "-sample", "2x2"),
        cjpeg(source("b.ppm", "43x27"), "t422.jpg", "-sample", "2x1"),
        cjpeg(source("c.ppm", "37x30"), "t440.jpg", "-sample", "1x2"),
        cjpeg(source("d.ppm", "50x21"), "t411.jpg", "-sample", "4x1"),
        cjpeg(source("e.ppm", "35x19"), "t444.jpg", "-sample", "1x1"),
        cjpeg(source("f.pgm", "29x21", gray=True), "tgray.jpg"),
        cjpeg(source("a.ppm", "45x29"), "tprog.jpg", "-sample", "2x2", "-progressive"),
        cjpeg(source("a.ppm", "45x29"), "tari.jpg", "-sample", "2x1", "-arithmetic"),
        cjpeg(source("a.ppm", "45x29"), "tari-prog.jpg", "-sample", "2x2", "-arithmetic", "-progressive"),
        cjpeg(source("g.ppm", "45x29", depth=16), "t12.jpg", "-sample", "2x2", "-precision", "12"),
        cjpeg(source("h.ppm", "64x48"), "t420-whole.jpg", "-sample", "2x2"),
    ]
    run("magick", source("i.ppm", "37x23"), "-colorspace", "CMYK", "-quality", "85", os.path.join(CORPUS, "tcmyk.jpg"))
    sources.append("tcmyk.jpg")

    entries = []

    def transform(src, operation, *, edges="Keep", crop=None, gray=False):
        args = ["jpegtran", "-copy", "none"] + OPERATIONS[operation]
        tag = operation.lower()
        if edges == "Trim":
            args.append("-trim")
            tag += "-trim"
        if gray:
            args.append("-grayscale")
            tag += "-gray"
        if crop:
            x, y, w, h = crop
            args += ["-crop", f"{w}x{h}+{x}+{y}"]
            tag += f"-crop{w}x{h}+{x}+{y}"
        name = f"{os.path.splitext(src)[0]}.{tag}.jpg"
        with open(os.path.join(TARGET, name), "wb") as out:
            subprocess.run(args + [os.path.join(CORPUS, src)], check=True, stdout=out)
        entries.append({"source": src, "result": name, "operation": operation, "edges": edges, "crop": crop, "grayscale": gray})

    def perfect(src, operation):
        args = ["jpegtran", "-copy", "none", "-perfect"] + OPERATIONS[operation] + [os.path.join(CORPUS, src)]
        ok = subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        entries.append({"source": src, "operation": operation, "edges": "Strict", "perfect": ok})

    for src in sources:
        for operation in OPERATIONS:
            transform(src, operation)
            if operation != "None":
                transform(src, operation, edges="Trim")
                perfect(src, operation)

    # Crops, in the transformed image: aligned inside, unaligned inside, over the partial right and bottom edges (with and without
    # -trim), and inside the partial MCU itself.
    for src in ["t420.jpg", "t422.jpg", "t444.jpg", "tgray.jpg", "tprog.jpg", "t12.jpg", "tcmyk.jpg"]:
        width, height = size(os.path.join(CORPUS, src))
        for operation in ["None", "FlipHorizontal", "Rotate90", "Rotate180", "Transverse"]:
            w, h = (height, width) if operation in ("Rotate90", "Transverse") else (width, height)
            transform(src, operation, crop=(0, 0, w // 2, h // 2))
            transform(src, operation, crop=(5, 3, w // 2, h // 2))
            corner = (w // 2 + 1, h // 2 + 1, w - (w // 2 + 1), h - (h // 2 + 1))
            transform(src, operation, crop=corner)
            transform(src, operation, crop=corner, edges="Trim")
            transform(src, operation, crop=(w - 2, h - 3, 2, 3), edges="Trim")

    for src in ["t420.jpg", "t422.jpg", "tprog.jpg", "t12.jpg"]:
        transform(src, "None", gray=True)
        transform(src, "Rotate90", gray=True)
        transform(src, "Rotate90", gray=True, edges="Trim")
        transform(src, "Transverse", gray=True, crop=(3, 4, 20, 15))  # 29 × 45 after the transverse

    version = subprocess.run(["jpegtran", "-version"], capture_output=True, text=True).stderr.strip()
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": f"{version}; ImageMagick; alpine:3.20", "entries": entries}, f, indent=1)
        f.write("\n")
    print(f"Wrote {len(sources)} sources and {len(entries)} results.")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        spec = importlib.util.spec_from_file_location("depth", os.path.join(os.path.dirname(__file__), "make-jpeg-depth-references.py"))
        depth = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(depth)
        subprocess.run(["docker", "build", "-t", IMAGE, "-"], input=depth.DOCKERFILE, text=True, check=True)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3",
                    "tools/make-jpeg-transform-references.py", "--inside"], check=True)


if __name__ == "__main__":
    main()
