"""Builds the BMP, TGA, QOI and PNM corpus in tests/images/{bmp,tga,qoi,pnm} and raw references in tests/reference/simple, with
Pillow, ImageMagick and the Netpbm tools in Docker (and qoiconv, built from qoi.h, when .cache/qoi/qoi.h is there).

Corpus (licenses in tests/images/LICENSES), made from the 1.2 public-domain photos (tests/images/ops/duck.png and egret.png),
reduced, or from generated shapes:
  BMP, by Pillow and ImageMagick: 24-bit, 32-bit with alpha, 8, 4 and 1-bit palettes, RLE8 and RLE4, 16-bit 5-6-5,
    1-5-5-5 with alpha, V3, V4 and V5 headers, OS/2 1.x (BMP2).
  BMP, by hand: 16-bit 5-5-5 (BI_RGB), top-down, 2-bit palette, RLE8 and RLE4 with delta and end-of-line escapes, RLE24 and 8-bit with an OS/2 2.x
    header, 32-bit BI_RGB whose fourth byte is zero everywhere (loads opaque) and with real alpha, BI_ALPHABITFIELDS, embedded
    PNG and JPEG, and an index past the palette.
  TGA, by Pillow and ImageMagick: 24 and 32-bit, raw and RLE, gray raw and RLE, palette raw and RLE.
  TGA, by hand: all four origins, 15 and 16-bit color with and without the attribute bit, a 15-bit color map with a first-entry
    offset, 16-bit gray with alpha, and TGA 2.0 footers with an extension area (author, comment, alpha types 0, 3 and 4).
  QOI, by ImageMagick (and qoiconv): 3 and 4 channels; by hand: the linear colorspace byte and a long run.
  PNM, by Pillow, ImageMagick and Netpbm: P1 to P6 plain and binary, 16-bit P5 and P6, odd maxvals, PAM with and without
    alpha; by hand: P1 with no spaces between digits, comments, a PAM with an unknown tuple type, and two images in one file.

References (tests/reference/simple):
  <file>.<tool>.rgba.gz     the decoded pixels as 8-bit RGBA, row by row, gzipped; tool is pillow, magick, netpbm, qoiconv, or
                            expected for hand-made files whose pixels the script knows
  manifest.json             per file: size, and which references exist
Where tools disagree (they may on BMP alpha and TGA origins), the format's specification decides, and the decoder tests say which
reference they follow.

Usage, from the repo root with Docker running:
  python tools/make-simple-references.py
The script runs alpine:3.20 and installs Pillow, ImageMagick and Netpbm inside it with apk.
"""
import gzip
import io
import json
import os
import struct
import subprocess
import sys
import zlib

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick imagemagick-jpeg netpbm gcc musl-dev"
TARGET = "tests/reference/simple"
OPS = "tests/images/ops"
FOLDERS = {"bmp": "tests/images/bmp", "tga": "tests/images/tga", "qoi": "tests/images/qoi", "pnm": "tests/images/pnm"}

expected = {}
embedded = {}  # Files whose pixels are a JPEG inside them: the Pillow reference decodes that JPEG.


def run(*args, **kwargs):
    return subprocess.run(list(args), check=True, **kwargs)


def photo(name="duck.png", size=(64, 48), mode="RGB"):
    from PIL import Image
    return Image.open(os.path.join(OPS, name)).convert("RGBA").resize(size, Image.LANCZOS).convert(mode)


def with_alpha(size=(64, 48)):
    """The photo with an alpha ramp and a fully transparent corner."""
    im = photo(size=size, mode="RGBA")
    px = im.load()
    for y in range(size[1]):
        for x in range(size[0]):
            r, g, b, _ = px[x, y]
            a = 0 if x < 8 and y < 8 else min(255, 40 + x * 4)
            px[x, y] = (r, g, b, a)
    return im


def path(kind, name):
    return os.path.join(FOLDERS[kind], name)


def save_expected(kind, name, data, width, height, rgba):
    with open(path(kind, name), "wb") as f:
        f.write(data)
    expected[f"{kind}/{name}"] = (width, height, bytes(rgba))


# ---------------------------------------------------------------- BMP

def bmp_file(header, palette, pixels, offset_extra=b""):
    offset = 14 + len(header) + len(palette) + len(offset_extra)
    size = offset + len(pixels)
    return b"BM" + struct.pack("<IHHI", size, 0, 0, offset) + header + palette + offset_extra + pixels


def info_header(width, height, bits, compression=0, image_size=0, colors=0, size=40, masks=b""):
    return struct.pack("<IiiHHIIiiII", size, width, height, 1, bits, compression, image_size, 2835, 2835, colors, 0) + masks


def rows_bottom_up(rows):
    return b"".join(reversed(rows))


def pad4(row):
    return row + b"\0" * ((4 - len(row) % 4) % 4)


def handmade_bmps():
    from PIL import Image
    w, h = 13, 7
    src = photo(size=(w, h))
    px = [src.getpixel((x, y)) for y in range(h) for x in range(w)]
    rgba = b"".join(bytes(p) + b"\xff" for p in px)

    # 24-bit top-down (negative height).
    rows = [pad4(b"".join(bytes((px[y * w + x][2], px[y * w + x][1], px[y * w + x][0])) for x in range(w))) for y in range(h)]
    save_expected("bmp", "top-down-24.bmp", bmp_file(info_header(w, -h, 24), b"", b"".join(rows)), w, h, rgba)

    # OS/2 1.x core header (12 bytes) with 24-bit pixels.
    core = struct.pack("<IHHHH", 12, w, h, 1, 24)
    save_expected("bmp", "os2-core-24.bmp", bmp_file(core, b"", rows_bottom_up(rows)), w, h, rgba)

    # 2-bit palette (Windows CE).
    pal = [(0, 0, 0), (255, 0, 0), (0, 255, 0), (255, 255, 255)]
    idx = [[(x + y) % 4 for x in range(w)] for y in range(h)]
    prows = []
    for y in range(h):
        bits = 0
        out = bytearray()
        for x in range(w):
            bits = (bits << 2) | idx[y][x]
            if x % 4 == 3:
                out.append(bits)
                bits = 0
        if w % 4:
            out.append(bits << (2 * (4 - w % 4)))
        prows.append(pad4(bytes(out)))
    palette = b"".join(bytes((b, g, r, 0)) for r, g, b in pal)
    want = b"".join(bytes(pal[idx[y][x]]) + b"\xff" for y in range(h) for x in range(w))
    save_expected("bmp", "pal2.bmp", bmp_file(info_header(w, h, 2, colors=4), palette, rows_bottom_up(prows)), w, h, want)

    # 8-bit with an index past the palette: drawn black.
    pal8 = [(10 * i, 255 - 10 * i, 128) for i in range(16)]
    idx8 = [[(x * 3 + y) % 20 for x in range(w)] for y in range(h)]
    rows8 = [pad4(bytes(idx8[y])) for y in range(h)]
    palette8 = b"".join(bytes((b, g, r, 0)) for r, g, b in pal8)
    want8 = b"".join((bytes(pal8[i]) if i < 16 else b"\0\0\0") + b"\xff" for y in range(h) for i in idx8[y])
    save_expected("bmp", "pal8-index-past-palette.bmp", bmp_file(info_header(w, h, 8, colors=16), palette8, rows_bottom_up(rows8)), w, h, want8)

    # RLE8 with an encoded run, a literal run, end of line, a delta and end of bitmap; skipped pixels are black.
    rw, rh = 10, 4
    pal_rle = [(0, 0, 0), (200, 10, 10), (10, 200, 10), (10, 10, 200), (250, 250, 0)]
    grid = [[0] * rw for _ in range(rh)]
    data = bytearray()
    # Row 0 (bottom): run of 4 index 1, literal 2 3 4, end of line.
    data += bytes((4, 1, 0, 3, 2, 3, 4, 0, 0, 0))
    for x in range(4):
        grid[rh - 1][x] = 1
    grid[rh - 1][4], grid[rh - 1][5], grid[rh - 1][6] = 2, 3, 4
    # Row 1: run of 10 index 2, end of line.
    data += bytes((10, 2, 0, 0))
    for x in range(10):
        grid[rh - 2][x] = 2
    # Row 2: delta right 3, up 1 -> row 3 at x 3; run of 2 index 4; end of bitmap.
    data += bytes((0, 2, 3, 1, 2, 4, 0, 1))
    grid[0][3] = grid[0][4] = 4
    want_rle = b"".join(bytes(pal_rle[grid[y][x]]) + b"\xff" for y in range(rh) for x in range(rw))
    palette_rle = b"".join(bytes((b, g, r, 0)) for r, g, b in pal_rle)
    save_expected("bmp", "rle8-delta.bmp", bmp_file(info_header(rw, rh, 8, compression=1, image_size=len(data), colors=5), palette_rle, bytes(data)), rw, rh, want_rle)

    # RLE4: runs alternate two indices; literal runs pack two pixels per byte.
    data4 = bytearray()
    grid4 = [[0] * rw for _ in range(rh)]
    data4 += bytes((6, 0x12))          # 1 2 1 2 1 2
    for x in range(6):
        grid4[rh - 1][x] = 1 if x % 2 == 0 else 2
    data4 += bytes((0, 3, 0x34, 0x40))  # literal of three pixels, 3 4 4, in two bytes (the low nibble of the last is unused)
    grid4[rh - 1][6], grid4[rh - 1][7], grid4[rh - 1][8] = 3, 4, 4
    data4 += bytes((0, 0))             # end of line
    data4 += bytes((10, 0x33, 0, 0))   # row 1: ten pixels of 3, end of line
    for x in range(10):
        grid4[rh - 2][x] = 3
    data4 += bytes((0, 1))             # end of bitmap; rows 2 and 3 stay black
    want4 = b"".join(bytes(pal_rle[grid4[y][x]]) + b"\xff" for y in range(rh) for x in range(rw))
    save_expected("bmp", "rle4-eol.bmp", bmp_file(info_header(rw, rh, 4, compression=2, image_size=len(data4), colors=5), palette_rle, bytes(data4)), rw, rh, want4)

    # OS/2 2.x header (64 bytes) with RLE24 (compression 4 in OS/2).
    os2 = struct.pack("<IiiHHIIiiII", 64, rw, rh, 1, 24, 4, 0, 2835, 2835, 0, 0) + b"\0" * 24
    data24 = bytearray()
    colors = [(200, 10, 10), (10, 200, 10), (10, 10, 200)]
    grid24 = [[(0, 0, 0)] * rw for _ in range(rh)]
    for y in range(rh):
        r, g, b = colors[y % 3]
        data24 += bytes((rw, b, g, r, 0, 0))
        grid24[rh - 1 - y] = [colors[y % 3]] * rw
    data24 += bytes((0, 1))
    want24 = b"".join(bytes(grid24[y][x]) + b"\xff" for y in range(rh) for x in range(rw))
    save_expected("bmp", "os2-rle24.bmp", bmp_file(os2, b"", bytes(data24)), rw, rh, want24)

    # OS/2 2.x header with an 8-bit palette (4 bytes per entry).
    os2p = struct.pack("<IiiHHIIiiII", 64, w, h, 1, 8, 0, 0, 2835, 2835, 16, 0) + b"\0" * 24
    save_expected("bmp", "os2-pal8.bmp", bmp_file(os2p, palette8, rows_bottom_up([pad4(bytes(min(i, 15) for i in idx8[y])) for y in range(h)])), w, h,
                  b"".join(bytes(pal8[min(i, 15)]) + b"\xff" for y in range(h) for i in idx8[y]))

    # 32-bit BI_RGB with the fourth byte zero everywhere: loads opaque.
    rows32 = [b"".join(bytes((px[y * w + x][2], px[y * w + x][1], px[y * w + x][0], 0)) for x in range(w)) for y in range(h)]
    save_expected("bmp", "rgb32-zero-alpha.bmp", bmp_file(info_header(w, h, 32), b"", rows_bottom_up(rows32)), w, h, rgba)

    # 32-bit BI_RGB with real alpha in the fourth byte.
    alphas = [[(x * 20 + y * 7) % 256 for x in range(w)] for y in range(h)]
    rows32a = [b"".join(bytes((px[y * w + x][2], px[y * w + x][1], px[y * w + x][0], alphas[y][x])) for x in range(w)) for y in range(h)]
    want32a = b"".join(bytes(px[y * w + x]) + bytes((alphas[y][x],)) for y in range(h) for x in range(w))
    save_expected("bmp", "rgb32-alpha.bmp", bmp_file(info_header(w, h, 32), b"", rows_bottom_up(rows32a)), w, h, want32a)

    # BI_ALPHABITFIELDS (6) with 4-4-4-4 masks in 16 bits; channels scaled with round(v * 255 / 15).
    masks = struct.pack("<IIII", 0x0F00, 0x00F0, 0x000F, 0xF000)
    rows16 = []
    want16 = bytearray()
    grid16 = [[((x + y) % 16, (x * 2) % 16, (y * 3) % 16, (15 - x) % 16) for x in range(w)] for y in range(h)]
    for y in range(h):
        row = b"".join(struct.pack("<H", (r << 8) | (g << 4) | b | (a << 12)) for r, g, b, a in grid16[y])
        rows16.append(pad4(row))
    for y in range(h):
        for r, g, b, a in grid16[y]:
            want16 += bytes(round(v * 255 / 15) for v in (r, g, b, a))
    save_expected("bmp", "alphabitfields-4444.bmp", bmp_file(info_header(w, h, 16, compression=6, size=56, masks=masks), b"", rows_bottom_up(rows16)), w, h, want16)

    # Embedded PNG (BI_PNG 5) and JPEG (BI_JPEG 4): the data is a whole PNG or JPEG file.
    buf = io.BytesIO()
    src.save(buf, "PNG")
    png = buf.getvalue()
    save_expected("bmp", "embedded-png.bmp", bmp_file(info_header(w, h, 0, compression=5, image_size=len(png)), b"", png), w, h, rgba)
    buf = io.BytesIO()
    photo(size=(32, 24)).save(buf, "JPEG", quality=90)
    jpg = buf.getvalue()
    with open(path("bmp", "embedded-jpeg.bmp"), "wb") as f:
        f.write(bmp_file(info_header(32, 24, 0, compression=4, image_size=len(jpg)), b"", jpg))
    embedded["bmp/embedded-jpeg.bmp"] = jpg

    # 16-bit BI_RGB: 5-5-5 with the top bit unused; channels scaled with round(v * 255 / 31).
    rows555 = []
    want555 = bytearray()
    for y in range(h):
        row = bytearray()
        for x in range(w):
            r, g, b = (c * 31 // 255 for c in px[y * w + x])
            row += struct.pack("<H", 0x8000 * ((x + y) % 2) | (r << 10) | (g << 5) | b)
            want555 += bytes(round(v * 255 / 31) for v in (r, g, b)) + b"\xff"
        rows555.append(pad4(bytes(row)))
    save_expected("bmp", "rgb555.bmp", bmp_file(info_header(w, h, 16), b"", rows_bottom_up(rows555)), w, h, want555)


def tool_bmps():
    rgb = photo(size=(64, 48))
    rgb.save(path("bmp", "rgb24.bmp"))
    with_alpha().save(path("bmp", "rgba32.bmp"))
    rgb.quantize(256).save(path("bmp", "pal8.bmp"))
    rgb.convert("1").save(path("bmp", "pal1.bmp"))
    duck = "/tmp/duck64.png"
    rgb.save(duck)
    shapes = "/tmp/shapes.png"
    shapes_image().save(shapes)
    run("magick", duck, "-colors", "16", "-type", "Palette", "BMP3:" + path("bmp", "pal4-magick.bmp"))
    run("magick", shapes, "-colors", "200", "-type", "Palette", "-compress", "RLE", "BMP3:" + path("bmp", "rle8-magick.bmp"))
    run("magick", shapes, "-colors", "16", "-type", "Palette", "-compress", "RLE", "BMP3:" + path("bmp", "rle4-magick.bmp"))
    run("magick", duck, "-define", "bmp:subtype=RGB565", path("bmp", "rgb565.bmp"))
    run("magick", "/tmp/alpha.png", "-define", "bmp:subtype=ARGB1555", path("bmp", "argb1555.bmp"))
    run("magick", "/tmp/alpha.png", "BMP:" + path("bmp", "v5-argb32.bmp"))
    run("magick", "/tmp/alpha.png", "-define", "bmp:format=bmp4", path("bmp", "v4-argb32.bmp"))
    run("magick", duck, "BMP2:" + path("bmp", "os2-bmp2-24.bmp"))


def shapes_image(size=(96, 64)):
    """Flat colors with long runs, as RLE likes."""
    from PIL import Image, ImageDraw
    im = Image.new("RGB", size, (240, 240, 240))
    d = ImageDraw.Draw(im)
    d.rectangle((5, 5, 40, 30), fill=(200, 30, 30))
    d.ellipse((30, 20, 80, 60), fill=(30, 120, 220))
    d.line((0, 63, 95, 0), fill=(0, 0, 0), width=3)
    return im


# ---------------------------------------------------------------- TGA

def tga_header(image_type, width, height, depth, descriptor, cmap=(0, 0, 0), id_bytes=b""):
    cmap_type = 1 if image_type in (1, 9) else 0
    first, length, entry = cmap
    return struct.pack("<BBBHHBHHHHBB", len(id_bytes), cmap_type, image_type, first, length, entry, 0, 0, width, height, depth, descriptor) + id_bytes


def tga_footer(extension_offset):
    return struct.pack("<II", extension_offset, 0) + b"TRUEVISION-XFILE.\0"


def tga_extension(author=b"", comment=b"", alpha_type=0):
    ext = bytearray(495)
    struct.pack_into("<H", ext, 0, 495)
    ext[2:2 + len(author)] = author
    ext[43:43 + len(comment)] = comment
    ext[494] = alpha_type
    return bytes(ext)


def handmade_tgas():
    w, h = 9, 5
    src = photo(size=(w, h), mode="RGBA")
    px = [src.getpixel((x, y)) for y in range(h) for x in range(w)]
    alpha = [(x * 30 + y * 11) % 256 for y in range(h) for x in range(w)]
    rgba = b"".join(bytes(p[:3]) + bytes((alpha[i],)) for i, p in enumerate(px))
    top = [b"".join(bytes((px[y * w + x][2], px[y * w + x][1], px[y * w + x][0], alpha[y * w + x])) for x in range(w)) for y in range(h)]

    # The four origins: bits 4 (right-to-left) and 5 (top-to-bottom) of the descriptor; 8 attribute bits.
    for name, right, topdown in (("origin-bottom-left", 0, 0), ("origin-top-left", 0, 1), ("origin-bottom-right", 1, 0), ("origin-top-right", 1, 1)):
        rows = [(b"".join(r[i * 4:i * 4 + 4] for i in reversed(range(w))) if right else r) for r in top]
        if not topdown:
            rows = list(reversed(rows))
        data = tga_header(2, w, h, 32, 8 | (right << 4) | (topdown << 5)) + b"".join(rows)
        save_expected("tga", name + ".tga", data, w, h, rgba)

    # 15 and 16-bit color: 5-5-5 with an attribute bit that is alpha only when the descriptor says 1 attribute bit.
    def c5(v):
        return (v * 31 + 127) // 255
    rows16 = []
    want16 = bytearray()
    want16a = bytearray()
    for y in range(h):
        row = bytearray()
        for x in range(w):
            r, g, b, _ = px[y * w + x]
            bit = (x + y) % 2
            row += struct.pack("<H", (bit << 15) | (c5(r) << 10) | (c5(g) << 5) | c5(b))
        rows16.append(bytes(row))
    for y in range(h):
        for x in range(w):
            r, g, b, _ = px[y * w + x]
            scaled = bytes(round(c5(v) * 255 / 31) for v in (r, g, b))
            want16 += scaled + b"\xff"
            want16a += scaled + (b"\xff" if (x + y) % 2 else b"\0")
    bottom16 = b"".join(reversed(rows16))
    save_expected("tga", "rgb15.tga", tga_header(2, w, h, 15, 0) + bottom16, w, h, want16)
    save_expected("tga", "rgb16-no-alpha.tga", tga_header(2, w, h, 16, 0) + bottom16, w, h, want16)
    save_expected("tga", "argb1555.tga", tga_header(2, w, h, 16, 1) + bottom16, w, h, want16a)

    # A 15-bit color map with a first-entry offset of 10: indices 10 to 17.
    cmap = [((i * 4) % 32, (31 - i * 3) % 32, (i * 7) % 32) for i in range(8)]
    cmap_bytes = b"".join(struct.pack("<H", (r << 10) | (g << 5) | b) for r, g, b in cmap)
    idx = [[10 + (x + 2 * y) % 8 for x in range(w)] for y in range(h)]
    data = tga_header(1, w, h, 8, 0, cmap=(10, 8, 15)) + cmap_bytes + b"".join(bytes(idx[y]) for y in reversed(range(h)))
    want = b"".join(bytes(round(v * 255 / 31) for v in cmap[i - 10]) + b"\xff" for y in range(h) for i in idx[y])
    save_expected("tga", "cmap15-offset.tga", data, w, h, want)

    # 16-bit gray with alpha (type 3, 8 attribute bits).
    gray = [[(x * 25 + y * 3) % 256 for x in range(w)] for y in range(h)]
    data = tga_header(3, w, h, 16, 8) + b"".join(b"".join(bytes((gray[y][x], 255 - gray[y][x])) for x in range(w)) for y in reversed(range(h)))
    want = b"".join(bytes((gray[y][x],) * 3) + bytes((255 - gray[y][x],)) for y in range(h) for x in range(w))
    save_expected("tga", "gray-alpha16.tga", data, w, h, want)

    # TGA 2.0 footers: alpha type 0 makes the attribute bits opaque, 3 is straight alpha, 4 premultiplied (un-premultiplied on load).
    bottom32 = b"".join(reversed(top))
    for name, alpha_type in (("footer-alpha-none", 0), ("footer-alpha-straight", 3)):
        body = tga_header(2, w, h, 32, 8) + bottom32
        ext = tga_extension(b"Egami tests", b"Made by tools/make-simple-references.py", alpha_type)
        data = body + ext + tga_footer(len(body))
        want = rgba if alpha_type == 3 else b"".join(bytes(p[:3]) + b"\xff" for p in px)
        save_expected("tga", name + ".tga", data, w, h, want)
    premul_rows = []
    want_pre = bytearray()
    for y in range(h):
        row = bytearray()
        for x in range(w):
            r, g, b, _ = px[y * w + x]
            a = alpha[y * w + x]
            pr, pg, pb = (round(v * a / 255) for v in (r, g, b))
            row += bytes((pb, pg, pr, a))
            want_pre += bytes((min(255, (v * 510 + a) // (2 * a)) if a else 0 for v in (pr, pg, pb))) + bytes((a,))
        premul_rows.append(bytes(row))
    body = tga_header(2, w, h, 32, 8) + b"".join(reversed(premul_rows))
    save_expected("tga", "footer-alpha-premultiplied.tga", body + tga_extension(alpha_type=4) + tga_footer(len(body)), w, h, want_pre)


def tool_tgas():
    rgb = photo(size=(64, 48))
    rgba = with_alpha()
    rgb.save(path("tga", "rgb24.tga"))
    rgb.save(path("tga", "rgb24-rle.tga"), compression="tga_rle")
    rgba.save(path("tga", "rgba32.tga"))
    rgba.save(path("tga", "rgba32-rle.tga"), compression="tga_rle")
    gray = rgb.convert("L")
    gray.save(path("tga", "gray8.tga"))
    gray.save(path("tga", "gray8-rle.tga"), compression="tga_rle")
    pal = rgb.quantize(64)
    pal.save(path("tga", "pal8.tga"))
    pal.save(path("tga", "pal8-rle.tga"), compression="tga_rle")
    shapes_image().save("/tmp/shapes.png")
    run("magick", "/tmp/shapes.png", "-compress", "RLE", path("tga", "shapes-rle-magick.tga"))
    run("magick", "/tmp/alpha.png", path("tga", "rgba32-magick.tga"))


# ---------------------------------------------------------------- QOI

def qoi_encode(pixels, width, height, channels, colorspace):
    """A plain QOI encoder for the hand-made files (the spec's choices: run, index, diff, luma, rgb, rgba)."""
    out = bytearray(b"qoif" + struct.pack(">IIBB", width, height, channels, colorspace))
    index = [(0, 0, 0, 0)] * 64
    prev = (0, 0, 0, 255)
    run_length = 0
    for i, p in enumerate(pixels):
        if p == prev:
            run_length += 1
            if run_length == 62 or i == len(pixels) - 1:
                out.append(0xC0 | (run_length - 1))
                run_length = 0
            continue
        if run_length:
            out.append(0xC0 | (run_length - 1))
            run_length = 0
        h = (p[0] * 3 + p[1] * 5 + p[2] * 7 + p[3] * 11) % 64
        if index[h] == p:
            out.append(h)
        else:
            index[h] = p
            if p[3] == prev[3]:
                dr, dg, db = ((p[k] - prev[k] + 128) % 256 - 128 for k in range(3))
                if -2 <= dr <= 1 and -2 <= dg <= 1 and -2 <= db <= 1:
                    out.append(0x40 | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2))
                elif -32 <= dg <= 31 and -8 <= dr - dg <= 7 and -8 <= db - dg <= 7:
                    out += bytes((0x80 | (dg + 32), ((dr - dg + 8) << 4) | (db - dg + 8)))
                else:
                    out += bytes((0xFE, p[0], p[1], p[2]))
            else:
                out += bytes((0xFF,) + p)
        prev = p
    out += b"\0" * 7 + b"\1"
    return bytes(out)


def handmade_qois():
    w, h = 40, 30
    src = with_alpha((w, h))
    px = [src.getpixel((x, y)) for y in range(h) for x in range(w)]
    save_expected("qoi", "linear-rgba.qoi", qoi_encode(px, w, h, 4, 1), w, h, b"".join(bytes(p) for p in px))
    # A long run (more than 62 pixels) of one color, then a gradient.
    flat = [(12, 34, 56, 255)] * 300 + [(i % 256, (2 * i) % 256, 100, 255) for i in range(300)]
    save_expected("qoi", "long-run.qoi", qoi_encode(flat, 20, 30, 3, 0), 20, 30, b"".join(bytes(p) for p in flat))


def tool_qois():
    photo(size=(64, 48)).save("/tmp/rgb.png")
    run("magick", "/tmp/rgb.png", path("qoi", "rgb-magick.qoi"))
    run("magick", "/tmp/alpha.png", path("qoi", "rgba-magick.qoi"))
    shapes_image().save("/tmp/shapes.png")
    run("magick", "/tmp/shapes.png", path("qoi", "shapes-magick.qoi"))
    if os.path.exists("/tmp/qoiconv"):
        write_pam("/tmp/rgb.pam", photo(size=(64, 48)))
        write_pam("/tmp/alpha.pam", with_alpha())
        run("/tmp/qoiconv", "/tmp/rgb.pam", path("qoi", "rgb-qoiconv.qoi"))
        run("/tmp/qoiconv", "/tmp/alpha.pam", path("qoi", "rgba-qoiconv.qoi"))


def write_pam(file, im):
    depth = 4 if im.mode == "RGBA" else 3
    with open(file, "wb") as f:
        f.write(f"P7\nWIDTH {im.width}\nHEIGHT {im.height}\nDEPTH {depth}\nMAXVAL 255\nENDHDR\n".encode() + im.tobytes())


# ---------------------------------------------------------------- PNM

def handmade_pnms():
    # P1 with no spaces between digits, and comments in the header.
    w, h = 8, 3
    bits = [[(x + y) % 3 == 0 for x in range(w)] for y in range(h)]
    body = "\n".join("".join("1" if b else "0" for b in row) for row in bits)
    data = f"P1\n# a comment\n{w} # width\n{h}\n{body}\n".encode()
    want = b"".join((b"\0\0\0\xff" if b else b"\xff\xff\xff\xff") for row in bits for b in row)
    save_expected("pnm", "p1-packed-digits.pbm", data, w, h, want)

    # A PAM with an unknown tuple type and depth 3: read by depth (as RGB) with a warning.
    w, h = 4, 2
    pixels = bytes((i * 30) % 256 for i in range(w * h * 3))
    data = f"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 3\nMAXVAL 255\nTUPLTYPE EGAMI_TEST\nENDHDR\n".encode() + pixels
    want = b"".join(pixels[i * 3:i * 3 + 3] + b"\xff" for i in range(w * h))
    save_expected("pnm", "pam-unknown-tupltype.pam", data, w, h, want)

    # Two images in one stream: only the first is read.
    first = b"P5\n2 2\n255\n" + bytes((0, 64, 128, 255))
    second = b"P5\n2 2\n255\n" + bytes((9, 9, 9, 9))
    want = b"".join(bytes((v, v, v, 255)) for v in (0, 64, 128, 255))
    save_expected("pnm", "two-images.pgm", first + second, 2, 2, want)

    # Maxval 1000 for a graymap: values scaled with round(v * 255 / 1000).
    vals = [0, 1, 500, 999, 1000, 250]
    data = b"P5\n3 2\n1000\n" + b"".join(struct.pack(">H", v) for v in vals)
    want = b"".join(bytes(((v * 510 + 1000) // 2000,) * 3) + b"\xff" for v in vals)
    save_expected("pnm", "maxval-1000.pgm", data, 3, 2, want)


def tool_pnms():
    rgb = photo(size=(64, 48))
    rgb.save(path("pnm", "rgb.ppm"))
    rgb.convert("L").save(path("pnm", "gray.pgm"))
    rgb.convert("1").save(path("pnm", "bits.pbm"))
    rgb.save("/tmp/rgb.png")
    run("magick", "/tmp/rgb.png", "-compress", "none", path("pnm", "rgb-plain.ppm"))
    run("magick", "/tmp/rgb.png", "-colorspace", "Gray", "-compress", "none", path("pnm", "gray-plain.pgm"))
    run("magick", "/tmp/rgb.png", "-monochrome", "-compress", "none", path("pnm", "bits-plain.pbm"))
    run("magick", "/tmp/rgb.png", "-depth", "16", path("pnm", "rgb16.ppm"))
    run("magick", "/tmp/rgb.png", "-colorspace", "Gray", "-depth", "16", path("pnm", "gray16.pgm"))
    run("magick", "/tmp/alpha.png", path("pnm", "rgba.pam"))
    run("magick", "/tmp/alpha.png", "-colorspace", "Gray", path("pnm", "gray-alpha.pam"))
    with open(path("pnm", "rgb.ppm"), "rb") as source, open(path("pnm", "maxval-31.ppm"), "wb") as out:
        run("pamdepth", "31", stdin=source, stdout=out)
    with open(path("pnm", "rgb.ppm"), "rb") as source, open(path("pnm", "rgb-netpbm.pam"), "wb") as out:
        run("pamtopam", stdin=source, stdout=out)


# ---------------------------------------------------------------- references

def frames_of(file, tool):
    from PIL import Image
    if tool == "pillow":
        key = os.path.relpath(file, "tests/images").replace(os.sep, "/")
        with Image.open(io.BytesIO(embedded[key]) if key in embedded else file) as im:
            return im.size, im.convert("RGBA").tobytes()
    if tool == "magick":
        raw = "/tmp/ref.rgba"
        run("magick", file + "[0]", "-depth", "8", "rgba:" + raw)
        size = subprocess.run(["magick", "identify", "-format", "%w %h", file + "[0]"], check=True, capture_output=True, text=True).stdout
        return tuple(int(v) for v in size.split()), open(raw, "rb").read()
    if tool == "netpbm":
        png = "/tmp/ref.png"
        with open(file, "rb") as source, open("/tmp/ref8.pam", "wb") as out:
            run("pamdepth", "255", stdin=source, stdout=out)  # pamtopng takes only maxvals 1, 3, 15, 255 and 65535
        with open("/tmp/ref8.pam", "rb") as source, open(png, "wb") as out:
            run("pamtopng", stdin=source, stdout=out)
        with Image.open(png) as im:
            return im.size, im.convert("RGBA").tobytes()
    if tool == "qoiconv":
        run("/tmp/qoiconv", file, "/tmp/ref.pam")
        data = open("/tmp/ref.pam", "rb").read()
        header, _, pixels = data.partition(b"ENDHDR\n")
        fields = dict(line.split(b" ", 1) for line in header.splitlines()[1:])
        return (int(fields[b"WIDTH"]), int(fields[b"HEIGHT"])), pixels
    raise ValueError(tool)


def tools_for(kind):
    return {"bmp": ["pillow", "magick"], "tga": ["pillow", "magick"], "qoi": ["pillow", "magick", "qoiconv"], "pnm": ["pillow", "magick", "netpbm"]}[kind]


def inside():
    for folder in list(FOLDERS.values()) + [TARGET]:
        os.makedirs(folder, exist_ok=True)
    if os.path.exists(".cache/qoi/qoi.h"):
        with open("/tmp/qoiconv.c", "w") as f:
            f.write(QOICONV)
        run("gcc", "-O2", "-I.cache/qoi", "-o", "/tmp/qoiconv", "/tmp/qoiconv.c", "-lm")
    with_alpha().save("/tmp/alpha.png")
    tool_bmps()
    handmade_bmps()
    tool_tgas()
    handmade_tgas()
    tool_qois()
    handmade_qois()
    tool_pnms()
    handmade_pnms()

    manifest = {}
    for kind, folder in FOLDERS.items():
        for name in sorted(os.listdir(folder)):
            if name == "LICENSES":
                continue
            key = f"{kind}/{name}"
            file = os.path.join(folder, name)
            entry = {"references": []}
            if key in expected:
                width, height, rgba = expected[key]
                write_reference(name, "expected", rgba)
                entry["width"], entry["height"] = width, height
                entry["references"].append("expected")
            for tool in tools_for(kind):
                if tool == "qoiconv" and not os.path.exists("/tmp/qoiconv"):
                    continue
                try:
                    (width, height), rgba = frames_of(file, tool)
                except Exception as e:  # A tool that cannot read a file is noted, not fatal.
                    entry.setdefault("unreadable", {})[tool] = str(e).splitlines()[0][:200] if str(e) else type(e).__name__
                    continue
                entry["width"], entry["height"] = width, height
                write_reference(name, tool, rgba)
                entry["references"].append(tool)
            manifest[key] = entry
    with open(os.path.join(TARGET, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
    print(f"{len(manifest)} files")


def write_reference(name, tool, rgba):
    with open(os.path.join(TARGET, f"{name}.{tool}.rgba.gz"), "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as f:
        f.write(rgba)


# A minimal qoiconv built on qoi.h: it converts between QOI and 8-bit PAM, which the script writes and reads itself.
QOICONV = r'''
#define QOI_IMPLEMENTATION
#include "qoi.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
/* qoiconv for the references: PAM (RGB or RGB_ALPHA, 8-bit) to QOI, or QOI to PAM. */
static int ends(const char *s, const char *e) { size_t a = strlen(s), b = strlen(e); return a >= b && !strcmp(s + a - b, e); }
int main(int argc, char **argv) {
    if (argc < 3) return 1;
    if (ends(argv[1], ".qoi")) {
        qoi_desc d; void *px = qoi_read(argv[1], &d, 4);
        if (!px) return 2;
        FILE *f = fopen(argv[2], "wb");
        fprintf(f, "P7\nWIDTH %u\nHEIGHT %u\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n", d.width, d.height);
        fwrite(px, 1, (size_t)d.width * d.height * 4, f); fclose(f); return 0;
    }
    FILE *f = fopen(argv[1], "rb"); unsigned w = 0, h = 0, depth = 0; char line[256];
    while (fgets(line, sizeof line, f) && strncmp(line, "ENDHDR", 6)) {
        sscanf(line, "WIDTH %u", &w); sscanf(line, "HEIGHT %u", &h); sscanf(line, "DEPTH %u", &depth);
    }
    size_t n = (size_t)w * h * depth; unsigned char *px = malloc(n);
    if (fread(px, 1, n, f) != n) return 3;
    fclose(f);
    qoi_desc d = { w, h, (unsigned char)depth, QOI_SRGB };
    return qoi_write(argv[2], px, &d) ? 0 : 4;
}
'''


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    if not os.path.exists(".cache/qoi/qoi.h"):
        print("No .cache/qoi/qoi.h: the qoiconv references are skipped (see the usage note).")
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-simple-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
