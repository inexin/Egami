"""Builds the TIFF corpus in tests/images/tiff and its references in tests/reference/tiff, with libtiff in Docker.

Corpus (tests/images/tiff; licenses in tests/images/LICENSES), all made here from ops/duck.png and ops/egret.png:
  - Base files written byte by byte by this script (uncompressed, little-endian, one strip, IFD after the header): bilevel
    (both photometrics), gray at 2, 4, 8, 10, 12, 14, 16, 24 and 32 bits, signed 8 and 16, float 16, 32 and 64; gray with
    unassociated and associated alpha; RGB at 8 and 16 bits and float 16 and 32; RGBA at 8 and 16 bits (both alpha kinds),
    float RGBA, RGB with an unspecified extra sample; palettes of 1, 2, 4 and 8 bits; CMYK at 8 and 16 bits, CMYK with alpha,
    two inks named by InkNames; YCbCr at 1x1, 2x1, 2x2, 4x2 and 4x4 subsampling (BT.601, and one BT.709 with
    ReferenceBlackWhite); CIELab and ICCLab at 8 and 16 bits; every Orientation; multi-page files (pages of different kinds and
    sizes, a reduced-resolution page, SubIFD pyramids, a transparency mask).
  - tiffcp variants of those: None, LZW (with and without the predictor), Deflate (predictor 2, and 3 for float), PackBits,
    JPEG (gray, RGB as YCbCr 2x2, RGB kept, CMYK), CCITT G3 1D, G3 2D, G3 2D with fill, G4, ZSTD (for the NotSupported
    message); tiles of 16x16, 32x16 and 64x64; separate planes; 1 and 7 rows a strip; big-endian; BigTIFF; FillOrder 2.
  - tiffref (tools/tiffref/tiffref.c, compiled in the container) writes SGILog (LogL, LogLuv, LogLuv24) and WebP (lossless and
    lossy) files, which tiffcp cannot make from plain samples.
  - exiftool adds EXIF with GPS, XMP, IPTC and an ICC profile (icc/compact/DisplayP3-v4.icc) to two files.

References (tests/reference/tiff):
  <file>.<page>.raw.gz    tiffref raw: libtiff's decoded samples (see tiffref.c for the layout)
  <file>.<page>.rgba.gz   tiffref rgba: TIFFReadRGBAImageOriented, 8-bit RGBA (unassociated alpha premultiplied by libtiff)
  <file>.<page>.magick.gz ImageMagick's 8-bit RGBA, for CMYK, YCbCr and Lab pages
  <file>.exif.json        exiftool -j -a -G1 -struct -n, for the metadata files
  manifest.json           per file: how it was made, tiffref info (every directory and SubIFD), which references hold its
                          pixels (pages that decode alike share one), and the tool versions

Local corpus (never committed): `python tools/make-tiff-references.py --pics` downloads libtiff's sample images
(https://download.osgeo.org/libtiff/pics-3.8.0.tar.gz, the set ClassicMac was checked against) into .cache/libtiff-pics and
writes the same kinds of references for them to .cache/libtiff-pics-ref. Their licenses vary, so they stay out of the repo.

Usage, from the repo root with Docker running:
  python tools/make-tiff-references.py [--pics]
The script runs alpine:3.20, installs libtiff 4.6.0 (tiff-tools, tiff-dev), gcc, musl-dev, Pillow, numpy, ImageMagick and
exiftool inside it with apk, and compiles tools/tiffref/tiffref.c.
"""
import gzip
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow py3-numpy gcc musl-dev tiff-dev tiff-tools imagemagick exiftool"
CORPUS = "tests/images/tiff"
TARGET = "tests/reference/tiff"
PICS = ".cache/libtiff-pics"
PICS_TARGET = ".cache/libtiff-pics-ref"
PICS_URL = "https://download.osgeo.org/libtiff/pics-3.8.0.tar.gz"
TOOL = "/tmp/tiffref"
WORK = "/tmp/tiffwork"

# TIFF field types: struct code and size.
TYPES = {1: "B", 2: "B", 3: "H", 4: "I", 5: "I", 6: "b", 7: "B", 8: "h", 9: "i", 10: "i", 11: "f", 12: "d", 16: "Q", 13: "I"}
SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 16: 8, 13: 4}


def run(*args, **kwargs):
    result = subprocess.run(list(map(str, args)), capture_output=True, text=True, **kwargs)
    if result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, args, result.stdout, result.stderr)
    return result.stdout


def tiffref(*args):
    return json.loads(run(TOOL, *args))


def write_gz(path, data):
    # No name and a zero time in the header, so regenerating gives identical files.
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


class Page:
    """One IFD: its tags (tag: (type, values)), its pixel data and the rows each strip holds, and its SubIFDs."""

    def __init__(self, width, height, tags, data, rows=None, subs=()):
        self.width, self.height, self.tags, self.data, self.rows, self.subs = width, height, dict(tags), data, rows, list(subs)


def write_tiff(path, pages, order="<"):
    """Writes classic TIFF: the header, then each page's IFD (pointing at data after it), then its data and values."""
    out = bytearray(b"II*\0" if order == "<" else b"MM\0*")
    out += struct.pack(order + "I", 0)

    def pack(type_, values):
        if type_ in (5, 10):
            return b"".join(struct.pack(order + TYPES[type_] * 2, n, d) for n, d in values)
        return b"".join(struct.pack(order + TYPES[type_], v) for v in values)

    def emit(page):
        subs = [emit(s)[0] for s in page.subs]
        rows = page.rows or page.height
        offsets, counts = [], []
        for data in page.data if isinstance(page.data, list) else [page.data]:
            row_bytes = len(data) // page.height
            for y in range(0, page.height, rows):
                if len(out) % 2:
                    out.append(0)
                chunk = data if rows >= page.height else data[y * row_bytes:min(page.height, y + rows) * row_bytes]
                offsets.append(len(out))
                counts.append(len(chunk))
                out.extend(chunk)
        tags = dict(page.tags)
        tags.setdefault(256, (4, [page.width]))
        tags.setdefault(257, (4, [page.height]))
        tags[278] = (4, [rows])
        tags[273] = (4, offsets)
        tags[279] = (4, counts)
        if subs:
            tags[330] = (4, subs)
        if len(out) % 2:
            out.append(0)
        ifd = len(out)
        values_at = ifd + 2 + 12 * len(tags) + 4
        entries, values = bytearray(), bytearray()
        for tag in sorted(tags):
            type_, vals = tags[tag]
            body = pack(type_, vals)
            count = len(vals) if type_ not in (2,) else len(body)
            entries += struct.pack(order + "HHI", tag, type_, count)
            if len(body) <= 4:
                entries += body.ljust(4, b"\0")
            else:
                entries += struct.pack(order + "I", values_at + len(values))
                values += body
                if len(values) % 2:
                    values.append(0)
        out.extend(struct.pack(order + "H", len(tags)))
        out.extend(entries)
        next_at = len(out)
        out.extend(b"\0\0\0\0")
        out.extend(values)
        return ifd, next_at

    previous = 4
    for page in pages:
        ifd, next_at = emit(page)
        struct.pack_into(order + "I", out, previous, ifd)
        previous = next_at
    with open(path, "wb") as f:
        f.write(out)


def sources():
    """The pictures the corpus is made from, by name, as float arrays (height, width, channels) in 0 to 255."""
    import numpy as np
    from PIL import Image

    duck = Image.open("tests/images/ops/duck.png").convert("RGB")
    egret = Image.open("tests/images/ops/egret.png").convert("RGB")

    def smooth(image, size):
        # Each channel resized in float mode, so 16-bit and float pages get fractional values, not 8-bit ones scaled.
        return np.dstack([np.asarray(c.convert("F").resize(size, Image.LANCZOS)) for c in image.split()]).clip(0, 255)

    return {"duck": smooth(duck, (64, 48)), "egret": smooth(egret, (97, 61)), "duck-small": smooth(duck, (32, 24)),
            "duck-large": smooth(duck, (160, 120))}


def packed(samples, bits):
    """Packs integer samples (height, width × channels) most significant bit first, each row padded to a byte."""
    import numpy as np

    height = samples.shape[0]
    rows = []
    for y in range(height):
        acc, n, row = 0, 0, bytearray()
        for v in samples[y].ravel():
            acc = (acc << bits) | int(v)
            n += bits
            while n >= 8:
                n -= 8
                row.append((acc >> n) & 0xFF)
        if n:
            row.append((acc << (8 - n)) & 0xFF)
        rows.append(bytes(row))
    return b"".join(rows)


def samples_bytes(values, bits, fmt=1, order="<"):
    """Integer or float samples (height, width, channels) as stored bytes."""
    import numpy as np

    values = np.asarray(values)
    if fmt == 3:
        dtype = {16: "f2", 32: "f4", 64: "f8"}[bits]
        return values.astype(order + dtype).tobytes()
    if bits in (8, 16, 32) and bits % 8 == 0:
        kind = "i" if fmt == 2 else "u"
        return values.astype(order + kind + str(bits // 8)).tobytes()
    if bits == 24:
        v = values.astype("<u4").reshape(-1)
        b = np.stack([v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF], axis=1).astype("u1")
        if order == ">":
            b = b[:, ::-1]
        return b.tobytes()
    flat = values.reshape(values.shape[0], -1)
    return packed(flat, bits)


def base_tags(width, height, bits, samples, photometric, fmt=1, extra=None):
    tags = {256: (4, [width]), 257: (4, [height]), 258: (3, [bits] * samples), 259: (3, [1]), 262: (3, [photometric]),
            277: (3, [samples]), 284: (3, [1]), 282: (5, [(72, 1)]), 283: (5, [(72, 1)]), 296: (3, [2])}
    if fmt != 1:
        tags[339] = (3, [fmt] * samples)
    if extra is not None:
        tags[338] = (3, extra)
    return tags


big_endian = set()


def make_bases(pictures):
    """Returns (name, pages, notes) for every base file."""
    import numpy as np

    duck, egret = pictures["duck"], pictures["duck-small"]
    h, w = duck.shape[:2]
    luma = duck @ np.array([0.299, 0.587, 0.114])
    alpha = np.tile(np.linspace(0, 255, w), (h, 1))
    alpha[:, : w // 8] = 0
    alpha[:, -w // 8:] = 255
    files = []

    def single(name, bits, samples, photometric, values, fmt=1, extra=None, more=None, note=""):
        tags = base_tags(w if values.shape[1] == w else values.shape[1], values.shape[0], bits, samples, photometric, fmt, extra)
        tags.update(more or {})
        data = samples_bytes(values, bits, fmt)
        files.append((name, [Page(values.shape[1], values.shape[0], tags, data)], note))

    def level(v, bits):
        return np.rint(v / 255 * ((1 << bits) - 1)).astype(np.int64)

    bilevel = (luma > 110).astype(np.int64)
    single("bilevel-bz", 1, 1, 1, bilevel[:, :, None])
    single("bilevel-wz", 1, 1, 0, (1 - bilevel)[:, :, None])
    for bits in (2, 4, 8, 10, 12, 14, 16):
        single(f"gray{bits}", bits, 1, 1, level(luma, bits)[:, :, None])
    single("gray8-wz", 8, 1, 0, level(255 - luma, 8)[:, :, None])
    single("gray24", 24, 1, 1, np.rint(luma / 255 * 0xFFFFFF).astype(np.int64)[:, :, None])
    single("gray32", 32, 1, 1, np.rint(luma / 255 * 0xFFFFFFFF).astype(np.uint64)[:, :, None])
    single("gray8-signed", 8, 1, 1, (level(luma, 8) - 128)[:, :, None], fmt=2)
    single("gray16-signed", 16, 1, 1, (level(luma, 16) - 32768)[:, :, None], fmt=2)
    for bits in (16, 32, 64):
        single(f"grayf{bits}", bits, 1, 1, (luma / 255)[:, :, None], fmt=3)
    single("grayf32-hdr", 32, 1, 1, (luma / 255 * 4 - 0.5)[:, :, None], fmt=3, note="values from -0.5 to 3.5")
    graya = np.dstack([luma, alpha])
    single("graya8", 8, 2, 1, level(graya, 8), extra=[2])
    single("graya8-assoc", 8, 2, 1, level(np.dstack([luma * alpha / 255, alpha]), 8), extra=[1])
    single("graya16", 16, 2, 1, level(graya, 16), extra=[2])
    single("rgb8", 8, 3, 2, level(duck, 8))
    single("rgb16", 16, 3, 2, level(duck, 16))
    single("rgbf16", 16, 3, 2, duck / 255, fmt=3)
    single("rgbf32", 32, 3, 2, duck / 255, fmt=3)
    rgba = np.dstack([duck, alpha])
    premul = np.dstack([duck * alpha[:, :, None] / 255, alpha])
    for name, values, bits, fmt, extra in (("rgb16-planar", level(duck, 16), 16, 1, None),
                                           ("rgbaf32-planar", rgba / 255, 32, 3, [2])):
        tags = base_tags(w, h, bits, values.shape[2], 2, fmt, extra)
        tags[284] = (3, [2])
        planes = [samples_bytes(values[:, :, c:c + 1], bits, fmt) for c in range(values.shape[2])]
        files.append((name, [Page(w, h, tags, planes)], "separate planes"))
    single("rgba8", 8, 4, 2, level(rgba, 8), extra=[2])
    single("rgba8-assoc", 8, 4, 2, level(premul, 8), extra=[1])
    single("rgba16", 16, 4, 2, level(rgba, 16), extra=[2])
    single("rgba16-assoc", 16, 4, 2, level(premul, 16), extra=[1])
    single("rgbaf32", 32, 4, 2, rgba / 255, fmt=3, extra=[2])
    single("rgbx8", 8, 4, 2, level(np.dstack([duck, luma]), 8), extra=[0], note="an unspecified extra sample")
    single("rgb2a8", 8, 5, 2, level(np.dstack([duck, alpha, luma]), 8), extra=[2, 0], note="alpha and an unspecified sample")

    # Palettes: the duck quantized by Pillow to 2^bits colors.
    from PIL import Image

    rgb8 = Image.fromarray(level(duck, 8).astype(np.uint8), "RGB")
    for bits in (1, 2, 4, 8):
        quantized = rgb8.quantize(colors=1 << bits, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
        palette = (quantized.getpalette() + [0] * 768)[: 3 * (1 << bits)]
        indexes = np.asarray(quantized).astype(np.int64)
        colormap = [palette[i * 3 + c] * 257 for c in range(3) for i in range(1 << bits)]
        single(f"palette{bits}", bits, 1, 3, indexes[:, :, None], more={320: (3, colormap)})

    # CMYK: naive inks from RGB, with some black.
    k = 255 - duck.max(axis=2)
    safe = np.where(k < 255, 255 - k, 1)[:, :, None]
    cmy = (255 - duck - k[:, :, None]) / safe * 255
    cmyk = np.dstack([cmy, k])
    single("cmyk8", 8, 4, 5, level(cmyk, 8), more={332: (3, [1])})
    single("cmyk16", 16, 4, 5, level(cmyk, 16), more={332: (3, [1])})
    single("cmyka8", 8, 5, 5, level(np.dstack([cmyk, alpha]), 8), extra=[2], more={332: (3, [1])})
    # Two inks: InkSet 2 (not CMYK), InkNames (ASCII) and NumberOfInks.
    single("inks2", 8, 2, 5, level(cmyk[:, :, :2], 8),
           more={332: (3, [2]), 333: (2, list(b"Cyan\0Magenta\0")), 334: (3, [2])}, note="two inks, InkSet 2")

    # YCbCr, uncompressed, in data units: (sub_h × sub_v luma samples, then Cb and Cr).
    def ycbcr(name, sub_h, sub_v, coefficients=(0.299, 0.587, 0.114), reference=None, deflate=False):
        kr, kg, kb = coefficients
        y = duck @ np.array(coefficients)
        cb = (duck[:, :, 2] - y) / (2 - 2 * kb)
        cr = (duck[:, :, 0] - y) / (2 - 2 * kr)
        ref = reference or [0, 255, 128, 255, 128, 255]
        yq = np.clip(np.rint(ref[0] + y * (ref[1] - ref[0]) / 255), 0, 255)
        cbq = np.clip(np.rint(ref[2] + cb * (ref[3] - ref[2]) / 127), 0, 255)
        crq = np.clip(np.rint(ref[4] + cr * (ref[5] - ref[4]) / 127), 0, 255)
        units = bytearray()
        for by in range(0, h, sub_v):
            for bx in range(0, w, sub_h):
                block = yq[by:by + sub_v, bx:bx + sub_h]
                full = np.zeros((sub_v, sub_h))
                full[:block.shape[0], :block.shape[1]] = block
                units += bytes(full.astype(np.uint8).ravel())
                units.append(int(np.rint(cbq[by:by + sub_v, bx:bx + sub_h].mean())))
                units.append(int(np.rint(crq[by:by + sub_v, bx:bx + sub_h].mean())))
        tags = base_tags(w, h, 8, 3, 6)
        tags[530] = (3, [sub_h, sub_v])
        tags[531] = (3, [1])
        tags[529] = (5, [(int(round(c * 10000)), 10000) for c in coefficients])
        tags[532] = (5, [(v, 1) for v in ref])
        # One strip; rows per strip must be a multiple of sub_v, and the data is in units, not rows.
        if deflate:
            import zlib

            tags[259] = (3, [8])
            units = zlib.compress(bytes(units), 6)
        page = Page(w, h, tags, bytes(units))
        page.rows = h
        files.append((name, [page], f"YCbCr {sub_h}x{sub_v}"))

    ycbcr("ycbcr11", 1, 1)
    ycbcr("ycbcr21", 2, 1)
    ycbcr("ycbcr22", 2, 2)
    ycbcr("ycbcr42", 4, 2)
    ycbcr("ycbcr44", 4, 4)
    ycbcr("ycbcr22-zip", 2, 2, deflate=True)
    ycbcr("ycbcr22-bt709", 2, 2, (0.2126, 0.7152, 0.0722), [16, 235, 128, 240, 128, 240])

    # CIELab and ICCLab from sRGB through XYZ, adapted from D65 to D50 by Bradford (the white Adobe's TIFF note and ICC use).
    lin = np.where(duck / 255 <= 0.04045, duck / 255 / 12.92, ((duck / 255 + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    bradford = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])
    d65 = np.array([0.3127 / 0.3290, 1.0, (1 - 0.3127 - 0.3290) / 0.3290])
    d50 = np.array([0.3457 / 0.3585, 1.0, (1 - 0.3457 - 0.3585) / 0.3585])
    adapt = np.linalg.inv(bradford) @ np.diag((bradford @ d50) / (bradford @ d65)) @ bradford
    xyz = lin @ m.T @ adapt.T / d50
    f = np.where(xyz > (6 / 29) ** 3, np.cbrt(xyz), xyz / (3 * (6 / 29) ** 2) + 4 / 29)
    lab_l, lab_a, lab_b = 116 * f[:, :, 1] - 16, 500 * (f[:, :, 0] - f[:, :, 1]), 200 * (f[:, :, 1] - f[:, :, 2])
    single("cielab8", 8, 3, 8, np.dstack([np.rint(lab_l * 255 / 100), np.rint(lab_a) % 256, np.rint(lab_b) % 256]).astype(np.int64))
    single("cielab16", 16, 3, 8,
           np.dstack([np.rint(lab_l * 65535 / 100), np.rint(lab_a * 256) % 65536, np.rint(lab_b * 256) % 65536]).astype(np.int64))
    single("icclab8", 8, 3, 9, np.dstack([np.rint(lab_l * 255 / 100), np.rint(lab_a + 128), np.rint(lab_b + 128)]).astype(np.int64))
    single("icclab16", 16, 3, 9,
           np.dstack([np.rint(lab_l * 65535 / 100), np.rint(lab_a * 256 + 32768), np.rint(lab_b * 256 + 32768)]).astype(np.int64))

    # Every orientation, on a picture whose corners differ.
    for orientation in range(1, 9):
        single(f"orientation{orientation}", 8, 3, 2, level(egret, 8), more={274: (3, [orientation])})

    # Multi-page files.
    def page_of(values, bits, samples, photometric, more=None, extra=None):
        tags = base_tags(values.shape[1], values.shape[0], bits, samples, photometric, extra=extra)
        tags.update(more or {})
        return Page(values.shape[1], values.shape[0], tags, samples_bytes(values, bits))

    big = pictures["duck-large"]
    lbig = big @ np.array([0.299, 0.587, 0.114])
    pages = [
        page_of(level(duck, 8), 8, 3, 2, {297: (3, [0, 3]), 254: (4, [2])}),
        page_of(level(luma, 16)[:, :, None], 16, 1, 1, {297: (3, [1, 3]), 254: (4, [2])}),
        page_of(level(big, 8), 8, 3, 2, {297: (3, [2, 3]), 254: (4, [2])}),
    ]
    files.append(("pages3", pages, "three pages: RGB 64x48, Gray16 64x48, RGB 160x120"))
    files.append(("pages-reduced", [page_of(level(big, 8), 8, 3, 2), page_of(level(duck, 8), 8, 3, 2, {254: (4, [1])}),
                                    page_of(level(lbig, 8)[:, :, None], 8, 1, 1)],
                  "a page, its reduced-resolution copy, a second page"))
    small = pictures["duck-small"]
    pyramid = Page(160, 120, base_tags(160, 120, 8, 3, 2), samples_bytes(level(big, 8), 8),
                   subs=[page_of(level(duck, 8), 8, 3, 2, {254: (4, [1])}), page_of(level(small, 8), 8, 3, 2, {254: (4, [1])})])
    files.append(("pyramid-subifd", [pyramid], "SubIFDs of 64x48 and 32x24"))
    mask = page_of(bilevel[:, :, None], 1, 1, 4, {254: (4, [4])})
    files.append(("pages-mask", [page_of(level(duck, 8), 8, 3, 2), mask], "a page and its transparency mask"))
    import zlib

    fp = np.ascontiguousarray((duck / 255).astype(">f4"))
    rows = []
    for y in range(h):
        row = fp[y].view(np.uint8).reshape(w * 3, 4)
        planes = bytearray(np.ascontiguousarray(row.T).ravel())
        for i in range(len(planes) - 1, 2, -1):
            planes[i] = (planes[i] - planes[i - 3]) & 0xFF
        rows.append(bytes(planes))
    tags = base_tags(w, h, 32, 3, 2, 3)
    tags[259] = (3, [8])
    tags[317] = (3, [3])
    files.append(("rgbf32-zip-fp-be", [Page(w, h, tags, zlib.compress(b"".join(rows), 6))],
                  "big-endian, floating-point predictor as Adobe's note describes it; written by the script"))
    files[-1][1][0].rows = h
    big_endian.add("rgbf32-zip-fp-be")
    strips = page_of(level(duck, 8), 8, 3, 2)
    strips.rows = 5
    files.append(("rgb8-strips5-ifdfirst", [strips], "written by this script: 5 rows a strip, IFD first"))
    return files


# tiffcp variants: (name, base, tiffcp options).
VARIANTS = [
    ("rgb8-lzw", "rgb8", ["-c", "lzw"]),
    ("rgb8-lzw-pred", "rgb8", ["-c", "lzw:2"]),
    ("rgb8-zip-pred", "rgb8", ["-c", "zip:2"]),
    ("rgb8-zip-p9", "rgb8", ["-c", "zip:p9"]),
    ("rgb8-packbits", "rgb8", ["-c", "packbits"]),
    ("rgb8-jpeg", "rgb8", ["-r", "16", "-c", "jpeg"]),
    ("rgb8-jpeg-q95", "rgb8", ["-r", "16", "-c", "jpeg:95"]),
    ("rgb8-jpeg-raw", "rgb8", ["-r", "16", "-c", "jpeg:r"]),
    ("rgb8-jpeg-tiles", "rgb8", ["-c", "jpeg", "-t", "-w", "32", "-l", "16"]),
    ("gray8-jpeg", "gray8", ["-r", "16", "-c", "jpeg"]),
    ("cmyk8-jpeg", "cmyk8", ["-r", "16", "-c", "jpeg:r"]),
    ("rgb8-zstd", "rgb8", ["-c", "zstd"]),
    ("rgb8-tiles16", "rgb8", ["-t", "-w", "16", "-l", "16"]),
    ("rgb8-tiles32x16-lzw", "rgb8", ["-c", "lzw:2", "-t", "-w", "32", "-l", "16"]),
    ("rgb8-tiles64-zip", "rgb8", ["-c", "zip:2", "-t", "-w", "64", "-l", "64"]),
    ("rgb8-planar", "rgb8", ["-p", "separate"]),
    ("rgb8-planar-lzw-pred", "rgb8", ["-p", "separate", "-c", "lzw:2"]),
    ("rgb8-planar-tiles", "rgb8", ["-p", "separate", "-t", "-w", "16", "-l", "32", "-c", "zip"]),
    ("rgb8-rows1", "rgb8", ["-r", "1", "-c", "packbits"]),
    ("rgb8-rows7-zip", "rgb8", ["-r", "7", "-c", "zip:2"]),
    ("rgb8-be", "rgb8", ["-B", "-c", "lzw:2"]),
    ("rgb8-lzw-lsb", "rgb8", ["-c", "lzw", "-f", "lsb2msb"]),
    ("rgb8-packbits-lsb", "rgb8", ["-c", "packbits", "-f", "lsb2msb"]),
    ("rgb8-bigtiff", "rgb8", ["-8", "-c", "zip:2"]),
    ("rgb8-bigtiff-tiles-be", "rgb8", ["-8", "-B", "-t", "-w", "16", "-l", "16", "-c", "lzw"]),
    ("rgb16-lzw-pred", "rgb16", ["-c", "lzw:2"]),
    ("rgb16-zip-pred-be", "rgb16", ["-c", "zip:2", "-B"]),
    ("rgb16-planar-zip-pred", "rgb16-planar", ["-c", "zip:2"]),
    ("rgbaf32-planar-zip-fp", "rgbaf32-planar", ["-c", "zip:3"]),
    ("rgb16-tiles-packbits", "rgb16", ["-t", "-w", "16", "-l", "16", "-c", "packbits"]),
    ("rgba8-lzw-pred", "rgba8", ["-c", "lzw:2"]),
    ("rgba8-planar-zip", "rgba8", ["-p", "separate", "-c", "zip:2"]),
    ("rgba8-assoc-zip", "rgba8-assoc", ["-c", "zip:2"]),
    ("rgba16-zip-pred", "rgba16", ["-c", "zip:2"]),
    ("rgbf32-zip-fp", "rgbf32", ["-c", "zip:3"]),
    ("rgbf32-lzw-fp", "rgbf32", ["-c", "lzw:3"]),
    ("rgbf32-zip-pred2", "rgbf32", ["-c", "zip:2"]),
    ("rgbf16-zip-fp", "rgbf16", ["-c", "zip:3"]),
    ("rgbaf32-tiles-zip-fp", "rgbaf32", ["-t", "-w", "16", "-l", "16", "-c", "zip:3"]),
    ("grayf32-zip-fp", "grayf32", ["-c", "zip:3"]),
    ("grayf64-zip-fp", "grayf64", ["-c", "zip:3"]),
    ("grayf32-be", "grayf32", ["-B"]),
    ("gray8-lzw-pred", "gray8", ["-c", "lzw:2"]),
    ("gray16-lzw-pred-be", "gray16", ["-c", "lzw:2", "-B"]),
    ("gray16-tiles-zip", "gray16", ["-t", "-w", "32", "-l", "32", "-c", "zip:2"]),
    ("gray32-zip-pred", "gray32", ["-c", "zip:2"]),
    ("gray4-packbits", "gray4", ["-c", "packbits"]),
    ("gray4-tiles-lzw", "gray4", ["-t", "-w", "16", "-l", "16", "-c", "lzw"]),
    ("gray12-be", "gray12", ["-B"]),
    ("gray12-lzw", "gray12", ["-c", "lzw"]),
    ("graya8-planar-lzw", "graya8", ["-p", "separate", "-c", "lzw"]),
    ("graya16-zip-pred", "graya16", ["-c", "zip:2"]),
    ("palette8-lzw", "palette8", ["-c", "lzw"]),
    ("palette4-packbits", "palette4", ["-c", "packbits"]),
    ("palette8-tiles", "palette8", ["-t", "-w", "16", "-l", "16"]),
    ("cmyk8-lzw-pred", "cmyk8", ["-c", "lzw:2"]),
    ("cmyk8-planar", "cmyk8", ["-p", "separate", "-c", "zip"]),
    ("cmyk16-zip-pred", "cmyk16", ["-c", "zip:2"]),
    ("bilevel-g3", "bilevel-bz", ["-c", "g3"]),
    ("bilevel-g3-2d", "bilevel-bz", ["-c", "g3:2d"]),
    ("bilevel-g3-2d-fill", "bilevel-bz", ["-c", "g3:2d:fill"]),
    ("bilevel-g3-wz", "bilevel-wz", ["-c", "g3:1d"]),
    ("bilevel-g4", "bilevel-bz", ["-c", "g4"]),
    ("bilevel-g4-wz", "bilevel-wz", ["-c", "g4"]),
    ("bilevel-g4-rows7", "bilevel-bz", ["-c", "g4", "-r", "7"]),
    ("bilevel-g4-lsb", "bilevel-bz", ["-c", "g4", "-f", "lsb2msb"]),
    ("bilevel-g4-tiles", "bilevel-bz", ["-c", "g4", "-t", "-w", "32", "-l", "16"]),
    ("bilevel-lsb", "bilevel-bz", ["-f", "lsb2msb"]),
    ("bilevel-packbits", "bilevel-bz", ["-c", "packbits"]),
    ("bilevel-lzw", "bilevel-bz", ["-c", "lzw"]),
    ("bilevel-zip-lsb", "bilevel-bz", ["-c", "zip", "-f", "lsb2msb"]),
    ("cielab8-lzw", "cielab8", ["-c", "lzw"]),
    ("pages3-zip", "pages3", ["-c", "zip:2"]),
    ("pages3-bigtiff-be", "pages3", ["-8", "-B", "-c", "lzw:2"]),
]


def inside(pics):
    from PIL import Image

    subprocess.run(["gcc", "-O2", "-Wall", "-o", TOOL, "tools/tiffref/tiffref.c", "-ltiff"], check=True)
    if pics:
        make_pics()
        return
    for folder in (CORPUS, TARGET):
        if os.path.isdir(folder):
            shutil.rmtree(folder)
        os.makedirs(folder)
    os.makedirs(WORK, exist_ok=True)
    entries = []
    first = {}  # sha256 of a reference -> the file that holds it

    def keep(name, data):
        """Writes a reference, or returns the earlier file with the same bytes."""
        digest = hashlib.sha256(data).hexdigest()
        if digest not in first:
            write_gz(os.path.join(TARGET, name), data)
            first[digest] = name
        return first[digest]

    def references(name, entry):
        entries.append(describe(os.path.join(CORPUS, name + ".tif"), name, entry, TARGET, keep))

    pictures = sources()
    for name, pages, note in make_bases(pictures):
        write_tiff(os.path.join(CORPUS, name + ".tif"), pages, ">" if name in big_endian else "<")
        references(name, {"file": name + ".tif", "tool": "make-tiff-references.py", "note": note})

    for name, base, options in VARIANTS:
        try:
            run("tiffcp", *options, os.path.join(CORPUS, base + ".tif"), os.path.join(CORPUS, name + ".tif"))
        except subprocess.CalledProcessError as e:
            raise RuntimeError(f"tiffcp {name}: {e.stderr}") from e
        references(name, {"file": name + ".tif", "tool": "tiffcp", "source": base + ".tif", "options": options})

    # SGILog from linear XYZ (LogLuv) or Y (LogL), scaled so the brightest pixel is about 4.
    import numpy as np

    duck = pictures["duck"]
    lin = np.where(duck / 255 <= 0.04045, duck / 255 / 12.92, ((duck / 255 + 0.055) / 1.055) ** 2.4) * 4
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = (lin @ m.T).astype("<f4")
    for name, mode, values in (("logl", "logl", xyz[:, :, 1].copy()), ("logluv", "logluv", xyz), ("logluv24", "logluv24", xyz)):
        raw = os.path.join(WORK, "in.f32")
        with open(raw, "wb") as f:
            f.write(values.tobytes())
        tiffref("logluv", raw, duck.shape[1], duck.shape[0], os.path.join(CORPUS, name + ".tif"), mode)
        references(name, {"file": name + ".tif", "tool": "tiffref logluv", "options": [mode]})

    rgb8 = np.rint(duck).astype(np.uint8)
    alpha = np.tile(np.linspace(0, 255, duck.shape[1]), (duck.shape[0], 1)).astype(np.uint8)
    for name, values, quality in (("webp-lossless", rgb8, 101), ("webp-lossless-alpha", np.dstack([rgb8, alpha]), 101),
                                  ("webp-lossy", rgb8, 80)):
        raw = os.path.join(WORK, "in.raw")
        with open(raw, "wb") as f:
            f.write(values.tobytes())
        tiffref("webp", raw, values.shape[1], values.shape[0], values.shape[2], os.path.join(CORPUS, name + ".tif"), quality)
        references(name, {"file": name + ".tif", "tool": "tiffref webp", "options": [str(quality)]})

    # Metadata through exiftool.
    meta = [
        ("meta-exif", "rgb8-lzw", ["-Artist=Egami Tester", "-Copyright=Public domain", "-Make=Egami", "-Model=Corpus",
                                   "-Software=make-tiff-references", "-ImageDescription=A duck", "-DateTime=2026:10:11 12:00:00",
                                   "-DateTimeOriginal=2026:10:11 12:00:00", "-ExposureTime=1/125", "-FNumber=5.6", "-ISO=200",
                                   "-GPSLatitude=59.91", "-GPSLatitudeRef=N", "-GPSLongitude=10.75", "-GPSLongitudeRef=E",
                                   "-XMP-dc:Creator=Egami", "-XMP-dc:Title=Duck", "-IPTC:Keywords=duck", "-IPTC:Keywords=bird",
                                   "-IPTC:By-line=Egami Tester"]),
        ("meta-icc", "rgb16-lzw-pred", ["-ICC_Profile<=tests/images/icc/compact/DisplayP3-v4.icc"]),
    ]
    for name, base, args in meta:
        out = os.path.join(CORPUS, name + ".tif")
        shutil.copy(os.path.join(CORPUS, base + ".tif"), out)
        run("exiftool", "-q", "-overwrite_original", *args, out)
        exif = run("exiftool", "-j", "-a", "-G1", "-struct", "-n", "-x", "System:all", out)
        with open(os.path.join(TARGET, name + ".exif.json"), "w", newline="\n") as f:
            f.write(exif)
        references(name, {"file": name + ".tif", "tool": "exiftool", "source": base + ".tif", "options": args,
                          "exif": name + ".exif.json"})

    tools = f"{tiffref('version')['version']}; {run('apk', 'list', '--installed', 'tiff').split()[0]}; " \
            f"{run('magick', '-version').splitlines()[0]}; exiftool {run('exiftool', '-ver').strip()}; {IMAGE}"
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": tools, "files": entries}, f, indent=1)
        f.write("\n")
    shutil.rmtree(WORK)
    print(f"Wrote {len(entries)} TIFF files and their references.")


def describe(path, name, entry, target, keep):
    """Adds tiffref info and the raw, RGBA and ImageMagick references of every page and SubIFD to entry."""
    info = tiffref("info", path)
    entry["info"] = info
    entry["size"] = os.path.getsize(path)
    out = os.path.join(WORK, "out.raw")
    for page in info["pages"]:
        which = f"{page['index']}" if page["sub"] < 0 else f"{page['index']}.{page['sub']}"
        refs = {}
        subsampled = page["photometric"] == 6 and page["compression"] not in (6, 7)
        if not subsampled and page["compression"] not in (6, 50000):
            try:
                tiffref("raw", path, which, out)
                with open(out, "rb") as f:
                    refs["raw"] = keep(f"{name}.{which}.raw.gz", f.read())
            except subprocess.CalledProcessError as e:
                refs["rawError"] = e.stderr.strip()
        try:
            tiffref("rgba", path, which, out)
            with open(out, "rb") as f:
                refs["rgba"] = keep(f"{name}.{which}.rgba.gz", f.read())
        except subprocess.CalledProcessError as e:
            refs["rgbaError"] = e.stderr.strip()
        if page["photometric"] in (5, 6, 8, 9) and page["sub"] < 0:
            try:
                run("magick", f"{path}[{page['index']}]", "-depth", "8", f"rgba:{out}")
                with open(out, "rb") as f:
                    refs["magick"] = keep(f"{name}.{which}.magick.gz", f.read())
            except subprocess.CalledProcessError as e:
                refs["magickError"] = e.stderr.strip()[:200]
        page["references"] = refs
    return entry


def make_pics():
    """Downloads libtiff's sample images into .cache/libtiff-pics and writes their references to .cache/libtiff-pics-ref."""
    import tarfile
    import urllib.request

    os.makedirs(WORK, exist_ok=True)
    if not os.path.isdir(PICS):
        archive = os.path.join(WORK, "pics.tar.gz")
        urllib.request.urlretrieve(PICS_URL, archive)
        os.makedirs(PICS)
        with tarfile.open(archive) as tar:
            for member in tar.getmembers():
                if member.isfile() and member.name.lower().endswith((".tif", ".tiff")):
                    member.name = os.path.basename(member.name)
                    tar.extract(member, PICS, filter="data")
    if os.path.isdir(PICS_TARGET):
        shutil.rmtree(PICS_TARGET)
    os.makedirs(PICS_TARGET)
    entries = []
    first = {}

    def keep(name, data):
        digest = hashlib.sha256(data).hexdigest()
        if digest not in first:
            write_gz(os.path.join(PICS_TARGET, name), data)
            first[digest] = name
        return first[digest]

    for file in sorted(os.listdir(PICS)):
        name = os.path.splitext(file)[0]
        entry = {"file": file}
        try:
            entries.append(describe(os.path.join(PICS, file), name, entry, PICS_TARGET, keep))
        except subprocess.CalledProcessError as e:
            entry["error"] = e.stderr.strip()
            entries.append(entry)
    with open(os.path.join(PICS_TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"source": PICS_URL, "tools": tiffref("version")["version"], "files": entries}, f, indent=1)
        f.write("\n")
    shutil.rmtree(WORK)
    print(f"Wrote references for {len(entries)} libtiff sample images.")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside("--pics" in sys.argv)
        return

    repo = os.getcwd()
    extra = " --pics" if "--pics" in sys.argv else ""
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-tiff-references.py --inside{extra}"],
                   check=True)


if __name__ == "__main__":
    main()
