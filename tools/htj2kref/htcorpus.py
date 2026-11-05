"""Builds the HTJ2K corpus and its OpenJPEG references, inside egami-htj2kref (see tools/make-htj2k-references.py).

Sources are a crop of tests/images/ops/duck.png as RGB, gray, 12-bit and 16-bit gray, signed 12-bit gray and planar 4:2:0
YCbCr; each is encoded with OpenJPH's ojph_compress in the option sets below, which cover HT code-blocks in .jph files and in
raw codestreams (OpenJPH writes codestreams only, so the script wraps the .jph ones in the boxes of T.814 Annex D),
reversible and irreversible, tiles, tile-parts, precincts, code-block sizes and every progression order.
opj_decompress 2.5.4 then writes each component's samples as PGX, at full resolution and with one level dropped, gzipped into
tests/reference/htj2k. opj_decompress converts subsampled YCbCr to RGB before writing, so for those files the references are the
planes encoded instead (name_src_component.pgx.gz).
"""
import glob
import gzip
import os
import subprocess
import sys

import struct

import numpy as np
from PIL import Image

ROOT = "/w"
IMAGES = f"{ROOT}/tests/images/htj2k"
REFS = f"{ROOT}/tests/reference/htj2k"
WORK = "/tmp/htj2k"
WIDTH, HEIGHT = 96, 72


def pgm16(path, plane, maxval):
    """Writes a 16-bit PGM: samples big-endian."""
    with open(path, "wb") as f:
        f.write(f"P5\n{plane.shape[1]} {plane.shape[0]}\n{maxval}\n".encode())
        f.write(plane.astype(">u2").tobytes())


def pgx(path, plane):
    """Writes an 8-bit unsigned component as PGX."""
    with open(path, "wb") as f:
        f.write(f"PG ML + 8 {plane.shape[1]} {plane.shape[0]}\n".encode())
        f.write(plane.astype("u1").tobytes())


def box(kind, payload):
    return struct.pack(">I", 8 + len(payload)) + kind + payload


def jph(codestream, target):
    """Wraps an HT codestream in the JPH file format (T.814 Annex D): signature, ftyp 'jph ', jp2h (ihdr, colr) and jp2c."""
    siz = codestream.index(bytes([0xFF, 0x51]))
    width, height, x0, y0 = struct.unpack(">IIII", codestream[siz + 6:siz + 22])
    count = struct.unpack(">H", codestream[siz + 38:siz + 40])[0]
    depths = [codestream[siz + 40 + 3 * c] for c in range(count)]
    bpc = depths[0] if len(set(depths)) == 1 else 255
    ihdr = struct.pack(">IIHBBBB", height - y0, width - x0, count, bpc, 7, 0, 0)
    colr = struct.pack(">BBBI", 1, 0, 0, 16 if count >= 3 else 17)
    with open(target, "wb") as f:
        f.write(box(b"jP  ", bytes([0x0D, 0x0A, 0x87, 0x0A])))
        f.write(box(b"ftyp", b"jph " + struct.pack(">I", 0) + b"jph "))
        f.write(box(b"jp2h", box(b"ihdr", ihdr) + box(b"colr", colr)))
        f.write(box(b"jp2c", codestream))


def sources():
    duck = Image.open(f"{ROOT}/tests/images/ops/duck.png").convert("RGB")
    duck = duck.crop((200, 120, 200 + 4 * WIDTH, 120 + 4 * HEIGHT)).resize((WIDTH, HEIGHT), Image.LANCZOS)
    duck.save(f"{WORK}/rgb.ppm")
    duck.crop((0, 0, 77, 51)).save(f"{WORK}/odd.ppm")
    gray = np.asarray(duck.convert("L"), dtype=np.int32)
    Image.fromarray(gray.astype(np.uint8)).save(f"{WORK}/gray.pgm")
    pgm16(f"{WORK}/gray12.pgm", (gray << 4) | (gray >> 4), 4095)
    pgm16(f"{WORK}/gray16.pgm", (gray << 8) | gray, 65535)
    signed = ((gray << 4) | (gray >> 4)) - 2048
    with open(f"{WORK}/signed12.yuv", "wb") as f:
        f.write(signed.astype("<i2").tobytes())
    ycc = np.asarray(duck.convert("YCbCr"), dtype=np.uint8)
    planes = (ycc[:, :, 0], ycc[::2, ::2, 1], ycc[::2, ::2, 2])
    with open(f"{WORK}/yuv420.yuv", "wb") as f:
        f.write(b"".join(plane.tobytes() for plane in planes))
    for c, plane in enumerate(planes):
        pgx(f"{WORK}/yuv420_src_{c}.pgx", plane)


RAW420 = ["-dims", f"{{{WIDTH},{HEIGHT}}}", "-num_comps", "3", "-signed", "false,false,false", "-bit_depth", "8,8,8",
          "-downsamp", "{1,1},{2,2},{2,2}"]
RAWSIGNED = ["-dims", f"{{{WIDTH},{HEIGHT}}}", "-num_comps", "1", "-signed", "true", "-bit_depth", "12", "-downsamp", "{1,1}"]
LOSSLESS = ["-reversible", "true"]

# name, source, extension, ojph_compress arguments
SETS = [
    ("rgb-lossless", "rgb.ppm", "jph", LOSSLESS),
    ("rgb-lossy", "rgb.ppm", "jph", ["-qstep", "0.01"]),
    ("rgb-lossless-j2c", "rgb.ppm", "j2c", LOSSLESS),
    ("rgb-lossy-fine", "rgb.ppm", "j2c", ["-qstep", "0.001"]),
    ("rgb-nocolour", "rgb.ppm", "jph", [*LOSSLESS, "-colour_trans", "false"]),
    ("rgb-levels1", "rgb.ppm", "j2c", [*LOSSLESS, "-num_decomps", "1"]),
    ("rgb-levels0", "rgb.ppm", "j2c", [*LOSSLESS, "-num_decomps", "0"]),
    ("rgb-cblk16", "rgb.ppm", "j2c", [*LOSSLESS, "-block_size", "{16,16}"]),
    ("rgb-cblk-wide", "rgb.ppm", "j2c", ["-block_size", "{8,128}", "-qstep", "0.005"]),
    ("rgb-cblk4", "rgb.ppm", "j2c", [*LOSSLESS, "-block_size", "{4,4}"]),
    ("rgb-precincts", "rgb.ppm", "j2c", [*LOSSLESS, "-precincts", "{16,16},{32,32},{64,64}", "-block_size", "{16,16}"]),
    ("rgb-tiles-rpcl", "rgb.ppm", "j2c", [*LOSSLESS, "-tile_size", "{40,32}", "-prog_order", "RPCL", "-num_decomps", "3"]),
    ("rgb-tiles-lrcp", "rgb.ppm", "jph", ["-tile_size", "{32,32}", "-prog_order", "LRCP", "-num_decomps", "2", "-qstep", "0.02"]),
    ("rgb-rlcp", "rgb.ppm", "j2c", [*LOSSLESS, "-prog_order", "RLCP"]),
    ("rgb-pcrl", "rgb.ppm", "j2c", [*LOSSLESS, "-prog_order", "PCRL", "-precincts", "{32,32}"]),
    ("rgb-cprl", "rgb.ppm", "j2c", [*LOSSLESS, "-prog_order", "CPRL", "-tile_size", "{48,48}", "-num_decomps", "3"]),
    ("rgb-tileparts", "rgb.ppm", "j2c", [*LOSSLESS, "-tile_size", "{48,48}", "-tileparts", "RC", "-tlm_marker", "true", "-num_decomps", "3"]),
    ("rgb-offsets", "rgb.ppm", "j2c", [*LOSSLESS, "-image_offset", "{13,7}", "-tile_offset", "{5,3}", "-tile_size", "{40,32}", "-num_decomps", "3"]),
    ("rgb-comment", "rgb.ppm", "jph", [*LOSSLESS, "-com", "Egami HT test"]),
    ("odd-lossless", "odd.ppm", "jph", LOSSLESS),
    ("odd-lossy", "odd.ppm", "j2c", ["-qstep", "0.008"]),
    ("gray-lossless", "gray.pgm", "jph", LOSSLESS),
    ("gray-lossy", "gray.pgm", "jph", ["-qstep", "0.004"]),
    ("gray12-lossless", "gray12.pgm", "j2c", LOSSLESS),
    ("gray12-lossy", "gray12.pgm", "jph", ["-qstep", "0.002"]),
    ("gray16-lossless", "gray16.pgm", "jph", LOSSLESS),
    ("signed12", "signed12.yuv", "j2c", [*RAWSIGNED, *LOSSLESS]),
    ("yuv420", "yuv420.yuv", "j2c", [*RAW420, *LOSSLESS]),
    ("yuv420-lossy", "yuv420.yuv", "j2c", [*RAW420, "-qstep", "0.01"]),
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
        stream = f"{WORK}/{name}.j2c"
        subprocess.run(["ojph_compress", "-i", f"{WORK}/{source}", "-o", stream, *args], check=True, stdout=subprocess.DEVNULL)
        with open(stream, "rb") as f:
            codestream = f.read()
        if extension == "jph":
            jph(codestream, target)
        else:
            with open(target, "wb") as f:
                f.write(codestream)
        if source.endswith(".yuv") and "yuv420" in source:
            # opj_decompress converts subsampled three-component codestreams to RGB, so the references are the planes encoded,
            # which the lossless file decodes to exactly.
            for c in range(3):
                gzipped(f"{WORK}/yuv420_src_{c}.pgx", f"{REFS}/{name}_src_{c}.pgx.gz")
            print(name, os.path.getsize(target))
            continue

        levels0 = "-num_decomps" in args and args[args.index("-num_decomps") + 1] == "0"
        for reduce in (0,) if levels0 else (0, 1):
            out = f"{WORK}/{name}_r{reduce}.pgx"
            subprocess.run(["opj_decompress", "-i", target, "-o", out, "-r", str(reduce)], check=True, stdout=subprocess.DEVNULL)
            for produced in sorted(glob.glob(f"{WORK}/{name}_r{reduce}*.pgx")):
                gzipped(produced, f"{REFS}/{os.path.basename(produced)}.gz")
                os.remove(produced)
        print(name, os.path.getsize(target))


if __name__ == "__main__":
    if sys.argv[1:2] == ["wrap"]:
        # htcorpus.py wrap <codestream> <jph>: wraps one codestream, for make-htj2k-references.py's speed set.
        with open(sys.argv[2], "rb") as source:
            jph(source.read(), sys.argv[3])
    else:
        main()
