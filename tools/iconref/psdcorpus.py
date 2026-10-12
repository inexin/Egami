"""Builds the PSD and PSB corpus (tests/images/psd) and its references (tests/reference/psd) inside egami-iconref.

Run by tools/make-psd-references.py. Every file is made here from ops/duck.png, written byte by byte from Adobe's Photoshop File
Format Specification by the writer below, except the magick-*.psd files, which ImageMagick writes:
  - every color mode: bitmap, gray, indexed (with and without a transparent index), RGB, CMYK, Lab, duotone, multichannel; at
    1, 8, 16 and 32 bits where Photoshop allows them; raw, PackBits, Deflate and Deflate with prediction; PSD and PSB;
  - transparency: a merged alpha channel, flattened onto white as Photoshop stores it, with a negative layer count (in the layer
    info, or in an Lr16 or Lr32 block for 16 and 32-bit files); an extra channel that is not transparency (positive count);
  - a file without a real composite (resource 1057), metadata resources (ICC, resolution, EXIF, XMP, IPTC, a thumbnail, an
    unknown one), layer records (Unicode names, hidden, opacity, blend modes, clipping, nested groups) and 300 small layers;
  - damaged files under bad/.

References, per file: <file>.truth.raw.gz, the natural pixels a correct reader gives (the layout in manifest.json), for the files
whose values are exact; <file>.magick.rgba.gz (8-bit, or 16 for 16-bit files) and <file>.magick.f32.gz (32-bit), ImageMagick's
composite as straight RGBA; <file>.pillow.rgba.gz, Pillow's composite (8-bit files); <file>.exif.json (exiftool -j -G1 -n) for
the metadata file; <file>.layers.json (psd-tools) for files with layers. manifest.json lists every file's header and tools.
"""
import gzip
import io
import json
import os
import struct
import subprocess
import sys
import zlib

import numpy as np
from PIL import Image

ROOT = "/w"
CORPUS = f"{ROOT}/tests/images/psd"
TARGET = f"{ROOT}/tests/reference/psd"
W, H = 64, 48

MODES = {"bitmap": 0, "gray": 1, "indexed": 2, "rgb": 3, "cmyk": 4, "multichannel": 7, "duotone": 8, "lab": 9}


def write_gz(path, data):
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def be16(v):
    return struct.pack(">H", v)


def be32(v):
    return struct.pack(">I", v)


def pascal(text, pad):
    b = text.encode("latin-1")
    out = bytes([len(b)]) + b
    while len(out) % pad:
        out += b"\0"
    return out


def packbits(row):
    out = bytearray()
    i, n = 0, len(row)
    while i < n:
        run = 1
        while i + run < n and run < 128 and row[i + run] == row[i]:
            run += 1
        if run >= 3:
            out += bytes([(257 - run) & 0xFF, row[i]])
            i += run
            continue
        start = i
        i += 1
        while i < n and i - start < 128 and not (i + 2 < n and row[i] == row[i + 1] == row[i + 2]):
            i += 1
        out += bytes([i - start - 1]) + bytes(row[start:i])
    return bytes(out)


def source():
    im = Image.open(f"{ROOT}/tests/images/ops/duck.png").convert("RGB").crop((200, 120, 200 + 4 * W, 120 + 4 * H)).resize((W, H), Image.LANCZOS)
    rgb = np.asarray(im, dtype=np.uint8)
    yy, xx = np.mgrid[0:H, 0:W]
    r = np.hypot((xx + 0.5) / W - 0.5, (yy + 0.5) / H - 0.5) * 2
    alpha = np.clip((1.0 - r) * 3 * 255, 0, 255).round().astype(np.uint8)
    return rgb, alpha


RGB, ALPHA = source()
GRAY = np.asarray(Image.fromarray(RGB).convert("L"), dtype=np.uint8)


def row_bytes(plane, depth):
    """Rows of a plane as stored: packed bits, bytes, big-endian 16-bit or big-endian float."""
    if depth == 1:
        return [np.packbits(plane[y].astype(np.uint8)).tobytes() for y in range(plane.shape[0])]
    dtype = {8: ">u1", 16: ">u2", 32: ">f4"}[depth]
    return [plane[y].astype(dtype).tobytes() for y in range(plane.shape[0])]


def predict(row, depth, width):
    if depth == 8:
        a = np.frombuffer(row, np.uint8)
        return np.concatenate([a[:1], np.diff(a)]).astype(np.uint8).tobytes()
    if depth == 16:
        a = np.frombuffer(row, ">u2").astype(np.int64)
        return (np.concatenate([a[:1], np.diff(a)]) % 65536).astype(">u2").tobytes()
    a = np.frombuffer(row, np.uint8).reshape(width, 4)
    planes = np.concatenate([a[:, k] for k in range(4)])
    return (np.concatenate([planes[:1], np.diff(planes.astype(np.int64))]) % 256).astype(np.uint8).tobytes()


def image_data(planes, depth, compression, version, width):
    rows = [row_bytes(p, depth) for p in planes]
    if compression == 0:
        return be16(0) + b"".join(b"".join(r) for r in rows)
    if compression == 1:
        packed = [[packbits(r) for r in plane] for plane in rows]
        counts = b"".join((be16 if version == 1 else be32)(len(r)) for plane in packed for r in plane)
        return be16(1) + counts + b"".join(b"".join(p) for p in packed)
    data = b"".join(b"".join(predict(r, depth, width) if compression == 3 else r for r in plane) for plane in rows)
    return be16(compression) + zlib.compress(data, 9)


def resource(rid, data, name=""):
    out = b"8BIM" + be16(rid) + pascal(name, 2) + be32(len(data)) + data
    return out + (b"\0" if len(data) % 2 else b"")


def version_info(real):
    writer = "Egami tests".encode("utf-16-be")
    return be32(1) + bytes([1 if real else 0]) + be32(len(writer) // 2) + writer + be32(len(writer) // 2) + writer + be32(1)


def layer_record(name, bounds, channels, *, blend=b"norm", opacity=255, clipping=0, hidden=False, extra_blocks=b"", version=1, unicode=None, section=None):
    top, left, bottom, right = bounds
    out = struct.pack(">iiii", top, left, bottom, right) + be16(len(channels))
    for cid, data in channels:
        out += struct.pack(">h", cid) + (be32 if version == 1 else lambda v: struct.pack(">Q", v))(len(data))
    flags = 0x02 if hidden else 0
    out += b"8BIM" + blend + bytes([opacity, clipping, flags, 0])
    blocks = extra_blocks
    if unicode is not None:
        u = unicode.encode("utf-16-be")
        blocks += b"8BIMluni" + be32(4 + len(u) + (len(u) % 4 and 4 - len(u) % 4)) + be32(len(u) // 2) + u + b"\0" * (len(u) % 4 and 4 - len(u) % 4)
    if section is not None:
        blocks += b"8BIMlsct" + be32(4) + be32(section)
    extra = be32(0) + be32(0) + pascal(name, 4) + blocks
    return out + be32(len(extra)) + extra


def plain_layer(name, planes_by_id, depth, version, **kw):
    """A layer covering the image, its channels raw."""
    channels = []
    for cid, plane in planes_by_id:
        data = be16(0) + b"".join(row_bytes(plane, depth))
        channels.append((cid, data))
    h, w = planes_by_id[0][1].shape
    return layer_record(name, (0, 0, h, w), channels, version=version, **kw), b"".join(d for _, d in channels)


def layer_info(records, count, version):
    body = struct.pack(">h", count) + b"".join(r for r, _ in records) + b"".join(d for _, d in records)
    if len(body) % 2:
        body += b"\0"
    return body


def length(v, version):
    return be32(v) if version == 1 else struct.pack(">Q", v)


def psd(path, mode, depth, planes, *, compression=1, version=1, resources=b"", color_data=b"", layers=None, count=None, layers_in_block=None, real=True):
    """Writes a file. planes are the composite's channels as stored; layers is a list of (record, data); count is the signed
    layer count (negative: the first extra channel is the merged transparency)."""
    channels = len(planes)
    h, w = planes[0].shape
    out = b"8BPS" + be16(version) + b"\0" * 6 + be16(channels) + be32(h) + be32(w) + be16(depth) + be16(MODES[mode])
    out += be32(len(color_data)) + color_data
    resources = resource(1057, version_info(real)) + resources
    out += be32(len(resources)) + resources
    if layers is None:
        out += length(0, version)
    else:
        info = layer_info(layers, len(layers) if count is None else count, version)
        if layers_in_block:
            key = layers_in_block
            block = b"8BIM" + key + length(len(info), version) + info
            section = length(0, version) + be32(0) + block
        else:
            section = length(len(info), version) + info + be32(0)
        out += length(len(section), version) + section
    out += image_data(planes, depth, compression, version, w)
    with open(path, "wb") as f:
        f.write(out)


def matte(color, alpha, white=255):
    """Photoshop's composite of a transparent document: each color flattened onto white."""
    a = alpha.astype(np.float64) / 255
    return np.round(color.astype(np.float64) * a + white * (1 - a)).astype(np.int64)


def unmatte(stored, alpha, white, full):
    """What a reader divides out: (full·s − (full − a)·white) / a, rounded half up in exact integers and clamped; 0 where alpha
    is 0, the stored value where alpha is full."""
    s = stored.astype(np.int64)
    a = alpha.astype(np.int64)
    num = full * s - (full - a) * white
    safe = np.where(a > 0, a, 1)
    c = np.floor_divide(2 * num + safe, 2 * safe)
    c = np.where(a == 0, 0, np.where(a == full, s, c))
    return np.clip(c, 0, full)


def unmatte8(stored, alpha, white=255):
    return unmatte(stored, alpha, white, 255).astype(np.uint8)


def unmatte16(stored, alpha):
    return unmatte(stored, alpha, 65535, 65535).astype(np.uint16)


def lab_to_srgb(l, a, b):
    """Photoshop Lab (L 0 to 100, a and b about -128 to 127, relative to D50) to gamma-encoded sRGB 0 to 1, adapted to D65 by
    Bradford, as Egami's TIFF and PSD readers do."""
    def xyz(x, y):
        return np.array([x / y, 1.0, (1 - x - y) / y])
    d50, d65 = xyz(0.3457, 0.3585), xyz(0.3127, 0.3290)
    fy = (l + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200
    g = lambda f: np.where(f > 6 / 29, f ** 3, (116 * f - 16) / (24389 / 27))
    X = np.stack([d50[0] * g(fx), d50[1] * g(fy), d50[2] * g(fz)], -1)
    brad = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])
    fr, to = brad @ d50, brad @ d65
    adapt = np.linalg.inv(brad) @ np.diag(to / fr) @ brad
    prim = np.array([xyz(0.64, 0.33), xyz(0.30, 0.60), xyz(0.15, 0.06)]).T
    scale = np.linalg.solve(prim, d65)
    rgb_to_xyz = prim * scale
    m = np.linalg.inv(rgb_to_xyz) @ adapt
    lin = np.clip(X @ m.T, 0, 1)
    return np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)


def unorm(v, full):
    return np.floor(np.clip(v, 0, 1) * full + 0.5).astype(np.int64)


def build():
    os.makedirs(f"{CORPUS}/bad", exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    truth = {}
    r, g, b = (RGB[..., i].astype(np.int64) for i in range(3))
    a8 = ALPHA.astype(np.int64)
    gray = GRAY.astype(np.int64)
    w16 = lambda p: np.minimum(p * 257 + (p >> 3), 65535)  # spread 8-bit values to 16 bits, not all multiples of 257
    f32 = lambda p: (p.astype(np.float32) / 255) ** np.float32(2.2)

    def put(name, layout, pixels):
        truth[name] = (layout, np.ascontiguousarray(pixels))

    # Gray, every compression and depth.
    for comp, tag in ((0, "raw"), (1, "rle"), (2, "zip"), (3, "zipp")):
        psd(f"{CORPUS}/gray8-{tag}.psd", "gray", 8, [gray], compression=comp)
        put(f"gray8-{tag}.psd", "Gray8", gray.astype(np.uint8))
    for comp, tag in ((1, "rle"), (3, "zipp"), (2, "zip")):
        psd(f"{CORPUS}/gray16-{tag}.psd", "gray", 16, [w16(gray)], compression=comp)
        put(f"gray16-{tag}.psd", "Gray16", w16(gray).astype("<u2"))
    for comp, tag in ((0, "raw"), (3, "zipp")):
        psd(f"{CORPUS}/gray32-{tag}.psd", "gray", 32, [f32(gray)], compression=comp)
        put(f"gray32-{tag}.psd", "GrayF32", f32(gray).astype("<f4"))

    # Gray with merged transparency.
    stored = matte(gray, a8)
    lay = plain_layer("Layer 1", [(0, gray), (-1, a8)], 8, 1)
    psd(f"{CORPUS}/gray8-alpha.psd", "gray", 8, [stored, a8], layers=[lay], count=-1)
    put("gray8-alpha.psd", "GrayAlpha8", np.dstack([unmatte8(stored, a8), a8]).astype(np.uint8))

    # Bitmap: 1 is black.
    bits = (gray < 128).astype(np.uint8)
    for comp, tag in ((0, "raw"), (1, "rle")):
        psd(f"{CORPUS}/bitmap-{tag}.psd", "bitmap", 1, [bits], compression=comp)
        put(f"bitmap-{tag}.psd", "Gray8", np.where(bits == 1, 0, 255).astype(np.uint8))

    # Indexed.
    q = Image.fromarray(RGB).quantize(200, dither=Image.Dither.NONE)
    pal = np.array(q.getpalette()[:768] + [0] * (768 - len(q.getpalette()[:768])), np.uint8).reshape(256, 3)
    idx = np.asarray(q, dtype=np.int64)
    table = pal[:, 0].tobytes() + pal[:, 1].tobytes() + pal[:, 2].tobytes()
    psd(f"{CORPUS}/indexed.psd", "indexed", 8, [idx], color_data=table)
    put("indexed.psd", "Rgb8", pal[idx])
    clear = int(idx[0, 0])
    psd(f"{CORPUS}/indexed-transparent.psd", "indexed", 8, [idx], color_data=table, resources=resource(1047, be16(clear)))
    put("indexed-transparent.psd", "Rgba8", np.dstack([pal[idx], np.where(idx == clear, 0, 255)]).astype(np.uint8))

    # RGB, every compression; transparency; an extra channel that is not transparency.
    for comp, tag in ((0, "raw"), (1, "rle"), (2, "zip"), (3, "zipp")):
        psd(f"{CORPUS}/rgb8-{tag}.psd", "rgb", 8, [r, g, b], compression=comp)
        put(f"rgb8-{tag}.psd", "Rgb8", RGB)
    stored = [matte(c, a8) for c in (r, g, b)]
    lay = plain_layer("Layer 1", [(0, r), (1, g), (2, b), (-1, a8)], 8, 1)
    psd(f"{CORPUS}/rgb8-alpha.psd", "rgb", 8, stored + [a8], layers=[lay], count=-1)
    put("rgb8-alpha.psd", "Rgba8", np.dstack([unmatte8(s, a8) for s in stored] + [a8]).astype(np.uint8))
    names = pascal("Spot mask", 1)
    psd(f"{CORPUS}/rgb8-extra.psd", "rgb", 8, [r, g, b, a8], resources=resource(1006, names))
    put("rgb8-extra.psd", "Rgb8", RGB)

    for comp, tag in ((1, "rle"), (3, "zipp")):
        psd(f"{CORPUS}/rgb16-{tag}.psd", "rgb", 16, [w16(r), w16(g), w16(b)], compression=comp)
        put(f"rgb16-{tag}.psd", "Rgb16", np.dstack([w16(r), w16(g), w16(b)]).astype("<u2"))
    a16 = w16(a8)
    stored16 = [np.round(w16(c).astype(np.float64) * a16 / 65535 + 65535 * (1 - a16 / 65535)).astype(np.int64) for c in (r, g, b)]
    lay = plain_layer("Layer 1", [(0, w16(r)), (1, w16(g)), (2, w16(b)), (-1, a16)], 16, 1)
    psd(f"{CORPUS}/rgb16-alpha.psd", "rgb", 16, stored16 + [a16], layers=[lay], count=-1, layers_in_block=b"Lr16")
    put("rgb16-alpha.psd", "Rgba16", np.dstack([unmatte16(s, a16) for s in stored16] + [a16]).astype("<u2"))

    for comp, tag in ((0, "raw"), (3, "zipp")):
        psd(f"{CORPUS}/rgb32-{tag}.psd", "rgb", 32, [f32(r), f32(g), f32(b)], compression=comp)
        put(f"rgb32-{tag}.psd", "RgbF32", np.dstack([f32(r), f32(g), f32(b)]).astype("<f4"))
    af = (a8.astype(np.float32) / 255)
    stored32 = [(f32(c) * af + (1 - af)).astype(np.float32) for c in (r, g, b)]
    lay = plain_layer("Layer 1", [(0, f32(r)), (1, f32(g)), (2, f32(b)), (-1, af)], 32, 1)
    psd(f"{CORPUS}/rgb32-alpha.psd", "rgb", 32, stored32 + [af], layers=[lay], count=-1, layers_in_block=b"Lr32")

    # CMYK: stored inverted, 0 is full ink.
    rgbf = RGB.astype(np.float64) / 255
    k = 1 - rgbf.max(axis=2)
    with np.errstate(divide="ignore", invalid="ignore"):
        cmy = np.where(k[..., None] < 1, (1 - rgbf - k[..., None]) / (1 - k[..., None]), 0)
    ink8 = [np.round(c * 255).astype(np.int64) for c in (cmy[..., 0], cmy[..., 1], cmy[..., 2], k)]
    psd(f"{CORPUS}/cmyk8.psd", "cmyk", 8, [255 - c for c in ink8])
    put("cmyk8.psd", "Cmyk8", np.dstack(ink8).astype(np.uint8))
    ink16 = [w16(c) for c in ink8]
    psd(f"{CORPUS}/cmyk16.psd", "cmyk", 16, [65535 - c for c in ink16])
    put("cmyk16.psd", "Cmyk16", np.dstack(ink16).astype("<u2"))
    stored = [matte(255 - c, a8) for c in ink8]
    lay = plain_layer("Layer 1", [(0, 255 - ink8[0]), (1, 255 - ink8[1]), (2, 255 - ink8[2]), (3, 255 - ink8[3]), (-1, a8)], 8, 1)
    psd(f"{CORPUS}/cmyk8-alpha.psd", "cmyk", 8, stored + [a8], layers=[lay], count=-1)
    plain = [unmatte(st, a8, 255, 255) for st in stored]
    put("cmyk8-alpha.psd", "Rgba8", np.dstack([(plain[i] * plain[3] + 127) // 255 for i in range(3)] + [a8]).astype(np.uint8))

    # Lab: L 0 to 100 over 0 to 255, a and b offset by 128 (or 32768).
    lab = np.asarray(Image.fromarray(RGB).convert("LAB"), dtype=np.int64)
    psd(f"{CORPUS}/lab8.psd", "lab", 8, [lab[..., 0], lab[..., 1], lab[..., 2]])
    put("lab8.psd", "Rgb8", unorm(lab_to_srgb(lab[..., 0] * 100 / 255, lab[..., 1] - 128.0, lab[..., 2] - 128.0), 255).astype(np.uint8))
    psd(f"{CORPUS}/lab16.psd", "lab", 16, [w16(lab[..., 0]), lab[..., 1] * 256, lab[..., 2] * 256])
    put("lab16.psd", "Rgb16", unorm(lab_to_srgb(w16(lab[..., 0]) * 100 / 65535, (lab[..., 1] * 256 - 32768) / 256, (lab[..., 2] * 256 - 32768) / 256), 65535).astype("<u2"))
    stored = [matte(lab[..., 0], a8), matte(lab[..., 1], a8, 128), matte(lab[..., 2], a8, 128)]
    lay = plain_layer("Layer 1", [(0, lab[..., 0]), (1, lab[..., 1]), (2, lab[..., 2]), (-1, a8)], 8, 1)
    psd(f"{CORPUS}/lab8-alpha.psd", "lab", 8, stored + [a8], layers=[lay], count=-1)
    ul, ua, ub = unmatte(stored[0], a8, 255, 255), unmatte(stored[1], a8, 128, 255), unmatte(stored[2], a8, 128, 255)
    put("lab8-alpha.psd", "Rgba8", np.dstack([unorm(lab_to_srgb(ul * 100 / 255, ua - 128.0, ub - 128.0), 255), a8]).astype(np.uint8))

    # Duotone and multichannel.
    duo = b"\0\0\0\x02" + b"\0" * 28
    psd(f"{CORPUS}/duotone8.psd", "duotone", 8, [gray], color_data=duo)
    put("duotone8.psd", "Gray8", gray.astype(np.uint8))
    psd(f"{CORPUS}/multichannel8.psd", "multichannel", 8, [r, g, b], resources=resource(1006, pascal("Cyan", 1) + pascal("Magenta", 1) + pascal("Yellow", 1)))
    put("multichannel8.psd", "Gray8", RGB[..., 0])
    psd(f"{CORPUS}/multichannel16.psd", "multichannel", 16, [w16(r), w16(g)])
    put("multichannel16.psd", "Gray16", w16(r).astype("<u2"))

    # PSB.
    psd(f"{CORPUS}/rgb8-rle.psb", "rgb", 8, [r, g, b], version=2)
    put("rgb8-rle.psb", "Rgb8", RGB)
    lay = plain_layer("Layer 1", [(0, w16(gray)), (-1, a16)], 16, 2)
    stored = np.round(w16(gray).astype(np.float64) * a16 / 65535 + 65535 * (1 - a16 / 65535)).astype(np.int64)
    psd(f"{CORPUS}/gray16-alpha.psb", "gray", 16, [stored, a16], version=2, layers=[lay], count=-1, layers_in_block=b"Lr16")
    put("gray16-alpha.psb", "GrayAlpha16", np.dstack([unmatte16(stored, a16), a16]).astype("<u2"))

    # No real composite: a white placeholder.
    white = np.full((H, W), 255, np.int64)
    lay = plain_layer("Layer 1", [(0, r), (1, g), (2, b)], 8, 1)
    psd(f"{CORPUS}/no-composite.psd", "rgb", 8, [white, white, white], layers=[lay], real=False)
    put("no-composite.psd", "Rgb8", np.full((H, W, 3), 255, np.uint8))

    # Metadata.
    icc = open(f"{ROOT}/tests/images/icc/compact/DisplayP3-v2-magic.icc", "rb").read()
    exif = Image.Exif()
    exif[0x0110] = "Egami test camera"
    exif[0x0112] = 6
    exif_bytes = exif.tobytes()
    exif_bytes = exif_bytes[6:] if exif_bytes.startswith(b"Exif\0\0") else exif_bytes
    xmp = b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:format>image/vnd.adobe.photoshop</dc:format></rdf:Description></rdf:RDF></x:xmpmeta>'
    iptc = b"\x1c\x02\x00\x00\x02\x00\x04" + b"\x1c\x02\x05" + be16(10) + b"Egami test" + b"\x1c\x02\x78" + be16(7) + b"Caption"
    thumb = io.BytesIO()
    Image.fromarray(RGB).resize((16, 12)).save(thumb, "JPEG")
    jpeg = thumb.getvalue()
    thumbnail = be32(1) + be32(16) + be32(12) + be32(48) + be32(48 * 12) + be32(len(jpeg)) + be16(24) + be16(1) + jpeg
    res = struct.pack(">IHHIHH", 300 << 16, 1, 1, 150 << 16 | 0x8000, 1, 1)
    meta = resource(1005, res) + resource(1039, icc) + resource(1058, exif_bytes) + resource(1060, xmp) + resource(1028, iptc) + resource(1036, thumbnail) + resource(9999, b"private", "Mine")
    psd(f"{CORPUS}/metadata.psd", "rgb", 8, [r, g, b], resources=meta)
    put("metadata.psd", "Rgb8", RGB)

    # Layers: names, Unicode names, visibility, opacity, blend modes, clipping, nested groups (bottom to top in the file).
    def small(name, v, **kw):
        channels = [(c, be16(0) + bytes([v]) * 24) for c in (0, 1, 2)]
        return layer_record(name, (2, 3, 6, 9), channels, **kw), b"".join(d for _, d in channels)

    def group(name, section, **kw):
        return layer_record(name, (0, 0, 0, 0), [], section=section, **kw), b""

    records = [
        small("Background", 10),
        group("</Layer group>", 3),
        small("Inner", 20, blend=b"mul ", opacity=128),
        group("</Layer group>", 3),
        small("Deep", 30, hidden=True, unicode="Dyp æøå 深"),
        small("Clipped", 40, clipping=1, blend=b"scrn"),
        group("Nested", 2),
        group("Group", 1, blend=b"pass"),
        small("Top", 50, blend=b"diff", unicode="Top — ü"),
    ]
    psd(f"{CORPUS}/layers.psd", "rgb", 8, [r, g, b], layers=records, count=len(records))
    put("layers.psd", "Rgb8", RGB)

    many = [small(f"Layer {i}", i % 256) for i in range(300)]
    psd(f"{CORPUS}/many-layers.psd", "rgb", 8, [r, g, b], layers=many, count=len(many))
    put("many-layers.psd", "Rgb8", RGB)

    # ImageMagick's own files.
    src = "/tmp/psd-src.png"
    Image.fromarray(np.dstack([RGB, ALPHA])).save(src)
    for name, args in (("magick-rgb.psd", ["-alpha", "off"]), ("magick-rgba.psd", []), ("magick-gray.psd", ["-alpha", "off", "-colorspace", "Gray"]),
                       ("magick-cmyk.psd", ["-alpha", "off", "-colorspace", "CMYK"]), ("magick-rgb16.psd", ["-alpha", "off", "-depth", "16"]),
                       ("magick-rgba16.psd", ["-depth", "16"]), ("magick-none.psd", ["-alpha", "off", "-compress", "None"]),
                       ("magick-zip.psd", ["-alpha", "off", "-compress", "Zip"])):
        subprocess.run(["magick", src] + args + [f"{CORPUS}/{name}"], check=True)

    bad(r, g, b)
    return truth


def bad(r, g, b):
    good = f"{CORPUS}/rgb8-rle.psd"
    data = open(good, "rb").read()
    def write(name, blob):
        with open(f"{CORPUS}/bad/{name}", "wb") as f:
            f.write(blob)
    write("signature.psd", b"8BPX" + data[4:])
    write("version3.psd", data[:4] + be16(3) + data[6:])
    write("no-channels.psd", data[:12] + be16(0) + data[14:])
    write("depth7.psd", data[:22] + be16(7) + data[24:])
    write("mode5.psd", data[:24] + be16(5) + data[26:])
    write("truncated.psd", data[: len(data) * 2 // 3])
    write("resources-too-long.psd", data[:30] + be32(0x7FFFFFF0) + data[34:])
    # PackBits row counts that point past the end.
    write("rle-counts.psd", huge_counts(data))
    write("huge.psd", data[:14] + be32(300000) + be32(300000) + data[22:])


def huge_counts(data):
    """Sets the first row's PackBits count to 65535."""
    # header 26, color data length 4 + 0, resources length + resources, layer section length 4 + 0, compression 2.
    pos = 26
    pos += 4 + struct.unpack_from(">I", data, pos)[0]
    pos += 4 + struct.unpack_from(">I", data, pos)[0]
    pos += 4 + struct.unpack_from(">I", data, pos)[0]
    pos += 2
    return data[:pos] + be16(65535) + data[pos + 2:]


def references(truth):
    manifest = {"tools": {
        "imagemagick": subprocess.run(["magick", "-version"], capture_output=True, text=True).stdout.splitlines()[0],
        "pillow": Image.__version__,
        "exiftool": subprocess.run(["exiftool", "-ver"], capture_output=True, text=True).stdout.strip(),
    }, "files": {}}
    for sub in ("", "bad/"):
        folder = f"{CORPUS}/{sub}" if sub else CORPUS
        for name in sorted(os.listdir(folder)):
            path = folder + "/" + name if sub else f"{CORPUS}/{name}"
            if os.path.isdir(path):
                continue
            key = sub + name
            stem = f"{TARGET}/{sub.replace('/', '-')}{name}"
            entry = {}
            data = open(path, "rb").read()
            if len(data) >= 26 and data[:4] == b"8BPS":
                entry["header"] = dict(zip(("version", "channels", "height", "width", "depth", "mode"), struct.unpack_from(">H6xHIIHH", data, 4)))
            if name in truth:
                layout, pixels = truth[name]
                entry["truth"] = layout
                if name.startswith("lab"):
                    entry["truthTolerance"] = 1
                write_gz(f"{stem}.truth.raw.gz", pixels.tobytes())
            depth = entry.get("header", {}).get("depth", 8)
            out = subprocess.run(["magick", f"{path}[0]", "-alpha", "on", "-depth", "32" if depth == 32 else "16" if depth == 16 else "8",
                                  *(["-define", "quantum:format=floating-point"] if depth == 32 else []), "rgba:-"], capture_output=True)
            if out.returncode == 0:
                write_gz(f"{stem}.magick.{'f32' if depth == 32 else 'rgba16' if depth == 16 else 'rgba'}.gz", out.stdout)
            else:
                entry["magickError"] = out.stderr.decode(errors="replace").strip().splitlines()[-1] if out.stderr else "failed"
            if entry.get("header", {}).get("mode") == 4:
                out = subprocess.run(["magick", f"{path}[0]", "-alpha", "off", "-depth", "16" if depth == 16 else "8", "cmyk:-"], capture_output=True)
                if out.returncode == 0:
                    write_gz(f"{stem}.magick.cmyk.gz", out.stdout)
            try:
                with Image.open(path) as im:
                    entry["pillowMode"] = im.mode
                    write_gz(f"{stem}.pillow.rgba.gz", im.convert("RGBA").tobytes())
            except Exception as e:
                entry["pillowError"] = str(e)
            if name == "metadata.psd":
                meta = subprocess.run(["exiftool", "-j", "-G1", "-n", "-b", path], capture_output=True, text=True).stdout
                with open(f"{stem}.exif.json", "w") as f:
                    f.write(meta)
            if not sub and name.endswith((".psd", ".psb")):
                try:
                    from psd_tools import PSDImage
                    doc = PSDImage.open(path)
                    layers = []
                    for layer in doc.descendants():
                        layers.append({"name": layer.name, "kind": layer.kind, "visible": layer.visible, "opacity": layer.opacity,
                                       "blend": str(layer.blend_mode), "clipping": layer.clipping_layer, "bbox": list(layer.bbox),
                                       "depth": depth_of(layer)})
                    if layers:
                        with open(f"{stem}.layers.json", "w") as f:
                            json.dump(layers, f, indent=1, ensure_ascii=False)
                            f.write("\n")
                except Exception as e:
                    entry["psdToolsError"] = str(e)
            manifest["files"][key] = entry
    with open(f"{TARGET}/manifest.json", "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")


def depth_of(layer):
    d = 0
    p = layer.parent
    while p is not None and getattr(p, "parent", None) is not None:
        d += 1
        p = p.parent
    return d


if __name__ == "__main__":
    for d in (CORPUS, TARGET):
        if os.path.isdir(d):
            for rootdir, _, files in os.walk(d):
                for name in files:
                    os.remove(os.path.join(rootdir, name))
    references(build())
    print("ok")
