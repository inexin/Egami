"""Builds the Phase 2.9.4 HEIC corpus and references inside the egami-heicref image. Run through tools/make-heic-references.py.

  corpus       tests/images/heic: files encoded here from public domain pictures (see tests/images/LICENSES): heif-enc over x265
               and kvazaar at 8, 10 and 12 bits in 4:0:0 to 4:4:4, lossless, alpha, grids, thumbnails, color and HDR metadata,
               sequences, and JPEG-coded and uncompressed items; HM 18.0's encoder steered to the HEVC intra tools (transform skip,
               scaling lists, PCM, transquant bypass, tiles, wavefront, slices and dependent slices, deblocking and SAO off, delta QP,
               chroma QP offsets, CTU and transform sizes, and the range extensions: cross-component prediction, residual DPCM,
               persistent Rice adaptation), each wrapped in a HEIF file by heif.py; files built by heif.py around those streams for
               the container features (clap, irot, imir, ICC, Exif, XMP, thumbnails, alpha and depth items, grids, overlays, idat,
               broken items); and tests/images/heic/phone, phone photos (CC0 and CC BY 4.0, from the Internet Archive) cut down to
               a corner of their grid by heif.py, every other item kept. tests/reference/heic: heif-dec's RGB decode of each (PNG,
               8 or 16 bits, transformations applied) and manifest.json with each file's source and options, heif-info's text,
               ExifTool's tags, every item (type, properties, references, data size) and, for every hvc1 item, the MD5 of HM's and
               dec265's decode (the item's planes as stored: Y, Cb, Cr, 16-bit little-endian above 8 bits), whether they agree,
               HM's check of the decoded picture hash SEI, and each slice's type; the YUV decodes stay in .cache/heic/reference.
  conformance  .cache/heic/reference/<set>/summary.json: for every stream of the ITU-T H.265.1 conformance suites (HEVC_v1, RExt)
               and Nokia's HEIF bitstreams, HM's decode of the whole stream against the stream's own MD5, HM's picture hash check,
               every picture's slice types (so the streams an intra decoder can pass are known), the first picture's MD5 and
               whether dec265 agrees; the first picture of every all-intra stream as YUV next to it.
  extra        .cache/heic/reference/extra: the corpus treatment for the HEIF files that stay in .cache: the phone photos whole,
               the ones whose licence or provenance keeps them out of the repo, MPEG's File Format conformance files and Nokia's
               HEIF conformance files.
  traces       HM's syntax trace (TAppDecoderTrace) of every HEVC item of the corpus and of the first picture of every
               conformance stream: the header elements (NAL unit headers, VPS, SPS, PPS, slice segment headers, SEI) as
               tests/reference/heic/hm-headers.json.gz and .cache/heic/reference/<set>/hm-headers.json.gz, and the whole traces,
               block syntax included, in .cache/heic/reference/traces.
"""
import glob
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import zipfile

import numpy as np

sys.path.insert(0, "/a")
from avifcorpus import read_png, write_png  # noqa: E402  (tools/avifref, mounted at /a)

sys.path.insert(0, "/t")
import heif  # noqa: E402
from phones import PHONES  # noqa: E402

W = "/w"
CACHE = f"{W}/.cache/heic"
IMAGES = f"{W}/tests/images"
OUT = f"{IMAGES}/heic"
REF = f"{W}/tests/reference/heic"
TMP = "/tmp/heic"
HMCFG = "/usr/local/share/hm-cfg"


def sh(*args, check=True, timeout=1800):
    r = subprocess.run([str(a) for a in args], capture_output=True, text=True, timeout=timeout)
    if check and r.returncode != 0:
        raise RuntimeError(f"{' '.join(map(str, args))}: {(r.stderr or r.stdout).strip()[-600:]}")
    return r


def md5(*chunks):
    m = hashlib.md5()
    for c in chunks:
        m.update(c)
    return m.hexdigest()


def tools():
    return {"hm": "HM 18.0 (fb4486d5)",
            "libheif": sh("heif-info", "--version").stdout.strip(), "libde265": "1.0.15", "x265": "4.1",
            "kvazaar": "2.3.1", "exiftool": sh("exiftool", "-ver").stdout.strip()}


# ---------------------------------------------------------------- sources

def to_yuv(rgb, depth, chroma):
    """Planar Y'CbCr (BT.601 matrix, full range) of an 8-bit RGB picture at the given depth and chroma format (400 to 444), as
    HM reads it: planes in order, 16-bit little-endian samples above 8 bits."""
    rgb = rgb.astype(np.float64) / 255
    y = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
    cb = (rgb[..., 2] - y) / 1.772 + 0.5
    cr = (rgb[..., 0] - y) / 1.402 + 0.5
    scale = (1 << depth) - 1
    h, w = y.shape
    planes = [y]
    if chroma != "400":
        def sub(p):
            if chroma in ("420", "422"):
                p = np.pad(p, ((0, h % 2 if chroma == "420" else 0), (0, w % 2)), mode="edge")
                p = (p[:, 0::2] + p[:, 1::2]) / 2
                if chroma == "420":
                    p = (p[0::2] + p[1::2]) / 2
            return p
        planes += [sub(cb), sub(cr)]
    dtype = "<u2" if depth > 8 else "u1"
    return b"".join(np.clip(np.round(p * scale), 0, scale).astype(dtype).tobytes() for p in planes)


def sources():
    os.makedirs(TMP, exist_ok=True)
    duck, _ = read_png(f"{IMAGES}/ops/duck.png")
    egret, _ = read_png(f"{IMAGES}/ops/egret.png")
    duck_alpha = duck.copy()
    duck, egret = duck[..., :3], egret[..., :3]
    s = {"rgb": {}}

    def put(name, pixels, depth=8):
        s[name] = f"{TMP}/src-{name}.png"
        write_png(s[name], pixels, depth)
        if depth == 8 and pixels.shape[2] == 3:
            s["rgb"][name] = pixels

    small = duck[96:288, 160:416]  # 256x192
    put("duck", duck)
    put("egret", egret)
    put("duck-mid", duck[120:360, 160:480])  # 320x240
    put("egret-mid", egret[160:480, 120:360])  # 240x320
    put("duck-small", small)
    gray = (small @ np.array([0.299, 0.587, 0.114])).round().astype(np.uint16)[..., None]
    put("duck-gray", gray)
    ramp = np.linspace(0, 255, small.shape[1]).round().astype(np.uint16)
    alpha = np.broadcast_to(ramp[None, :, None], small.shape[:2] + (1,)).copy()
    alpha[60:132, 96:160] = 0
    put("duck-alpha", np.concatenate([small, alpha], axis=2))
    put("duck-full-alpha", duck_alpha)
    wide = small.astype(np.uint32) * 257 ^ (np.arange(small.size, dtype=np.uint32).reshape(small.shape) * 2654435761 >> 24 & 0xFF)
    put("duck-16bit", wide.astype(np.uint16), 16)
    wide_alpha = np.concatenate([wide.astype(np.uint16), (alpha.astype(np.uint32) * 257).astype(np.uint16)], axis=2)
    put("duck-16bit-alpha", wide_alpha, 16)
    put("odd-31x17", duck[200:217, 300:331])
    put("odd-1x1", duck[240:241, 320:321])
    put("odd-33x65", duck[100:165, 300:333])
    put("odd-129x67", duck[100:167, 200:329])
    put("odd-130x66", duck[100:166, 200:330])
    put("odd-30x17", duck[200:217, 300:330])
    put("duck-tile", duck[0:256, 0:256])
    for i in range(4):
        put(f"pan-{i}", duck[120:312, 160 + 16 * i:416 + 16 * i])
    for name in ("duck-pq", "duck-hlg"):
        s[name] = f"{IMAGES}/hdr/{name}.png"
    sh("exiftool", "-b", "-exif", f"{IMAGES}/metadata/synthetic/phone-orientation6.jpg", "-w!", f"{TMP}/%f.exif")
    sh("exiftool", "-b", "-xmp", f"{IMAGES}/metadata/synthetic/xmp-elements.jpg", "-w!", f"{TMP}/%f.xmp")
    s["exif"] = open(f"{TMP}/phone-orientation6.exif", "rb").read()
    s["xmp"] = open(f"{TMP}/xmp-elements.xmp", "rb").read()
    # heif-enc copies Exif from a JPEG input; a JPEG of the duck with the Exif above.
    s["duck-jpeg-exif"] = f"{IMAGES}/metadata/synthetic/phone-orientation6.jpg"
    return s


# ---------------------------------------------------------------- heif-enc files

# name: (source(s), heif-enc options, what it covers). Sources are keys of sources() (several joined by +, for sequences).
# {icc:<file>} is replaced with a path. The encoder is x265 unless the options say otherwise.
HEIFENC = {
    "duck-420-q60": ("duck-mid", "-q 60", "8-bit 4:2:0, x265 slow preset"),
    "duck-422-q60": ("duck-mid", "-q 60 -p chroma=422", "8-bit 4:2:2 (Main 4:2:2 10 profile)"),
    "duck-444-q60": ("duck-mid", "-q 60 -p chroma=444", "8-bit 4:4:4 (Main 4:4:4 profile)"),
    "duck-gray": ("duck-gray", "-q 60", "gray input, monochrome (4:0:0)"),
    "duck-10bit-420": ("duck-16bit", "-b 10 -q 60", "10-bit 4:2:0 (Main 10)"),
    "duck-10bit-422": ("duck-16bit", "-b 10 -q 60 -p chroma=422", "10-bit 4:2:2, as Canon and Sony HIF files"),
    "duck-10bit-444": ("duck-16bit", "-b 10 -q 60 -p chroma=444", "10-bit 4:4:4"),
    "duck-12bit-420": ("duck-16bit", "-b 12 -q 60", "12-bit 4:2:0 (Main 12)"),
    "duck-12bit-422": ("duck-16bit", "-b 12 -q 60 -p chroma=422", "12-bit 4:2:2"),
    "duck-12bit-444": ("duck-16bit", "-b 12 -q 60 -p chroma=444", "12-bit 4:4:4"),
    "duck-lossless": ("duck-mid", "-L", "lossless: RGB as GBR 4:4:4, transquant bypass"),
    "duck-lossless-10bit": ("duck-16bit", "-L -b 10", "lossless, 10 bits"),
    "duck-q5": ("duck-mid", "-q 5", "very low quality: large blocks, strong filters"),
    "duck-q95": ("duck-mid", "-q 95", "high quality: small blocks, many coefficients"),
    "duck-ultrafast": ("duck-mid", "-q 60 -p preset=ultrafast", "x265 ultrafast"),
    "duck-placebo": ("duck-small", "-q 60 -p preset=placebo -p tu-intra-depth=4", "x265 placebo, transform depth 4"),
    "duck-full": ("duck", "-q 60", "640x480, several 64x64 CTUs"),
    "egret-full": ("egret", "-q 60", "480x640 portrait"),
    "duck-ctu16": ("duck-mid", "-q 60 -p x265:ctu=16", "16x16 CTUs"),
    "duck-ctu32-tskip": ("duck-mid", "-q 60 -p x265:ctu=32 -p x265:tskip=1", "32x32 CTUs, transform skip"),
    "duck-no-sao-deblock": ("duck-mid", "-q 60 -p x265:sao=0 -p x265:deblock=0", "no SAO, no deblocking"),
    "duck-no-wpp": ("duck", "-q 60 -p x265:wpp=0", "no wavefront (x265 uses it by default)"),
    "duck-scaling-list": ("duck-mid", "-q 60 -p x265:scaling-list=default", "default scaling lists"),
    "duck-constrained-intra": ("duck-mid", "-q 60 -p x265:constrained-intra=1 -p x265:strong-intra-smoothing=0",
                               "constrained intra, no strong intra smoothing"),
    "duck-signhide-off": ("duck-mid", "-q 60 -p x265:signhide=0 -p x265:rdoq-level=0", "no sign data hiding, no RDOQ"),
    "duck-kvazaar": ("duck-mid", "-e kvazaar -q 60", "kvazaar 2.3.1"),
    "odd-1x1": ("odd-1x1", "-q 60 -p chroma=444", "1x1"),
    "odd-31x17": ("odd-31x17", "-q 60", "31x17, 4:2:0, conformance window and clap"),
    "odd-33x65-422": ("odd-33x65", "-q 60 -p chroma=422", "33x65, 4:2:2"),
    "odd-129x67": ("odd-129x67", "-q 60", "129x67, past two CTUs"),
    "duck-alpha": ("duck-alpha", "-q 60", "alpha item (urn:mpeg:hevc:2015:auxid:1)"),
    "duck-alpha-premultiplied": ("duck-alpha", "-q 60 --premultiplied-alpha", "premultiplied alpha (prem reference)"),
    "duck-alpha-10bit": ("duck-16bit-alpha", "-b 10 -q 60", "10-bit color and alpha"),
    "duck-thumb": ("duck", "-q 60 -t 96", "thumbnail item (thmb)"),
    "duck-thumb-alpha": ("duck-full-alpha", "-q 60 -t 96", "thumbnail with its own alpha"),
    "duck-grid-256": ("duck", "-q 60 --cut-tiles 256", "grid of 256x256 tiles, the last row and column cut"),
    "duck-grid-alpha": ("duck-full-alpha", "-q 60 --cut-tiles 320", "2x2 grid with an alpha grid"),
    "duck-exif-jpeg": ("duck-jpeg-exif", "-q 60", "Exif from a JPEG input (orientation 6)"),
    "duck-bt709-limited": ("duck-mid", "-q 60 --matrix_coefficients 1 --colour_primaries 1 --transfer_characteristic 1 "
                           "--full_range_flag 0", "BT.709 matrix, limited range"),
    "duck-bt2020-ncl": ("duck-mid", "-q 60 --matrix_coefficients 9 --colour_primaries 9 --transfer_characteristic 14",
                        "BT.2020 non-constant luminance matrix"),
    "duck-ycgco": ("duck-mid", "-q 60 -p chroma=444 --matrix_coefficients 8", "YCgCo matrix"),
    "duck-pq": ("duck-pq", "-b 10 -q 70 --matrix_coefficients 9 --colour_primaries 9 --transfer_characteristic 16 "
                "--clli 2000,400", "PQ, BT.2020, clli"),
    "duck-hlg": ("duck-hlg", "-b 10 -q 70 --matrix_coefficients 9 --colour_primaries 9 --transfer_characteristic 18", "HLG"),
    "duck-icc-p3": ("duck-mid", "-q 60 --color-profile custom --enable-two-colr-boxes {icc:DisplayP3-v4.icc}",
                    "Display P3 ICC with nclx (two colr boxes)"),
    "duck-pasp": ("duck-mid", "-q 60 --pasp 4,3", "pasp 4:3"),
    "duck-rotate-90": ("duck-mid", "-q 60 --rotate-cw 90", "irot from heif-enc's rotation"),
    "duck-flip-h": ("duck-mid", "-q 60 --flip-h", "imir from heif-enc's flip"),
    "pan-sequence-intra": ("pan-0+pan-1+pan-2+pan-3", "-S --gop-structure intra-only -q 60",
                           "hvc1 image sequence (msf1), every frame intra"),
    "pan-sequence": ("pan-0+pan-1+pan-2+pan-3", "-S --gop-structure low-delay -q 60",
                     "hvc1 image sequence with inter frames (first frame only)"),
    "duck-jpeg": ("duck-mid", "--jpeg -q 80", "JPEG-coded item (jpeg), core Egami"),
    "duck-uncompressed": ("duck-mid", "-U", "uncompressed item (unci, ISO/IEC 23001-17), core Egami"),
    "duck-uncompressed-alpha": ("duck-alpha", "-U", "uncompressed RGBA"),
    "duck-uncompressed-16bit": ("duck-16bit", "-U -b 16", "uncompressed, 16 bits"),
    "duck-uncompressed-deflate": ("duck-mid", "-U --unci-compression deflate", "uncompressed with deflate (cmpC)"),
}


def heifenc(name, source, options, src):
    out = f"{OUT}/{name}.heic"
    while "{icc:" in options:
        start = options.index("{icc:")
        end = options.index("}", start)
        options = options[:start] + f"{IMAGES}/icc/compact/{options[start + 5:end]}" + options[end + 1:]
    args = options.split()
    icc = [a for a in args if a.endswith(".icc")]
    args = [a for a in args if not a.endswith(".icc")]
    inputs = [src[s] for s in source.split("+")]
    if icc:
        # heif-enc takes the ICC profile from the PNG's iCCP chunk.
        inputs = [add_iccp(inputs[0], open(icc[0], "rb").read())]
    if "-e" not in args and "--jpeg" not in args and "-U" not in args:
        args = ["-e", "x265"] + args
    if len(inputs) > 1:
        # heif-enc -S pulls in every file with the same pattern.
        folder = f"{TMP}/seq-{name}"
        os.makedirs(folder, exist_ok=True)
        for i, path in enumerate(inputs):
            shutil.copy(path, f"{folder}/frame-{i + 1}.png")
        inputs = [f"{folder}/frame-1.png"]
    r = sh("heif-enc", *args, *inputs, "-o", out, check=False)
    if r.returncode != 0 or not os.path.isfile(out):
        print(f"skipped {name}: {(r.stderr or r.stdout).strip()[-300:]}")
        return None
    return " ".join(a.replace(TMP + "/", "").replace(IMAGES + "/", "") for a in options.split())


def add_iccp(png, profile):
    import zlib
    data = open(png, "rb").read()
    body = b"icc\0\0" + zlib.compress(profile)
    chunk = struct.pack(">I", len(body)) + b"iCCP" + body + struct.pack(">I", zlib.crc32(b"iCCP" + body))
    out = png[:-4] + "-icc.png"
    with open(out, "wb") as f:
        f.write(data[:33] + chunk + data[33:])
    return out


# ---------------------------------------------------------------- HM streams

# name: (source, chroma format, bit depth, HM configuration, HM options, what it covers). Every stream carries an MD5 decoded
# picture hash SEI unless its options set another. Sources are 8-bit pictures, converted to BT.601 full-range Y'CbCr here.
HM_MAIN = "encoder_intra_main.cfg"
HM_MAIN10 = "encoder_intra_main10.cfg"
HM_REXT = "encoder_intra_main_rext.cfg"
HMENC = {
    "hm-main": ("duck-mid", "420", 8, HM_MAIN, "", "HM Main intra, QP 32"),
    "hm-main10": ("duck-mid", "420", 10, HM_MAIN10, "", "HM Main 10 intra"),
    "hm-qp10": ("duck-mid", "420", 8, HM_MAIN, "--QP=10", "QP 10"),
    "hm-qp45": ("duck-mid", "420", 8, HM_MAIN, "--QP=45", "QP 45"),
    "hm-qp51-10bit": ("duck-mid", "420", 10, HM_MAIN10, "--QP=51", "QP 51 at 10 bits"),
    "hm-no-filters": ("duck-mid", "420", 8, HM_MAIN, "--LoopFilterDisable=1 --SAO=0", "deblocking and SAO off"),
    "hm-no-filters-10bit": ("duck-mid", "420", 10, HM_MAIN10, "--LoopFilterDisable=1 --SAO=0", "filters off, 10 bits"),
    "hm-no-filters-422": ("duck-mid", "422", 10, HM_REXT, "--LoopFilterDisable=1 --SAO=0", "filters off, 4:2:2"),
    "hm-no-filters-444": ("duck-mid", "444", 8, HM_REXT, "--LoopFilterDisable=1 --SAO=0", "filters off, 4:4:4"),
    "hm-no-filters-400": ("duck-mid", "400", 8, HM_REXT, "--LoopFilterDisable=1 --SAO=0", "filters off, 4:0:0"),
    "hm-no-sao": ("duck-mid", "420", 8, HM_MAIN, "--SAO=0", "deblocking only"),
    "hm-deblock-offsets": ("duck-mid", "420", 8, HM_MAIN, "--QP=40 --LoopFilterOffsetInPPS=1 --LoopFilterBetaOffset_div2=3 "
                           "--LoopFilterTcOffset_div2=-2", "deblocking beta and tc offsets in the PPS"),
    "hm-tskip": ("duck-mid", "420", 8, HM_MAIN, "--TransformSkip=1 --TransformSkipFast=0", "transform skip 4x4"),
    "hm-no-signhide": ("duck-mid", "420", 8, HM_MAIN, "--SignHideFlag=0", "no sign data hiding"),
    "hm-no-rdoq": ("duck-mid", "420", 8, HM_MAIN, "--RDOQ=0 --RDOQTS=0", "no RDOQ"),
    "hm-scaling-default": ("duck-mid", "420", 8, HM_MAIN, "--ScalingList=1", "default scaling lists"),
    "hm-scaling-custom": ("duck-mid", "420", 8, HM_MAIN, "--ScalingList=2 --ScalingListFile={scaling}",
                          "scaling lists in the SPS from a file, with prediction"),
    "hm-pcm": ("duck-mid", "420", 8, HM_MAIN, "--PCMEnabledFlag=1 --PCMLog2MinSize=3 --PCMLog2MaxSize=5 --PCMInputBitDepthFlag=1 "
               "--PCMFilterDisableFlag=0 --QP=10", "PCM blocks (PCM chosen where cheaper)"),
    "hm-pcm-forced": ("duck-small", "420", 8, HM_MAIN, "--PCMEnabledFlag=1 --PCMLog2MinSize=3 --PCMLog2MaxSize=5 "
                      "--PCMInputBitDepthFlag=0 --PCMFilterDisableFlag=1 --QP=0",
                      "PCM at QP 0, PCM loop filter disabled"),
    "hm-lossless": ("duck-mid", "420", 8, HM_MAIN, "--TransquantBypassEnable=1 --CUTransquantBypassFlagForce=1",
                    "lossless: every CU transquant bypass"),
    "hm-lossless-444-10bit": ("duck-small", "444", 10, HM_REXT, "--TransquantBypassEnable=1 --CUTransquantBypassFlagForce=1 "
                              "--ImplicitResidualDPCM=1", "lossless 4:4:4, implicit residual DPCM"),
    "hm-tiles": ("duck", "420", 8, HM_MAIN, "--TileUniformSpacing=1 --NumTileColumnsMinus1=1 --NumTileRowsMinus1=2",
                 "2x3 uniform tiles, loop filter across tiles"),
    "hm-tiles-nonuniform": ("duck", "420", 8, HM_MAIN, "--TileUniformSpacing=0 --NumTileColumnsMinus1=1 "
                            "--TileColumnWidthArray=4 --NumTileRowsMinus1=1 --TileRowHeightArray=3 "
                            "--LFCrossTileBoundaryFlag=0", "non-uniform tiles, no loop filter across them"),
    "hm-wpp": ("duck", "420", 8, HM_MAIN, "--WaveFrontSynchro=1", "wavefront rows (entry points)"),
    "hm-slices": ("duck", "420", 8, HM_MAIN, "--SliceMode=1 --SliceArgument=7 --LFCrossSliceBoundaryFlag=0",
                  "slices of 7 CTUs, no loop filter across them"),
    "hm-slices-bytes": ("duck", "420", 8, HM_MAIN, "--SliceMode=2 --SliceArgument=3000", "slices of at most 3000 bytes"),
    "hm-dependent-slices": ("duck", "420", 8, HM_MAIN, "--SliceMode=1 --SliceArgument=20 --SliceSegmentMode=1 "
                            "--SliceSegmentArgument=4", "dependent slice segments of 4 CTUs"),
    "hm-dependent-slices-wpp": ("duck", "420", 8, HM_MAIN, "--WaveFrontSynchro=1 --SliceSegmentMode=1 "
                                "--SliceSegmentArgument=5", "dependent slice segments with wavefront"),
    "hm-ctu16": ("duck-mid", "420", 8, HM_MAIN, "--MaxCUSize=16 --MaxPartitionDepth=2 --QuadtreeTULog2MaxSize=4", "16x16 CTUs"),
    "hm-ctu32": ("duck-mid", "420", 8, HM_MAIN, "--MaxCUSize=32 --MaxPartitionDepth=3", "32x32 CTUs"),
    "hm-cu-min16": ("duck-mid", "420", 8, HM_MAIN, "--MaxPartitionDepth=2 --ConformanceWindowMode=1 --QP=40",
                    "CUs of 64x64 to 16x16 only"),
    "hm-tu-depth1": ("duck-mid", "420", 8, HM_MAIN, "--QuadtreeTUMaxDepthIntra=1", "transform tree depth 1"),
    "hm-tu-depth4": ("duck-mid", "420", 8, HM_MAIN, "--QuadtreeTUMaxDepthIntra=4 --QuadtreeTULog2MinSize=2",
                     "transform tree depth 4"),
    "hm-tu-max16": ("duck-mid", "420", 8, HM_MAIN, "--QuadtreeTULog2MaxSize=4", "largest transform 16x16"),
    "hm-constrained-intra": ("duck-mid", "420", 8, HM_MAIN, "--ConstrainedIntraPred=1", "constrained intra prediction"),
    "hm-no-strong-smoothing": ("duck-mid", "420", 8, HM_MAIN, "--StrongIntraSmoothing=0", "no strong intra smoothing"),
    "hm-deltaqp": ("duck-mid", "420", 8, HM_MAIN, "--MaxDeltaQP=6 --MaxCuDQPDepth=2 --DeltaQpRD=2", "delta QP at 16x16"),
    "hm-adaptive-qp": ("duck", "420", 8, HM_MAIN, "--AdaptiveQP=1 --MaxQPAdaptationRange=6 --MaxCuDQPDepth=3",
                       "adaptive QP, delta QP at 8x8"),
    "hm-chroma-qp-offsets": ("duck-mid", "420", 8, HM_MAIN, "--CbQpOffset=4 --CrQpOffset=-3 --SliceCbQpOffsetIntraOrPeriodic=-2 "
                             "--SliceCrQpOffsetIntraOrPeriodic=2 --SliceChromaQPOffsetPeriodicity=1",
                             "PPS and slice chroma QP offsets"),
    "hm-cu-chroma-qp-adj": ("duck-mid", "444", 8, HM_REXT, "--MaxCUChromaQpAdjustmentDepth=1",
                            "CU chroma QP adjustment (range extension)"),
    "hm-crc-hash": ("duck-mid", "420", 8, HM_MAIN, "--SEIDecodedPictureHash=2", "CRC decoded picture hash"),
    "hm-checksum-hash": ("duck-mid", "420", 8, HM_MAIN, "--SEIDecodedPictureHash=3", "checksum decoded picture hash"),
    "hm-no-hash": ("duck-mid", "420", 8, HM_MAIN, "--SEIDecodedPictureHash=0", "no decoded picture hash"),
    "hm-vui": ("duck-mid", "420", 8, HM_MAIN, "--VuiParametersPresent=1 --TargetBitrate=1000000 --VideoSignalTypePresent=1 --VideoFullRange=1 "
               "--ColourDescriptionPresent=1 --ColourPrimaries=1 --TransferCharacteristics=13 --MatrixCoefficients=6 "
               "--ChromaLocInfoPresent=1 --ChromaSampleLocTypeTopField=0 --ChromaSampleLocTypeBottomField=0",
               "VUI colour description and chroma location, no colr box", ),
    "hm-odd-size": ("odd-130x66", "420", 8, HM_MAIN, "--ConformanceWindowMode=1", "130x66 padded, conformance window"),
    "hm-odd-30x17": ("odd-30x17", "422", 10, HM_REXT, "--ConformanceWindowMode=1", "30x17 4:2:2, conformance window"),
    "hm-egret-422-12bit": ("egret-mid", "422", 12, HM_REXT, "", "12-bit 4:2:2 (Main 4:2:2 12)"),
    "hm-444-8bit": ("duck-mid", "444", 8, HM_REXT, "", "Main 4:4:4"),
    "hm-444-10bit": ("duck-mid", "444", 10, HM_REXT, "", "Main 4:4:4 10"),
    "hm-444-12bit": ("duck-mid", "444", 12, HM_REXT, "", "Main 4:4:4 12"),
    "hm-422-10bit": ("duck-mid", "422", 10, HM_REXT, "", "Main 4:2:2 10, the 4:2:2 intra mode mapping"),
    "hm-420-12bit": ("duck-mid", "420", 12, HM_REXT, "", "Main 12"),
    "hm-400-8bit": ("duck-mid", "400", 8, HM_REXT, "", "Monochrome"),
    "hm-400-12bit": ("duck-mid", "400", 12, HM_REXT, "", "Monochrome 12"),
    "hm-ccp": ("duck-mid", "444", 10, HM_REXT, "--CrossComponentPrediction=1 --ReconBasedCrossCPredictionEstimate=1",
               "cross-component prediction"),
    "hm-rdpcm": ("duck-mid", "444", 8, HM_REXT, "--ImplicitResidualDPCM=1 --ExplicitResidualDPCM=1 --TransformSkip=1 "
                 "--TransformSkipFast=0 --TransformSkipLog2MaxSize=4", "implicit and explicit residual DPCM, transform skip 16x16"),
    "hm-tskip-ext": ("duck-mid", "422", 10, HM_REXT, "--TransformSkip=1 --TransformSkipFast=0 --TransformSkipLog2MaxSize=5 "
                     "--ResidualRotation=1 --SingleSignificanceMapContext=1", "transform skip to 32x32, residual rotation, single significance map context"),
    "hm-rice": ("duck-mid", "444", 12, HM_REXT, "--GolombRiceParameterAdaptation=1 --QP=4", "persistent Rice adaptation"),
    "hm-no-ref-smoothing": ("duck-mid", "444", 8, HM_REXT, "--IntraReferenceSmoothing=0", "intra smoothing disabled (range extension)"),
}

# A scaling list file for HM (its ScalingListFile format): every list, with a few patterns.
SCALING = "\n".join(
    [f"INTRA{size}X{size}_{c}\n" + "\n".join(",".join(str(16 + (x * 3 + y * 5 + k) % 40) for x in range(min(size, 8)))
                                            for y in range(min(size, 8))) + (f"\nINTRA{size}X{size}_{c}_DC\n{12 + k}" if size > 8 else "")
     for k, (size, c) in enumerate([(s, c) for s in (4, 8, 16, 32) for c in (("LUMA", "CHROMAU", "CHROMAV") if s < 32 else ("LUMA",))])]
    + [f"INTER{size}X{size}_{c}\n" + "\n".join(",".join("16" for _ in range(min(size, 8))) for _ in range(min(size, 8)))
       + (f"\nINTER{size}X{size}_{c}_DC\n16" if size > 8 else "")
       for size in (4, 8, 16, 32) for c in (("LUMA", "CHROMAU", "CHROMAV") if size < 32 else ("LUMA",))]) + "\n"


def hmenc(name, source, chroma, depth, cfg, options, src):
    rgb = src["rgb"][source]
    h, w = rgb.shape[:2]
    yuv = f"{TMP}/{name}.yuv"
    with open(yuv, "wb") as f:
        f.write(to_yuv(rgb, depth, chroma))
    with open(f"{TMP}/scaling.txt", "w") as f:
        f.write(SCALING)
    opts = options.replace("{scaling}", f"{TMP}/scaling.txt").split()
    if not any(o.startswith("--SEIDecodedPictureHash") for o in opts):
        opts.append("--SEIDecodedPictureHash=1")
    if not any(o.startswith("--ConformanceWindowMode") for o in opts) and (w % 8 or h % 8):
        opts.append("--ConformanceWindowMode=1")
    stream = f"{TMP}/{name}.265"
    args = ["TAppEncoder", "-c", f"{HMCFG}/{cfg}", "-i", yuv, "-wdt", w, "-hgt", h, "-fr", 1, "-f", 1, "-b", stream,
            "-o", "/dev/null", f"--InputBitDepth={depth}", f"--InternalBitDepth={depth}", f"--OutputBitDepth={depth}",
            f"--InputChromaFormat={chroma}", f"--ChromaFormatIDC={chroma}", *opts]
    r = sh(*args, check=False, timeout=3600)
    if r.returncode != 0 or not os.path.isfile(stream):
        print(f"skipped {name}: {(r.stderr or r.stdout).strip()[-400:]}")
        return None
    data = open(stream, "rb").read()
    props = [] if "--ColourDescriptionPresent=1" in opts else [(heif.nclx(2, 2, 6, True), False)]
    f = heif.wrap_stream(data, extra_props=props)
    with open(f"{OUT}/{name}.heic", "wb") as out:
        out.write(heif.write(f))
    return " ".join(o.replace(TMP + "/", "") for o in [f"-c {cfg}", f"{chroma} {depth}-bit", *opts])


# ---------------------------------------------------------------- container features, built by heif.py

def from_file(name):
    return heif.read(open(f"{OUT}/{name}.heic", "rb").read())


def save(name, f):
    with open(f"{OUT}/{name}.heic", "wb") as out:
        out.write(heif.write(f))


def container_files(src):
    """Builds the files whose container features heif-enc does not write, from the streams above. Returns name: (built from, what
    it covers)."""
    made = {}

    def make(name, base, covers, change):
        f = from_file(base)
        change(f, f.item(f.primary))
        save(name, f)
        made[name] = (base, covers)

    make("duck-clap", "hm-main", "clap crop to 280x200, offset from the center",
         lambda f, p: p.props.append((heif.clap(280, 200, (-8, 1), (6, 1)), True)))
    make("duck-clap-odd", "hm-main", "clap with fractional offsets (odd crop edges)",
         lambda f, p: p.props.append((heif.clap(279, 201, (-3, 2), (5, 2)), True)))
    for turns in (1, 2, 3):
        make(f"duck-irot{turns}", "hm-main", f"irot {90 * turns} degrees anticlockwise",
             lambda f, p, t=turns: p.props.append((heif.irot(t), True)))
    make("duck-imir0", "hm-main", "imir axis 0 (top-to-bottom mirror)", lambda f, p: p.props.append((heif.imir(0), True)))
    make("duck-imir1", "hm-main", "imir axis 1 (left-to-right mirror)", lambda f, p: p.props.append((heif.imir(1), True)))
    make("duck-clap-irot3-imir1", "hm-444-8bit", "clap, irot 270 and imir in that order",
         lambda f, p: p.props.extend([(heif.clap(300, 220), True), (heif.irot(3), True), (heif.imir(1), True)]))
    make("duck-transforms-not-essential", "hm-main", "clap, irot and imir not marked essential",
         lambda f, p: p.props.extend([(heif.clap(300, 220), False), (heif.irot(1), False), (heif.imir(0), False)]))
    make("duck-icc-rec2020-10bit", "hm-main10", "Rec. 2020 ICC profile only",
         lambda f, p: p.set_prop(heif.icc(open(f"{IMAGES}/icc/compact/Rec2020-v4.icc", "rb").read())))
    make("duck-no-colr", "hm-main", "no colr box (the SPS has no VUI)",
         lambda f, p: setattr(p, "props", [x for x in p.props if x[0][4:8] != b"colr"]))

    def exif_xmp(f, p):
        exif = f.add("Exif", struct.pack(">I", 0) + src["exif"])
        exif.refs["cdsc"] = [p.id]
        xmp = f.add("mime", src["xmp"], content_type="application/rdf+xml")
        xmp.refs["cdsc"] = [p.id]
    make("duck-exif-xmp", "hm-main", "Exif (orientation 6) and XMP items (cdsc)", exif_xmp)

    def exif_offset(f, p):
        # Exif data with a 6-byte "Exif\0\0" header: tiff_header_offset 6.
        exif = f.add("Exif", struct.pack(">I", 6) + b"Exif\0\0" + src["exif"])
        exif.refs["cdsc"] = [p.id]
    make("duck-exif-offset", "hm-main", "Exif item with a header offset of 6", exif_offset)

    def thumbnail(f, p):
        t = from_file("hm-thumb-src").item(1)
        t.id = f.next_id()
        t.refs = {"thmb": [p.id]}
        f.items.append(t)
    sh_thumb(src)
    make("duck-hm-thumb", "hm-main", "thumbnail item (thmb) of another size", thumbnail)

    def alpha(f, p, urn, premultiplied=False):
        a = from_file("hm-alpha-src").item(1)
        a.id = f.next_id()
        a.hidden = True
        a.props.append((heif.auxc(urn), True))
        a.refs = {"auxl": [p.id]}
        f.items.append(a)
        if premultiplied:
            p.refs["prem"] = [a.id]
    sh_alpha(src)
    make("duck-hm-alpha", "hm-small", "HEVC-coded 4:0:0 alpha (urn:mpeg:hevc:2015:auxid:1)",
         lambda f, p: alpha(f, p, "urn:mpeg:hevc:2015:auxid:1"))
    make("duck-hm-alpha-mpegb", "hm-small", "alpha with the MPEG-B URN",
         lambda f, p: alpha(f, p, "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"))
    make("duck-hm-alpha-prem", "hm-small", "premultiplied alpha (prem)",
         lambda f, p: alpha(f, p, "urn:mpeg:hevc:2015:auxid:1", True))
    make("duck-hm-depth", "hm-small", "depth auxiliary item (urn:mpeg:hevc:2015:auxid:2)",
         lambda f, p: alpha(f, p, "urn:mpeg:hevc:2015:auxid:2"))
    make("duck-hm-apple-gainmap-urn", "hm-small", "an auxiliary item with Apple's HDR gain map URN (a 4:0:0 picture)",
         lambda f, p: alpha(f, p, "urn:com:apple:photo:2020:aux:hdrgainmap"))

    def idat(f, p):
        p.idat = True
    make("duck-idat", "hm-main", "item data in idat (construction method 1)", idat)

    def ispe_mismatch(f, p):
        p.set_prop(heif.ispe(330, 240))
    make("broken-ispe-mismatch", "hm-main", "ispe wider than the SPS picture (must fail)", ispe_mismatch)

    def unknown_essential(f, p):
        p.props.append((heif.box("xyzw", b"\0\0\0\0"), True))
    make("broken-unknown-essential", "hm-main", "an unknown essential property (must fail)", unknown_essential)

    def truncated(f, p):
        p.data = p.data[:len(p.data) // 2]
    make("broken-truncated-item", "hm-main", "item data cut in half (must fail or decode partly)", truncated)

    def hidden_primary(f, p):
        p.hidden = True
    make("broken-hidden-primary", "hm-main", "primary item marked hidden", hidden_primary)

    # A grid of HM tiles: duck cut into 3x2 tiles of 224x240, output 640x480 (the last column cut).
    grid_files(src, made)
    return made


def sh_thumb(src):
    hmenc("hm-thumb-src", "duck-small", "420", 8, HM_MAIN, "--QP=37", src)
    hmenc("hm-small", "duck-small", "420", 8, HM_MAIN, "", src)


def sh_alpha(src):
    rgb = read_png(src["duck-alpha"])[0]
    a = np.repeat(rgb[..., 3:4], 3, axis=2)
    src["rgb"]["alpha-plane"] = a
    hmenc("hm-alpha-src", "alpha-plane", "400", 8, HM_REXT, "--QP=20", src)


def grid_files(src, made):
    duck = src["rgb"]["duck"]

    def tiles(tag, tw, th, chroma, depth, cfg, extra=""):
        out = []
        rows, cols = -(-duck.shape[0] // th), -(-duck.shape[1] // tw)
        for r in range(rows):
            for c in range(cols):
                cell = duck[r * th:(r + 1) * th, c * tw:(c + 1) * tw]
                cell = np.pad(cell, ((0, th - cell.shape[0]), (0, tw - cell.shape[1]), (0, 0)), mode="edge")
                key = f"cell-{tag}-{r}-{c}"
                src["rgb"][key] = cell
                hmenc(key, key, chroma, depth, cfg, extra, src)
                out.append(from_file(key).item(1))
                os.remove(f"{OUT}/{key}.heic")
        return rows, cols, out

    def build(name, tag, tw, th, chroma, depth, cfg, covers, out_w=640, out_h=480, extra=""):
        rows, cols, cells = tiles(tag, tw, th, chroma, depth, cfg, extra)
        f = heif.Heif()
        g = f.add("grid", heif.grid_data(rows, cols, out_w, out_h), idat=True)
        g.props = [(heif.ispe(out_w, out_h), False), (heif.nclx(2, 2, 6, True), False)]
        for cell in cells:
            cell.id = f.next_id()
            cell.hidden = True
            cell.props.append((heif.nclx(2, 2, 6, True), False))
            f.items.append(cell)
        g.refs["dimg"] = [c.id for c in cells]
        f.primary = g.id
        save(name, f)
        made[name] = ("duck", covers)
        return f

    build("grid-hm-3x2", "a", 224, 240, "420", 8, HM_MAIN, "grid of 3x2 HM tiles of 224x240, the last column cut")
    build("grid-hm-512-10bit", "b", 512, 512, "420", 10, HM_MAIN10, "grid of 512x512 10-bit tiles, as iPhones write")
    build("grid-hm-422", "c", 320, 240, "422", 10, HM_REXT, "2x2 grid of 4:2:2 tiles")
    f = build("grid-hm-crop", "d", 256, 256, "420", 8, HM_MAIN, "grid output smaller than its tiles (600x400)", 600, 400)
    # The same grid with clap and irot on the grid item.
    g = f.item(f.primary)
    g.props += [(heif.clap(500, 300), True), (heif.irot(1), True)]
    save("grid-hm-clap-irot", f)
    made["grid-hm-clap-irot"] = ("duck", "grid with clap and irot on the grid item")
    # Overlay (iovl): two tiles of the 3x2 grid placed on a filled canvas.
    base = from_file("grid-hm-3x2")
    o = heif.Heif()
    cells = [base.items[1], base.items[5]]
    iovl = o.add("iovl", bytes([0, 0]) + struct.pack(">HHHH", 0x2000, 0x8000, 0xC000, 0xFFFF) + struct.pack(">HH", 500, 400)
                 + struct.pack(">hhhh", 10, 20, 250, 120), idat=True)
    iovl.props = [(heif.ispe(500, 400), False)]
    for c in cells:
        c.id = o.next_id()
        o.items.append(c)
    iovl.refs["dimg"] = [c.id for c in cells]
    o.primary = iovl.id
    save("overlay-hm", o)
    made["overlay-hm"] = ("duck", "overlay (iovl) of two tiles on a filled canvas")


# ---------------------------------------------------------------- phone photos

def phone_path(entry):
    return f"{CACHE}/phones/{entry[0]}/{os.path.basename(entry[1])}"


def phone_name(entry):
    return "phone-" + re.sub(r"[^a-z0-9]+", "-", f"{entry[5]} {re.sub('heic$', '', os.path.splitext(os.path.basename(entry[1]))[0])}".lower()).strip("-")


def cut_grid(f, keep_w, keep_h):
    """Cuts every grid item of a HEIF down to the tiles covering its top-left keep_w x keep_h (scaled to each grid's size against
    the primary's), removing the other tiles; grids of auxiliary images (alpha, depth, gain maps) are cut to the same share."""
    primary = f.item(f.primary)
    full_w, full_h = struct.unpack(">II", primary.prop("ispe")[4:12])
    removed = []
    for g in [i for i in f.items if i.type == "grid"]:
        rows, cols, w, h = heif.read_grid(g.data)
        cells = g.refs["dimg"]
        tw, th = struct.unpack(">II", f.item(cells[0]).prop("ispe")[4:12])
        new_w = max(1, min(w, -(-keep_w * w // full_w)))
        new_h = max(1, min(h, -(-keep_h * h // full_h)))
        new_cols, new_rows = -(-new_w // tw), -(-new_h // th)
        kept = [cells[r * cols + c] for r in range(new_rows) for c in range(new_cols)]
        removed += [c for c in cells if c not in kept]
        g.data = heif.grid_data(new_rows, new_cols, new_w, new_h)
        g.refs["dimg"] = kept
        g.set_prop(heif.ispe(new_w, new_h))
    for t in [i for i in f.items if i.type == "tmap"]:
        t.set_prop(heif.ispe(*struct.unpack(">II", f.item(f.primary).prop("ispe")[4:12])))
    f.remove(removed)


def phones():
    """Cuts the committed phone photos to their top-left 1024x768 (a few grid tiles), every other item kept as it is."""
    made = {}
    os.makedirs(f"{OUT}/phone", exist_ok=True)
    for entry in PHONES:
        if not entry[6]:
            continue
        path = phone_path(entry)
        if not os.path.isfile(path):
            print(f"missing {path}")
            continue
        f = heif.read(open(path, "rb").read())
        cut_grid(f, 1024, 768)
        name = phone_name(entry)
        with open(f"{OUT}/phone/{name}.heic", "wb") as out:
            out.write(heif.write(f))
        made[f"phone/{name}.heic"] = {"source": f"https://archive.org/details/{entry[0]} {entry[1]}", "licence": entry[3],
                                      "creator": entry[4], "camera": entry[5],
                                      "covers": f"{entry[5]} photo, cut to the top-left 1024x768 of its grid"}
    return made


# ---------------------------------------------------------------- references

def heif_info(path):
    r = sh("heif-info", path, check=False)
    return (r.stdout + r.stderr).strip(), r.returncode == 0


def exiftool(path):
    r = sh("exiftool", "-j", "-G1", "-a", "-n", path, check=False)
    try:
        tags = json.loads(r.stdout)[0]
    except (ValueError, IndexError):
        return {"error": (r.stderr or r.stdout).strip()}
    return {k: v for k, v in tags.items() if not k.startswith("System:") and k not in ("SourceFile", "ExifTool:ExifToolVersion")}


def describe_prop(raw, essential):
    kind = raw[4:8].decode("latin-1")
    body = raw[8:]
    p = {"type": kind, "essential": essential}
    if kind == "ispe":
        p["size"] = list(struct.unpack(">II", body[4:12]))
    elif kind == "hvcC":
        _, nals, fields = heif.read_hvcc(body)
        p.update(fields)
        p["nal_types"] = [heif.nal_type(n) for n in nals]
    elif kind == "pixi":
        p["depths"] = list(body[5:5 + body[4]])
    elif kind == "colr":
        p["colour_type"] = body[:4].decode("latin-1")
        if p["colour_type"] == "nclx":
            p["cicp"] = list(struct.unpack(">HHH", body[4:10])) + [body[10] >> 7]
        else:
            p["icc_size"] = len(body) - 4
    elif kind in ("irot", "imir"):
        p["value"] = body[0] & (3 if kind == "irot" else 1)
    elif kind == "clap":
        p["fractions"] = list(struct.unpack(">8i", body[:32]))
    elif kind == "auxC":
        p["aux_type"] = body[4:].split(b"\0")[0].decode("latin-1")
    else:
        p["bytes"] = body[:64].hex()
    return p


def slice_types(stdout):
    return [m for m in re.findall(r"POC +(-?\d+) .*?\( ([IPB])-SLICE", stdout)]


def decode_hevc(stream, yuv_out=None):
    """HM's and dec265's decodes of an Annex B stream: MD5s, HM's picture hash checks and slice types."""
    with open(f"{TMP}/item.265", "wb") as f:
        f.write(stream)
    result = {}
    r = sh("TAppDecoder", "-b", f"{TMP}/item.265", "-o", f"{TMP}/hm.yuv", "-d", "0", check=False)
    out = r.stdout + r.stderr
    if r.returncode != 0 or not os.path.isfile(f"{TMP}/hm.yuv"):
        result["hm_error"] = out.strip()[-300:]
    else:
        hm = open(f"{TMP}/hm.yuv", "rb").read()
        result["hm_md5"] = md5(hm)
        result["hm_bytes"] = len(hm)
        hashes = re.findall(r"\[(MD5|CRC|Checksum):[^\]]*\((OK|\*\*\*ERROR\*\*\*)\)\]", out)
        result["hm_hash"] = [f"{k} {v}" for k, v in hashes]
        result["slices"] = [f"{poc} {t}" for poc, t in re.findall(r"POC +(-?\d+) .*?\( ([IPB])-SLICE", out)]
        if yuv_out:
            shutil.copy(f"{TMP}/hm.yuv", yuv_out)
        os.remove(f"{TMP}/hm.yuv")
    r = sh("libde265-dec265", "-q", "-o", f"{TMP}/de265.yuv", f"{TMP}/item.265", check=False)
    if not os.path.isfile(f"{TMP}/de265.yuv"):
        result["dec265_error"] = (r.stdout + r.stderr).strip()[-300:]
    else:
        d = open(f"{TMP}/de265.yuv", "rb").read()
        result["dec265_md5"] = md5(d)
        os.remove(f"{TMP}/de265.yuv")
        if "hm_md5" in result:
            result["dec265_equal"] = result["dec265_md5"] == result["hm_md5"]
    return result


def reference(name, path, entry, ref_folder, yuv_folder):
    entry["heif_info"], entry["heif_info_ok"] = heif_info(path)
    entry["exiftool"] = exiftool(path)
    try:
        f = heif.read(open(path, "rb").read())
    except Exception as e:  # malformed on purpose, or a sequence without meta
        f, entry["items_error"] = None, str(e)
    if f is not None:
        entry["brands"] = [f.major] + f.compatible
        entry["primary"] = f.primary
        listed = []
        os.makedirs(yuv_folder, exist_ok=True)
        for item in f.items:
            e = {"id": item.id, "type": item.type, "hidden": item.hidden, "data_size": len(item.data), "idat": item.idat,
                 "refs": item.refs, "properties": [describe_prop(r, es) for r, es in item.props]}
            if item.name:
                e["name"] = item.name
            if item.content_type:
                e["content_type"] = item.content_type
            if item.type == "hvc1" and item.prop("hvcC") and item.data:
                try:
                    e.update(decode_hevc(heif.item_annexb(item), f"{yuv_folder}/{name}.{item.id}.yuv"))
                except Exception as x:
                    e["decode_error"] = str(x)[-300:]
            listed.append(e)
        entry["items"] = listed
    png = f"{ref_folder}/{name}.png"
    os.makedirs(ref_folder, exist_ok=True)
    r = sh("heif-dec", "--quiet", "--png-compression-level", "9", path, png, check=False)
    # With several top-level images heif-dec writes each of them, as name-1.png, name-2.png and so on.
    numbered = re.compile(re.escape(name) + r"-(\d+)\.png")
    several = sorted((p for p in glob.glob(f"{ref_folder}/{glob.escape(name)}-*.png") if numbered.fullmatch(os.path.basename(p))),
                     key=lambda p: int(numbered.fullmatch(os.path.basename(p)).group(1)))
    if r.returncode == 0 and os.path.isfile(png):
        entry["reference"] = os.path.basename(png)
    elif r.returncode == 0 and several:
        entry["references"] = [os.path.basename(p) for p in several]
    else:
        entry["heif_dec_error"] = (r.stderr or r.stdout).strip()[-400:]
    r = sh("heif-dec", "--quiet", path, f"{TMP}/out.y4m", check=False)
    if r.returncode == 0 and os.path.isfile(f"{TMP}/out.y4m"):
        data = open(f"{TMP}/out.y4m", "rb").read()
        entry["heif_dec_y4m_header"] = data[:data.index(b"\n")].decode("latin-1")
        entry["heif_dec_y4m_md5"] = md5(data[data.index(b"FRAME\n") + 6:])
        os.remove(f"{TMP}/out.y4m")


def write_manifest(path, manifest):
    with open(path, "w") as f:
        json.dump({"tools": tools(), "files": manifest}, f, indent=1, sort_keys=True, ensure_ascii=False)
        f.write("\n")


def corpus():
    os.makedirs(TMP, exist_ok=True)
    for folder in (OUT, REF):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    src = sources()
    manifest = {}
    for name, (source, options, covers) in HEIFENC.items():
        used = heifenc(name, source, options, src)
        if used is not None:
            manifest[f"{name}.heic"] = {"source": source, "encoder": "heif-enc 1.23.4", "options": used, "covers": covers}
    for name, (source, chroma, depth, cfg, options, covers) in HMENC.items():
        used = hmenc(name, source, chroma, depth, cfg, options, src)
        if used is not None:
            manifest[f"{name}.heic"] = {"source": source, "encoder": "HM 18.0 TAppEncoder, wrapped by heif.py",
                                        "options": used, "covers": covers}
    for name, (base, covers) in container_files(src).items():
        manifest[f"{name}.heic"] = {"source": base, "encoder": "heif.py around HM 18.0 streams", "covers": covers}
    for helper in ("hm-thumb-src", "hm-alpha-src"):
        os.remove(f"{OUT}/{helper}.heic")
    for name in ("hm-small",):
        manifest[f"{name}.heic"] = {"source": "duck-small", "encoder": "HM 18.0 TAppEncoder, wrapped by heif.py",
                                    "covers": "HM Main intra, 256x192 (the base of the alpha and depth files)"}
    manifest.update(phones())
    for file, entry in sorted(manifest.items()):
        print(file, flush=True)
        name = file[:-5]
        sub = os.path.dirname(name)
        reference(os.path.basename(name), f"{OUT}/{file}", entry, f"{REF}/{sub}".rstrip("/"),
                  f"{CACHE}/reference/corpus/{sub}".rstrip("/"))
    write_manifest(f"{REF}/manifest.json", manifest)


# ---------------------------------------------------------------- conformance streams (stay in .cache)

def stream_frames(stream):
    """The SPS's picture size, chroma format and depths, for splitting a decode into pictures."""
    sps = next(n for n in heif.split_annexb(stream) if heif.nal_type(n) == 33)
    return heif.sps_info(sps)


def picture_bytes(info):
    w, h, c = info["width"], info["height"], info["chroma"]
    luma = w * h
    chroma = {0: 0, 1: 2 * ((w + 1) // 2) * ((h + 1) // 2), 2: 2 * ((w + 1) // 2) * h, 3: 2 * w * h}[c]
    bps = 2 if max(info["luma_depth"], info["chroma_depth"]) > 8 else 1
    return (luma + chroma) * bps


def conformance_stream(path, entry, folder, name):
    stream = open(path, "rb").read()
    r = sh("TAppDecoder", "-b", path, "-o", f"{TMP}/conf.yuv", "-d", "0", check=False, timeout=7200)
    out = r.stdout + r.stderr
    entry["hm_exit"] = r.returncode
    entry["slices"] = [f"{poc} {t}" for poc, t in re.findall(r"POC +(-?\d+) .*?\( ([IPB])-SLICE", out)]
    hashes = re.findall(r"\[(MD5|CRC|Checksum):[^\]]*\((OK|\*\*\*ERROR\*\*\*)\)\]", out)
    entry["hm_hash_ok"] = sum(1 for _, v in hashes if v == "OK")
    entry["hm_hash_errors"] = sum(1 for _, v in hashes if v != "OK")
    entry["all_intra"] = bool(entry["slices"]) and all(s.endswith(" I") for s in entry["slices"])
    try:
        info = stream_frames(stream)
        entry["sps"] = {k: v for k, v in info.items() if k != "ptl"}
        entry["sps"]["profile_idc"] = info["ptl"][0] & 31
    except Exception as e:
        info, entry["sps_error"] = None, str(e)
    if os.path.isfile(f"{TMP}/conf.yuv"):
        data = open(f"{TMP}/conf.yuv", "rb").read()
        entry["hm_md5"] = md5(data)
        if info:
            n = picture_bytes(info)
            entry["first_md5"] = md5(data[:n])
            if entry["all_intra"] or (entry["slices"] and entry["slices"][0].endswith(" I")):
                with open(f"{folder}/{name}.first.yuv", "wb") as f:
                    f.write(data[:n])
        os.remove(f"{TMP}/conf.yuv")
    r = sh("libde265-dec265", "-q", "-f", "1", "-o", f"{TMP}/conf265.yuv", path, check=False, timeout=3600)
    if os.path.isfile(f"{TMP}/conf265.yuv"):
        d = open(f"{TMP}/conf265.yuv", "rb").read()
        if "first_md5" in entry and info:
            entry["dec265_first_equal"] = md5(d[:picture_bytes(info)]) == entry["first_md5"]
        os.remove(f"{TMP}/conf265.yuv")


def suite_md5(path):
    """The suite's own MD5 of a stream's whole decode, from the MD5 files next to it: the line or file that names a .yuv (some
    hold the bitstream's MD5 instead, named .bit or .bin)."""
    for s in sorted(glob.glob(os.path.join(os.path.dirname(path), "*"))):
        base = os.path.basename(s)
        if not re.search(r"md5", base, re.IGNORECASE) or base.lower().endswith((".bit", ".bin", ".265")):
            continue
        for line in open(s, "rb").read().decode("latin-1").splitlines():
            m = re.match(r"\s*([0-9a-fA-F]{32})\b\s*\*?\s*(\S*)", line)
            if not m:
                continue
            name, label = m.group(2).lower(), (m.group(2) or base).lower()
            if name.endswith((".bit", ".bin", ".265")):
                continue
            # A decode's MD5 names its .yuv (or .gbr, for RGB streams); a file holding only a hash is the decode's unless its
            # name says it is the bitstream's (.bit.md5, _bin.md5).
            if re.search(r"yuv|gbr|rgb", label) or (not name and not re.search(r"[._](bit|bin|265)\.md5$", base.lower())):
                return {"suite_md5": m.group(1).lower(), "suite_md5_file": base}
    return {}


def suite():
    """Fills the suite MD5s of existing summaries again, without decoding."""
    for set_name in ("HEVC_v1", "RExt", "nokia"):
        path = f"{CACHE}/reference/{set_name}/summary.json"
        if not os.path.isfile(path):
            continue
        summary = json.load(open(path))
        for entry in summary["streams"].values():
            for key in ("suite_md5", "suite_md5_file", "hm_matches_suite"):
                entry.pop(key, None)
            entry.update(suite_md5(f"{CACHE}/{entry['path']}"))
            if "suite_md5" in entry and "hm_md5" in entry:
                entry["hm_matches_suite"] = entry["hm_md5"] == entry["suite_md5"]
        with open(path, "w") as f:
            json.dump(summary, f, indent=1, sort_keys=True)
            f.write("\n")


def conformance():
    os.makedirs(TMP, exist_ok=True)
    sets = {"HEVC_v1": f"{CACHE}/hevc/HEVC_v1", "RExt": f"{CACHE}/hevc/RExt", "nokia": f"{CACHE}/nokia/bitstreams"}
    for set_name, root in sets.items():
        if not os.path.isdir(root):
            continue
        folder = f"{CACHE}/reference/{set_name}"
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
        summary = {}
        streams = sorted(glob.glob(f"{root}/**/*.bit", recursive=True) + glob.glob(f"{root}/**/*.bin", recursive=True)
                         + glob.glob(f"{root}/**/*.265", recursive=True))
        for path in streams:
            name = os.path.basename(path)
            entry = {"path": os.path.relpath(path, CACHE)}
            entry.update(suite_md5(path))
            print(set_name, name, flush=True)
            conformance_stream(path, entry, folder, name)
            if "suite_md5" in entry and "hm_md5" in entry:
                entry["hm_matches_suite"] = entry["hm_md5"] == entry["suite_md5"]
            summary[name] = entry
            with open(f"{folder}/summary.json", "w") as f:
                json.dump({"tools": tools(), "streams": summary}, f, indent=1, sort_keys=True)
                f.write("\n")


def extra():
    os.makedirs(TMP, exist_ok=True)
    folder = f"{CACHE}/reference/extra"
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    manifest = {}
    files = {}
    for entry in PHONES:
        path = phone_path(entry)
        if os.path.isfile(path):
            files[f"phones/{phone_name(entry)}.heic"] = (path, {"source": f"https://archive.org/details/{entry[0]} {entry[1]}",
                                                                 "licence": entry[3], "creator": entry[4], "camera": entry[5]})
    for path in sorted(glob.glob(f"{CACHE}/ffc/**/*.hei[cf]", recursive=True)):
        files["ffc/" + os.path.relpath(path, f"{CACHE}/ffc")] = (path, {"source": "MPEG File Format Conformance"})
    for path in sorted(glob.glob(f"{CACHE}/nokia/conformance_files/*.heic")):
        files["nokia/" + os.path.basename(path)] = (path, {"source": "Nokia HEIF conformance"})
    for key, (path, entry) in sorted(files.items()):
        print(key, flush=True)
        name = key.rsplit(".", 1)[0]
        reference(os.path.basename(name), path, entry, f"{folder}/{os.path.dirname(name)}", f"{folder}/yuv/{os.path.dirname(name)}")
        manifest[key] = entry
    write_manifest(f"{folder}/manifest.json", manifest)


def frames():
    """Per-frame MD5s of HM's whole decode of every conformance stream, in output order (Phase 2.9.10): {set}/frames.json."""
    os.makedirs(TMP, exist_ok=True)
    for set_name in ("HEVC_v1", "RExt"):
        path = f"{CACHE}/reference/{set_name}/summary.json"
        if not os.path.isfile(path):
            continue
        result = {}
        for name, entry in sorted(json.load(open(path))["streams"].items()):
            print(set_name, name, flush=True)
            info = entry.get("sps")
            r = sh("TAppDecoder", "-b", f"{CACHE}/{entry['path']}", "-o", f"{TMP}/frames.yuv", "-d", "0", check=False, timeout=7200)
            if not info or not os.path.isfile(f"{TMP}/frames.yuv"):
                result[name] = {"hm_exit": r.returncode}
                continue
            data = open(f"{TMP}/frames.yuv", "rb").read()
            os.remove(f"{TMP}/frames.yuv")
            n = picture_bytes(info)
            item = {"hm_exit": r.returncode, "frame_bytes": n, "bytes": len(data)}
            if len(data) % n == 0:
                item["frames"] = [md5(data[i:i + n]) for i in range(0, len(data), n)]
            result[name] = item
            with open(f"{CACHE}/reference/{set_name}/frames.json", "w") as f:
                json.dump({"tools": tools(), "streams": result}, f, indent=1, sort_keys=True)
                f.write("\n")


HEADER_LINE = re.compile(r"^\s*\d+\s+(\S+)\s+(?:u\(\d+\)|ue\(v\)|se\(v\)|f\(\d+\))\s*:\s*(-?\d+)\s*$")


def hm_trace(stream, keep=None):
    """HM's syntax trace of an Annex B stream: the header elements of each NAL unit as [nal type, [[name, value], ...]], and,
    when keep names a file, the whole trace gzipped there."""
    import gzip
    folder = f"{TMP}/trace"
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    with open(f"{folder}/in.265", "wb") as f:
        f.write(stream)
    subprocess.run(["TAppDecoderTrace", "-b", "in.265", "-o", "/dev/null"], cwd=folder, capture_output=True, timeout=3600)
    path = f"{folder}/TraceDec.txt"
    if not os.path.isfile(path):
        return None
    nals = []
    with open(path, encoding="latin-1") as f:
        for line in f:
            if line.startswith("*********** NAL UNIT"):
                nals.append([line.split("(", 1)[1].split(")", 1)[0], []])
                continue
            m = HEADER_LINE.match(line)
            if m and nals:
                nals[-1][1].append([m.group(1), int(m.group(2))])
    if keep:
        os.makedirs(os.path.dirname(keep), exist_ok=True)
        with open(path, "rb") as src, gzip.open(keep, "wb", compresslevel=6) as dst:
            shutil.copyfileobj(src, dst)
    return nals


def traces():
    import gzip
    os.makedirs(TMP, exist_ok=True)
    manifest = json.load(open(f"{REF}/manifest.json"))["files"]
    headers = {}
    for file, entry in sorted(manifest.items()):
        try:
            f = heif.read(open(f"{OUT}/{file}", "rb").read())
        except Exception:
            continue
        for item in f.items:
            if item.type != "hvc1" or not item.prop("hvcC") or not item.data or file.startswith("broken-"):
                continue
            key = f"{file}#{item.id}"
            print(key, flush=True)
            nals = hm_trace(heif.item_annexb(item), f"{CACHE}/reference/traces/corpus/{file[:-5]}.{item.id}.trace.gz")
            if nals is not None:
                headers[key] = nals
    with gzip.open(f"{REF}/hm-headers.json.gz", "wt", encoding="utf-8", compresslevel=9) as out:
        json.dump({"tools": tools(), "items": headers}, out, separators=(",", ":"), sort_keys=True)
    for set_name in ("HEVC_v1", "RExt"):
        path = f"{CACHE}/reference/{set_name}/summary.json"
        if not os.path.isfile(path):
            continue
        headers = {}
        for name, entry in sorted(json.load(open(path))["streams"].items()):
            if "hm_md5" not in entry or not entry.get("slices") or not entry["slices"][0].endswith(" I"):
                continue
            print(set_name, name, flush=True)
            unit = heif.first_access_unit(open(f"{CACHE}/{entry['path']}", "rb").read())
            nals = hm_trace(unit, f"{CACHE}/reference/traces/{set_name}/{name}.trace.gz")
            if nals is not None:
                headers[name] = nals
        with gzip.open(f"{CACHE}/reference/{set_name}/hm-headers.json.gz", "wt", encoding="utf-8", compresslevel=9) as out:
            json.dump({"tools": tools(), "streams": headers}, out, separators=(",", ":"), sort_keys=True)


if __name__ == "__main__":
    for step in sys.argv[1:]:
        {"corpus": corpus, "conformance": conformance, "extra": extra, "suite": suite, "traces": traces, "frames": frames}[step]()
