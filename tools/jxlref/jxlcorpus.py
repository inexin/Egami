"""Builds the Phase 2.9.3 JPEG XL corpus and references inside the egami-jxlref image. Run through tools/make-jxl-references.py.

  corpus       tests/images/jxl: files encoded here from public domain and CC0 pictures (see tests/images/LICENSES) by cjxl
               0.12.0 (lossless and lossy at several efforts and options, alpha, gray, 16-bit, float, animation, Exif, brob and
               level boxes; "camera" files from raw.pixls.us developments in four color spaces) and by Squoosh's CLI, JPEGs
               recompressed by cjxl, and small files copied from libjxl's testdata (CC BY 4.0) into tests/images/jxl/libjxl.
               tests/reference/jxl: djxl's decode of each (PNG at 8 or 16 bits, APNG for animations, PFM for float), and
               manifest.json with each file's source and options, jxlinfo -v's text, ExifTool's tags, whether djxl gives the
               original JPEG back, and how far jxl-oxide's decode is from djxl's.
  conformance  .cache/jxl/reference/conformance/<case> and .cache/jxl/reference/chromium/<file>: djxl's decode as the 18181-3
               runner calls it (npy, ICC out, original ICC, metadata JSON, spot colors not rendered, the JPEG when the case has
               one), jxl-oxide's npy and ICC, jxlinfo -v and ExifTool's JSON; summary.json with each decoder's peak and RMS
               error per frame against the case's reference_image.npy, compared as they are (no color transform), and whether
               its output ICC profile is byte-equal to the case's reference.icc.
"""
import glob
import hashlib
import json
import os
import shutil
import subprocess
import sys

import numpy as np

W = "/w"
CACHE = f"{W}/.cache/jxl"
IMAGES = f"{W}/tests/images"
OUT = f"{IMAGES}/jxl"
REF = f"{W}/tests/reference/jxl"
TMP = "/tmp/jxl"


def sh(*args, check=True, timeout=None):
    r = subprocess.run(list(args), capture_output=True, text=True, timeout=timeout)
    if check and r.returncode != 0:
        raise RuntimeError(f"{' '.join(args)}: {r.stderr.strip()}")
    return r


# ---------------------------------------------------------------- pixel files (PAM, PPM, PGM, PFM) without Pillow

def read_pnm(path):
    """Reads djxl's PPM/PGM (8 or 16 bits) or PAM into an (h, w, c) array."""
    data = open(path, "rb").read()
    if data[:2] == b"P7":
        end = data.index(b"ENDHDR\n") + 7
        head = dict(line.split(" ", 1) for line in data[3:end - 7].decode().splitlines() if " " in line)
        w, h, c, maxval = (int(head[k]) for k in ("WIDTH", "HEIGHT", "DEPTH", "MAXVAL"))
    else:
        fields, pos = [], 2
        while len(fields) < 3:
            while data[pos:pos + 1].isspace():
                pos += 1
            start = pos
            while not data[pos:pos + 1].isspace():
                pos += 1
            fields.append(int(data[start:pos]))
        end = pos + 1
        w, h, maxval = fields
        c = 3 if data[:2] == b"P6" else 1
    dtype = ">u2" if maxval > 255 else "u1"
    return np.frombuffer(data[end:], dtype=dtype, count=w * h * c).reshape(h, w, c).astype(np.uint16), maxval


def write_pnm(path, pixels, maxval):
    h, w, c = pixels.shape
    body = pixels.astype(">u2" if maxval > 255 else "u1").tobytes()
    if c in (1, 3):
        head = f"{'P5' if c == 1 else 'P6'}\n{w} {h}\n{maxval}\n"
    else:
        kind = "GRAYSCALE_ALPHA" if c == 2 else "RGB_ALPHA"
        head = f"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH {c}\nMAXVAL {maxval}\nTUPLTYPE {kind}\nENDHDR\n"
    with open(path, "wb") as f:
        f.write(head.encode() + body)


def write_pfm(path, pixels):
    h, w, c = pixels.shape
    with open(path, "wb") as f:
        f.write(f"{'PF' if c == 3 else 'Pf'}\n{w} {h}\n-1.0\n".encode())
        f.write(np.ascontiguousarray(pixels[::-1]).astype("<f4").tobytes())


def pixels_of(png):
    """Decodes a PNG to an array through a lossless cjxl and djxl round trip (the image has no Pillow)."""
    sh("cjxl", png, f"{TMP}/rt.jxl", "-d", "0", "-e", "1", "--quiet")
    sh("djxl", f"{TMP}/rt.jxl", f"{TMP}/rt.pam", "--quiet")
    return read_pnm(f"{TMP}/rt.pam")


# ---------------------------------------------------------------- corpus

def sources():
    """Writes the cjxl inputs to TMP and returns their paths by name."""
    duck, _ = pixels_of(f"{IMAGES}/ops/duck.png")
    egret, _ = pixels_of(f"{IMAGES}/ops/egret.png")
    duck, egret = duck[..., :3], egret[..., :3]
    small = duck[96:288, 160:416]  # 256x192, one group
    mid, egret_mid = duck[120:360, 160:480], egret[160:480, 120:360]  # 320x240 and 240x320, two groups
    s = {}

    def put(name, pixels, maxval=255):
        s[name] = f"{TMP}/src-{name}.{'ppm' if pixels.shape[2] == 3 else 'pgm' if pixels.shape[2] == 1 else 'pam'}"
        write_pnm(s[name], pixels, maxval)

    put("duck", duck)
    put("egret", egret)
    put("duck-mid", mid)
    put("egret-mid", egret_mid)
    put("duck-small", small)
    put("duck-posterized", (small // 51 * 51).astype(np.uint16))  # 216 colors, so the encoder picks a palette
    for name in ("duck-mid", "egret-mid"):  # PNG inputs for Squoosh
        sh("cjxl", s[name], f"{TMP}/rt.jxl", "-d", "0", "-e", "1", "--quiet")
        s[name + ".png"] = f"{TMP}/src-{name}.png"
        sh("djxl", f"{TMP}/rt.jxl", s[name + ".png"], "--quiet")
    gray = (small @ np.array([0.299, 0.587, 0.114])).round().astype(np.uint16)[..., None]
    put("duck-gray", gray)
    ramp = np.linspace(0, 255, small.shape[1]).round().astype(np.uint16)
    alpha = np.broadcast_to(ramp[None, :, None], small.shape[:2] + (1,))
    put("duck-alpha", np.concatenate([small, alpha], axis=2))
    put("duck-gray-alpha", np.concatenate([gray, alpha], axis=2))
    # 16 bits: the 8-bit picture spread over the full range with a low-bit pattern, so all 16 bits carry information.
    wide = small.astype(np.uint32) * 257 ^ (np.arange(small.size, dtype=np.uint32).reshape(small.shape) * 2654435761 >> 24 & 0xFF)
    put("duck-16bit", wide.astype(np.uint16), 65535)
    put("duck-12bit", (small.astype(np.uint32) * 4095 // 255).astype(np.uint16), 4095)
    s["duck-float"] = f"{TMP}/src-duck-float.pfm"
    linear = (small / 255.0) ** 2.2 * np.array([1.0, 2.5, 4.0])  # beyond 1.0, as HDR files are
    write_pfm(s["duck-float"], linear.astype(np.float32))
    put("odd-31x17", duck[200:217, 300:331])
    put("odd-1x1", duck[240:241, 320:321])
    return s


# name: (source, cjxl options, what it covers). Sources are keys of sources(), or paths under tests/images.
CJXL = {
    "duck-lossless-e1": ("duck-mid", "-d 0 -e 1", "lossless, fastest effort (fixed tree)"),
    "duck-lossless-e3": ("duck-mid", "-d 0 -e 3", "lossless, weighted predictor only"),
    "duck-lossless-e7": ("duck", "-d 0 -e 7", "lossless, default effort, multi-group"),
    "duck-small-lossless-e9": ("duck-small", "-d 0 -e 9", "lossless, tree learning at effort 9"),
    "duck-small-lossless-group128": ("duck-small", "-d 0 -e 7 -g 0", "lossless, 128-pixel groups"),
    "duck-posterized-lossless-palette": ("duck-posterized", "-d 0 -e 7", "lossless, palette of 216 colors"),
    "duck-small-lossy-palette": ("duck-small", "-d 0 --modular_lossy_palette --modular_palette_colors=0", "lossy (delta) palette"),
    "duck-small-lossless-gradient": ("duck-small", "-d 0 -P 5", "lossless, gradient predictor only"),
    "duck-small-lossless-prev-channels": ("duck-small", "-d 0 -e 7 -E 3", "lossless, previous channels in the MA tree"),
    "duck-small-lossless-ycocg": ("duck-small", "-d 0 -C 6", "lossless, a fixed reversible color transform"),
    "duck-small-modular-lossy": ("duck-small", "-m 1 -d 1", "lossy Modular (squeeze)"),
    "duck-small-modular-progressive": ("duck-small", "-d 0 -p", "lossless, progressive (squeeze)"),
    "duck-d1": ("duck", "-d 1", "VarDCT at distance 1, default effort, multi-group"),
    "duck-d1-e3": ("duck-mid", "-d 1 -e 3", "VarDCT, effort 3 (DCT8 only)"),
    "duck-d3-e9": ("duck-mid", "-d 3 -e 9", "VarDCT, effort 9, large transforms"),
    "duck-d0.3": ("duck-mid", "-d 0.3", "VarDCT, near-visually-lossless"),
    "duck-d1-progressive": ("duck-mid", "-d 1 -p", "VarDCT, progressive passes and LF frame"),
    "duck-d1-faster-decoding": ("duck-mid", "-d 1 --faster_decoding=4", "VarDCT, faster decoding"),
    "duck-d1-resampling2": ("duck-mid", "-d 1 --resampling=2", "VarDCT, upsampling by 2"),
    "egret-d1": ("egret", "-d 1", "VarDCT, portrait"),
    "egret-d2-no-filters": ("egret-mid", "-d 2 --gaborish=0 --epf=0", "VarDCT, Gaborish and EPF off"),
    "egret-d2-epf3": ("egret-mid", "-d 2 --epf=3", "VarDCT, strongest edge-preserving filter"),
    "egret-d1-noise": ("egret-mid", "-d 1 --photon_noise_iso=3200", "VarDCT, photon noise"),
    "egret-d1-level10": ("egret-mid", "-d 1 --codestream_level=10 --container=1", "container with a jxll box"),
    "duck-gray-lossless": ("duck-gray", "-d 0", "gray lossless"),
    "duck-gray-d1": ("duck-gray", "-d 1", "gray VarDCT"),
    "duck-alpha-lossless": ("duck-alpha", "-d 0", "alpha, lossless"),
    "duck-alpha-d1": ("duck-alpha", "-d 1", "alpha, VarDCT color with Modular alpha"),
    "duck-alpha-premultiplied": ("duck-alpha", "-d 1 --premultiply=1", "premultiplied alpha"),
    "duck-gray-alpha-lossless": ("duck-gray-alpha", "-d 0", "gray with alpha"),
    "duck-16bit-lossless": ("duck-16bit", "-d 0", "16-bit lossless"),
    "duck-16bit-d1": ("duck-16bit", "-d 1", "16-bit input, VarDCT"),
    "duck-12bit-lossless": ("duck-12bit", "-d 0", "12-bit lossless"),
    "duck-float-lossless": ("duck-float", "-d 0", "32-bit float lossless, values above 1"),
    "duck-float-d1": ("duck-float", "-d 1", "float input, VarDCT, linear"),
    "odd-31x17-lossless": ("odd-31x17", "-d 0", "odd size, lossless"),
    "odd-31x17-d1": ("odd-31x17", "-d 1", "odd size, VarDCT"),
    "odd-1x1-lossless": ("odd-1x1", "-d 0", "one pixel"),
    "pan-anim-lossless": ("apng/pan-rgb.png", "-d 0", "animation, lossless"),
    "pan-anim-d1": ("apng/pan-rgb.png", "-d 1", "animation, VarDCT"),
    "sprite-anim-lossless": ("apng/sprite-over-previous.png", "-d 0", "animation with alpha, crops and blending"),
    # Color (task 12): the pixels are tagged with each space, so XYB decodes into it as djxl does.
    "duck-p3-d1": ("duck-mid", "-d 1 -x color_space=RGB_D65_DCI_Rel_SRG", "color, Display P3 (enumerated), VarDCT"),
    "duck-rec2020-perceptual-d1": ("duck-mid", "-d 1 -x color_space=RGB_D65_202_Per_709", "color, Rec. 2020 with the BT.709 curve, perceptual intent"),
    "duck-dci-d1": ("duck-mid", "-d 1 -x color_space=RGB_DCI_DCI_Rel_DCI", "color, DCI-P3 white and the DCI curve"),
    "duck-custom-primaries-d1": ("duck-mid", "-d 1 -x color_space=RGB_D65_0.64;0.33;0.21;0.71;0.15;0.06_Rel_g0.45455", "color, custom primaries and gamma"),
    "duck-linear-d1": ("duck-mid", "-d 1 -x color_space=RGB_D65_SRG_Rel_Lin", "color, linear sRGB"),
    "duck-gray-d50-d1": ("duck-gray", "-d 1 -x color_space=Gra_0.3457;0.3585_Rel_SRG", "color, gray with a D50 white"),
    # HDR (task 14): 16-bit samples tagged with each curve, so XYB decodes into PQ or HLG as djxl does.
    "duck-pq-d1": ("duck-16bit", "-d 1 -x color_space=RGB_D65_202_Rel_PeQ --intensity_target=4000", "HDR, Rec. 2100 PQ, 4000 nits"),
    "duck-hlg-d1": ("duck-16bit", "-d 1 -x color_space=RGB_D65_202_Rel_HLG --intensity_target=1000", "HDR, Rec. 2100 HLG, 1000 nits"),
    "duck-pq-lossless": ("duck-16bit", "-d 0 -x color_space=RGB_D65_202_Rel_PeQ --intensity_target=10000", "HDR, Rec. 2100 PQ, lossless"),
}

# raw.pixls.us camera developments (CC0) from libjxl's testdata: 16-bit PNGs with ICC profiles.
CAMERA = {
    "camera-nikon-d300-rec2020-d1": ("Nikon-D300-12bit_2020_g1_dt.png", "-d 1", "camera, Rec. 2020 linear ICC, VarDCT"),
    "camera-sony-rx1rm2-709-d1": ("Sony-DSC-RX1RM2-14bit_709_v4_krita.png", "-d 1", "camera, Rec. 709 ICC, VarDCT"),
    "camera-pixel2xl-acescg-lossless": ("Google-Pixel2XL-16bit_acescg_g1_v4_krita.png", "-d 0", "camera, ACEScg ICC, lossless"),
    "camera-dji-fc6310-srgb-d1": ("DJI-FC6310-16bit_srgb8_v4_krita.png", "-d 1", "camera, 8-bit sRGB, VarDCT"),
    "camera-huawei-eva-709-lossless": ("HUAWEI-EVA-L09-16bit_709_g1_dt.png", "-d 0 -e 3", "camera, Rec. 709 linear ICC, lossless"),
}

# JPEGs recompressed losslessly (jbrd); public domain or IJG (see tests/images/LICENSES).
RECOMPRESS = {
    "jpeg-duck-420": ("jpeg/duck-420.jpg", "--compress_boxes=0", "recompressed JPEG, 4:2:0"),
    "jpeg-duck-422": ("jpeg/duck-422.jpg", "--compress_boxes=0", "recompressed JPEG, 4:2:2"),
    "jpeg-duck-440": ("jpeg/duck-440.jpg", "--compress_boxes=0", "recompressed JPEG, 4:4:0"),
    "jpeg-duck-444": ("jpeg/duck-444.jpg", "--compress_boxes=0", "recompressed JPEG, 4:4:4"),
    "jpeg-duck-gray": ("jpeg/duck-gray.jpg", "--compress_boxes=0", "recompressed JPEG, gray"),
    "jpeg-duck-progressive": ("jpeg/duck-progressive.jpg", "--compress_boxes=0", "recompressed progressive JPEG"),
    "jpeg-duck-restart-rows": ("jpeg/duck-restart-rows.jpg", "--compress_boxes=0", "recompressed JPEG with restart markers"),
    "jpeg-duck-optimized": ("jpeg/duck-optimized.jpg", "--compress_boxes=0", "recompressed JPEG with optimized Huffman tables"),
    "jpeg-duck-q100": ("jpeg/duck-q100.jpg", "--compress_boxes=0", "recompressed JPEG, quality 100"),
    "jpeg-egret-420": ("jpeg/egret-420.jpg", "--compress_boxes=0", "recompressed JPEG, portrait"),
    "jpeg-exif-orientation-6": ("jpeg/exif-orientation-6.jpg", "--compress_boxes=0", "recompressed JPEG with Exif orientation 6"),
    "jpeg-exif-tags": ("jpeg/exif-tags.jpg", "--compress_boxes=0", "recompressed JPEG with Exif tags"),
    "jpeg-exif-tags-brob": ("jpeg/exif-tags.jpg", "--compress_boxes=1", "recompressed JPEG, Exif in a brob box"),
    "jpeg-odd-17x9": ("jpeg/odd-17x9.jpg", "--compress_boxes=0", "recompressed JPEG, odd size"),
    "jpeg-odd-1x1-progressive": ("jpeg/odd-1x1-progressive.jpg", "--compress_boxes=0", "recompressed JPEG, one pixel, progressive"),
    "jpeg-testorig": ("jpeg/testorig.jpg", "--compress_boxes=0", "recompressed JPEG, libjpeg-turbo's test image (IJG)"),
    "jpeg-exif-orientation-6-d1": ("jpeg/exif-orientation-6.jpg", "--lossless_jpeg=0 -d 1 --compress_boxes=0", "JPEG re-encoded lossy, Exif kept"),
}

# Squoosh CLI 0.7.3 (its libjxl WebAssembly build); options as Squoosh's JSON.
SQUOOSH = {
    "squoosh-duck-q75": ("duck-mid.png", {"quality": 75}, "Squoosh, quality 75"),
    "squoosh-egret-q90-progressive": ("egret-mid.png", {"quality": 90, "progressive": True}, "Squoosh, quality 90, progressive"),
    "squoosh-duck-lossless": ("duck-mid.png", {"lossless": True}, "Squoosh, lossless"),
    "squoosh-egret-full-q90-progressive": ("ops/egret.png", {"quality": 90, "progressive": True},
                                           "Squoosh, full size, progressive: djxl 0.12.0 fails on it, jxl-oxide decodes it"),
    "squoosh-egret-q50-effort3": ("egret-mid.png", {"quality": 50, "effort": 3}, "Squoosh, quality 50, effort 3"),
}

# Small files from libjxl's testdata (CC BY 4.0), copied as they are.
LIBJXL = {
    "pq_gradient.jxl": ("jxl/pq_gradient.jxl", "PQ transfer function"),
    "splines.jxl": ("jxl/splines.jxl", "splines"),
    "spline_on_first_frame.jxl": ("jxl/spline_on_first_frame.jxl", "splines on the first of two frames"),
    "1x1_exif_xmp.jxl": ("jxl/jpeg_reconstruction/1x1_exif_xmp.jxl", "recompressed JPEG with Exif and XMP"),
    "cropped_traffic_light.jxl": ("jxl/blending/cropped_traffic_light.jxl", "animation with cropped frames"),
    "square-extended-size-container.jxl": ("jxl/boxes/square-extended-size-container.jxl", "box with an extended (64-bit) size"),
}


# Gain maps (task 14): an Ultra HDR JPEG's primary image and gain map, each decoded and encoded lossless by cjxl, with the map's
# ISO 21496-1 metadata, in libjxl's jhgm bundle (version, metadata, no color encoding, no ICC, the map as a bare codestream).
GAINMAP = {
    "gainmap-uhdr-default": ("gainmap/uhdr-default.jpg", "gain map (jhgm), RGB map, from uhdr-default.jpg"),
    "gainmap-uhdr-mono-map": ("gainmap/uhdr-mono-map.jpg", "gain map (jhgm), gray map, from uhdr-mono-map.jpg"),
    "gainmap-uhdr-quarter-map": ("gainmap/uhdr-quarter-map.jpg", "gain map (jhgm), quarter-size map, from uhdr-quarter-map.jpg"),
}


def jpeg_end(data, start):
    """Returns where the JPEG starting at start ends (after its EOI), walking its segments and entropy-coded data."""
    i = start + 2
    while i < len(data):
        if data[i] != 0xFF:
            raise ValueError("not a marker")
        marker = data[i + 1]
        if marker == 0xD9:
            return i + 2
        if marker == 0xFF or 0xD0 <= marker <= 0xD7 or marker == 0x01:
            i += 1 if marker == 0xFF else 2
            continue
        length = int.from_bytes(data[i + 2:i + 4], "big")
        i += 2 + length
        if marker == 0xDA:
            while not (data[i] == 0xFF and data[i + 1] not in (0x00,) and not 0xD0 <= data[i + 1] <= 0xD7):
                i += 1
    raise ValueError("no EOI")


def iso_metadata(jpeg):
    """Returns the ISO 21496-1 metadata of a gain map JPEG's APP2 segment."""
    ns = b"urn:iso:std:iso:ts:21496:-1\0"
    i = 2
    while i < len(jpeg) and jpeg[i] == 0xFF and jpeg[i + 1] != 0xDA:
        length = int.from_bytes(jpeg[i + 2:i + 4], "big")
        body = jpeg[i + 4:i + 2 + length]
        if jpeg[i + 1] == 0xE2 and body.startswith(ns):
            return body[len(ns):]
        i += 2 + length
    raise ValueError("no ISO 21496-1 metadata")


def without_app_segments(jpeg):
    """Returns a JPEG without its APP1 to APP15 segments: a gain map needs no ICC profile (cjxl refuses an RGB one on gray)."""
    out = bytearray(jpeg[:2])
    i = 2
    while jpeg[i] == 0xFF and jpeg[i + 1] != 0xDA:
        length = int.from_bytes(jpeg[i + 2:i + 4], "big")
        if not 0xE1 <= jpeg[i + 1] <= 0xEF:
            out += jpeg[i:i + 2 + length]
        i += 2 + length
    return bytes(out + jpeg[i:])


def codestream(path):
    """Returns the codestream of a JPEG XL file: the file itself when bare, else its jxlc box."""
    data = open(path, "rb").read()
    if data[:2] == b"\xff\x0a":
        return data
    i = 0
    while i < len(data):
        size = int.from_bytes(data[i:i + 4], "big")
        kind = data[i + 4:i + 8]
        if kind == b"jxlc":
            return data[i + 8:i + size] if size else data[i + 8:]
        i += size
    raise ValueError("no jxlc box")


def box(kind, payload):
    return (8 + len(payload)).to_bytes(4, "big") + kind + payload


def gainmap_file(name, jpeg_path, covers):
    data = open(f"{IMAGES}/{jpeg_path}", "rb").read()
    end = jpeg_end(data, 0)
    primary, gain_map = data[:end], data[data.index(b"\xff\xd8", end):]
    metadata = iso_metadata(gain_map)
    for part, jpeg in (("base", primary), ("map", without_app_segments(gain_map))):
        open(f"{TMP}/{part}.jpg", "wb").write(jpeg)
        sh("cjxl", f"{TMP}/{part}.jpg", f"{TMP}/{part}.jxl", "--lossless_jpeg=0", "-d", "0", "-e", "3", "--quiet")
    bundle = bytes([0]) + len(metadata).to_bytes(2, "big") + metadata + bytes([0]) + (0).to_bytes(4, "big") + codestream(f"{TMP}/map.jxl")
    signature = bytes.fromhex("0000000c4a584c200d0a870a")
    ftyp = box(b"ftyp", b"jxl \0\0\0\0jxl ")
    with open(f"{OUT}/{name}.jxl", "wb") as f:
        f.write(signature + ftyp + box(b"jhgm", bundle) + box(b"jxlc", codestream(f"{TMP}/base.jxl")))
    return {"source": f"{jpeg_path} (primary and gain map decoded by cjxl's libjpeg)", "encoder": "cjxl -d 0 -e 3 per image, jhgm by jxlcorpus.py",
            "covers": covers}


def exiftool(path):
    tags = json.loads(sh("exiftool", "-j", "-G1", "-a", "-n", path).stdout)[0]
    return {k: v for k, v in tags.items() if not k.startswith("System:") and k not in ("SourceFile", "ExifTool:ExifToolVersion")}


def info(path):
    r = sh("jxlinfo", "-v", path, check=False)
    return r.stdout if r.returncode == 0 else "error: " + (r.stderr or r.stdout).strip()


def oxide(path, out, *extra):
    """Runs jxl-oxide; it has hung, rarely and not reproducibly, on a busy machine, so a stuck run is retried."""
    for attempt in range(3):
        try:
            return sh("jxl-oxide", path, "-f", "npy", "-o", out, *extra, "-q", check=False, timeout=300)
        except subprocess.TimeoutExpired:
            print(f"jxl-oxide timed out on {path}, attempt {attempt + 1}")
    raise RuntimeError(f"jxl-oxide hangs on {path}")


def npy_decode(path):
    """Decodes with djxl and jxl-oxide to npy; returns the shape and how far apart they are, or which one failed."""
    d = sh("djxl", path, f"{TMP}/d.npy", "--quiet", check=False)
    o = oxide(path, f"{TMP}/o.npy")
    result = {}
    if d.returncode:
        result["djxl_error"] = (d.stderr or d.stdout).strip()
    if o.returncode:
        result["oxide_error"] = (o.stderr or o.stdout).strip()[-400:]
    if result:
        return result
    a, b = np.load(f"{TMP}/d.npy"), np.load(f"{TMP}/o.npy")
    if a.shape != b.shape:
        return {"djxl_shape": list(a.shape), "oxide_shape": list(b.shape)}
    e = np.abs(a.astype(np.float64) - b)
    nan = int(np.isnan(e).sum())
    e = np.nan_to_num(e, nan=0.0)
    result = {"shape": list(a.shape), "oxide_peak": float(e.max()), "oxide_rms": float(np.sqrt((e * e).mean()))}
    if nan:
        result["oxide_nan_samples"] = nan  # jxl-oxide 0.12.6 gives NaN for some HLG samples
    return result


def reference(name, path, entry):
    """Writes djxl's decode of one corpus file and fills its manifest entry."""
    text = info(path)
    entry["jxlinfo"] = text
    entry["exiftool"] = exiftool(path)
    entry.update(npy_decode(path))
    if "djxl_error" in entry:
        return
    deep = any(f"{b}-bit" in text for b in range(9, 17))
    if "float" in name:
        out = f"{REF}/{name}.pfm"
        sh("djxl", path, out, "--quiet")
    else:
        out = f"{REF}/{name}.png"
        sh("djxl", path, out, "--quiet", f"--bits_per_sample={16 if deep else 8}")
    entry["reference"] = os.path.basename(out)
    if "JPEG bitstream reconstruction data available" in text:
        sh("djxl", path, f"{TMP}/r.jpg", "--quiet")
        jpeg = open(f"{TMP}/r.jpg", "rb").read()
        entry["reconstructed_jpeg_sha256"] = hashlib.sha256(jpeg).hexdigest()
        if entry.get("source", "").endswith(".jpg"):
            original = open(f"{IMAGES}/{entry['source']}", "rb").read()
            entry["reconstructs_original"] = jpeg == original


def corpus():
    os.makedirs(TMP, exist_ok=True)
    for folder in (OUT, f"{OUT}/libjxl", REF):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    src = sources()
    manifest = {}
    cjxl_version = sh("cjxl", "--version").stdout.split("\n")[0]

    def encode(name, source, options, covers, encoder="cjxl"):
        out = f"{OUT}/{name}.jxl"
        path = src.get(source) or (source if source.startswith("/") else f"{IMAGES}/{source}")
        r = sh("cjxl", path, out, *options.split(), "--quiet", check=False)
        if r.returncode != 0:
            print(f"skipped {name}: {r.stderr.strip()}")
            return
        manifest[f"{name}.jxl"] = {"source": source if source not in src else f"generated:{source}", "encoder": cjxl_version,
                                   "options": options, "covers": covers}

    for name, (source, options, covers) in CJXL.items():
        encode(name, source, options, covers)
    for name, (png, options, covers) in CAMERA.items():
        encode(name, f"{CACHE}/testdata/external/raw.pixls/{png}", options, covers)
        if f"{name}.jxl" in manifest:
            manifest[f"{name}.jxl"]["source"] = f"libjxl testdata external/raw.pixls/{png}"
    for name, (jpeg, options, covers) in RECOMPRESS.items():
        encode(name, jpeg, options, covers)
    for name, (png, options, covers) in SQUOOSH.items():
        shutil.rmtree(f"{TMP}/sq", ignore_errors=True)
        sh("squoosh-cli", "--jxl", json.dumps(options), "-d", f"{TMP}/sq", src.get(png) or f"{IMAGES}/{png}")
        shutil.move(glob.glob(f"{TMP}/sq/*.jxl")[0], f"{OUT}/{name}.jxl")
        manifest[f"{name}.jxl"] = {"source": f"generated:{png[:-4]}" if png in src else png, "encoder": "Squoosh CLI 0.7.3", "options": json.dumps(options), "covers": covers}
    for name, (jpeg, covers) in GAINMAP.items():
        manifest[f"{name}.jxl"] = gainmap_file(name, jpeg, covers)
    for name, (path, covers) in LIBJXL.items():
        shutil.copyfile(f"{CACHE}/testdata/{path}", f"{OUT}/libjxl/{name}")
        manifest[f"libjxl/{name}"] = {"source": f"libjxl testdata {path}", "covers": covers}

    for file, entry in manifest.items():
        reference(file[:-4].replace("/", "-"), f"{OUT}/{file}", entry)
        print(f"{file}: {os.path.getsize(f'{OUT}/{file}')} bytes, oxide peak {entry.get('oxide_peak')}")
    tools = {"djxl": sh("djxl", "--version").stdout.strip(), "jxl-oxide": sh("jxl-oxide", "--version").stdout.strip(),
             "exiftool": sh("exiftool", "-ver").stdout.strip()}
    with open(f"{REF}/manifest.json", "w") as f:
        json.dump({"tools": tools, "files": manifest}, f, indent=1, sort_keys=True)
        f.write("\n")


# ---------------------------------------------------------------- conformance and Chromium files (stay in .cache)

def error(ref, dec):
    rows = []
    for i in range(min(ref.shape[0], dec.shape[0])):
        r, d = ref[i], dec[i]
        if d.shape[2] == 4 and r.shape[2] == 3:
            d = d[..., :3]
        if r.shape != d.shape:
            rows.append(None)
            continue
        e = np.abs(r.astype(np.float64) - d)
        rows.append({"peak": float(e.max()), "rms": float(max(np.sqrt((e[..., c] ** 2).mean()) for c in range(e.shape[2])))})
    return rows


def decode_case(path, out, spec):
    os.makedirs(out, exist_ok=True)
    args = ["djxl", path, f"{out}/djxl.npy", "--quiet", "--icc_out", f"{out}/djxl.icc", "--orig_icc_out", f"{out}/djxl-original.icc",
            "--metadata_out", f"{out}/djxl-meta.json", "--norender_spotcolors"]
    if spec and "preview" in spec:
        args += ["--preview_out", f"{out}/djxl-preview.npy"]
    result = {"djxl": sh(*args, check=False).returncode == 0}
    if spec and "SpotColor" in spec.get("extra_channel_type", []):
        # Spot colors rendered into the color, as Load gives them by default.
        result["djxl_spot"] = sh("djxl", path, f"{out}/djxl-spot.npy", "--quiet", check=False).returncode == 0
    if spec and "reconstructed_jpeg" in spec:
        result["djxl_jpeg"] = sh("djxl", path, f"{out}/djxl.jpg", "--quiet", check=False).returncode == 0
    r = oxide(path, f"{out}/oxide.npy", "--icc-output", f"{out}/oxide.icc")
    result["oxide"] = r.returncode == 0
    with open(f"{out}/jxlinfo.txt", "w") as f:
        f.write(info(path))
    with open(f"{out}/exiftool.json", "w") as f:
        json.dump(exiftool(path), f, indent=1, sort_keys=True)
    return result


def conformance():
    os.makedirs(TMP, exist_ok=True)
    cases = f"{CACHE}/conformance/testcases"
    summary = {}
    for name in sorted(os.listdir(cases)):
        spec_path = f"{cases}/{name}/test.json"
        if not os.path.isfile(spec_path):
            continue
        spec = json.load(open(spec_path))
        out = f"{CACHE}/reference/conformance/{name}"
        result = decode_case(f"{cases}/{name}/input.jxl", out, spec)
        ref = np.load(f"{cases}/{name}/reference_image.npy")
        reference_icc = open(f"{cases}/{name}/reference.icc", "rb").read()
        for tool in ("djxl", "oxide"):
            if result[tool]:
                icc = f"{out}/{tool}.icc"
                result[f"{tool}_same_icc"] = os.path.isfile(icc) and open(icc, "rb").read() == reference_icc
                result[f"{tool}_frames"] = error(ref, np.load(f"{out}/{tool}.npy"))
        if "reconstructed_jpeg" in spec:
            result["djxl_jpeg_exact"] = result["djxl_jpeg"] and open(f"{out}/djxl.jpg", "rb").read() == open(f"{cases}/{name}/reconstructed.jpg", "rb").read()
        limits = [(f["peak_error"], f["rms_error"]) for f in spec["frames"]]
        for tool in ("djxl", "oxide"):
            frames = result.get(f"{tool}_frames", [])
            result[f"{tool}_within"] = len(frames) == len(limits) and all(
                e and e["peak"] <= p and e["rms"] <= q for e, (p, q) in zip(frames, limits))
        summary[name] = result
        print(f"{name}: within thresholds: djxl {result['djxl_within']}, jxl-oxide {result['oxide_within']}")
    with open(f"{CACHE}/reference/conformance/summary.json", "w") as f:
        json.dump(summary, f, indent=1, sort_keys=True)
    for path in sorted(glob.glob(f"{CACHE}/chromium/*.jxl")):
        result = decode_case(path, f"{CACHE}/reference/chromium/{os.path.basename(path)[:-4]}", None)
        print(f"chromium {os.path.basename(path)}: {result}")


{"corpus": corpus, "conformance": conformance}[sys.argv[1]]()
