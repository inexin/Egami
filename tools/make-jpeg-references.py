"""Builds the JPEG corpus in tests/images/jpeg and its references in tests/reference/jpeg, with libjpeg-turbo, Pillow and
ImageMagick in Docker.

Corpus (tests/images/jpeg; licenses in tests/images/LICENSES):
  - The 1.2 public-domain photos (tests/images/ops/duck.png and egret.png), reduced and encoded by cjpeg in every mode the decoder
    supports: 4:2:0, 4:2:2, 4:4:4, 4:4:0, 4:1:1, gray, progressive, restart intervals, optimized tables, qualities 50 to 100, and
    odd sizes down to 1 × 1.
  - CMYK (Adobe) written by Pillow. No common tool writes YCCK, so the decoder tests build that case by hand.
  - EXIF files written by Pillow: the photo tagged with each of the 8 orientations, and one with many typed tags.
  - libjpeg-turbo's own test images (IJG license), including the arithmetic-coded testimgari.jpg.

References (tests/reference/jpeg), all from libjpeg-turbo's djpeg with its defaults (accurate integer IDCT, fancy upsampling):
  <file>.rgb8.gz / .gray8.gz          the decoded pixels, raw, row by row, gzipped
  <file>.s2/.s4/.s8.*.gz              djpeg -scale 1/2, 1/4, 1/8
  <file>.fast.*.gz                    djpeg -nosmooth (replicated chroma), for FastUpsampling
  orient-<n>.rgb8.gz                  the orientation-n file turned upright by jpegtran (lossless), then decoded
  <file>.exif.json                    Pillow's reading of the EXIF block
  encode/<source>.png, encode/*.jpg  encoder sources and what cjpeg makes of them (listed under "encodes")
  manifest.json                       what each file is and which references it has

CMYK files are decoded by Pillow (which undoes Adobe's inverted storage) and converted with R = (255 - C)(255 - K) / 255, the plain
formula the design names, since djpeg does not convert CMYK to RGB.

Usage, from the repo root with Docker running:
  1. Download libjpeg-turbo's test images into .cache/jpeg-sources (the commands are in LIBJPEG_TURBO_FILES below).
  2. python tools/make-jpeg-references.py
The script runs alpine:3.20 and installs libjpeg-turbo-utils, ImageMagick and Pillow inside it with apk.
"""
import gzip
import json
import os
import shutil
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow libjpeg-turbo-utils imagemagick imagemagick-jpeg"
CORPUS = "tests/images/jpeg"
TARGET = "tests/reference/jpeg"
CACHE = ".cache/jpeg-sources"
OPS = "tests/images/ops"

# From libjpeg-turbo 3.1.4 (IJG license, README.ijg at the repository root):
# curl -L -o .cache/jpeg-sources/<name> https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/3.1.4/testimages/<name>
# curl -L -o .cache/jpeg-sources/README.ijg https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/3.1.4/README.ijg
LIBJPEG_TURBO_FILES = ["testorig.jpg", "testimgint.jpg", "testimgari.jpg", "README.ijg"]

# EXIF orientation -> the lossless jpegtran transform that shows the image upright.
UPRIGHT = {2: ["-flip", "horizontal"], 3: ["-rotate", "180"], 4: ["-flip", "vertical"], 5: ["-transpose"],
           6: ["-rotate", "90"], 7: ["-transverse"], 8: ["-rotate", "270"]}


def run(*args):
    subprocess.run(list(args), check=True)


def ppm(png, out, size=None, crop=None, gray=False):
    """Writes a PNG as PPM (or PGM), flattened onto white, optionally cropped then resized."""
    args = ["magick", png, "-background", "white", "-alpha", "remove", "-alpha", "off"]
    if crop:
        args += ["-crop", crop, "+repage"]
    if size:
        args += ["-filter", "Lanczos", "-resize", size + "!"]
    if gray:
        args += ["-colorspace", "Gray"]
    run(*args, out)


def cjpeg(source, name, *options):
    out = os.path.join(CORPUS, name)
    run("cjpeg", "-dct", "int", *options, "-outfile", out, source)
    return out


def djpeg(path, out_base, *options):
    """Decodes with djpeg and writes the raw samples gzipped; returns (name, width, height, channels)."""
    tmp = "/tmp/out.pnm"
    run("djpeg", "-dct", "int", *options, "-pnm", "-outfile", tmp, path)
    with open(tmp, "rb") as f:
        data = f.read()
    magic, width, height, _, pixels = parse_pnm(data)
    channels = 3 if magic == b"P6" else 1
    name = f"{out_base}.{'rgb8' if channels == 3 else 'gray8'}.gz"
    write_gz(os.path.join(TARGET, name), pixels)
    return name, width, height, channels


def parse_pnm(data):
    fields = []
    i = 0
    while len(fields) < 4:
        while data[i:i + 1].isspace():
            i += 1
        if data[i:i + 1] == b"#":
            while data[i:i + 1] != b"\n":
                i += 1
            continue
        j = i
        while not data[j:j + 1].isspace():
            j += 1
        fields.append(data[i:j])
        i = j
    return fields[0], int(fields[1]), int(fields[2]), int(fields[3]), data[i + 1:]


def write_gz(path, data):
    # No name and a zero time in the header, so regenerating gives identical files.
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def exif_json(path):
    from PIL import Image, TiffImagePlugin

    def value(v):
        if isinstance(v, TiffImagePlugin.IFDRational):
            return [v.numerator, v.denominator]
        if isinstance(v, bytes):
            return {"bytes": v.hex()}
        if isinstance(v, tuple):
            return [value(x) for x in v]
        return v

    with Image.open(path) as im:
        exif = im.getexif()
        result = {"ifd0": {str(k): value(v) for k, v in exif.items() if k not in (0x8769, 0x8825)}}
        for name, tag in (("exif", 0x8769), ("gps", 0x8825)):
            ifd = exif.get_ifd(tag)
            if ifd:
                result[name] = {str(k): value(v) for k, v in ifd.items()}
    out = os.path.join(TARGET, os.path.basename(path) + ".exif.json")
    with open(out, "w", newline="\n") as f:
        json.dump(result, f, indent=1, sort_keys=True)
        f.write("\n")
    return os.path.basename(out)


def dedupe(entries):
    """Points every reference with the same bytes at one file and deletes the copies (the orientation files share pixels)."""
    import hashlib

    first = {}

    def keep(name):
        path = os.path.join(TARGET, name)
        with open(path, "rb") as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        if digest in first and first[digest] != name:
            os.remove(path)
            return first[digest]
        first.setdefault(digest, name)
        return name

    for entry in entries:
        for key in ("reference", "fast", "upright"):
            if key in entry:
                entry[key] = keep(entry[key])
        if "scaled" in entry:
            entry["scaled"] = {s: keep(n) for s, n in entry["scaled"].items()}


def inside():
    from PIL import Image, TiffImagePlugin

    os.makedirs(CORPUS, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    work = "/tmp/src"
    os.makedirs(work, exist_ok=True)
    duck = os.path.join(work, "duck.ppm")
    duck_gray = os.path.join(work, "duck.pgm")
    egret = os.path.join(work, "egret.ppm")
    ppm(f"{OPS}/duck.png", duck, size="320x240")
    ppm(f"{OPS}/duck.png", duck_gray, size="320x240", gray=True)
    ppm(f"{OPS}/egret.png", egret, size="240x320")

    entries = []

    def add(path, *, sampling, progressive=False, orientation=1, color="YCbCr", scales=False, fast=False, note=""):
        base = os.path.splitext(os.path.basename(path))[0]
        name, width, height, channels = djpeg(path, base)
        entry = {"file": os.path.basename(path), "width": width, "height": height, "channels": channels, "sampling": sampling,
                 "progressive": progressive, "orientation": orientation, "colorSpace": color, "reference": name}
        if scales:
            entry["scaled"] = {str(s): djpeg(path, f"{base}.s{s}", "-scale", f"1/{s}")[0] for s in (2, 4, 8)}
        if fast:
            entry["fast"] = djpeg(path, f"{base}.fast", "-nosmooth")[0]
        if note:
            entry["note"] = note
        entries.append(entry)

    # The photo in every sampling and mode cjpeg writes.
    add(cjpeg(duck, "duck-420.jpg", "-quality", "90", "-sample", "2x2"), sampling="2x2,1x1,1x1", scales=True, fast=True)
    add(cjpeg(duck, "duck-422.jpg", "-quality", "90", "-sample", "2x1"), sampling="2x1,1x1,1x1", fast=True)
    add(cjpeg(duck, "duck-444.jpg", "-quality", "90", "-sample", "1x1"), sampling="1x1,1x1,1x1", scales=True)
    add(cjpeg(duck, "duck-440.jpg", "-quality", "90", "-sample", "1x2"), sampling="1x2,1x1,1x1", fast=True)
    add(cjpeg(duck, "duck-411.jpg", "-quality", "90", "-sample", "4x1"), sampling="4x1,1x1,1x1", fast=True)
    add(cjpeg(duck_gray, "duck-gray.jpg", "-quality", "90"), sampling="1x1", color="Gray", scales=True)
    add(cjpeg(duck, "duck-progressive.jpg", "-quality", "90", "-sample", "2x2", "-progressive"), sampling="2x2,1x1,1x1", progressive=True, scales=True)
    add(cjpeg(duck, "duck-progressive-444.jpg", "-quality", "90", "-sample", "1x1", "-progressive"), sampling="1x1,1x1,1x1", progressive=True)
    add(cjpeg(duck_gray, "duck-progressive-gray.jpg", "-quality", "90", "-progressive"), sampling="1x1", color="Gray", progressive=True)
    add(cjpeg(duck, "duck-restart-rows.jpg", "-quality", "90", "-sample", "2x2", "-restart", "1"), sampling="2x2,1x1,1x1")
    add(cjpeg(duck, "duck-restart-blocks.jpg", "-quality", "90", "-sample", "2x2", "-restart", "3B"), sampling="2x2,1x1,1x1")
    add(cjpeg(duck, "duck-progressive-restart.jpg", "-quality", "90", "-sample", "2x2", "-progressive", "-restart", "2"), sampling="2x2,1x1,1x1", progressive=True)
    add(cjpeg(duck, "duck-optimized.jpg", "-quality", "90", "-sample", "2x2", "-optimize"), sampling="2x2,1x1,1x1")
    add(cjpeg(duck, "duck-q50.jpg", "-quality", "50", "-sample", "2x2"), sampling="2x2,1x1,1x1")
    add(cjpeg(duck, "duck-q100.jpg", "-quality", "100", "-sample", "1x1"), sampling="1x1,1x1,1x1")
    add(cjpeg(egret, "egret-420.jpg", "-quality", "85", "-sample", "2x2"), sampling="2x2,1x1,1x1", scales=True)

    # Odd sizes, baseline and progressive, cut from the egret.
    for w, h in ((1, 1), (17, 9), (33, 1), (1, 33), (31, 17)):
        src = os.path.join(work, f"odd-{w}x{h}.ppm")
        ppm(f"{OPS}/egret.png", src, crop=f"{w}x{h}+200+300")
        add(cjpeg(src, f"odd-{w}x{h}.jpg", "-quality", "90", "-sample", "2x2"), sampling="2x2,1x1,1x1")
        add(cjpeg(src, f"odd-{w}x{h}-progressive.jpg", "-quality", "90", "-sample", "2x2", "-progressive"), sampling="2x2,1x1,1x1", progressive=True)

    # CMYK, as Photoshop and Pillow store it (Adobe segment, inverted samples).
    cmyk_path = os.path.join(CORPUS, "duck-cmyk.jpg")
    with Image.open(duck) as im:
        im.convert("CMYK").save(cmyk_path, quality=90)
    with Image.open(cmyk_path) as im:
        c, m, y, k = (list(b.getdata()) for b in im.split())
        rgb = bytearray()
        for i in range(len(c)):
            rgb += bytes((round((255 - c[i]) * (255 - k[i]) / 255), round((255 - m[i]) * (255 - k[i]) / 255), round((255 - y[i]) * (255 - k[i]) / 255)))
        write_gz(os.path.join(TARGET, "duck-cmyk.rgb8.gz"), bytes(rgb))
        entries.append({"file": "duck-cmyk.jpg", "width": im.width, "height": im.height, "channels": 3, "sampling": "1x1,1x1,1x1,1x1",
                        "progressive": False, "orientation": 1, "colorSpace": "Cmyk", "reference": "duck-cmyk.rgb8.gz",
                        "note": "Decoded by Pillow, converted with R = (255 - C)(255 - K) / 255"})

    # The photo tagged with each orientation, and the upright version jpegtran makes losslessly (320 × 240 is whole MCUs).
    # Pillow writes the EXIF block, re-encoding with the source's own tables; every reference is taken from the file as written.
    for n in range(1, 9):
        path = os.path.join(CORPUS, f"exif-orientation-{n}.jpg")
        exif = Image.Exif()
        exif[0x0112] = n
        with Image.open(os.path.join(CORPUS, "duck-420.jpg")) as im:
            im.save(path, quality="keep", exif=exif.tobytes())
        add(path, sampling="2x2,1x1,1x1", orientation=n)
        entries[-1]["exif"] = exif_json(path)
        if n > 1:
            upright = f"/tmp/upright-{n}.jpg"
            run("jpegtran", "-perfect", "-copy", "none", *UPRIGHT[n], "-outfile", upright, path)
            entries[-1]["upright"] = djpeg(upright, f"orient-{n}")[0]

    # One file with many typed tags in IFD0, the EXIF and GPS sub-IFDs.
    rich = os.path.join(CORPUS, "exif-tags.jpg")
    exif = Image.Exif()
    exif[0x010F] = "Egami Test Camera"
    exif[0x0110] = "Model 1"
    exif[0x0132] = "2026:10:08 12:34:56"
    exif[0x011A] = TiffImagePlugin.IFDRational(300, 1)
    exif[0x011B] = TiffImagePlugin.IFDRational(300, 1)
    exif[0x0128] = 2
    exif[0x0131] = "Egami tests"
    sub = exif.get_ifd(0x8769)
    sub[0x829A] = TiffImagePlugin.IFDRational(1, 250)
    sub[0x829D] = TiffImagePlugin.IFDRational(28, 10)
    sub[0x8827] = 200
    sub[0x9003] = "2026:10:08 12:34:56"
    sub[0x920A] = TiffImagePlugin.IFDRational(50, 1)
    sub[0x9204] = TiffImagePlugin.IFDRational(-1, 3)
    sub[0xA002] = 320
    sub[0xA003] = 240
    gps = exif.get_ifd(0x8825)
    gps[1] = "N"
    gps[2] = (TiffImagePlugin.IFDRational(59, 1), TiffImagePlugin.IFDRational(54, 1), TiffImagePlugin.IFDRational(1234, 100))
    gps[3] = "E"
    gps[4] = (TiffImagePlugin.IFDRational(10, 1), TiffImagePlugin.IFDRational(45, 1), TiffImagePlugin.IFDRational(0, 1))
    gps[5] = 0
    gps[6] = TiffImagePlugin.IFDRational(2345, 10)
    with Image.open(os.path.join(CORPUS, "duck-420.jpg")) as im:
        im.save(rich, quality="keep", exif=exif.tobytes())
    add(rich, sampling="2x2,1x1,1x1")
    entries[-1]["exif"] = exif_json(rich)

    # libjpeg-turbo's test images.
    # testimgari.jpg is testimgint.jpg arithmetic-coded, so its reference is the same file; 12-bit testorig12.jpg is in the 2.4.2
    # corpus (tools/make-jpeg-depth-references.py).
    for name in ("testorig.jpg", "testimgint.jpg", "testimgari.jpg"):
        path = os.path.join(CORPUS, name)
        shutil.copy(os.path.join(CACHE, name), path)
        with Image.open(path) as im:
            # Pillow lists each component as (id, horizontal, vertical, quantization table).
            sampling = ",".join(f"{h}x{v}" for _, h, v, _ in im.layer)
            progressive = bool(im.info.get("progressive") or im.info.get("progression"))
        add(path, sampling=sampling, progressive=progressive, scales=name == "testorig.jpg", note="libjpeg-turbo test image")

    dedupe(entries)
    encodes = encode_references(work, duck, duck_gray)
    version = subprocess.run(["cjpeg", "-version"], capture_output=True, text=True).stderr.strip()
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": f"{version}; Pillow {__import__('PIL').__version__}; {IMAGE}", "files": entries, "encodes": encodes}, f, indent=1)
        f.write("\n")
    print(f"Wrote {len(entries)} corpus files.")


def encode_references(work, duck, duck_gray):
    """
    Encoder references (tests/reference/jpeg/encode): each source as a lossless PNG, and what cjpeg makes of it at the settings
    the encoder tests use. The tests encode the PNG with Egami at the same settings and compare file size and PSNR.
    """
    from PIL import Image

    target = os.path.join(TARGET, "encode")
    os.makedirs(target, exist_ok=True)
    sources = {"duck": duck, "duck-gray": duck_gray}
    for w, h in ((1, 1), (17, 9), (33, 1), (31, 17)):
        sources[f"odd-{w}x{h}"] = os.path.join(work, f"odd-{w}x{h}.ppm")
    for name, path in sources.items():
        with Image.open(path) as im:
            im.save(os.path.join(target, f"{name}.png"))

    # (output, source, quality, subsampling (cjpeg -sample), optimize, restart interval in MCUs, gray output)
    cases = [
        ("duck-q75.jpg", "duck", 75, "2x2", False, 0, False),
        ("duck-q90-422.jpg", "duck", 90, "2x1", False, 0, False),
        ("duck-q50-444.jpg", "duck", 50, "1x1", False, 0, False),
        ("duck-q100-444.jpg", "duck", 100, "1x1", False, 0, False),
        ("duck-q10.jpg", "duck", 10, "2x2", False, 0, False),
        ("duck-q95-optimized.jpg", "duck", 95, "2x2", True, 0, False),
        ("duck-q75-444-optimized.jpg", "duck", 75, "1x1", True, 0, False),
        ("duck-q75-restart.jpg", "duck", 75, "2x2", False, 3, False),
        ("duck-q80-grayscale.jpg", "duck", 80, "2x2", False, 0, True),
        ("duck-gray-q85.jpg", "duck-gray", 85, "1x1", False, 0, False),
        ("duck-gray-q85-optimized.jpg", "duck-gray", 85, "1x1", True, 0, False),
    ]
    for w, h in ((1, 1), (17, 9), (33, 1), (31, 17)):
        cases.append((f"odd-{w}x{h}-q90.jpg", f"odd-{w}x{h}", 90, "2x2", False, 0, False))
        cases.append((f"odd-{w}x{h}-q90-422.jpg", f"odd-{w}x{h}", 90, "2x1", False, 0, False))

    encodes = []
    for out, source, quality, sample, optimize, restart, gray in cases:
        args = ["-quality", str(quality), "-sample", sample]
        if optimize:
            args.append("-optimize")
        if restart:
            args += ["-restart", f"{restart}B"]
        if gray:
            args.append("-grayscale")
        run("cjpeg", "-dct", "int", *args, "-outfile", os.path.join(target, out), sources[source])
        encodes.append({"file": f"encode/{out}", "source": f"encode/{source}.png", "quality": quality, "sampling": sample,
                        "optimize": optimize, "restart": restart, "gray": gray, "size": os.path.getsize(os.path.join(target, out))})
    return encodes


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    for name in LIBJPEG_TURBO_FILES:
        if not os.path.exists(os.path.join(CACHE, name)):
            sys.exit(f"Missing {CACHE}/{name}; download it as the comment above LIBJPEG_TURBO_FILES says.")

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-jpeg-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
