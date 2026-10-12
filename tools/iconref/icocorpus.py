"""Builds the ICO and CUR corpus (tests/images/ico) and its references (tests/reference/ico) inside egami-iconref.

Run by tools/make-ico-references.py. Every file is made here from ops/duck.png and ops/egret.png:
  - hand-written files (struct, byte by byte): BMP entries at 1, 4, 8, 16 (5-5-5 and 5-6-5 bit fields), 24 and 32 bits, a 32-bit
    entry whose alpha is all 0 (the mask applies), a 32-bit entry whose mask is all 1 (alpha wins), odd sizes (1x1, 13x7, 33x33),
    a 256x256 BMP entry, PNG entries, sets of several sizes, a directory whose sizes disagree with the entries, cursors with
    hotspots. Their pixels are known exactly and written as <file>.<entry>.truth.rgba.gz.
  - Pillow's ICO writer (PNG entries, and BMP entries with bitmap_format="bmp") and ImageMagick's (icon:auto-resize).
  - damaged files under bad/: no entries, an offset into the directory, overlapping entries, a truncated entry, an entry that is
    neither PNG nor BMP, a missing mask.

References, per file and entry: <file>.<entry>.magick.rgba.gz (magick 'file[entry]' rgba:-) and <file>.<entry>.pillow.rgba.gz
(IcoFile.frame), 8-bit straight RGBA; manifest.json lists the entries each tool saw and the tool versions.
"""
import gzip
import io
import json
import os
import struct
import subprocess
import sys

import numpy as np
from PIL import Image, IcoImagePlugin

ROOT = "/w"
CORPUS = f"{ROOT}/tests/images/ico"
TARGET = f"{ROOT}/tests/reference/ico"


def write_gz(path, data):
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def source(name, size, alpha="soft"):
    """A square RGBA crop of an ops image resized to size x size, with a round alpha edge: soft, hard (0 or 255) or opaque."""
    im = Image.open(f"{ROOT}/tests/images/ops/{name}.png").convert("RGB")
    side = min(im.size)
    im = im.crop(((im.width - side) // 2, (im.height - side) // 2, (im.width + side) // 2, (im.height + side) // 2))
    im = im.resize(size if isinstance(size, tuple) else (size, size), Image.LANCZOS)
    rgb = np.asarray(im, dtype=np.uint8)
    h, w = rgb.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.hypot((xx + 0.5) / w - 0.5, (yy + 0.5) / h - 0.5) * 2
    if alpha == "opaque":
        a = np.full((h, w), 255, np.uint8)
    elif alpha == "hard":
        a = np.where(r < 0.9, 255, 0).astype(np.uint8)
    else:
        a = np.clip((1.0 - r) * 4 * 255, 0, 255).round().astype(np.uint8)
    return np.dstack([rgb, a])


# ---- BMP entries, written by hand --------------------------------------------------------------------------------------------

def mask_bytes(alpha, ones=False):
    """The AND mask: 1 where alpha is 0, rows bottom-up, padded to 4 bytes."""
    h, w = alpha.shape
    stride = (w + 31) // 32 * 4
    out = bytearray(stride * h)
    for fy in range(h):
        y = h - 1 - fy
        for x in range(w):
            if ones or alpha[y, x] == 0:
                out[fy * stride + (x >> 3)] |= 0x80 >> (x & 7)
    return bytes(out)


def info_header(w, h, bits, compression=0, colors_used=0, size_image=0, height_field=None):
    return struct.pack("<IiiHHIIiiII", 40, w, height_field if height_field is not None else 2 * h, 1, bits, compression, size_image, 0, 0, colors_used, 0)


def bmp_entry(px, bits, *, palette=None, alpha_zero=False, mask_ones=False, fields=None, drop_mask=False):
    """Writes px (h, w, 4) as a BMP entry and returns (bytes, the RGBA a correct reader gives)."""
    h, w = px.shape[:2]
    a = px[..., 3]
    truth = px.copy()
    rows = []
    stride = ((w * bits) + 31) // 32 * 4
    table = b""
    if bits <= 8:
        # Indices by nearest palette color; truth is the palette color, alpha from the mask.
        pal = np.array(palette, dtype=np.int32)
        flat = px[..., :3].reshape(-1, 3).astype(np.int32)
        idx = np.argmin(((flat[:, None, :] - pal[None, :, :]) ** 2).sum(-1), axis=1).reshape(h, w)
        truth[..., :3] = pal[idx].astype(np.uint8)
        table = b"".join(struct.pack("BBBB", int(c[2]), int(c[1]), int(c[0]), 0) for c in palette)
        for fy in range(h):
            y = h - 1 - fy
            row = bytearray(stride)
            per = 8 // bits
            for x in range(w):
                row[x // per] |= int(idx[y, x]) << (8 - bits * (x % per + 1))
            rows.append(bytes(row))
        truth[..., 3] = np.where(a == 0, 0, 255)
    elif bits == 16:
        rmask, gmask, bmask = fields or (0x7C00, 0x03E0, 0x001F)
        out = np.zeros((h, w), np.uint32)
        for m, c in ((rmask, 0), (gmask, 1), (bmask, 2)):
            shift = (m & -m).bit_length() - 1
            top = m >> shift
            v = (px[..., c].astype(np.uint32) * top + 127) // 255
            out |= v << shift
            truth[..., c] = ((v * 510 + top) // (2 * top)).astype(np.uint8)
        for fy in range(h):
            rows.append(out[h - 1 - fy].astype("<u2").tobytes().ljust(stride, b"\0"))
        truth[..., 3] = np.where(a == 0, 0, 255)
    elif bits == 24:
        for fy in range(h):
            rows.append(px[h - 1 - fy, :, 2::-1].tobytes().ljust(stride, b"\0"))
        truth[..., 3] = np.where(a == 0, 0, 255)
    else:
        bgra = px[..., [2, 1, 0, 3]].copy()
        if alpha_zero:
            bgra[..., 3] = 0
            truth[..., 3] = np.where(a == 0, 0, 255)
        for fy in range(h):
            rows.append(bgra[h - 1 - fy].tobytes())
    compression = 3 if fields else 0
    masks = struct.pack("<III", *fields) if fields else b""
    mask = b"" if drop_mask else mask_bytes(a, ones=mask_ones)
    data = b"".join(rows)
    header = info_header(w, h, bits, compression, len(palette) if palette is not None else 0, len(data) + len(mask))
    return header + masks + table + data + mask, truth


def png_entry(px):
    buf = io.BytesIO()
    Image.fromarray(px, "RGBA").save(buf, "PNG")
    return buf.getvalue(), px.copy()


def ico(path, entries, *, kind=1, hotspots=None, dir_sizes=None):
    """Writes a directory and the entries' data; entries are (bytes, truth). Returns the truths."""
    count = len(entries)
    offset = 6 + 16 * count
    head = struct.pack("<HHH", 0, kind, count)
    body = b""
    for i, (data, truth) in enumerate(entries):
        h, w = truth.shape[:2]
        dw, dh = dir_sizes[i] if dir_sizes else (w, h)
        bits = 32 if data[1:4] == b"PNG" else struct.unpack_from("<H", data, 14)[0]
        colors = 1 << bits if bits < 8 else 0
        if kind == 2:
            f1, f2 = hotspots[i]
        else:
            f1, f2 = 1, bits
        head += struct.pack("<BBBBHHII", dw % 256, dh % 256, colors, 0, f1, f2, len(data), offset)
        offset += len(data)
        body += data
    with open(path, "wb") as f:
        f.write(head + body)
    return [t for _, t in entries]


def palette_of(px, colors):
    q = Image.fromarray(px[..., :3], "RGB").quantize(colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    pal = q.getpalette()[: colors * 3]
    pal += [0] * (colors * 3 - len(pal))
    return [tuple(pal[i:i + 3]) for i in range(0, colors * 3, 3)]


def build():
    os.makedirs(f"{CORPUS}/bad", exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    truths = {}

    duck32 = source("duck", 32)
    duck16 = source("duck", 16, "hard")
    egret48 = source("egret", 48)

    def make(name, entries, **kw):
        truths[name] = ico(f"{CORPUS}/{name}", entries, **kw)

    make("bmp1.ico", [bmp_entry(duck16, 1, palette=[(0, 0, 0), (255, 255, 255)])])
    make("bmp4.ico", [bmp_entry(duck16, 4, palette=palette_of(duck16, 16))])
    make("bmp8.ico", [bmp_entry(duck32, 8, palette=palette_of(duck32, 256))])
    make("bmp8-short-palette.ico", [bmp_entry(duck32, 8, palette=palette_of(duck32, 40))])
    make("bmp16.ico", [bmp_entry(duck32, 16)])
    make("bmp16-565.ico", [bmp_entry(duck32, 16, fields=(0xF800, 0x07E0, 0x001F))])
    make("bmp24.ico", [bmp_entry(duck32, 24)])
    make("bmp32.ico", [bmp_entry(duck32, 32)])
    make("bmp32-zero-alpha.ico", [bmp_entry(duck32, 32, alpha_zero=True)])
    make("bmp32-mask-ones.ico", [bmp_entry(duck32, 32, mask_ones=True)])
    make("bmp32-fields.ico", [bmp_entry(source("duck", 32, "opaque"), 32, fields=(0xFF0000, 0xFF00, 0xFF))])
    make("bmp-1x1.ico", [bmp_entry(source("duck", 1, "opaque"), 24)])
    make("bmp-13x7.ico", [bmp_entry(source("egret", (13, 7)), 32), bmp_entry(source("egret", (13, 7), "hard"), 4, palette=palette_of(source("egret", (13, 7)), 16))])
    make("bmp-33.ico", [bmp_entry(source("egret", 33, "hard"), 1, palette=[(0, 0, 0), (255, 255, 255)]), bmp_entry(source("egret", 33), 32)])
    make("bmp-256.ico", [bmp_entry(source("duck", 256), 32)])
    make("png-256.ico", [png_entry(source("duck", 256))])
    make("png-odd.ico", [png_entry(source("egret", (40, 24)))])
    sizes = [16, 24, 32, 48, 64]
    make("set.ico", [bmp_entry(source("duck", s), 32) for s in sizes] + [png_entry(source("duck", 256))])
    make("set-unsorted.ico", [png_entry(source("egret", 48)), bmp_entry(source("egret", 16), 32), bmp_entry(source("egret", 32), 8, palette=palette_of(source("egret", 32), 256)), bmp_entry(source("egret", 32), 32)])
    make("set-depths.ico", [bmp_entry(source("duck", 32, "hard"), 4, palette=palette_of(source("duck", 32), 16)), bmp_entry(source("duck", 32, "hard"), 8, palette=palette_of(source("duck", 32), 256)), bmp_entry(source("duck", 32), 32)])
    make("dir-mismatch.ico", [bmp_entry(duck16, 32)], dir_sizes=[(32, 32)])
    make("cursor.cur", [bmp_entry(source("egret", 32, "hard"), 32)], kind=2, hotspots=[(5, 9)])
    make("cursor-set.cur", [bmp_entry(source("egret", 32, "hard"), 1, palette=[(0, 0, 0), (255, 255, 255)]), png_entry(source("egret", 48))], kind=2, hotspots=[(3, 4), (4, 6)])

    # Written by the reference tools.
    base = Image.fromarray(source("duck", 256), "RGBA")
    base.save(f"{CORPUS}/pillow-png.ico", sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
    base.save(f"{CORPUS}/pillow-bmp.ico", sizes=[(16, 16), (32, 32), (48, 48)], bitmap_format="bmp")
    src = "/tmp/ico-src.png"
    Image.fromarray(source("egret", 256), "RGBA").save(src)
    subprocess.run(["magick", src, "-define", "icon:auto-resize=256,64,48,32,16", f"{CORPUS}/magick-auto.ico"], check=True)
    subprocess.run(["magick", src, "-resize", "32x32", "-colors", "16", f"{CORPUS}/magick-16colors.ico"], check=True)
    subprocess.run(["magick", src, "-resize", "32x32", f"{CORPUS}/magick.cur"], check=True)

    # Damaged files.
    good, _ = bmp_entry(duck16, 32)
    def raw(name, data):
        with open(f"{CORPUS}/bad/{name}", "wb") as f:
            f.write(data)
    raw("no-entries.ico", struct.pack("<HHH", 0, 1, 0) + b"\0" * 32)
    raw("offset-in-directory.ico", struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, len(good), 10) + good)
    two = struct.pack("<HHH", 0, 1, 2)
    two += struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, len(good), 38) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, len(good), 38 + 16)
    raw("overlap.ico", two + good + b"\0" * 16)
    raw("truncated.ico", struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, len(good), 22) + good[: len(good) // 2])
    raw("not-an-image.ico", struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, 64, 22) + b"\x01" * 64)
    nomask, _ = bmp_entry(duck16, 24, drop_mask=True)
    raw("no-mask.ico", struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 24, len(nomask), 22) + nomask)
    damaged_second = struct.pack("<HHH", 0, 1, 2) + struct.pack("<BBBBHHII", 16, 16, 0, 0, 1, 32, len(good), 38) + struct.pack("<BBBBHHII", 32, 32, 0, 0, 1, 32, 64, 38 + len(good)) + good + b"\x01" * 64
    raw("one-bad-entry.ico", damaged_second)
    return truths


def references(truths):
    manifest = {"tools": {
        "imagemagick": subprocess.run(["magick", "-version"], capture_output=True, text=True).stdout.splitlines()[0],
        "pillow": Image.__version__,
    }, "files": {}}
    for sub in ("", "bad/"):
        for name in sorted(os.listdir(f"{CORPUS}/{sub}" if sub else CORPUS)):
            path = f"{CORPUS}/{sub}{name}"
            if os.path.isdir(path):
                continue
            key = sub + name
            entry = {"truth": len(truths.get(name, [])) if not sub else 0}
            stem = f"{TARGET}/{sub.replace('/', '-')}{name}"
            for i, t in enumerate(truths.get(name, []) if not sub else []):
                write_gz(f"{stem}.{i}.truth.rgba.gz", np.ascontiguousarray(t).tobytes())
            # ImageMagick, every entry it lists.
            ident = subprocess.run(["magick", "identify", "-format", "%w %h\n", path], capture_output=True, text=True)
            sizes = [tuple(map(int, l.split())) for l in ident.stdout.split("\n") if l.strip()]
            entry["magick"] = [list(s) for s in sizes]
            if ident.returncode != 0:
                entry["magickError"] = ident.stderr.strip().splitlines()[-1] if ident.stderr.strip() else "failed"
            for i in range(len(sizes)):
                out = subprocess.run(["magick", f"{path}[{i}]", "-depth", "8", "rgba:-"], capture_output=True)
                if out.returncode == 0:
                    write_gz(f"{stem}.{i}.magick.rgba.gz", out.stdout)
            # Pillow: IcoFile for icons, every entry, mapped back to directory order by offset; CurImageFile's one image for cursors.
            try:
                if name.endswith(".cur"):
                    im = Image.open(path)
                    im = im.convert("RGBA")
                    write_gz(f"{stem}.load.pillow.rgba.gz", im.tobytes())
                    entry["pillow"] = [[im.width, im.height]]
                else:
                    with open(path, "rb") as f:
                        data = f.read()
                        f.seek(0)
                        icofile = IcoImagePlugin.IcoFile(f)
                        count = struct.unpack_from("<H", data, 4)[0]
                        offsets = [struct.unpack_from("<I", data, 6 + 16 * i + 12)[0] for i in range(count)]
                        found = [None] * count
                        for k, e in enumerate(icofile.entry):
                            i = offsets.index(e.offset)
                            try:
                                im = icofile.frame(k).convert("RGBA")
                                write_gz(f"{stem}.{i}.pillow.rgba.gz", im.tobytes())
                                found[i] = [im.width, im.height]
                            except Exception as ex:
                                found[i] = str(ex)
                        entry["pillow"] = found
            except Exception as e:
                entry["pillow"] = str(e)
            manifest["files"][key] = entry
    with open(f"{TARGET}/manifest.json", "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    for d in (CORPUS, TARGET):
        if os.path.isdir(d):
            for rootdir, _, files in os.walk(d):
                for name in files:
                    os.remove(os.path.join(rootdir, name))
    references(build())
    print("ok")
