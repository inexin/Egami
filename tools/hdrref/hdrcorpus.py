"""Builds the Phase 2.9.1 corpora and references inside the egami-hdrref image. Run through tools/make-hdr-references.py.

  radiance   tests/images/radiance: Radiance pictures written by this script from the RGBE description (every scan order,
             flat, old repeat-pixel and per-channel run-length scanlines, XYZE, header lines, extreme values) and by
             ImageMagick; tests/reference/radiance: each one's floats from stb_image where it reads the file, else from this
             script's own decode, which must agree with stb_image on every file stb_image reads. .cache/hdr-speed: larger
             pictures for the speed gate.

References are raw files: int32 width, height and channel count, then the samples as little-endian float32, gzipped.
"""
import glob
import gzip
import json
import os
import struct
import subprocess
import sys

import numpy as np
import OpenEXR

W = "/w"
EXR_IMAGES = f"{W}/.cache/openexr-images"


def write_raw(path, pixels):
    """Writes a reference: header, then float32 samples, gzipped with no timestamp."""
    h, w, c = pixels.shape
    data = struct.pack("<3i", w, h, c) + pixels.astype("<f4").tobytes()
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", compresslevel=9, mtime=0) as g:
            g.write(data)


def read_raw(path):
    with open(path, "rb") as f:
        data = f.read()
    w, h, c = struct.unpack("<3i", data[:12])
    return np.frombuffer(data[12:], dtype="<f4").reshape(h, w, c)


def exr_rgb(path):
    """Loads an EXR's R, G and B as float32, top row first."""
    channels = OpenEXR.File(path).channels()
    if "RGBA" in channels:
        return channels["RGBA"].pixels[..., :3].astype(np.float32)
    if "RGB" in channels:
        return channels["RGB"].pixels.astype(np.float32)
    return np.stack([channels[n].pixels for n in "RGB"], axis=-1).astype(np.float32)


# --- Radiance -------------------------------------------------------------------------------------------------------------


def to_rgbe(rgb):
    """float2rgbe on every pixel: the largest component's frexp exponent is shared, mantissas truncate."""
    rgb = np.where(np.isfinite(rgb), np.maximum(rgb, 0), 0).astype(np.float32)
    v = rgb.max(axis=-1)
    m, e = np.frexp(v)
    out = np.zeros(rgb.shape[:-1] + (4,), np.uint8)
    ok = v >= 1e-32
    scale = np.where(ok, np.ldexp(np.float32(1), 8 - e), 0).astype(np.float32)
    out[..., :3] = np.where(ok[..., None], np.floor(rgb * scale[..., None]), 0).astype(np.uint8)
    out[..., 3] = np.where(ok, e + 128, 0).astype(np.uint8)
    return out


def rgbe_to_float(rgbe):
    """stb_image's conversion: mantissa × 2^(e − 136), 0 when e is 0."""
    e = rgbe[..., 3].astype(np.int32)
    f = np.where(e > 0, np.ldexp(np.float32(1), e - 136), 0).astype(np.float32)
    return (rgbe[..., :3].astype(np.float32) * f[..., None]).astype(np.float32)


def rle_scanline(line):
    """Per-channel run-length encoding of one scanline of RGBE pixels (width 8 to 32767)."""
    w = len(line)
    out = bytearray([2, 2, w >> 8, w & 255])
    for c in range(4):
        data = line[:, c].tobytes()
        x = 0
        while x < w:
            # the next run of 4 or more
            run_start, run_len = x, 0
            while run_start < w:
                run_len = 1
                while run_len < 127 and run_start + run_len < w and data[run_start + run_len] == data[run_start]:
                    run_len += 1
                if run_len >= 4:
                    break
                run_start += run_len
                run_len = 0
            while x < run_start:
                n = min(128, run_start - x)
                out.append(n)
                out += data[x:x + n]
                x += n
            if run_len >= 4:
                out += bytes([128 + run_len, data[run_start]])
                x = run_start + run_len
    return bytes(out)


def old_scanline(line):
    """The old encoding: a pixel 1, 1, 1, n repeats the one before n times, n shifted 8 more bits for each repeat in a row."""
    out = bytearray()
    w = len(line)
    x = 0
    while x < w:
        p = line[x].tobytes()
        out += p
        x += 1
        run = 0
        while x + run < w and line[x + run].tobytes() == p:
            run += 1
        if run >= 2:
            left, shift = run, 0
            while left:
                out += bytes([1, 1, 1, left & 255])
                left >>= 8
            x += run
    return bytes(out)


def scan_order(rgbe, orientation):
    """Returns the scanlines of an image (rows top first) in the order a resolution line names."""
    a, b = orientation.split()[0], orientation.split()[2]
    if a[1] == "Y":
        rows = rgbe if a[0] == "-" else rgbe[::-1]
        return rows if b[0] == "+" else rows[:, ::-1]
    cols = np.transpose(rgbe, (1, 0, 2))  # cols[x] is column x, top to bottom
    cols = cols if a[0] == "+" else cols[::-1]
    return cols if b[0] == "-" else cols[:, ::-1]


def write_hdr(path, rgbe, orientation="-Y", encoding="rle", header_lines=(), signature="#?RADIANCE", fmt="32-bit_rle_rgbe"):
    h, w = rgbe.shape[:2]
    if orientation == "-Y":
        orientation = f"-Y {h} +X {w}"
    lines = [signature, *header_lines, f"FORMAT={fmt}", "", orientation]
    data = bytearray("\n".join(lines).encode("latin-1") + b"\n")
    for i, line in enumerate(scan_order(rgbe, orientation)):
        kind = encoding if encoding != "mixed" else ("rle" if i % 2 == 0 else "flat")
        if kind == "rle" and 8 <= len(line) <= 0x7FFF:
            data += rle_scanline(line)
        elif kind == "old":
            data += old_scanline(line)
        else:
            data += line.tobytes()
    with open(path, "wb") as f:
        f.write(data)


def decode_hdr(path):
    """This script's own decode of every scan order and scanline encoding, for files stb_image does not read."""
    data = open(path, "rb").read()
    pos = 0
    lines = []
    while True:
        end = data.index(b"\n", pos)
        line = data[pos:end].decode("latin-1").rstrip("\r")
        pos = end + 1
        if line == "" and lines:
            break
        lines.append(line)
    end = data.index(b"\n", pos)
    res = data[pos:end].decode().split()
    pos = end + 1
    major, minor = int(res[1]), int(res[3])
    scans = []
    for _ in range(major):
        line = np.zeros((minor, 4), np.uint8)
        if 8 <= minor <= 0x7FFF and data[pos] == 2 and data[pos + 1] == 2 and data[pos + 2] & 0x80 == 0:
            pos += 4
            for c in range(4):
                x = 0
                while x < minor:
                    n = data[pos]
                    if n > 128:
                        line[x:x + n - 128, c] = data[pos + 1]
                        x += n - 128
                        pos += 2
                    else:
                        line[x:x + n, c] = np.frombuffer(data[pos + 1:pos + 1 + n], np.uint8)
                        x += n
                        pos += 1 + n
        else:
            x, shift = 0, 0
            while x < minor:
                p = data[pos:pos + 4]
                pos += 4
                if p[0] == 1 and p[1] == 1 and p[2] == 1:
                    n = p[3] << shift
                    line[x:x + n] = line[x - 1]
                    x += n
                    shift += 8
                else:
                    line[x] = np.frombuffer(p, np.uint8)
                    x += 1
                    shift = 0
        scans.append(line)
    scans = np.stack(scans)
    a, b = res[0], res[2]
    if a[1] == "Y":
        img = scans if b[0] == "+" else scans[:, ::-1]
        img = img if a[0] == "-" else img[::-1]
    else:
        cols = scans if b[0] == "-" else scans[:, ::-1]
        cols = cols if a[0] == "+" else cols[::-1]
        img = np.transpose(cols, (1, 0, 2))
    return rgbe_to_float(np.ascontiguousarray(img)), lines


def stb_decode(path):
    out = "/tmp/stb.raw"
    r = subprocess.run(["stbhdr", "decode", path, out], capture_output=True)
    return read_raw_plain(out) if r.returncode == 0 else None


def read_raw_plain(path):
    data = open(path, "rb").read()
    w, h, c = struct.unpack("<3i", data[:12])
    return np.frombuffer(data[12:], dtype="<f4").reshape(h, w, c)


# Files stb_image reads differently from Radiance. Once one scanline is not run-length encoded, stb_image reads the rest of
# the file flat, and it has no old repeat-pixel encoding; Radiance decides per scanline and expands repeats, as Egami does.
STB_QUIRKS = {
    "desk-mixed.hdr": "stb_image reads the scanlines after a flat one as flat",
    "runs-old.hdr": "stb_image does not expand old-style repeat pixels",
}


def radiance():
    images = f"{W}/tests/images/radiance"
    refs = f"{W}/tests/reference/radiance"
    for d in (images, refs):
        os.makedirs(d, exist_ok=True)
        for f in glob.glob(f"{d}/*"):
            os.remove(f)

    desk = exr_rgb(f"{EXR_IMAGES}/ScanLines/Desk.exr")[300:364, 200:296]  # 96 x 64: lamp, highlights to about 40
    rgbe = to_rgbe(desk)
    write_hdr(f"{images}/desk-rle.hdr", rgbe, header_lines=["SOFTWARE=Egami hdrcorpus.py"])
    write_hdr(f"{images}/desk-flat.hdr", rgbe, encoding="flat")
    write_hdr(f"{images}/desk-mixed.hdr", rgbe, encoding="mixed")
    write_hdr(f"{images}/desk-old.hdr", rgbe, encoding="old")
    write_hdr(f"{images}/desk-rgbe-signature.hdr", rgbe, signature="#?RGBE")
    write_hdr(f"{images}/desk-header.hdr", rgbe, header_lines=[
        "# made by hdrcorpus.py", "EXPOSURE=0.5", "EXPOSURE=4", "COLORCORR=1 0.9 1.1", "PIXASPECT=1.0",
        "PRIMARIES=0.680 0.320 0.265 0.690 0.150 0.060 0.3127 0.3290", "VIEW= -vtv -vp 0 0 0 -vd 0 1 0", "SOFTWARE=hdrcorpus"])

    # Every scan order, on an image whose corners differ.
    y, x = np.mgrid[0:23, 0:37].astype(np.float32)
    ramp = np.stack([x / 4, y / 2, (x + y) / 60], axis=-1) ** 2
    ramp[0, 0] = (50, 0, 0)
    oriented = to_rgbe(ramp)
    h, w = oriented.shape[:2]
    for name, line in [("ny-px", f"-Y {h} +X {w}"), ("py-px", f"+Y {h} +X {w}"), ("ny-nx", f"-Y {h} -X {w}"), ("py-nx", f"+Y {h} -X {w}"),
                       ("px-ny", f"+X {w} -Y {h}"), ("px-py", f"+X {w} +Y {h}"), ("nx-ny", f"-X {w} -Y {h}"), ("nx-py", f"-X {w} +Y {h}")]:
        write_hdr(f"{images}/orient-{name}.hdr", oriented, orientation=line)

    # Long runs for the old encoding's shifted counts, and narrow scanlines that are always flat.
    runs = np.zeros((3, 600, 3), np.float32)
    runs[:, :300] = (2, 1, 0.5)
    runs[:, 300:310] = (0, 0, 1)
    runs[:, 310:] = (0.25, 0.25, 0.25)
    write_hdr(f"{images}/runs-old.hdr", to_rgbe(runs), encoding="old")
    write_hdr(f"{images}/runs-rle.hdr", to_rgbe(runs))
    write_hdr(f"{images}/narrow.hdr", to_rgbe(desk[:, :5]))

    # Extremes: zero, the smallest and largest exponents, unnormalized mantissas.
    ext = np.zeros((4, 16, 4), np.uint8)
    ext[0] = [[0, 0, 0, 0], [255, 255, 255, 255], [1, 2, 3, 1], [255, 0, 0, 1], [128, 64, 32, 136], [0, 0, 0, 200],
              [10, 20, 30, 0], [255, 255, 255, 1], [1, 1, 1, 128], [127, 3, 0, 129], [64, 64, 64, 130], [200, 100, 50, 140],
              [12, 34, 56, 100], [255, 254, 253, 254], [0, 1, 0, 2], [3, 2, 1, 250]]
    ext[1] = ext[0, ::-1]
    ext[2] = np.roll(ext[0], 5, axis=0)
    ext[3] = np.roll(ext[0], 11, axis=0)
    write_hdr(f"{images}/extremes.hdr", ext)

    # XYZE: the desk crop through Radiance's standard primaries, white (1/3, 1/3).
    prim = [(0.640, 0.330), (0.290, 0.600), (0.150, 0.060)]
    m = np.array([[px / py, 1, (1 - px - py) / py] for px, py in prim]).T
    s = np.linalg.solve(m, np.array([1.0, 1.0, 1.0]))
    xyz = desk @ (m * s).T.astype(np.float32)
    write_hdr(f"{images}/desk-xyze.hdr", to_rgbe(xyz), fmt="32-bit_rle_xyze")

    # ImageMagick's own writer, from the same crop through a float TIFF.
    desk.astype("<f4").tofile("/tmp/desk.rgb")
    subprocess.run(["convert", "-size", "96x64", "-depth", "32", "-define", "quantum:format=floating-point", "-endian", "lsb",
                    "rgb:/tmp/desk.rgb", f"{images}/desk-magick.hdr"], check=True)

    manifest = {}
    for path in sorted(glob.glob(f"{images}/*.hdr")):
        name = os.path.basename(path)
        mine, _ = decode_hdr(path)
        stb = stb_decode(path)
        if stb is not None and name in STB_QUIRKS:
            source = f"hdrcorpus.py ({STB_QUIRKS[name]})"
        elif stb is not None:
            if not np.array_equal(stb.view(np.uint32), mine.view(np.uint32)):
                sys.exit(f"{name}: hdrcorpus.py's decode differs from stb_image's")
            source = "stb_image"
        else:
            source = "hdrcorpus.py"
        write_raw(f"{refs}/{name}.raw.gz", mine)
        manifest[name] = source
    with open(f"{refs}/manifest.json", "w") as f:
        json.dump({"tool": "stb_image 2c980bb5 where it reads the file, else hdrcorpus.py's decode", "files": manifest}, f, indent=1)
    print(json.dumps(manifest, indent=1))

    # The speed set: whole OpenEXR scanline images as Radiance, written by ImageMagick (per-channel run-length scanlines).
    speed = f"{W}/.cache/hdr-speed"
    os.makedirs(speed, exist_ok=True)
    for name in ["StillLife", "Tree", "MtTamWest"]:
        rgb = exr_rgb(f"{EXR_IMAGES}/ScanLines/{name}.exr")
        h, w = rgb.shape[:2]
        rgb.astype("<f4").tofile("/tmp/speed.rgb")
        subprocess.run(["convert", "-size", f"{w}x{h}", "-depth", "32", "-define", "quantum:format=floating-point", "-endian", "lsb",
                        "rgb:/tmp/speed.rgb", f"{speed}/{name}.hdr"], check=True)
        print(name, w, h, os.path.getsize(f"{speed}/{name}.hdr"))


# --- MPF ------------------------------------------------------------------------------------------------------------------


def mpf_segment(entries, little, attribute_ifd=False):
    """An APP2 MPF segment for entries (type, length, offset), lengths and offsets already final; returns its bytes."""
    e = "<" if little else ">"
    n = len(entries)
    ifd_entries = 3
    ifd_size = 2 + (12 * ifd_entries) + 4
    entry_offset = 8 + ifd_size
    attr_offset = entry_offset + (16 * n) if attribute_ifd else 0
    tiff = bytearray(b"II*\0" if little else b"MM\0*")
    tiff += struct.pack(e + "I", 8)
    tiff += struct.pack(e + "H", ifd_entries)
    tiff += struct.pack(e + "HHI", 0xB000, 7, 4) + b"0100"
    tiff += struct.pack(e + "HHII", 0xB001, 4, 1, n)
    tiff += struct.pack(e + "HHII", 0xB002, 7, 16 * n, entry_offset)
    tiff += struct.pack(e + "I", attr_offset)
    for kind, length, offset, flags in entries:
        tiff += struct.pack(e + "IIIHH", flags | kind, length, offset, 0, 0)
    if attribute_ifd:
        # MP attribute IFD: version and individual image number 1.
        tiff += struct.pack(e + "H", 2)
        tiff += struct.pack(e + "HHI", 0xB000, 7, 4) + b"0100"
        tiff += struct.pack(e + "HHII", 0xB101, 4, 1, 1)
        tiff += struct.pack(e + "I", 0)
    payload = b"MPF\0" + bytes(tiff)
    return b"\xff\xe2" + struct.pack(">H", len(payload) + 2) + payload


def make_mpo(path, images, types, little, attribute_ifd=False, flags=None):
    """Writes a Multi-Picture file: the first JPEG with an MPF segment after SOI (and APP0/APP1), the others appended."""
    primary = images[0]
    insert = 2
    while primary[insert] == 0xFF and primary[insert + 1] in (0xE0, 0xE1):
        insert += 2 + struct.unpack(">H", primary[insert + 2:insert + 4])[0]
    flags = flags or [0x20000000] + [0] * (len(images) - 1)
    # The segment's size does not depend on the offsets, so build it twice.
    seg = mpf_segment([(t, 0, 0, f) for t, f in zip(types, flags)], little, attribute_ifd)
    header_at = insert + 4 + 4  # marker, length, "MPF\0"
    first_len = len(primary) + len(seg)
    entries = [(types[0], first_len, 0, flags[0])]
    at = first_len
    for img, t, f in zip(images[1:], types[1:], flags[1:]):
        entries.append((t, len(img), at - header_at, f))
        at += len(img)
    seg = mpf_segment(entries, little, attribute_ifd)
    with open(path, "wb") as out:
        out.write(primary[:insert] + seg + primary[insert:])
        for img in images[1:]:
            out.write(img)


def magick_jpeg(width, height, pattern, quality=85):
    out = "/tmp/m.jpg"
    subprocess.run(["convert", "-size", f"{width}x{height}", pattern, "-quality", str(quality), out], check=True)
    return open(out, "rb").read()


def mpf():
    images = f"{W}/tests/images/mpf"
    refs = f"{W}/tests/reference/mpf"
    for d in (images, refs):
        os.makedirs(d, exist_ok=True)
        for f in glob.glob(f"{d}/*"):
            os.remove(f)

    # Apple's two gain map layouts, from libultrahdr's test data (CC-BY-4.0).
    for name in ["apple_gainmap_new.jpg", "apple_gainmap_old.jpg"]:
        with open(f"/src/libultrahdr/tests/data/{name}", "rb") as src, open(f"{images}/{name.replace('_', '-')}", "wb") as dst:
            dst.write(src.read())

    # Ultra HDR files libultrahdr writes from a 256 x 192 crop of Desk.exr, linear Rec. 709: its defaults, a quarter-size map,
    # and a one-channel map.
    desk = OpenEXR.File(f"{EXR_IMAGES}/ScanLines/Desk.exr").channels()["RGBA"].pixels[300:492, 200:456].astype(np.float16)
    desk[..., 3] = 1
    desk.tofile("/tmp/desk.rgbahalf")
    for name, extra in [("uhdr-default", []), ("uhdr-quarter-map", ["-s", "4"]), ("uhdr-mono-map", ["-M", "0"])]:
        subprocess.run(["ultrahdr_app", "-m", "0", "-p", "/tmp/desk.rgbahalf", "-a", "4", "-t", "0", "-C", "0", "-w", "256", "-h", "192",
                        "-q", "90", "-Q", "85", *extra, "-z", f"{images}/{name}.jpg"], check=True, capture_output=True)

    # Synthetic Multi-Picture files: a stereo pair with a large thumbnail, in both byte orders, one with an attribute IFD.
    left = magick_jpeg(64, 48, "gradient:red-blue")
    right = magick_jpeg(64, 48, "gradient:green-yellow")
    thumb = magick_jpeg(32, 24, "plasma:fractal")
    make_mpo(f"{images}/stereo-be.jpg", [left, right, thumb], [0x020002, 0x020002, 0x010001], little=False,
             flags=[0xA0000000, 0, 0])
    make_mpo(f"{images}/stereo-le.jpg", [left, right, thumb], [0x020002, 0x020002, 0x010001], little=True, attribute_ifd=True,
             flags=[0xA0000000, 0, 0])
    make_mpo(f"{images}/preview.jpg", [left, thumb], [0x030000, 0x010001], little=False)

    for path in sorted(glob.glob(f"{images}/*.jpg")):
        name = os.path.basename(path)
        r = subprocess.run(["exiftool", "-j", "-G1", "-n", "-a", "-MPF:all", "-MPImage*:all", path], capture_output=True, text=True, check=True)
        listing = json.loads(r.stdout)[0]
        listing.pop("SourceFile")
        with open(f"{refs}/{name}.json", "w") as f:
            json.dump(listing, f, indent=1, sort_keys=True)
        print(name, listing.get("MPF0:NumberOfImages"))
    with open(f"{refs}/README", "w") as f:
        f.write(subprocess.run(["exiftool", "-ver"], capture_output=True, text=True).stdout.strip() + " (ExifTool), from tools/make-hdr-references.py mpf\n")


# --- OpenEXR --------------------------------------------------------------------------------------------------------------

COMPRESSIONS = {
    "none": OpenEXR.NO_COMPRESSION, "rle": OpenEXR.RLE_COMPRESSION, "zips": OpenEXR.ZIPS_COMPRESSION, "zip": OpenEXR.ZIP_COMPRESSION,
    "piz": OpenEXR.PIZ_COMPRESSION, "pxr24": OpenEXR.PXR24_COMPRESSION, "b44": OpenEXR.B44_COMPRESSION,
    "b44a": OpenEXR.B44A_COMPRESSION, "dwaa": OpenEXR.DWAA_COMPRESSION, "dwab": OpenEXR.DWAB_COMPRESSION,
}


def write_exr(path, channels, compression="zip", **header):
    h = {"compression": COMPRESSIONS[compression], "type": OpenEXR.scanlineimage}
    h.update(header)
    with OpenEXR.File(h, channels) as f:
        f.write(path)


def reference(path, out):
    subprocess.run(["exrref", path, "/tmp/ref.bin"], check=True)
    data = open("/tmp/ref.bin", "rb").read()
    with open(out, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", compresslevel=9, mtime=0) as g:
            g.write(data)


def exr():
    images = f"{W}/tests/images/exr"
    refs = f"{W}/tests/reference/exr"
    for d in (images, refs):
        os.makedirs(d, exist_ok=True)
        for f in glob.glob(f"{d}/*"):
            os.remove(f)

    rgba = OpenEXR.File(f"{EXR_IMAGES}/ScanLines/Desk.exr").channels()["RGBA"].pixels
    desk = rgba[300:364, 200:296].astype(np.float16)                 # 96 x 64, highlights to about 40
    odd = rgba[330:353, 240:277].astype(np.float16)                  # 37 x 23: partial blocks and tiles
    deskf = desk.astype(np.float32)
    rng = np.random.default_rng(7)

    def split(a, names="RGBA"):
        return {n: np.ascontiguousarray(a[..., i]) for i, n in enumerate(names)}

    # Every compression, half RGBA; the lossless and float-capable ones on float RGB too.
    for name in COMPRESSIONS:
        write_exr(f"{images}/half-{name}.exr", split(desk), name)
    for name in ["none", "rle", "zips", "zip", "piz", "pxr24", "dwaa", "dwab", "b44"]:
        write_exr(f"{images}/float-{name}.exr", split(deskf[..., :3], "RGB"), name)
    for name in ["zip", "piz", "b44", "b44a", "dwaa", "dwab", "pxr24", "rle"]:
        write_exr(f"{images}/odd-{name}.exr", split(odd), name)

    # Non-color channels: a uint ID, depth, and a half channel holding every finite half value pattern.
    ids = rng.integers(0, 2**32 - 1, size=(64, 96), dtype=np.uint32)
    ids[:8, :8] = 7
    depth = (np.arange(96 * 64, dtype=np.float32).reshape(64, 96) / 3).astype(np.float32)
    every_half = np.arange(96 * 64, dtype=np.uint16).reshape(64, 96).view(np.float16).copy()
    for name in ["none", "zip", "piz", "pxr24", "rle", "dwaa"]:
        write_exr(f"{images}/aov-{name}.exr", {**split(desk), "id": ids, "Z": depth, "bits": every_half}, name)

    # Layers: a default RGBA and two named layers.
    write_exr(f"{images}/layers.exr", {**split(desk), "diffuse.R": desk[..., 0] * 0.5, "diffuse.G": desk[..., 1] * 0.5,
                                       "diffuse.B": desk[..., 2] * 0.5, "specular.R": desk[..., 2], "specular.G": desk[..., 0],
                                       "specular.B": desk[..., 1], "specular.A": desk[..., 3]}, "piz")
    write_exr(f"{images}/layers-only.exr", {"beauty.R": desk[..., 0], "beauty.G": desk[..., 1], "beauty.B": desk[..., 2]}, "zip")

    # Gray, windows, line order and chromaticities.
    write_exr(f"{images}/gray-y.exr", {"Y": desk[..., 1]}, "zip")
    write_exr(f"{images}/gray-ya.exr", {"Y": desk[..., 1], "A": desk[..., 3]}, "piz")
    write_exr(f"{images}/data-window.exr", split(desk), "zip",
              dataWindow=(np.array([10, -5], np.int32), np.array([105, 58], np.int32)),
              displayWindow=(np.array([0, 0], np.int32), np.array([79, 47], np.int32)))
    write_exr(f"{images}/decreasing-y.exr", split(desk), "zip", lineOrder=OpenEXR.DECREASING_Y)
    write_exr(f"{images}/decreasing-y-piz.exr", split(desk), "piz", lineOrder=OpenEXR.DECREASING_Y)
    write_exr(f"{images}/p3.exr", split(desk), "zip",
              chromaticities=(0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290))

    # Luminance and chroma, as RgbaOutputFile writes them (Y full, RY and BY at 2 x 2).
    for name in ["piz", "zip", "b44"]:
        subprocess.run(["exrref", "yca", f"{images}/half-none.exr", f"{images}/yca-{name}.exr",
                        str(int(COMPRESSIONS[name].value))], check=True)

    # Tiles: exrmaketiled one level, mipmap and ripmap (rounding down and up); and a tiled file Python writes.
    for name, args in [("tiled-one", ["-o"]), ("tiled-mip", ["-m"]), ("tiled-mip-up", ["-m", "-u"]), ("tiled-rip", ["-r"])]:
        subprocess.run(["exrmaketiled", *args, "-t", "32", "32", "-z", "zip", f"{images}/half-none.exr", f"{images}/{name}.exr"],
                       check=True, capture_output=True)
    for name, z in [("tiled-odd-piz", "piz"), ("tiled-odd-b44", "b44"), ("tiled-odd-dwab", "dwab")]:
        subprocess.run(["exrmaketiled", "-o", "-t", "16", "8", "-z", z, f"{images}/odd-zip.exr", f"{images}/{name}.exr"],
                       check=True, capture_output=True)

    # Several parts: color and depth, and a tiled part beside a scanline one.
    parts = [OpenEXR.Part({"compression": OpenEXR.ZIP_COMPRESSION, "type": OpenEXR.scanlineimage}, split(desk), "beauty"),
             OpenEXR.Part({"compression": OpenEXR.PIZ_COMPRESSION, "type": OpenEXR.scanlineimage}, {"Z": depth}, "depth")]
    with OpenEXR.File(parts) as f:
        f.write(f"{images}/multipart.exr")
    subprocess.run(["exrmultipart", "-combine", "-i", f"{images}/tiled-one.exr", f"{images}/float-piz.exr",
                    "-o", f"{images}/multipart-tiled.exr"], check=True, capture_output=True)

    for path in sorted(glob.glob(f"{images}/*.exr")):
        reference(path, f"{refs}/{os.path.basename(path)}.ref.gz")
        print(os.path.basename(path), os.path.getsize(path))

    # openexr-images: references for every file Egami reads (deep files are refused), kept in .cache.
    cache = f"{W}/.cache/exr-ref"
    os.makedirs(cache, exist_ok=True)
    for path in sorted(glob.glob(f"{EXR_IMAGES}/**/*.exr", recursive=True)):
        rel = os.path.relpath(path, EXR_IMAGES).replace("/", "__")
        r = subprocess.run(["exrref", path, f"{cache}/{rel}.ref"], capture_output=True, text=True)
        print(rel, "ok" if r.returncode == 0 else r.stderr.strip()[:120])

    # The speed set: three scanline images in each compression, half RGBA.
    speed = f"{W}/.cache/exr-speed"
    os.makedirs(speed, exist_ok=True)
    for src in ["StillLife", "Tree", "MtTamWest"]:
        img = OpenEXR.File(f"{EXR_IMAGES}/ScanLines/{src}.exr").channels()
        pixels = img["RGBA"].pixels.astype(np.float16) if "RGBA" in img else None
        if pixels is None:
            continue
        for name in COMPRESSIONS:
            write_exr(f"{speed}/{src}-{name}.exr", split(pixels), name)


# --- Gain maps -------------------------------------------------------------------------------------------------------------

ISO_NS = b"urn:iso:std:iso:ts:21496:-1\0"
XMP_NS = b"http://ns.adobe.com/xap/1.0/\0"


def segments(jpeg):
    """Splits a JPEG into (marker, payload) up to SOS, and the rest from SOS on."""
    pos, out = 2, []
    while True:
        marker = jpeg[pos + 1]
        length = struct.unpack(">H", jpeg[pos + 2:pos + 4])[0]
        if marker == 0xDA:
            return out, jpeg[pos:]
        out.append((marker, jpeg[pos + 4:pos + 2 + length]))
        pos += 2 + length


def build(segs, rest):
    data = bytearray(b"\xff\xd8")
    for marker, payload in segs:
        data += bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload
    return bytes(data + rest)


def split_mpf(path):
    """Returns the primary and gain map JPEGs of a two-image MPF file."""
    data = open(path, "rb").read()
    i = data.index(b"MPF\0") + 4
    little = data[i:i + 2] == b"II"
    e = "<" if little else ">"
    ifd = struct.unpack(e + "I", data[i + 4:i + 8])[0]
    n = struct.unpack(e + "H", data[i + ifd:i + ifd + 2])[0]
    for k in range(n):
        tag, typ, cnt, val = struct.unpack(e + "HHII", data[i + ifd + 2 + 12 * k:i + ifd + 14 + 12 * k])
        if tag == 0xB002:
            entries = val
    _, size1, off1, _, _ = struct.unpack(e + "IIIHH", data[i + entries:i + entries + 16])
    _, size2, off2, _, _ = struct.unpack(e + "IIIHH", data[i + entries + 16:i + entries + 32])
    return data[:off2 + i], data[off2 + i:off2 + i + size2]


def hdrgm_xmp(meta, primary):
    """The Ultra HDR 1.0 XMP: for the primary, hdrgm:Version and the GContainer directory; for the map, its metadata."""
    import math
    if primary:
        return (XMP_NS + b'<x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 5.1.2"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
                b'<rdf:Description xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/" '
                b'xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0"><Container:Directory><rdf:Seq>'
                b'<rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/></rdf:li>'
                b'<rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="GainMap" Item:Mime="image/jpeg" Item:Length="LENGTH"/></rdf:li>'
                b'</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>')
    v = lambda x: ("%.6f" % x).encode()
    return (XMP_NS + b'<x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 5.1.2"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            b'<rdf:Description xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0" '
            b'hdrgm:GainMapMin="' + v(math.log2(meta["minContentBoost"][0])) + b'" hdrgm:GainMapMax="' + v(math.log2(meta["maxContentBoost"][0])) +
            b'" hdrgm:Gamma="' + v(meta["gamma"][0]) + b'" hdrgm:OffsetSDR="' + v(meta["offsetSdr"][0]) + b'" hdrgm:OffsetHDR="' + v(meta["offsetHdr"][0]) +
            b'" hdrgm:HDRCapacityMin="' + v(math.log2(meta["hdrCapacityMin"])) + b'" hdrgm:HDRCapacityMax="' + v(math.log2(meta["hdrCapacityMax"])) +
            b'" hdrgm:BaseRenditionIsHDR="False"/></rdf:RDF></x:xmpmeta>')


def uhdr_meta(path):
    return json.loads(subprocess.run(["uhdrref", "meta", path], capture_output=True, text=True, check=True).stdout)


def gainmap():
    images = f"{W}/tests/images/gainmap"
    refs = f"{W}/tests/reference/gainmap"
    for d in (images, refs):
        os.makedirs(d, exist_ok=True)
        for f in glob.glob(f"{d}/*"):
            os.remove(f)
    mpf_dir = f"{W}/tests/images/mpf"
    for name in ["uhdr-default", "uhdr-mono-map", "uhdr-quarter-map", "apple-gainmap-new", "apple-gainmap-old"]:
        open(f"{images}/{name}.jpg", "wb").write(open(f"{mpf_dir}/{name}.jpg", "rb").read())

    # Gamma, a minimum boost below 1 and offsets, and a map scaled by a fraction, written by libultrahdr.
    desk = OpenEXR.File(f"{EXR_IMAGES}/ScanLines/Desk.exr").channels()["RGBA"].pixels[300:540, 200:520].astype(np.float16)
    desk[..., 3] = 1
    desk.tofile("/tmp/desk.rgbahalf")
    for name, extra in [("uhdr-gamma", ["-G", "2.0", "-k", "0.5", "-K", "64"]), ("uhdr-third-map", ["-s", "3", "-M", "0"])]:
        subprocess.run(["ultrahdr_app", "-m", "0", "-p", "/tmp/desk.rgbahalf", "-a", "4", "-t", "0", "-C", "0", "-w", "320", "-h", "240",
                        "-q", "90", "-Q", "90", *extra, "-z", f"{images}/{name}.jpg"], check=True, capture_output=True)

    # Ultra HDR 1.0: the same file with Adobe's hdrgm XMP in place of the ISO metadata.
    meta = uhdr_meta(f"{images}/uhdr-default.jpg")
    primary, gm = split_mpf(f"{images}/uhdr-default.jpg")
    gsegs, grest = segments(gm)
    gsegs = [s for s in gsegs if not (s[0] == 0xE2 and s[1].startswith(ISO_NS))]
    gsegs.insert(0, (0xE1, hdrgm_xmp(meta, False)))
    gm2 = build(gsegs, grest)
    psegs, prest = segments(primary)
    psegs = [s for s in psegs if not (s[0] == 0xE2 and (s[1].startswith(ISO_NS) or s[1].startswith(b"MPF\0")))]
    xmp = hdrgm_xmp(meta, True).replace(b"LENGTH", str(len(gm2)).encode())
    psegs.insert(0, (0xE1, xmp))
    primary2 = build(psegs, prest)
    open("/tmp/p.jpg", "wb").write(primary2)
    open("/tmp/g.jpg", "wb").write(gm2)
    make_mpo(f"{images}/adobe-xmp.jpg", [primary2, gm2], [0x030000, 0], little=False)

    manifest = {}
    for path in sorted(glob.glob(f"{images}/*.jpg")):
        name = os.path.basename(path)
        meta = uhdr_meta(path)
        boosts = sorted({2.0, 4.0, float(meta["hdrCapacityMax"])})
        for boost in boosts:
            out = f"{refs}/{name}.{boost:g}.raw"
            subprocess.run(["uhdrref", "decode", path, str(boost), "/tmp/out.raw"], check=True)
            with open(out + ".gz", "wb") as f:
                with gzip.GzipFile(fileobj=f, mode="wb", compresslevel=9, mtime=0) as g:
                    g.write(open("/tmp/out.raw", "rb").read())
        meta["boosts"] = boosts
        manifest[name] = meta
        print(name, meta["hdrCapacityMax"], boosts)
    with open(f"{refs}/manifest.json", "w") as f:
        json.dump({"tool": "libultrahdr 2.0.2 (uhdrref.c), linear half-float RGBA at each display boost", "files": manifest}, f, indent=1)

    # The speed set: three openexr-images pictures as linear half RGBA (even sizes, as libultrahdr needs), and the Ultra HDR
    # files libultrahdr writes from them with its defaults and with a quarter-size map.
    speed = f"{W}/.cache/gainmap-speed"
    os.makedirs(speed, exist_ok=True)
    for src in ["StillLife", "Tree", "MtTamWest"]:
        pixels = OpenEXR.File(f"{EXR_IMAGES}/ScanLines/{src}.exr").channels()["RGBA"].pixels.astype(np.float16)
        h, w = pixels.shape[0] & ~1, pixels.shape[1] & ~1
        pixels = np.ascontiguousarray(pixels[:h, :w])
        pixels[..., 3] = 1
        raw = f"{speed}/{src}-{w}x{h}.rgbahalf"
        pixels.tofile(raw)
        for name, extra in [(src, []), (f"{src}-quarter", ["-s", "4"])]:
            subprocess.run(["ultrahdr_app", "-m", "0", "-p", raw, "-a", "4", "-t", "0", "-C", "0", "-w", str(w), "-h", str(h),
                            *extra, "-z", f"{speed}/{name}.jpg"], check=True, capture_output=True)


if __name__ == "__main__":
    {"radiance": radiance, "mpf": mpf, "exr": exr, "gainmap": gainmap}[sys.argv[1]]()
