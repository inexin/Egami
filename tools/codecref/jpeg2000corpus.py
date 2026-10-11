"""Builds the JPEG 2000 corpus and its OpenJPEG references, inside egami-codecref (see tools/make-jpeg2000-references.py).

Sources are a crop of tests/images/ops/duck.png as RGB, RGBA, gray, 12-bit gray (unsigned and signed) and planar 4:2:0 and
4:2:2 YCbCr; each is encoded with opj_compress in the option sets below, which cover every coding style, progression order and
marker the decoder reads. opj_decompress 2.5.4 then writes each component's samples as PGX, at full resolution and with one
level dropped, gzipped into tests/reference/jpeg2000. opj_decompress converts YCbCr to RGB before writing, so for the YCbCr
files the references are the planes encoded instead (name_src_component.pgx.gz), which the lossless ones decode to exactly.
"""
import glob
import gzip
import os
import subprocess

import numpy as np
from PIL import Image

ROOT = "/w"
IMAGES = f"{ROOT}/tests/images/jpeg2000"
REFS = f"{ROOT}/tests/reference/jpeg2000"
WORK = "/tmp/j2k"
WIDTH, HEIGHT = 96, 72


def pgx(path, plane, bits, signed):
    """Writes one component as PGX: a header line, then samples big-endian in 1 or 2 bytes."""
    size = 1 if bits <= 8 else 2
    with open(path, "wb") as f:
        f.write(f"PG ML {'-' if signed else '+'} {bits} {plane.shape[1]} {plane.shape[0]}\n".encode())
        data = plane.astype(">i2" if signed else ">u2") if size == 2 else plane.astype("i1" if signed else "u1")
        f.write(data.tobytes())


def sources():
    duck = Image.open(f"{ROOT}/tests/images/ops/duck.png").convert("RGBA")
    duck = duck.crop((200, 120, 200 + 4 * WIDTH, 120 + 4 * HEIGHT)).resize((WIDTH, HEIGHT), Image.LANCZOS)
    rgb = duck.convert("RGB")
    rgb.save(f"{WORK}/rgb.ppm")
    duck.save(f"{WORK}/rgba.png")
    gray = np.asarray(rgb.convert("L"), dtype=np.int32)
    Image.fromarray(gray.astype(np.uint8)).save(f"{WORK}/gray.pgm")
    deep = (gray << 4) | (gray >> 4)
    pgx(f"{WORK}/gray12.pgx", deep, 12, False)
    pgx(f"{WORK}/signed12.pgx", deep - 2048, 12, True)
    ycc = np.asarray(rgb.convert("YCbCr"), dtype=np.uint8)
    y = ycc[:, :, 0]
    for name, sx, sy in (("yuv420", 2, 2), ("yuv422", 2, 1)):
        cb = ycc[::sy, ::sx, 1]
        cr = ycc[::sy, ::sx, 2]
        with open(f"{WORK}/{name}.raw", "wb") as f:
            f.write(y.tobytes() + cb.tobytes() + cr.tobytes())
        for c, plane in enumerate((y, cb, cr)):
            pgx(f"{WORK}/{name}_src_{c}.pgx", plane.astype(np.int32), 8, False)


# name, source, extension, opj_compress arguments
SETS = [
    ("rgb-lossless", "rgb.ppm", "j2k", []),
    ("rgb-lossy", "rgb.ppm", "j2k", ["-I", "-r", "20"]),
    ("rgb-lossy-jp2", "rgb.ppm", "jp2", ["-I", "-r", "20"]),
    ("rgb-layers", "rgb.ppm", "jp2", ["-I", "-r", "80,40,20,10", "-p", "RLCP"]),
    ("rgb-layers-lossless", "rgb.ppm", "j2k", ["-r", "40,20,1"]),
    ("rgb-mct0", "rgb.ppm", "j2k", ["-mct", "0", "-I", "-r", "15"]),
    ("rgb-tiles", "rgb.ppm", "j2k", ["-t", "32,32", "-n", "4", "-I", "-r", "10"]),
    ("rgb-tiles-lossless", "rgb.ppm", "j2k", ["-t", "40,24", "-n", "3"]),
    ("rgb-offsets", "rgb.ppm", "j2k", ["-d", "13,7", "-T", "5,3", "-t", "40,32", "-n", "4"]),
    ("rgb-offsets-lossy", "rgb.ppm", "j2k", ["-d", "3,5", "-T", "1,2", "-t", "32,32", "-n", "4", "-I", "-r", "12"]),
    ("rgb-precincts-rpcl", "rgb.ppm", "j2k", ["-c", "[32,32],[16,16],[16,16]", "-b", "8,8", "-p", "RPCL", "-n", "4"]),
    ("rgb-precincts-pcrl", "rgb.ppm", "j2k", ["-c", "[64,64],[32,32],[16,16]", "-b", "16,16", "-p", "PCRL", "-I", "-r", "10,5"]),
    ("rgb-precincts-cprl", "rgb.ppm", "j2k", ["-c", "[16,16]", "-b", "8,8", "-p", "CPRL", "-t", "48,48", "-n", "4"]),
    ("rgb-poc", "rgb.ppm", "j2k", ["-r", "40,20,10", "-POC", "T0=0,0,1,6,3,LRCP/T0=0,0,3,6,3,RLCP"]),
    ("rgb-sop-eph", "rgb.ppm", "j2k", ["-SOP", "-EPH", "-I", "-r", "20,10"]),
    ("rgb-plt-tlm", "rgb.ppm", "j2k", ["-PLT", "-TLM", "-t", "48,48", "-n", "4"]),
    ("rgb-tileparts", "rgb.ppm", "j2k", ["-TP", "R", "-t", "48,48", "-n", "4", "-r", "30,10"]),
    ("rgb-bypass", "rgb.ppm", "j2k", ["-M", "1"]),
    ("rgb-reset", "rgb.ppm", "j2k", ["-M", "2", "-I", "-r", "10"]),
    ("rgb-termall", "rgb.ppm", "j2k", ["-M", "4"]),
    ("rgb-vcausal", "rgb.ppm", "j2k", ["-M", "8", "-I", "-r", "8"]),
    ("rgb-pterm", "rgb.ppm", "j2k", ["-M", "16"]),
    ("rgb-segsym", "rgb.ppm", "j2k", ["-M", "32", "-I", "-r", "10"]),
    ("rgb-allstyles", "rgb.ppm", "j2k", ["-M", "63"]),
    ("rgb-allstyles-lossy", "rgb.ppm", "j2k", ["-M", "63", "-I", "-r", "30,10"]),
    ("rgb-bypass-termall", "rgb.ppm", "j2k", ["-M", "5", "-r", "20,1"]),
    ("rgb-roi", "rgb.ppm", "j2k", ["-ROI", "c=0,U=6", "-I", "-r", "20"]),
    ("rgb-roi-lossless", "rgb.ppm", "j2k", ["-ROI", "c=1,U=9"]),
    ("rgb-levels1", "rgb.ppm", "j2k", ["-n", "2"]),
    ("rgb-levels6", "rgb.ppm", "j2k", ["-n", "7", "-I", "-r", "20"]),
    ("rgb-cblk4", "rgb.ppm", "j2k", ["-b", "4,4", "-I", "-r", "10"]),
    ("rgb-cblk-wide", "rgb.ppm", "j2k", ["-b", "64,16"]),
    ("rgb-guard1", "rgb.ppm", "j2k", ["-GuardBits", "1", "-I", "-r", "10"]),
    ("rgb-comment", "rgb.ppm", "jp2", ["-C", "Egami test comment"]),
    ("rgba", "rgba.png", "jp2", []),
    ("rgba-lossy", "rgba.png", "jp2", ["-I", "-r", "15"]),
    ("gray-lossless", "gray.pgm", "j2k", []),
    ("gray-lossy", "gray.pgm", "jp2", ["-I", "-r", "10"]),
    ("gray12-lossless", "gray12.pgx", "j2k", []),
    ("gray12-lossy", "gray12.pgx", "jp2", ["-I", "-r", "8"]),
    ("signed12", "signed12.pgx", "j2k", []),
    ("signed12-lossy", "signed12.pgx", "j2k", ["-I", "-r", "8"]),
    ("yuv420", "yuv420.raw", "j2k", ["-F", f"{WIDTH},{HEIGHT},3,8,u@1x1:2x2:2x2", "-mct", "0"]),
    ("yuv420-lossy", "yuv420.raw", "jp2", ["-F", f"{WIDTH},{HEIGHT},3,8,u@1x1:2x2:2x2", "-mct", "0", "-I", "-r", "10"]),
    ("yuv422", "yuv422.raw", "jp2", ["-F", f"{WIDTH},{HEIGHT},3,8,u@1x1:2x1:2x1", "-mct", "0"]),
]


def gzipped(source, target):
    """Writes <source> gzipped to <target>, with no name or time in the header, so a rebuild changes nothing."""
    with open(source, "rb") as f, open(target, "wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, filename="") as g:
            g.write(f.read())


def main():
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(REFS, exist_ok=True)
    for old in glob.glob(f"{IMAGES}/*") + glob.glob(f"{REFS}/*"):
        os.remove(old)
    sources()
    for name, source, extension, args in SETS:
        target = f"{IMAGES}/{name}.{extension}"
        subprocess.run(["opj_compress", "-i", f"{WORK}/{source}", "-o", target, *args], check=True, stdout=subprocess.DEVNULL)
        if source.endswith(".raw"):
            for c in range(3):
                gzipped(f"{WORK}/{source[:-4]}_src_{c}.pgx", f"{REFS}/{name}_src_{c}.pgx.gz")
            print(name, os.path.getsize(target))
            continue

        for reduce in (0, 1):
            out = f"{WORK}/{name}_r{reduce}.pgx"
            subprocess.run(["opj_decompress", "-i", target, "-o", out, "-r", str(reduce)], check=True, stdout=subprocess.DEVNULL)
            for produced in sorted(glob.glob(f"{WORK}/{name}_r{reduce}*.pgx")):
                gzipped(produced, f"{REFS}/{os.path.basename(produced)}.gz")
                os.remove(produced)
        print(name, os.path.getsize(target))


if __name__ == "__main__":
    main()
