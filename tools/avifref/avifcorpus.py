"""Builds the Phase 2.9.2 AVIF corpus and references inside the egami-avifref image. Run through tools/make-avif-references.py.

  corpus       tests/images/avif: files encoded here by avifenc (libaom, SVT-AV1 and rav1e), avifgainmaputil and aomenc from
               public domain and CC0 pictures (see tests/images/LICENSES), covering every chroma format and bit depth, lossless,
               the coding tools libaom can be steered to (tiles, superres, palette, intra block copy, film grain, quantizer
               matrices, delta q, 128x128 superblocks), alpha, grids, clap, irot and imir, ICC and CICP, HDR metadata, Exif and
               XMP, sequences, layered files and gain maps; plus libavif's own test files (BSD-2) copied into
               tests/images/avif/libavif. tests/reference/avif: avifdec's RGB decode of each (PNG at 8 or 16 bits) and manifest.json with each file's source and
               options, avifdec --info's text, ExifTool's tags, the MD5 of avifdec's YUV decode through dav1d (the planes
               YuvRaster.Load gives; the y4m itself stays in .cache/avif/reference/corpus), and every item (type, properties, references, extents) with
               the MD5 of dav1d's decode of each AV1 item: with film grain, without it, with every in-loop filter off, and
               whether libaom's aomdec decodes it to the same planes.
  conformance  .cache/avif/reference/<set>/summary.json and <stream>.y4m (for Argon, .cache/avif/argon/reference): dav1d's
               first frame of every stream of libaom's test vectors, dav1d's test data and, when unpacked, the Argon streams; the MD5 of that frame with film grain, without
               it and with the in-loop filters off; whether aomdec agrees; and the frame's type. For Argon also dav1d's MD5 of
               the whole stream against Argon's own, and whether every frame is intra (the streams an intra decoder can pass).
  extra        .cache/avif/reference/extra/: the corpus treatment (manifest entries and decodes) for the AVIF files that stay in
               .cache: AOM's av1-avif test files (Apple, Link-U, Microsoft, Netflix, Xiph) and libavif's files whose pictures
               have other licenses.
"""
import glob
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import zlib

import numpy as np

W = "/w"
CACHE = f"{W}/.cache/avif"
IMAGES = f"{W}/tests/images"
OUT = f"{IMAGES}/avif"
REF = f"{W}/tests/reference/avif"
TMP = "/tmp/avif"
RAW_PIXLS = f"{W}/.cache/jxl/testdata/external/raw.pixls"


def sh(*args, check=True, timeout=600):
    r = subprocess.run([str(a) for a in args], capture_output=True, text=True, timeout=timeout)
    if check and r.returncode != 0:
        raise RuntimeError(f"{' '.join(map(str, args))}: {(r.stderr or r.stdout).strip()}")
    return r


# ---------------------------------------------------------------- PNG and y4m without Pillow

def read_png(path):
    """Reads a non-interlaced PNG (gray, RGB, gray-alpha or RGBA; 8 or 16 bits) into an (h, w, c) uint16 array."""
    data = open(path, "rb").read()
    pos, idat, plte = 8, b"", None
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            w, h, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert interlace == 0 and depth in (8, 16), path
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
    bpp = channels * depth // 8
    raw = zlib.decompress(idat)
    stride = w * bpp
    out = np.zeros((h, stride), dtype=np.uint8)
    prev = np.zeros(stride, dtype=np.int32)
    for y in range(h):
        f = raw[y * (stride + 1)]
        line = np.frombuffer(raw, dtype=np.uint8, count=stride, offset=y * (stride + 1) + 1).astype(np.int32)
        cur = np.zeros(stride, dtype=np.int32)
        if f == 0:
            cur = line
        elif f == 2:
            cur = (line + prev) & 255
        else:
            for i in range(stride):
                a = cur[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                if f == 1:
                    p = a
                elif f == 3:
                    p = (a + b) >> 1
                else:
                    pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                    p = a if pa <= pb and pa <= pc else b if pb <= pc else c
                cur[i] = (line[i] + p) & 255
        out[y] = cur
        prev = cur
    if depth == 16:
        pixels = out.reshape(h, w * channels, 2).astype(np.uint16)
        pixels = (pixels[..., 0] << 8) | pixels[..., 1]
    else:
        pixels = out.astype(np.uint16)
    return pixels.reshape(h, w, channels), depth


def write_png(path, pixels, depth=8):
    h, w, c = pixels.shape
    color = {1: 0, 2: 4, 3: 2, 4: 6}[c]
    rows = pixels.astype(">u2" if depth == 16 else "u1").reshape(h, -1).view(np.uint8)
    raw = b"".join(b"\0" + rows[y].tobytes() for y in range(h))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, depth, color, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def read_y4m(path):
    """Returns the header fields and each frame's planes as a list of (frame, [planes as bytes])."""
    data = open(path, "rb").read()
    if not data.startswith(b"YUV4MPEG2 "):
        return {}, []
    end = data.index(b"\n")
    fields = data[:end].split()[1:]
    head = {f[:1].decode(): f[1:].decode() for f in fields}
    w, h = int(head["W"]), int(head["H"])
    c = head.get("C", "420jpeg")
    # 8-bit forms are 420jpeg, 420mpeg2, 422, 444 and mono; deeper ones end in the depth, as 420p10 or mono12.
    bps = 2 if c.endswith(("10", "12", "16")) else 1
    if c.startswith("mono"):
        sizes = [w * h]
    elif c.startswith("444"):
        sizes = [w * h] * 3
    elif c.startswith("422"):
        sizes = [w * h, (w + 1) // 2 * h, (w + 1) // 2 * h]
    else:
        sizes = [w * h, (w + 1) // 2 * ((h + 1) // 2), (w + 1) // 2 * ((h + 1) // 2)]
    sizes = [s * bps for s in sizes]
    frames, pos = [], end + 1
    while data.startswith(b"FRAME", pos):
        pos = data.index(b"\n", pos) + 1
        planes = []
        for s in sizes:
            planes.append(data[pos:pos + s])
            pos += s
        frames.append(planes)
    return head, frames


def md5(*chunks):
    m = hashlib.md5()
    for c in chunks:
        m.update(c)
    return m.hexdigest()


# ---------------------------------------------------------------- HEIF items (for the references only)

def boxes(data, start, end):
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        head = 8
        if size == 1:
            size, head = struct.unpack(">Q", data[pos + 8:pos + 16])[0], 16
        elif size == 0:
            size = end - pos
        if size < head or pos + size > end:
            raise ValueError(f"bad box {kind} at {pos}")
        yield kind.decode("latin-1"), pos + head, pos + size
        pos += size


def uint(data, pos, n):
    return int.from_bytes(data[pos:pos + n], "big") if n else 0


def items(data):
    """Lists the meta box's items: id, type, properties, references, extents and payload. Raises on a malformed file."""
    top = {k: (s, e) for k, s, e in boxes(data, 0, len(data))}
    ms, me = top["meta"]
    meta = {k: (s, e) for k, s, e in boxes(data, ms + 4, me)}
    result, idat = {}, b""
    if "idat" in meta:
        idat = data[meta["idat"][0]:meta["idat"][1]]
    s, e = meta["pitm"]
    primary = uint(data, s + 4, 2 if data[s] == 0 else 4)
    s, e = meta["iinf"]
    version = data[s]
    for kind, bs, be in boxes(data, s + 4 + (2 if version == 0 else 4), e):
        if kind != "infe":
            continue
        v = data[bs]
        if v < 2:
            continue
        p = bs + 4
        n = 2 if v == 2 else 4
        item_id = uint(data, p, n)
        p += n + 2
        item_type = data[p:p + 4].decode("latin-1")
        p += 4
        name_end = data.index(b"\0", p, be) if b"\0" in data[p:be] else be
        entry = {"id": item_id, "type": item_type, "hidden": bool(data[bs + 3] & 1), "properties": [], "refs": {}}
        if item_type == "mime":
            q = name_end + 1
            ce = data.index(b"\0", q, be) if b"\0" in data[q:be] else be
            entry["content_type"] = data[q:ce].decode("latin-1")
        result[item_id] = entry
    s, e = meta["iloc"]
    v = data[s]
    p = s + 4
    off_size, len_size = data[p] >> 4, data[p] & 15
    base_size, index_size = data[p + 1] >> 4, (data[p + 1] & 15) if v in (1, 2) else 0
    p += 2
    count = uint(data, p, 2 if v < 2 else 4)
    p += 2 if v < 2 else 4
    for _ in range(count):
        item_id = uint(data, p, 2 if v < 2 else 4)
        p += 2 if v < 2 else 4
        method = 0
        if v in (1, 2):
            method = uint(data, p, 2) & 15
            p += 2
        p += 2
        base = uint(data, p, base_size)
        p += base_size
        extents = []
        extent_count = uint(data, p, 2)
        p += 2
        for _ in range(extent_count):
            p += index_size
            off = uint(data, p, off_size)
            p += off_size
            length = uint(data, p, len_size)
            p += len_size
            extents.append((base + off, length))
        source = idat if method == 1 else data
        payload = b"".join(source[o:o + n] if n else source[o:] for o, n in extents)
        if item_id in result:
            result[item_id].update(construction_method=method, extents=[list(x) for x in extents])
            result[item_id]["payload"] = payload
    if "iref" in meta:
        s, e = meta["iref"]
        n = 2 if data[s] == 0 else 4
        for kind, bs, be in boxes(data, s + 4, e):
            src = uint(data, bs, n)
            count = uint(data, bs + n, 2)
            targets = [uint(data, bs + n + 2 + i * n, n) for i in range(count)]
            if src in result:
                result[src]["refs"].setdefault(kind, []).extend(targets)
    if "iprp" in meta:
        s, e = meta["iprp"]
        iprp = list(boxes(data, s, e))
        props = [(k, bs, be) for k, bs, be in boxes(data, *[(bs, be) for k, bs, be in iprp if k == "ipco"][0])]
        for k, bs, be in iprp:
            if k != "ipma":
                continue
            v, flags = data[bs], uint(data, bs + 1, 3)
            p = bs + 4
            entry_count = uint(data, p, 4)
            p += 4
            for _ in range(entry_count):
                item_id = uint(data, p, 2 if v < 1 else 4)
                p += 2 if v < 1 else 4
                n_assoc = data[p]
                p += 1
                for _ in range(n_assoc):
                    if flags & 1:
                        raw = uint(data, p, 2)
                        essential, index = raw >> 15, raw & 0x7FFF
                        p += 2
                    else:
                        essential, index = data[p] >> 7, data[p] & 0x7F
                        p += 1
                    if index == 0 or item_id not in result:
                        continue
                    kind, ps, pe = props[index - 1]
                    prop = {"type": kind, "essential": bool(essential)}
                    if kind == "ispe":
                        prop["size"] = [uint(data, ps + 4, 4), uint(data, ps + 8, 4)]
                    elif kind == "auxC":
                        prop["aux_type"] = data[ps + 4:pe].split(b"\0")[0].decode("latin-1")
                    elif kind == "pixi":
                        prop["depths"] = list(data[ps + 5:ps + 5 + data[ps + 4]])
                    elif kind == "av1C":
                        b1, b2 = data[ps + 1], data[ps + 2]
                        prop.update(profile=b1 >> 5, level=b1 & 31, tier=b2 >> 7, high_bitdepth=b2 >> 6 & 1,
                                    twelve_bit=b2 >> 5 & 1, monochrome=b2 >> 4 & 1, subsampling=[b2 >> 3 & 1, b2 >> 2 & 1],
                                    chroma_sample_position=b2 & 3, config_obus=pe - ps - 4)
                    elif kind == "colr":
                        prop["colour_type"] = data[ps:ps + 4].decode("latin-1")
                        if prop["colour_type"] == "nclx":
                            prop["cicp"] = [uint(data, ps + 4, 2), uint(data, ps + 6, 2), uint(data, ps + 8, 2), data[ps + 10] >> 7]
                        else:
                            prop["icc_size"] = pe - ps - 4
                    elif kind in ("irot", "imir", "lsel"):
                        prop["value"] = uint(data, ps, pe - ps)
                    elif kind == "clap":
                        prop["fractions"] = list(struct.unpack(">8i", data[ps:ps + 32]))
                    elif kind in ("clli", "mdcv", "pasp", "a1op", "a1lx"):
                        prop["bytes"] = data[ps:pe].hex()
                    result[item_id]["properties"].append(prop)
    for entry in result.values():
        entry["primary"] = entry["id"] == primary
    return [result[k] for k in sorted(result)]


# ---------------------------------------------------------------- decoding one AV1 item

def decode_item(payload, out_y4m=None):
    """Decodes one AV1 item with dav1d (grain on, grain off, filters off) and aomdec; returns the MD5s of the last frame's
    planes (a layered item's frames are its layers, and the last is the best)."""
    obu = f"{TMP}/item.obu"
    with open(obu, "wb") as f:
        # dav1d's Section 5 reader needs a temporal delimiter first; libavif writes one in its items, other writers may not.
        f.write(payload if payload[:1] == b"\x12" else b"\x12\x00" + payload)
    result = {}
    for key, extra in (("dav1d", ["--filmgrain", "1"]), ("dav1d_no_grain", ["--filmgrain", "0"]),
                       ("dav1d_no_filters", ["--filmgrain", "0", "--inloopfilters", "none"])):
        # One file per frame: a layered item's frames differ in size.
        for old in glob.glob(f"{TMP}/d_*.y4m"):
            os.remove(old)
        r = sh("dav1d", "-q", "-i", obu, "--demuxer", "section5", "-o", f"{TMP}/d_%n.y4m", *extra, check=False)
        written = sorted(glob.glob(f"{TMP}/d_*.y4m"), key=lambda f: int(f[len(TMP) + 3:-4]))
        if r.returncode or not written:
            result[key + "_error"] = (r.stderr or r.stdout).strip()[-300:]
            continue
        shutil.copy(written[-1], f"{TMP}/d.y4m")
        head, frames = read_y4m(f"{TMP}/d.y4m")
        result[key + "_md5"] = md5(*frames[0])
        if key == "dav1d":
            result["frames"] = len(written)
            result["y4m_colorspace"] = head.get("C", "420jpeg")
            result["size"] = [int(head["W"]), int(head["H"])]
            if out_y4m:
                shutil.copy(f"{TMP}/d.y4m", out_y4m)
    if result.get("frames", 1) > 1:
        # A layered item: aomdec's y4m cannot change size between frames, so compare the end of its raw output with
        # dav1d's last frame.
        r = sh("aomdec", "--rawvideo", "-o", f"{TMP}/a.yuv", obu, check=False)
        if r.returncode:
            result["aomdec_error"] = (r.stderr or r.stdout).strip()[-300:]
        elif "dav1d_md5" in result and not result["y4m_colorspace"].startswith("mono"):
            last = b"".join(read_y4m(f"{TMP}/d.y4m")[1][0])
            result["aomdec_equal"] = open(f"{TMP}/a.yuv", "rb").read()[-len(last):] == last
        return result
    r = sh("aomdec", "-o", f"{TMP}/a.y4m", obu, check=False)
    if r.returncode:
        result["aomdec_error"] = (r.stderr or r.stdout).strip()[-300:]
    elif "dav1d_md5" in result:
        _, frames = read_y4m(f"{TMP}/a.y4m")
        planes = frames[0][:1] if result["y4m_colorspace"].startswith("mono") else frames[0]
        result["aomdec_equal"] = md5(*planes) == result["dav1d_md5"]
    return result


# ---------------------------------------------------------------- corpus sources

def sources():
    """Writes the avifenc inputs to TMP and returns their paths by name."""
    duck, _ = read_png(f"{IMAGES}/ops/duck.png")
    egret, _ = read_png(f"{IMAGES}/ops/egret.png")
    duck, egret = duck[..., :3], egret[..., :3]
    small = duck[96:288, 160:416]  # 256x192
    mid, egret_mid = duck[120:360, 160:480], egret[160:480, 120:360]  # 320x240 and 240x320
    s = {}

    def put(name, pixels, depth=8):
        s[name] = f"{TMP}/src-{name}.png"
        write_png(s[name], pixels, depth)

    put("duck", duck)
    put("egret", egret)
    put("duck-mid", mid)
    put("egret-mid", egret_mid)
    put("duck-small", small)
    gray = (small @ np.array([0.299, 0.587, 0.114])).round().astype(np.uint16)[..., None]
    put("duck-gray", gray)
    ramp = np.linspace(0, 255, small.shape[1]).round().astype(np.uint16)
    alpha = np.broadcast_to(ramp[None, :, None], small.shape[:2] + (1,))
    # A hard-edged hole as well as the ramp, so alpha has both smooth and sharp content.
    alpha = alpha.copy()
    alpha[60:132, 96:160] = 0
    put("duck-alpha", np.concatenate([small, alpha], axis=2))
    put("duck-gray-alpha", np.concatenate([gray, alpha], axis=2))
    wide = small.astype(np.uint32) * 257 ^ (np.arange(small.size, dtype=np.uint32).reshape(small.shape) * 2654435761 >> 24 & 0xFF)
    put("duck-16bit", wide.astype(np.uint16), 16)
    put("odd-31x17", duck[200:217, 300:331])
    put("odd-1x1", duck[240:241, 320:321])
    put("odd-33x65", duck[100:165, 300:333])
    put("odd-129x67", duck[100:167, 200:329])
    # Four frames panning over the duck, for sequences.
    for i in range(4):
        put(f"pan-{i}", duck[120:312, 160 + 16 * i:416 + 16 * i])
    for name in ("zone", "shapes"):
        s[name] = f"{IMAGES}/ops/{name}.png"
    for name in ("duck-pq", "duck-hlg"):
        s[name] = f"{IMAGES}/hdr/{name}.png"
    # SDR rendition of duck-pq at its size, the base of the gain map files.
    pq, _ = read_png(f"{IMAGES}/hdr/duck-pq.png")
    put("duck-pq-sdr", duck[::2, ::2][:pq.shape[0], :pq.shape[1]])
    sh("exiftool", "-b", "-exif", f"{IMAGES}/metadata/synthetic/phone-orientation6.jpg", "-w!", f"{TMP}/%f.exif")
    sh("exiftool", "-b", "-xmp", f"{IMAGES}/metadata/synthetic/xmp-elements.jpg", "-w!", f"{TMP}/%f.xmp")
    s["exif"] = f"{TMP}/phone-orientation6.exif"
    s["xmp"] = f"{TMP}/xmp-elements.xmp"
    return s


# name: (source(s), avifenc options, what it covers). Sources are keys of sources() (several joined by +), or paths under
# tests/images. {exif}, {xmp} and {icc:<file>} in the options are replaced with paths.
AVIFENC = {
    # Chroma formats and bit depths (libaom, default speed 6).
    "duck-420-q60": ("duck-mid", "-y 420 -q 60", "8-bit 4:2:0, default speed"),
    "duck-422-q60": ("duck-mid", "-y 422 -q 60", "8-bit 4:2:2 (profile 2)"),
    "duck-444-q60": ("duck-mid", "-y 444 -q 60", "8-bit 4:4:4 (profile 1)"),
    "duck-400-q60": ("duck-mid", "-y 400 -q 60", "8-bit monochrome"),
    "duck-10bit-420": ("duck-small", "-d 10 -y 420 -q 60", "10-bit 4:2:0"),
    "duck-10bit-422": ("duck-small", "-d 10 -y 422 -q 60", "10-bit 4:2:2"),
    "duck-10bit-444": ("duck-small", "-d 10 -y 444 -q 60", "10-bit 4:4:4"),
    "duck-10bit-400": ("duck-small", "-d 10 -y 400 -q 60", "10-bit monochrome"),
    "duck-12bit-420": ("duck-small", "-d 12 -y 420 -q 60", "12-bit 4:2:0 (profile 2)"),
    "duck-12bit-422": ("duck-small", "-d 12 -y 422 -q 60", "12-bit 4:2:2"),
    "duck-12bit-444": ("duck-small", "-d 12 -y 444 -q 60", "12-bit 4:4:4"),
    "duck-12bit-400": ("duck-small", "-d 12 -y 400 -q 60", "12-bit monochrome"),
    "duck-16bit-input-12bit": ("duck-16bit", "-d 12 -y 444 -q 80", "16-bit PNG input at 12 bits"),
    "duck-gray-auto": ("duck-gray", "-q 60", "gray PNG, monochrome by default"),
    "duck-limited-range": ("duck-mid", "-y 420 -r limited -q 60", "limited range"),
    "duck-bt709-matrix": ("duck-mid", "-y 420 --cicp 1/13/1 -q 60", "BT.709 matrix"),
    "duck-bt2020-ncl": ("duck-mid", "-y 420 --cicp 9/13/9 -q 60", "BT.2020 non-constant luminance matrix"),
    "duck-ycgco": ("duck-mid", "-y 444 --cicp 1/13/8 -q 60", "YCgCo matrix"),
    "duck-unspecified-cicp": ("duck-mid", "-y 420 --cicp 2/2/2 -q 60", "unspecified CICP (2/2/2)"),
    # Lossless (identity matrix, Walsh-Hadamard).
    "duck-lossless": ("duck-mid", "-l", "lossless, identity matrix (GBR)"),
    "duck-lossless-10bit": ("duck-small", "-l -d 10", "lossless, 10 bits"),
    "duck-lossless-12bit": ("duck-16bit", "-l -d 12", "lossless, 12 bits"),
    "duck-lossless-400": ("duck-gray", "-l", "lossless monochrome"),
    "duck-lossless-420": ("duck-mid", "-y 420 -q 100 --cicp 1/13/6", "lossless coding of 4:2:0 YUV (lossy conversion)"),
    # Quality and speed: different partitions, transforms and filter strengths.
    "duck-q5": ("duck-mid", "-y 420 -q 5", "very low quality: large blocks, strong filters"),
    "duck-q30": ("duck-mid", "-y 420 -q 30", "low quality"),
    "duck-q95": ("duck-mid", "-y 420 -q 95", "high quality: small blocks, many coefficients"),
    "duck-speed0": ("duck-small", "-y 420 -q 60 -s 0", "speed 0: every tool libaom searches"),
    "duck-speed3": ("duck-mid", "-y 444 -q 60 -s 3", "speed 3, 4:4:4"),
    "duck-speed10": ("duck-mid", "-y 420 -q 60 -s 10", "speed 10"),
    "duck-full": ("duck", "-y 420 -q 60", "640x480, several superblocks"),
    "egret-full": ("egret", "-y 420 -q 60", "480x640 portrait"),
    # Coding tools libaom can be steered to.
    "duck-tiles-2x2": ("duck", "-y 420 -q 60 --tilerowslog2 1 --tilecolslog2 1", "2x2 tiles"),
    "duck-tiles-4x1": ("duck-mid", "-y 420 -q 60 --tilerowslog2 0 --tilecolslog2 2", "four tile columns"),
    "duck-sb128": ("duck", "-y 420 -q 60 -a sb-size=128", "128x128 superblocks"),
    "duck-sb64": ("duck-mid", "-y 420 -q 60 -a sb-size=64", "64x64 superblocks"),
    "zone-screen": ("zone", "-y 444 -q 60 -a tune-content=screen", "screen content: palette, intra block copy"),
    "shapes-screen": ("shapes", "-y 420 -q 70 -a tune-content=screen -a enable-intrabc=1 -a enable-palette=1",
                      "screen content, 4:2:0"),
    "shapes-screen-10bit": ("shapes", "-y 444 -d 10 -q 70 -a tune-content=screen", "screen content, 10 bits"),
    "duck-grain": ("duck-mid", "-y 420 -q 60 -a denoise-noise-level=25", "film grain from denoising"),
    "duck-grain-test1": ("duck-mid", "-y 420 -q 60 -a film-grain-test=1", "film grain test vector 1"),
    "duck-grain-test10-10bit": ("duck-small", "-y 420 -d 10 -q 60 -a film-grain-test=10", "film grain test vector 10, 10 bits"),
    "duck-grain-test16-444": ("duck-mid", "-y 444 -q 60 -a film-grain-test=16", "film grain test vector 16, 4:4:4"),
    "duck-grain-400": ("duck-gray", "-q 60 -a film-grain-test=3", "film grain, monochrome"),
    "duck-qm": ("duck-mid", "-y 420 -q 60 -a enable-qm=1 -a qm-min=0 -a qm-max=8", "quantizer matrices"),
    "duck-deltaq": ("duck-mid", "-y 420 -q 60 -a deltaq-mode=1 -a aq-mode=1", "delta q and segmentation"),
    "duck-chroma-deltaq": ("duck-mid", "-y 444 -q 60 -a enable-chroma-deltaq=1", "chroma delta q"),
    "duck-sharpness": ("duck-mid", "-y 420 -q 40 -a sharpness=7", "loop filter sharpness 7"),
    "duck-no-cdef": ("duck-mid", "-y 420 -q 40 -a enable-cdef=0", "no CDEF"),
    "duck-restoration": ("duck-mid", "-y 420 -q 40 -a enable-restoration=1 -s 4", "loop restoration"),
    "duck-no-filter-intra": ("duck-mid", "-y 420 -q 60 -a enable-filter-intra=0 -a enable-cfl-intra=0",
                             "no filter intra, no chroma from luma"),
    "duck-tx64": ("duck", "-y 420 -q 20 -a enable-tx64=1 -a max-partition-size=128", "64x64 transforms"),
    "duck-rect-tx-off": ("duck-mid", "-y 420 -q 60 -a enable-rect-tx=0 -a enable-rect-partitions=0",
                         "square partitions and transforms only"),
    # Odd sizes.
    "odd-1x1": ("odd-1x1", "-y 444 -q 60", "1x1"),
    "odd-31x17-420": ("odd-31x17", "-y 420 -q 60", "31x17, 4:2:0"),
    "odd-33x65-422": ("odd-33x65", "-y 422 -q 60", "33x65, 4:2:2"),
    "odd-129x67-420": ("odd-129x67", "-y 420 -q 60", "129x67, past one 128 superblock"),
    # Alpha.
    "duck-alpha": ("duck-alpha", "-y 420 -q 60 --qalpha 80", "alpha item"),
    "duck-alpha-lossless": ("duck-alpha", "-y 444 -q 60 --qalpha 100", "lossless alpha"),
    "duck-alpha-premultiplied": ("duck-alpha", "-y 420 -q 60 --qalpha 80 -p", "premultiplied alpha (prem reference)"),
    "duck-alpha-10bit": ("duck-alpha", "-y 444 -d 10 -q 60 --qalpha 80", "10-bit color and alpha"),
    "duck-gray-alpha": ("duck-gray-alpha", "-q 60 --qalpha 80", "monochrome with alpha"),
    # Grids.
    "duck-grid-2x2": ("duck", "-y 420 -q 60 -g 2x2", "2x2 grid of 320x240 cells"),
    "duck-grid-4x1-alpha": ("duck-alpha", "-y 420 -q 60 --qalpha 80 -g 4x1", "grid of 64x192 cells, alpha grid"),
    "duck-grid-1x3-444": ("duck-mid", "-y 444 -d 10 -q 60 -g 1x3", "1x3 grid of 320x80 cells, 10-bit 4:4:4"),
    # Geometry.
    "duck-clap": ("duck-mid", "-y 420 -q 60 --crop 20,16,280,200", "clap crop to 280x200"),
    "duck-irot1": ("duck-mid", "-y 420 -q 60 --irot 1", "irot 90 degrees anticlockwise"),
    "duck-irot2-imir0": ("duck-mid", "-y 420 -q 60 --irot 2 --imir 0", "irot 180 and top-to-bottom mirror"),
    "duck-imir1": ("duck-mid", "-y 420 -q 60 --imir 1", "left-to-right mirror"),
    "duck-clap-irot3-imir1": ("duck-mid", "-y 444 -q 60 --crop 10,8,300,220 --irot 3 --imir 1", "clap, irot 270 and mirror"),
    "duck-pasp": ("duck-mid", "-y 420 -q 60 --pasp 4,3", "pasp 4:3"),
    # Color and metadata.
    "duck-icc-p3": ("duck-mid", "-y 420 -q 60 --icc {icc:DisplayP3-v4.icc}", "Display P3 ICC with nclx"),
    "duck-icc-rec2020-10bit": ("duck-small", "-y 444 -d 10 -q 60 --icc {icc:Rec2020-v4.icc}", "Rec. 2020 ICC, 10 bits"),
    "duck-pq": ("duck-pq", "-y 444 -d 10 -q 70 --cicp 9/16/9 --clli 2000,400", "PQ, BT.2020, clli"),
    "duck-pq-420-12bit": ("duck-pq", "-y 420 -d 12 -q 70 --cicp 9/16/9 -r limited", "PQ, 12-bit 4:2:0 limited range"),
    "duck-hlg": ("duck-hlg", "-y 420 -d 10 -q 70 --cicp 9/18/9", "HLG, BT.2020"),
    "duck-exif-xmp": ("duck-mid", "-y 420 -q 60 --exif {exif} --xmp {xmp}", "Exif (orientation 6) and XMP items"),
    "duck-exif-irot": ("duck-mid", "-y 420 -q 60 --exif {exif} --irot 3", "Exif orientation 6 and irot both"),
    # Sequences and layers.
    "pan-sequence": ("pan-0+pan-1+pan-2+pan-3", "-y 420 -q 60", "avis sequence with inter frames"),
    "pan-sequence-keyframes": ("pan-0+pan-1+pan-2+pan-3", "-y 420 -q 60 -k 1", "avis sequence, every frame a keyframe"),
    "pan-sequence-alpha": ("duck-alpha+duck-alpha", "-y 420 -q 60 --qalpha 80", "avis sequence with an alpha track"),
    "duck-progressive": ("duck-mid", "-y 420 -q 60 --progressive", "progressive (layered) still, two layers"),
    "duck-layered-3": ("duck-mid", "-y 420 --layered -q:u 20 {in} -q:u 50 {in} -q:u 80 {in}",
                       "three quality layers (a1lx, lsel)"),
    # Other encoders.
    "duck-svt": ("duck-mid", "-c svt -y 420 -q 60", "SVT-AV1 2.3.0"),
    "duck-svt-10bit": ("duck-small", "-c svt -y 420 -d 10 -q 60", "SVT-AV1, 10 bits"),
    "duck-rav1e": ("duck-mid", "-c rav1e -y 420 -q 60", "rav1e 0.7.1"),
    "duck-rav1e-444-12bit": ("duck-small", "-c rav1e -y 444 -d 12 -q 60", "rav1e, 12-bit 4:4:4"),
    "duck-rav1e-tiles": ("duck-mid", "-c rav1e -y 420 -q 60 --tilerowslog2 1 --tilecolslog2 1", "rav1e, 2x2 tiles"),
}

# Streams libaom can only be steered to through aomenc (avifenc's all-intra mode refuses these options), one frame each, put in
# the container avifenc writes for the same picture: (source, chroma format, aomenc options, what it covers).
AOMENC = {
    "duck-superres": ("duck-mid", "420", "--superres-mode=1 --superres-denominator=12", "superres, denominator 12"),
    "duck-superres-444": ("duck-mid", "444", "--profile=1 --superres-mode=1 --superres-denominator=16",
                          "superres at half width, 4:4:4"),
    "duck-superres-restoration": ("duck", "420", "--superres-mode=1 --superres-denominator=10 --enable-restoration=1",
                                  "superres with loop restoration"),
    "duck-aomenc-reduced-tx": ("duck-mid", "420", "--reduced-tx-type-set=1 --enable-smooth-intra=0 --enable-paeth-intra=0",
                               "reduced transform set, no smooth or Paeth"),
}


def write_y4m(path, rgb, chroma):
    """Writes an 8-bit full-range BT.601 y4m (4:2:0 or 4:4:4) of an RGB picture, for aomenc and avifenc."""
    rgb = rgb.astype(np.float64)
    y = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
    u = (rgb[..., 2] - y) / 1.772 + 128
    v = (rgb[..., 0] - y) / 1.402 + 128
    h, w = y.shape
    if chroma == "420":
        def sub(p):
            p = np.pad(p, ((0, h % 2), (0, w % 2)), mode="edge")
            return (p[0::2, 0::2] + p[1::2, 0::2] + p[0::2, 1::2] + p[1::2, 1::2]) / 4
        u, v = sub(u), sub(v)
    planes = [np.clip(np.round(p), 0, 255).astype(np.uint8).tobytes() for p in (y, u, v)]
    with open(path, "wb") as f:
        f.write(f"YUV4MPEG2 W{w} H{h} F25:1 Ip A1:1 C{'420jpeg' if chroma == '420' else '444'} XCOLORRANGE=FULL\n".encode())
        f.write(b"FRAME\n" + b"".join(planes))


def obus(stream):
    """Splits a Section 5 stream into (type, whole OBU bytes, payload)."""
    pos, out = 0, []
    while pos < len(stream):
        start, header = pos, stream[pos]
        kind, has_ext, has_size = header >> 3 & 15, header >> 2 & 1, header >> 1 & 1
        pos += 1 + has_ext
        size, shift = 0, 0
        while has_size:
            b = stream[pos]
            pos += 1
            size |= (b & 127) << shift
            shift += 7
            if b < 128:
                break
        if not has_size:
            size = len(stream) - pos
        out.append((kind, stream[start:pos + size], stream[pos:pos + size]))
        pos += size
    return out


def swap_payload(container, name, stream):
    """Puts an aomenc stream in place of the primary item of an avifenc file with one item (a temporal delimiter first, as
    libavif writes its items)."""
    data = open(container, "rb").read()
    listed = items(data)
    primary = [i for i in listed if i["primary"]][0]
    assert len(listed) == 1 and len(primary["extents"]) == 1, name
    (offset, length), = primary["extents"]
    parts = obus(stream)
    payload = stream
    head = data[:offset]
    old = struct.pack(">II", offset, length)
    assert head.count(old) == 1, f"{name}: iloc extent not found"
    head = head.replace(old, struct.pack(">II", offset, len(payload)))
    mdat = head.rfind(b"mdat") - 4
    size = struct.unpack(">I", head[mdat:mdat + 4])[0]
    head = head[:mdat] + struct.pack(">I", size + len(payload) - length) + head[mdat + 4:]
    # av1C's profile and level from the new sequence header (no timing info, one operating point, as aomenc writes stills).
    seq = [body for kind, _, body in parts if kind == 1][0]
    bits = int.from_bytes(seq[:4], "big")
    profile, reduced = bits >> 29, bits >> 27 & 1
    level = bits >> 22 & 31 if reduced else bits >> 7 & 31
    av1c = head.index(b"av1C") + 4
    head = head[:av1c + 1] + bytes([profile << 5 | level]) + head[av1c + 2:]
    with open(f"{OUT}/{name}.avif", "wb") as f:
        f.write(head + payload + data[offset + length:])


# Camera files from raw.pixls.us developments (CC0) in libjxl's testdata.
CAMERA = {
    "camera-nikon-d300-rec2020-12bit": ("Nikon-D300-12bit_2020_g1_dt.png", "-y 444 -d 12 -q 80 --ignore-icc --cicp 9/8/9",
                                        "camera, Rec. 2020 linear, 12 bits"),
    "camera-pixel2xl-srgb": ("Google-Pixel2XL-16bit_srgb8_v4_krita.png", "-y 420 -q 60", "camera, sRGB ICC"),
    "camera-sony-rx1rm2-709-10bit": ("Sony-DSC-RX1RM2-14bit_709_v4_krita.png", "-y 420 -d 10 -q 70", "camera, Rec. 709 ICC"),
    "camera-huawei-eva-srgb-422": ("HUAWEI-EVA-L09-16bit_srgb8_dt.png", "-y 422 -q 60", "camera, 4:2:2"),
}

# Small libavif test files (tests/data at v1.4.2, BSD-2), copied as they are.
LIBAVIF = {
    "abc_color_irot_alpha_irot.avif": "irot on color and alpha",
    "abc_color_irot_alpha_NOirot.avif": "irot on color only",
    "alpha_noispe.avif": "alpha item without ispe",
    "circle_custom_properties.avif": "unknown properties",
    "clap_irot_imir_non_essential.avif": "clap, irot and imir not marked essential",
    "clop_irot_imor.avif": "unknown clop and imor properties",
    "color_grid_alpha_grid_gainmap_nogrid.avif": "grid color and alpha, gain map",
    "color_grid_alpha_grid_tile_shared_in_dimg.avif": "grid with a shared cell",
    "color_grid_alpha_nogrid.avif": "grid color, single alpha",
    "color_grid_gainmap_different_grid.avif": "gain map on a different grid",
    "color_nogrid_alpha_nogrid_gainmap_grid.avif": "gain map grid",
    "colors-animated-12bpc-keyframes-0-2-3.avif": "12-bit sequence, keyframes 0, 2 and 3",
    "colors-animated-8bpc.avif": "8-bit sequence",
    "colors-animated-8bpc-alpha-exif-xmp.avif": "sequence with alpha, Exif and XMP",
    "colors-animated-8bpc-audio.avif": "sequence with an audio track",
    "colors-animated-8bpc-depth-exif-xmp.avif": "sequence with a depth track",
    "draw_points_idat.avif": "item data in idat",
    "draw_points_idat_metasize0.avif": "meta box of size 0",
    "draw_points_idat_progressive.avif": "progressive, idat",
    "draw_points_idat_progressive_metasize0.avif": "progressive, meta size 0",
    "extended_pixi.avif": "pixi with an extended form",
    "paris_icc_exif_xmp.avif": "ICC, Exif and XMP",
    "poc_b_506387278.avif": "a fuzzer find",
    "seine_hdr_gainmap_small_srgb.avif": "HDR base, small gain map",
    "seine_hdr_gainmap_srgb.avif": "HDR base with a gain map",
    "seine_hdr_gainmap_wrongaltr.avif": "gain map with a wrong altr group",
    "seine_hdr_rec2020.avif": "HDR Rec. 2020",
    "seine_hdr_srgb.avif": "HDR sRGB primaries",
    "seine_sdr_gainmap_big_srgb.avif": "gain map bigger than the base",
    "seine_sdr_gainmap_gammazero.avif": "gain map gamma zero",
    "seine_sdr_gainmap_notmapbrand.avif": "gain map without the tmap brand",
    "seine_sdr_gainmap_srgb.avif": "SDR base with a gain map",
    "seine_sdr_gainmap_srgb_icc.avif": "SDR base with ICC and a gain map",
    "sofa_grid1x5_420.avif": "1x5 grid",
    "sofa_grid1x5_420_dimg_repeat.avif": "grid with a repeated dimg",
    "sofa_grid1x5_420_reversed_dimg_order.avif": "grid with reversed dimg order",
    "supported_gainmap_writer_version_with_extra_bytes.avif": "gain map metadata with extra bytes",
    "unsupported_gainmap_minimum_version.avif": "unsupported gain map minimum version",
    "unsupported_gainmap_version.avif": "unsupported gain map version",
    "unsupported_gainmap_writer_version_with_extra_bytes.avif": "unsupported writer version",
    "white_1x1.avif": "one white pixel",
    "colors_hdr_p3.avif": "HDR P3",
    "colors_hdr_rec2020.avif": "HDR Rec. 2020",
    "colors_hdr_srgb.avif": "HDR sRGB",
    "colors_sdr_srgb.avif": "SDR sRGB",
    "colors_text_hdr_p3.avif": "HDR P3 with text",
    "colors_text_hdr_rec2020.avif": "HDR Rec. 2020 with text",
    "colors_text_hdr_srgb.avif": "HDR sRGB with text",
    "colors_text_sdr_srgb.avif": "SDR sRGB with text",
    "colors_text_wcg_hdr_rec2020.avif": "wide gamut HDR with text",
    "colors_text_wcg_sdr_rec2020.avif": "wide gamut SDR with text",
    "colors_wcg_hdr_rec2020.avif": "wide gamut HDR",
}


def exiftool(path):
    r = sh("exiftool", "-j", "-G1", "-a", "-n", path, check=False)
    try:
        tags = json.loads(r.stdout)[0]
    except (ValueError, IndexError):
        return {"error": (r.stderr or r.stdout).strip()}
    return {k: v for k, v in tags.items() if not k.startswith("System:") and k not in ("SourceFile", "ExifTool:ExifToolVersion")}


def reference(name, path, entry, ref_folder, yuv_folder):
    """Writes avifdec's RGB and YUV decodes of one file and fills its manifest entry."""
    r = sh("avifdec", "--info", "-j", "1", path, check=False)
    entry["avifdec_info"] = (r.stdout + r.stderr).strip()
    entry["avifdec_info_ok"] = r.returncode == 0
    entry["exiftool"] = exiftool(path)
    data = open(path, "rb").read()
    try:
        listed = items(data)
    except Exception as e:  # malformed on purpose in a few test files
        listed, entry["items_error"] = [], str(e)
    for item in listed:
        payload = item.pop("payload", b"")
        item["payload_size"] = len(payload)
        if item["type"] == "av01" and payload:
            item.update(decode_item(payload))
    entry["items"] = listed
    deep = any(p.get("high_bitdepth") for i in listed for p in i["properties"] if p["type"] == "av1C")
    png = f"{ref_folder}/{name}.png"
    r = sh("avifdec", "-j", "1", "--png-compress", "9", "-d", "16" if deep else "8", path, png, check=False)
    if r.returncode:
        entry["avifdec_error"] = (r.stderr or r.stdout).strip()[-400:]
        return
    entry["reference"] = os.path.basename(png)
    # The YUV decode is large, so only its MD5 is committed; the y4m stays in .cache/avif/reference.
    yuv = f"{yuv_folder}/{name}.y4m"
    os.makedirs(yuv_folder, exist_ok=True)
    r = sh("avifdec", "-j", "1", "-c", "dav1d", path, yuv, check=False)
    if r.returncode:
        entry["y4m_error"] = (r.stderr or r.stdout).strip()[-400:]
    else:
        head, frames = read_y4m(yuv)
        entry["yuv_md5"] = md5(*frames[0])
        entry["yuv_colorspace"] = head.get("C", "420jpeg")
        r = sh("avifdec", "-j", "1", "-c", "aom", path, f"{TMP}/aom.y4m", check=False)
        if r.returncode == 0:
            entry["avifdec_aom_equal"] = open(f"{TMP}/aom.y4m", "rb").read() == open(yuv, "rb").read()


def corpus():
    os.makedirs(TMP, exist_ok=True)
    for folder in (OUT, f"{OUT}/libavif", REF):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    src = sources()
    manifest = {}
    version = sh("avifenc", "--version").stdout.strip().splitlines()[0]

    def path_of(source):
        return src.get(source) or (source if source.startswith("/") else f"{IMAGES}/{source}")

    def encode(name, source, options, covers):
        out = f"{OUT}/{name}.avif"
        options = options.replace("{exif}", src["exif"]).replace("{xmp}", src["xmp"])
        while "{icc:" in options:
            start = options.index("{icc:")
            end = options.index("}", start)
            options = options[:start] + f"{IMAGES}/icc/compact/{options[start + 5:end]}" + options[end + 1:]
        args = options.split()
        inputs = [path_of(s) for s in source.split("+")]
        if "{in}" in args:
            # Each -q:u applies to the inputs after it, so the options place the inputs.
            args = [inputs[0] if a == "{in}" else a for a in args]
            inputs = []
        r = sh("avifenc", "-j", "1", *args, *inputs, "-o", out, check=False)
        if r.returncode != 0:
            print(f"skipped {name}: {(r.stderr or r.stdout).strip()[-300:]}")
            return
        manifest[f"{name}.avif"] = {"source": source if source.split("+")[0] in src else source, "encoder": version,
                                    "options": options.replace(TMP + "/", "").replace(IMAGES + "/", ""), "covers": covers}

    for name, (source, options, covers) in AVIFENC.items():
        encode(name, source, options, covers)
    for name, (source, chroma, options, covers) in AOMENC.items():
        pixels, _ = read_png(src[source])
        write_y4m(f"{TMP}/aom.y4m", pixels[..., :3], chroma)
        sh("avifenc", "-j", "1", "-q", "60", f"{TMP}/aom.y4m", "-o", f"{TMP}/container.avif")
        sh("aomenc", "--limit=1", "--obu", "--cpu-used=4", "--end-usage=q", "--cq-level=28", *options.split(),
           "-o", f"{TMP}/aom.obu", f"{TMP}/aom.y4m")
        swap_payload(f"{TMP}/container.avif", name, open(f"{TMP}/aom.obu", "rb").read())
        manifest[f"{name}.avif"] = {"source": source, "encoder": "aomenc 3.15.2 in the container of " + version, "options": f"{chroma} {options}", "covers": covers}
    for name, (png, options, covers) in CAMERA.items():
        encode(name, f"{RAW_PIXLS}/{png}", options, covers)
        if f"{name}.avif" in manifest:
            manifest[f"{name}.avif"]["source"] = f"libjxl testdata external/raw.pixls/{png}"

    # Gain maps: an Ultra HDR JPEG converted, and an SDR base with a PQ alternate combined.
    for name, args, covers in (
            ("gainmap-uhdr-convert", ["convert", f"{IMAGES}/gainmap/adobe-xmp.jpg"], "gain map from a JPEG with Adobe hdrgm XMP"),
            ("gainmap-sdr-base", ["combine", src["duck-pq-sdr"], src["duck-pq"], "-q", "60", "--qgain-map", "60"],
             "SDR base, PQ alternate, gain map"),
            ("gainmap-hdr-base", ["combine", src["duck-pq"], src["duck-pq-sdr"], "-q", "60", "--qgain-map", "60", "-d", "10"],
             "PQ base, SDR alternate, gain map")):
        out = f"{OUT}/{name}.avif"
        r = sh("avifgainmaputil", *args, out, check=False)
        if r.returncode:
            print(f"skipped {name}: {(r.stderr or r.stdout).strip()[-300:]}")
            continue
        manifest[f"{name}.avif"] = {"source": " ".join(os.path.basename(a) for a in args[1:2]), "encoder": "avifgainmaputil "
                                    + version, "options": " ".join(a.replace(TMP + "/", "").replace(IMAGES + "/", "") for a in args), "covers": covers}

    for file, covers in LIBAVIF.items():
        shutil.copy(f"{CACHE}/libavif-1.4.2/tests/data/{file}", f"{OUT}/libavif/{file}")
        manifest[f"libavif/{file}"] = {"source": f"libavif v1.4.2 tests/data/{file}", "covers": covers}

    os.makedirs(f"{REF}/libavif", exist_ok=True)
    for file, entry in sorted(manifest.items()):
        print(file, flush=True)
        name = file[:-5]
        sub = os.path.dirname(name)
        reference(os.path.basename(name), f"{OUT}/{file}", entry, f"{REF}/{sub}".rstrip("/"),
                  f"{CACHE}/reference/corpus/{sub}".rstrip("/"))
    with open(f"{REF}/manifest.json", "w") as f:
        json.dump({"tools": tools(), "files": manifest}, f, indent=1, sort_keys=True)
        f.write("\n")


def tools():
    return {"avif": sh("avifenc", "--version").stdout.strip().splitlines()[0], "dav1d": sh("dav1d", "--version").stdout.strip(),
            "exiftool": sh("exiftool", "-ver").stdout.strip()}


# ---------------------------------------------------------------- conformance streams (stay in .cache)

def ivf_frames(path):
    data = open(path, "rb").read()
    if data[:4] != b"DKIF":
        return None
    pos = struct.unpack("<H", data[6:8])[0]
    frames = []
    while pos + 12 <= len(data):
        size = struct.unpack("<I", data[pos:pos + 4])[0]
        frames.append(data[pos + 12:pos + 12 + size])
        pos += 12 + size
    return frames


def frame_type(payload):
    """Reads the first frame header's frame_type from a temporal unit's OBUs (0 key, 1 inter, 2 intra-only, 3 switch)."""
    pos = 0
    reduced = False
    while pos < len(payload):
        header = payload[pos]
        kind, has_ext, has_size = header >> 3 & 15, header >> 2 & 1, header >> 1 & 1
        pos += 1 + has_ext
        size, shift = 0, 0
        if has_size:
            while True:
                b = payload[pos]
                pos += 1
                size |= (b & 127) << shift
                shift += 7
                if b < 128:
                    break
        else:
            size = len(payload) - pos
        body = payload[pos:pos + size]
        if kind == 1:
            reduced = bool(body[0] >> 3 & 1)
        if kind in (3, 6):
            if reduced:
                return 0
            show_existing = body[0] >> 7
            return None if show_existing else body[0] >> 5 & 3
        pos += size
    return None


def argon_full(path, demux, entry):
    """Checks dav1d's decode of a whole Argon stream (every frame and layer, as Argon's aomdec command) against Argon's MD5s,
    and whether every frame is intra (decoding the intra frames only gives the same output)."""
    folder, stem = os.path.dirname(os.path.dirname(path)), os.path.splitext(os.path.basename(path))[0]
    for key, sub in (("argon_md5", "md5_ref"), ("argon_md5_no_grain", "md5_no_film_grain")):
        ref = f"{folder}/{sub}/{stem}.md5"
        if os.path.isfile(ref):
            entry[key] = open(ref).read().split()[0]
    for key, extra in (("dav1d_full_md5", ["--filmgrain", "1"]), ("dav1d_full_no_grain_md5", ["--filmgrain", "0"]),
                       ("dav1d_intra_md5", ["--filmgrain", "1", "--decodeframetype", "intra"])):
        r = sh("dav1d", "-q", "-i", path, *demux, "--alllayers", "1", "--threads", "4", "--muxer", "md5", "-o", f"{TMP}/full.md5",
               *extra, check=False, timeout=900)
        if r.returncode:
            entry[key.replace("_md5", "_error")] = (r.stderr or r.stdout).strip()[-300:]
        else:
            entry[key] = open(f"{TMP}/full.md5").read().split()[0]
    if "argon_md5" in entry and "dav1d_full_md5" in entry:
        entry["dav1d_matches_argon"] = entry["dav1d_full_md5"] == entry["argon_md5"]
    if "dav1d_intra_md5" in entry and "dav1d_full_md5" in entry:
        entry["intra_only"] = entry["dav1d_intra_md5"] == entry["dav1d_full_md5"]


def conformance():
    os.makedirs(TMP, exist_ok=True)
    sets = {
        "aom": sorted(glob.glob(f"{CACHE}/aom-test-data/*.ivf")),
        "dav1d": sorted(p for p in glob.glob(f"{CACHE}/dav1d-test-data-1.5.4/**/*", recursive=True)
                        if os.path.isfile(p) and p.endswith((".ivf", ".obu", ".av1")) and "/oss-fuzz/" not in p),
        "argon": sorted(glob.glob(f"{CACHE}/argon/**/streams/*.obu", recursive=True)),
    }
    for name, streams in sets.items():
        if not streams:
            continue
        # Argon's references stay beside the streams (on another drive when .cache/avif/argon is a junction).
        folder = f"{CACHE}/argon/reference" if name == "argon" else f"{CACHE}/reference/{name}"
        os.makedirs(folder, exist_ok=True)
        summary = {}
        # The Argon set takes hours, so a rerun keeps the first frames already decoded and only redoes missing whole-stream
        # checks.
        previous = {}
        if name == "argon" and os.path.isfile(f"{folder}/summary.json"):
            with open(f"{folder}/summary.json") as f:
                previous = json.load(f)["streams"]
        for path in streams:
            rel = os.path.relpath(path, CACHE)
            # Argon's streams are named by set and test, as profile0_core_test10001.obu.
            key = "_".join(rel.split("/")[-3::2]) if name == "argon" else rel.replace("/", "_")
            # Argon's streams (also those in dav1d's test data) are Annex B except in its *_not_annexb sets; so is dav1d's
            # features/annexb.obu.
            annexb = ("argon" in rel and "not_annexb" not in rel) or rel.endswith("/annexb.obu")
            demux = ["--demuxer", "annexb"] if annexb else []
            if "dav1d_md5" in previous.get(key, {}):
                entry = previous[key]
                if "dav1d_full_md5" not in entry:
                    for k in [k for k in entry if k.startswith(("dav1d_full", "dav1d_intra", "argon_md5"))
                              or k in ("dav1d_matches_argon", "intra_only")]:
                        del entry[k]
                    argon_full(path, demux, entry)
                    print(key, "whole stream", entry.get("dav1d_full_md5", entry.get("dav1d_full_error")), flush=True)
                summary[key] = entry
                continue
            entry = {"stream": rel}
            frames = ivf_frames(path)
            if frames:
                entry["first_frame_type"] = frame_type(frames[0])
                entry["frames"] = len(frames)
            out = f"{folder}/{os.path.splitext(key)[0]}.y4m"
            for k, extra in (("dav1d", ["--filmgrain", "1"]), ("dav1d_no_grain", ["--filmgrain", "0"]),
                             ("dav1d_no_filters", ["--filmgrain", "0", "--inloopfilters", "none"])):
                target = out if k == "dav1d" else f"{TMP}/c.y4m"
                r = sh("dav1d", "-q", "-i", path, *demux, "--limit", "1", "--threads", "1", "-o", target, *extra, check=False,
                       timeout=300)
                if r.returncode:
                    entry[k + "_error"] = (r.stderr or r.stdout).strip()[-300:]
                    continue
                head, decoded = read_y4m(target)
                if not decoded:
                    entry[k + "_error"] = "no frame"
                    continue
                entry[k + "_md5"] = md5(*decoded[0])
                if k == "dav1d":
                    entry["size"] = [int(head["W"]), int(head["H"])]
                    entry["y4m_colorspace"] = head.get("C", "420jpeg")
                    entry["reference_yuv"] = os.path.basename(out)
            r = sh("aomdec", "--limit=1", "-o", f"{TMP}/a.y4m", *(["--annexb"] if annexb else []), path,
                   check=False, timeout=300)
            _, decoded = read_y4m(f"{TMP}/a.y4m") if r.returncode == 0 else ({}, [])
            if not decoded:
                entry["aomdec_error"] = (r.stderr or r.stdout).strip()[-300:] or "no frame"
            elif "dav1d_md5" in entry:
                planes = decoded[0][:1] if entry["y4m_colorspace"].startswith("mono") else decoded[0]
                entry["aomdec_equal"] = md5(*planes) == entry["dav1d_md5"]
            if name == "argon":
                argon_full(path, demux, entry)
            summary[key] = entry
            print(key, entry.get("dav1d_md5", entry.get("dav1d_error")), flush=True)
        with open(f"{folder}/summary.json", "w") as f:
            json.dump({"tools": tools(), "streams": summary}, f, indent=1, sort_keys=True)
            f.write("\n")


def extra():
    """The corpus treatment for AVIF files that stay in .cache (licenses that do not allow committing them)."""
    os.makedirs(TMP, exist_ok=True)
    folder = f"{CACHE}/reference/extra"
    os.makedirs(folder, exist_ok=True)
    manifest = {}
    files = sorted(glob.glob(f"{CACHE}/av1-avif-bf4c18d/testFiles/**/*.avif", recursive=True))
    files += [f"{CACHE}/libavif-1.4.2/tests/data/{f}" for f in ("arc_triomphe_extent1000_nullbyte_extent1310.avif",
                                                                 "weld_sato_12B_8B_q0.avif")]
    for path in files:
        rel = os.path.relpath(path, CACHE)
        name = rel.replace("/", "_")[:-5]
        entry = {"source": rel}
        print(rel, flush=True)
        reference(name, path, entry, folder, folder)
        manifest[rel] = entry
    with open(f"{folder}/manifest.json", "w") as f:
        json.dump({"tools": tools(), "files": manifest}, f, indent=1, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    for step in sys.argv[1:]:
        {"corpus": corpus, "conformance": conformance, "extra": extra}[step]()
