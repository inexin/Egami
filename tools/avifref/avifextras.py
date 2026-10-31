"""Builds the AVIF extras corpus (2026-10-31) in Docker: sample transform (sato), mini box and spatially layered files.

Runs in egami-avifref-extras:1 (tools/avifref/Dockerfile.extras: egami-avifref:2.9.2 plus avifenc-mini and avifdec-mini, libavif
1.4.2 with its experimental mini box). Writes tests/images/avif/extras/*.avif from Egami's own pictures (tests/images/ops), and
tests/reference/avif/extras: avifdec's RGB decode of each (with --sato for sample transforms, through avifdec-mini for mini
files), the fallback decode of each sato file (avifdec without --sato), and manifest.json with each file's options and avifdec
--info's text. The tools run as oracles only.
"""
import json
import os
import shutil
import subprocess
import sys

import numpy as np

sys.path.insert(0, "/t")
from avifcorpus import read_png, sh, write_png  # noqa: E402

W = "/w"
IMAGES = f"{W}/tests/images"
OUT = f"{IMAGES}/avif/extras"
REF = f"{W}/tests/reference/avif/extras"
TMP = "/tmp/avifextras"


def sources():
    duck, _ = read_png(f"{IMAGES}/ops/duck.png")
    duck = duck[..., :3]
    small = duck[96:288, 160:416]  # 256x192
    mid = duck[120:360, 160:480]  # 320x240
    s = {}

    def put(name, pixels, depth=8):
        s[name] = f"{TMP}/src-{name}.png"
        write_png(s[name], pixels, depth)

    put("duck-small", small)
    put("duck-mid", mid)
    put("odd-31x17", duck[200:217, 300:331])
    # Gray is a 16-bit sato source only: avifenc-mini fails to write a monochrome mini file.
    gray = (small @ np.array([0.299, 0.587, 0.114])).round().astype(np.uint16)[..., None]
    ramp = np.linspace(0, 255, small.shape[1]).round().astype(np.uint16)
    alpha = np.broadcast_to(ramp[None, :, None], small.shape[:2] + (1,)).copy()
    alpha[60:132, 96:160] = 0
    put("duck-alpha", np.concatenate([small, alpha], axis=2))
    # 16 bits with noise in the low byte, so the residual image has work to do.
    noise = np.arange(small.size, dtype=np.uint32).reshape(small.shape) * 2654435761 >> 24 & 0xFF
    wide = (small.astype(np.uint32) * 257 ^ noise).astype(np.uint16)
    put("duck-16bit", wide, 16)
    wide_alpha = (alpha.astype(np.uint32) * 257 ^ (noise[..., :1] >> 1)).astype(np.uint16)
    put("duck-16bit-alpha", np.concatenate([wide, wide_alpha], axis=2), 16)
    wide_gray = (gray.astype(np.uint32) * 257 ^ noise[..., :1]).astype(np.uint16)
    put("duck-16bit-gray", wide_gray, 16)
    return s


# name: (tool, source, options, covers)
FILES = {
    "sato-8b8b-lossless": ("avifenc", "duck-16bit", "-l -d 8,8", "16 bits lossless as 8-bit base and 8-bit residual"),
    "sato-12b4b-lossless": ("avifenc", "duck-16bit", "-l -d 12,4", "16 bits lossless as 12-bit base and 4 bits"),
    "sato-12b8b-q60": ("avifenc", "duck-16bit", "-y 444 -q 60 -d 12,8", "16 bits lossy, 12-bit base and 8-bit residual"),
    "sato-12b8b-q90-420": ("avifenc", "duck-16bit", "-y 420 -q 90 -d 12,8", "16 bits lossy 4:2:0"),
    "sato-alpha": ("avifenc", "duck-16bit-alpha", "-l -d 8,8", "16-bit color and alpha"),
    "sato-gray": ("avifenc", "duck-16bit-gray", "-l -d 12,8", "16-bit gray"),
    "mini-420": ("avifenc-mini", "duck-small", "--mini -y 420 -q 60", "mini box, 4:2:0"),
    "mini-444-10bit": ("avifenc-mini", "duck-16bit", "--mini -y 444 -q 70 -d 10", "mini box, 10-bit 4:4:4"),
    "mini-lossless": ("avifenc-mini", "duck-small", "--mini -l", "mini box, lossless"),
    "mini-alpha": ("avifenc-mini", "duck-alpha", "--mini -y 420 -q 60", "mini box with alpha"),
    "mini-odd": ("avifenc-mini", "odd-31x17", "--mini -y 420 -q 60", "mini box, odd size"),
    "mini-icc-exif-xmp": ("avifenc-mini", "duck-small",
                          "--mini -y 420 -q 60 --icc {icc} --exif {exif} --xmp {xmp}", "mini box with ICC, Exif and XMP"),
    "mini-irot-imir": ("avifenc-mini", "duck-small", "--mini -y 444 -q 60 --irot 1 --imir 0", "mini box with irot and imir"),
    "mini-hdr": ("avifenc-mini", "duck-16bit", "--mini -y 420 -q 60 -d 10 --cicp 9/16/9 --clli 1000,400", "mini box, PQ with clli"),
    "layered-scaled": ("avifenc", "duck-mid+duck-mid",
                       "-y 420 --layered --scaling-mode:u 1/2 -q:u 30 {in} --scaling-mode:u 1 -q:u 70 {in}",
                       "two layers, the first at half size"),
}


def main():
    os.makedirs(TMP, exist_ok=True)
    for folder in (OUT, REF):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    src = sources()
    exif = f"{TMP}/exif.bin"
    with open(exif, "wb") as f:
        # A small big-endian TIFF with one IFD entry: Orientation 1.
        f.write(bytes.fromhex("4d4d002a00000008000101120003000000010001000000000000"))
    xmp = f"{TMP}/xmp.xml"
    with open(xmp, "w") as f:
        f.write('<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"/></x:xmpmeta>')
    icc = f"{IMAGES}/icc/compact/DisplayP3Compat-v4.icc"
    manifest = {}
    for name, (tool, source, options, covers) in FILES.items():
        out = f"{OUT}/{name}.avif"
        args = options.replace("{icc}", icc).replace("{exif}", exif).replace("{xmp}", xmp).split()
        inputs = [src[s] for s in source.split("+")]
        if "{in}" in args:
            args = [inputs[0] if a == "{in}" else a for a in args]
            inputs = []
        r = sh(tool, "-j", "1", *args, *inputs, "-o", out, check=False)
        if r.returncode:
            print(f"skipped {name}: {(r.stderr or r.stdout).strip()[-300:]}")
            continue
        entry = {"source": source, "encoder": sh(tool, "--version").stdout.strip().splitlines()[0], "tool": tool,
                 "options": options, "covers": covers}
        decoder = "avifdec-mini" if tool == "avifenc-mini" else "avifdec"
        sato = name.startswith("sato")
        r = sh(decoder, "--info", "-j", "1", *(["--sato"] if sato else []), out, check=False)
        entry["avifdec_info"] = (r.stdout + r.stderr).strip()
        deep = sato or "-d 10" in options
        png = f"{REF}/{name}.png"
        r = sh(decoder, "-j", "1", "--png-compress", "9", "-d", "16" if deep else "8", *(["--sato"] if sato else []), out, png, check=False)
        if r.returncode:
            entry["avifdec_error"] = (r.stderr or r.stdout).strip()[-400:]
        else:
            entry["reference"] = os.path.basename(png)
        if sato:
            fallback = f"{REF}/{name}-fallback.png"
            r = sh(decoder, "-j", "1", "--png-compress", "9", "-d", "16", out, fallback, check=False)
            if r.returncode == 0:
                entry["fallback"] = os.path.basename(fallback)
        manifest[f"{name}.avif"] = entry
        print(name, flush=True)
    with open(f"{REF}/manifest.json", "w") as f:
        json.dump({"files": manifest}, f, indent=1, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    main()
