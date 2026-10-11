"""Builds the Phase 2.4.2 JPEG corpus (arithmetic, 12-bit and lossless files) in tests/images/jpeg/depth and its references in
tests/reference/jpeg/depth, with libjpeg-turbo 3.x and Thomas Richter's libjpeg in Docker. Both are oracles only: they run in the
container, and nothing from them is linked into Egami.

Corpus (tests/images/jpeg/depth; the source photo is tests/images/ops/duck.png, public domain):
  - arith-*.jpg      cjpeg -arithmetic: sequential, progressive, restart intervals, gray, odd sizes
  - p12-*.jpg        cjpeg -precision 12 from a 16-bit source: every sampling, progressive, arithmetic, restart intervals
  - ll-*.jpg         cjpeg -lossless (SOF3) with every predictor, point transforms, 8, 12 and 16 bits, RGB, YCbCr and gray
  - lla-*.jpg        Richter's jpeg -p -a (SOF11, arithmetic lossless, which libjpeg-turbo cannot write or read), and -p (SOF3)
                     at precisions cjpeg does not offer
  - hierarchical.jpg Richter's jpeg -y 1, which Egami must reject
  - testorig12.jpg   libjpeg-turbo's 12-bit test image (IJG license), already in the corpus

References (tests/reference/jpeg/depth), raw samples row by row, gzipped:
  <file>.rgb8.gz / .gray8.gz              8-bit samples, from djpeg (or Richter's decoder for lla-*)
  <file>.rgb16.gz / .gray16.gz            deeper samples at their own precision (0 to 2^P - 1), little-endian 16-bit
  <file>.s2/.s4/.s8.*.gz, <file>.fast.*   djpeg -scale 1/N and -nosmooth
  manifest.json                           what each file is and which references it has

djpeg runs with its defaults (accurate integer IDCT, fancy upsampling), as tools/make-jpeg-references.py does.

Usage, from the repo root with Docker running:
  python tools/make-jpeg-depth-references.py
The script builds the image egami-jpeg-depth (alpine 3.20, libjpeg-turbo, ImageMagick and Richter's libjpeg from GitHub, with a
one-line fix so its encoder writes predictive lossless files without quantization tables) and runs itself inside it.
"""
import gzip
import json
import os
import subprocess
import sys

IMAGE = "egami-jpeg-depth"
DOCKERFILE = r"""
FROM alpine:3.20
RUN apk add --no-cache g++ make git autoconf automake libjpeg-turbo-utils imagemagick python3
RUN git clone --depth 1 https://github.com/thorfdbg/libjpeg /src/libjpeg && cd /src/libjpeg \
 && sed -i '1502,1503s/JPG_THROW(OBJECT_DOESNT_EXIST,"Tables::QuantizationTableIndexOf","DQT marker missing, no quantization table defined");/return 0;/' codestream/tables.cpp \
 && (./configure || true) && make -j8 final
"""
CORPUS = "tests/images/jpeg/depth"
TARGET = "tests/reference/jpeg/depth"
OPS = "tests/images/ops"
RICHTER = "/src/libjpeg/jpeg"


def run(*args):
    subprocess.run(list(args), check=True, stdout=subprocess.DEVNULL)


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


def reference(pnm_path, out_base):
    """Stores a decoded PNM as raw samples: bytes up to maxval 255, else little-endian 16-bit. Returns (name, w, h, channels, maxval)."""
    with open(pnm_path, "rb") as f:
        magic, width, height, maxval, pixels = parse_pnm(f.read())
    channels = 3 if magic == b"P6" else 1
    kind = "rgb" if channels == 3 else "gray"
    if maxval > 255:
        swapped = bytearray(len(pixels))
        swapped[0::2] = pixels[1::2]
        swapped[1::2] = pixels[0::2]
        name = f"{out_base}.{kind}16.gz"
        write_gz(os.path.join(TARGET, name), bytes(swapped))
    else:
        name = f"{out_base}.{kind}8.gz"
        write_gz(os.path.join(TARGET, name), pixels)
    return name, width, height, channels, maxval


def djpeg(path, out_base, *options):
    run("djpeg", "-dct", "int", *options, "-pnm", "-outfile", "/tmp/out.pnm", path)
    return reference("/tmp/out.pnm", out_base)


def richter_decode(path, out_base):
    run(RICHTER, path, "/tmp/out.pnm")
    return reference("/tmp/out.pnm", out_base)


def frame_info(path):
    """Reads the frame and first scan headers: (SOF marker, precision, sampling, scan predictor)."""
    with open(path, "rb") as f:
        data = f.read()
    i = 2
    marker = precision = predictor = None
    sampling = []
    while i < len(data) and data[i] == 0xFF:
        m = data[i + 1]
        length = (data[i + 2] << 8) | data[i + 3]
        segment = data[i + 4:i + 2 + length]
        if 0xC0 <= m <= 0xCF and m not in (0xC4, 0xC8, 0xCC):
            marker, precision = m, segment[0]
            sampling = [f"{segment[7 + 3 * c] >> 4}x{segment[7 + 3 * c] & 15}" for c in range(segment[5])]
        if m == 0xDA:
            predictor = segment[1 + 2 * segment[0]]
            break
        i += 2 + length
    return marker, precision, ",".join(sampling), predictor


def inside():
    os.makedirs(CORPUS, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    work = "/tmp/src"
    os.makedirs(work, exist_ok=True)

    def source(name, size, *, crop=None, gray=False, depth=8, max_value=None):
        path = os.path.join(work, name)
        args = ["magick", f"{OPS}/duck.png", "-background", "white", "-alpha", "remove", "-alpha", "off"]
        if crop:
            args += ["-crop", crop, "+repage"]
        args += ["-filter", "Lanczos", "-resize", size + "!"]
        if gray:
            args += ["-colorspace", "Gray"]
        args += ["-depth", str(depth)]
        run(*args, path)
        if max_value:
            # A PGM/PPM at an odd precision: the 16-bit samples scaled to 0..max_value.
            with open(path, "rb") as f:
                magic, w, h, maxval, pixels = parse_pnm(f.read())
            values = [(pixels[k] << 8 | pixels[k + 1]) for k in range(0, len(pixels), 2)]
            scaled = bytearray()
            for v in values:
                s = (v * max_value + maxval // 2) // maxval
                scaled += bytes((s >> 8, s & 255))
            with open(path, "wb") as f:
                f.write(magic + f"\n{w} {h}\n{max_value}\n".encode() + bytes(scaled))
        return path

    duck = source("duck.ppm", "160x120")
    duck16 = source("duck16.ppm", "160x120", depth=16)
    gray16 = source("gray16.pgm", "160x120", gray=True, depth=16)
    odd = source("odd.ppm", "37x23", crop="200x130+150+100")
    odd16 = source("odd16.ppm", "37x23", crop="200x130+150+100", depth=16)
    small = source("small.ppm", "64x48")
    small16 = source("small16.ppm", "64x48", depth=16)
    small_gray = source("small-gray.pgm", "64x48", gray=True)
    small_gray16 = source("small-gray16.pgm", "64x48", gray=True, depth=16)
    small10 = source("small10.pgm", "64x48", gray=True, depth=16, max_value=1023)
    small4 = source("small4.pgm", "64x48", gray=True, depth=16, max_value=15)
    small14 = source("small14.ppm", "64x48", depth=16, max_value=16383)

    entries = []

    def add(path, *, tool="djpeg", scales=False, fast=False, note=""):
        base = os.path.splitext(os.path.basename(path))[0]
        decode = djpeg if tool == "djpeg" else (lambda p, b, *o: richter_decode(p, b))
        name, width, height, channels, maxval = decode(path, base)
        marker, precision, sampling, predictor = frame_info(path)
        entry = {"file": os.path.basename(path), "width": width, "height": height, "channels": channels, "precision": precision,
                 "sampling": sampling, "progressive": marker in (0xC2, 0xCA), "arithmetic": marker in (0xC9, 0xCA, 0xCB),
                 "lossless": marker in (0xC3, 0xCB), "reference": name, "decodedBy": tool}
        if marker in (0xC3, 0xCB):
            entry["predictor"] = predictor
        if scales:
            entry["scaled"] = {str(s): djpeg(path, f"{base}.s{s}", "-scale", f"1/{s}")[0] for s in (2, 4, 8)}
        if fast:
            entry["fast"] = djpeg(path, f"{base}.fast", "-nosmooth")[0]
        if note:
            entry["note"] = note
        entries.append(entry)

    def cjpeg(src, name, *options):
        out = os.path.join(CORPUS, name)
        run("cjpeg", "-dct", "int", *options, "-outfile", out, src)
        return out

    def richter(src, name, *options):
        out = os.path.join(CORPUS, name)
        run(RICHTER, *options, src, out)
        return out

    # Arithmetic coding, 8 bits.
    add(cjpeg(duck, "arith-420.jpg", "-arithmetic", "-quality", "90", "-sample", "2x2"), scales=True, fast=True)
    add(cjpeg(duck, "arith-444-restart.jpg", "-arithmetic", "-quality", "90", "-sample", "1x1", "-restart", "3B"))
    add(cjpeg(duck, "arith-progressive.jpg", "-arithmetic", "-quality", "90", "-sample", "2x2", "-progressive"), scales=True)
    add(cjpeg(duck, "arith-progressive-restart.jpg", "-arithmetic", "-quality", "85", "-sample", "2x1", "-progressive", "-restart", "1"))
    add(cjpeg(source("gray.pgm", "160x120", gray=True), "arith-gray.jpg", "-arithmetic", "-quality", "90"))
    add(cjpeg(odd, "arith-odd-37x23.jpg", "-arithmetic", "-quality", "90", "-sample", "2x2", "-progressive"))

    # 12-bit DCT.
    add(cjpeg(duck16, "p12-420.jpg", "-precision", "12", "-quality", "90", "-sample", "2x2"), scales=True, fast=True)
    add(cjpeg(duck16, "p12-444.jpg", "-precision", "12", "-quality", "95", "-sample", "1x1"))
    add(cjpeg(duck16, "p12-422.jpg", "-precision", "12", "-quality", "90", "-sample", "2x1"), fast=True)
    add(cjpeg(duck16, "p12-440.jpg", "-precision", "12", "-quality", "90", "-sample", "1x2"))
    add(cjpeg(duck16, "p12-progressive.jpg", "-precision", "12", "-quality", "90", "-sample", "2x2", "-progressive"), scales=True)
    add(cjpeg(duck16, "p12-arith-progressive.jpg", "-precision", "12", "-quality", "90", "-sample", "2x2", "-progressive", "-arithmetic"))
    add(cjpeg(duck16, "p12-restart.jpg", "-precision", "12", "-quality", "90", "-sample", "2x2", "-restart", "1"))
    add(cjpeg(duck16, "p12-q100.jpg", "-precision", "12", "-quality", "100", "-sample", "1x1"))
    add(cjpeg(gray16, "p12-gray.jpg", "-precision", "12", "-quality", "90"), scales=True)
    add(cjpeg(odd16, "p12-odd-37x23.jpg", "-precision", "12", "-quality", "90", "-sample", "2x2"))

    # Lossless with Huffman coding (SOF3), every predictor, from libjpeg-turbo.
    for psv in range(1, 8):
        add(cjpeg(small, f"ll-rgb-p{psv}.jpg", "-lossless", str(psv), "-rgb"))
    add(cjpeg(small, "ll-ycc-p1.jpg", "-lossless", "1"), note="cjpeg's default color conversion: YCbCr with a JFIF segment")
    add(cjpeg(small_gray, "ll-gray-p6-pt2.jpg", "-lossless", "6,2"))
    add(cjpeg(small, "ll-rgb-p4-restart.jpg", "-lossless", "4", "-rgb", "-restart", "2"))
    add(cjpeg(small16, "ll12-rgb-p7.jpg", "-lossless", "7", "-precision", "12", "-rgb"))
    add(cjpeg(small16, "ll12-rgb-p5-pt3.jpg", "-lossless", "5,3", "-precision", "12", "-rgb"))
    add(cjpeg(small_gray16, "ll16-gray-p4.jpg", "-lossless", "4", "-precision", "16"))
    add(cjpeg(small16, "ll16-rgb-p2.jpg", "-lossless", "2", "-precision", "16", "-rgb"))
    add(cjpeg(odd, "ll-odd-37x23-p3.jpg", "-lossless", "3", "-rgb"))
    add(cjpeg(small, "ll-420-p1.jpg", "-lossless", "1", "-sample", "2x2"), note="Subsampled chroma, upsampled by djpeg")
    add(cjpeg(odd, "ll-odd-422-p7.jpg", "-lossless", "7", "-sample", "2x1"), note="Subsampled chroma, upsampled by djpeg")

    # Lossless from Richter's libjpeg: arithmetic coding (SOF11) and precisions cjpeg does not write.
    add(richter(small, "lla-rgb.jpg", "-p", "-c", "-a"), tool="richter")
    add(richter(small_gray16, "lla-gray16.jpg", "-p", "-a"), tool="richter")
    add(richter(small, "lla-rgb-restart.jpg", "-p", "-c", "-a", "-z", "64"), tool="richter")
    add(richter(small10, "lla-gray10.jpg", "-p", "-a"), tool="richter")
    add(richter(small14, "lla-rgb14.jpg", "-p", "-c", "-a"), tool="richter")
    add(richter(small10, "ll-gray10.jpg", "-p"), tool="richter")
    add(richter(small4, "ll-gray4.jpg", "-p"), tool="richter")

    # libjpeg-turbo's 12-bit test image, kept here since tools/make-jpeg-references.py copied it (IJG license; see LICENSES).
    add(os.path.join(CORPUS, "testorig12.jpg"), note="libjpeg-turbo test image")

    # Hierarchical, which stays unsupported.
    richter(small, "hierarchical.jpg", "-q", "85", "-y", "1", "-h")
    entries.append({"file": "hierarchical.jpg", "unsupported": "Hierarchical", "note": "Richter's jpeg -y 1"})

    version = subprocess.run(["cjpeg", "-version"], capture_output=True, text=True).stderr.strip()
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": f"{version}; Thomas Richter's libjpeg (GitHub master); alpine:3.20", "files": entries}, f, indent=1)
        f.write("\n")
    print(f"Wrote {len(entries)} corpus files.")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    subprocess.run(["docker", "build", "-t", IMAGE, "-"], input=DOCKERFILE, text=True, check=True)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3",
                    "tools/make-jpeg-depth-references.py", "--inside"], check=True)


if __name__ == "__main__":
    main()
