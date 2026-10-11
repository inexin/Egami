"""Builds the WebP corpus in tests/images/webp and its references in tests/reference/webp, with libwebp in Docker.

Corpus (tests/images/webp; licenses in tests/images/LICENSES), all made here from the 1.x corpora by libwebp's encoder:
  - Lossless stills (VP8L) at every lossless preset (z 0 to 9, which covers every method and the palette, color-cache and
    transform choices libwebp makes), exact and cleared transparent colors, near-lossless, palette images of 2 to 128 colors
    (color indexing with and without pixel bundling), gray, and odd sizes down to 1 × 1.
  - Lossy stills (VP8) at qualities 0 to 100, methods 0 to 6, 1 to 4 segments, noise shaping off and full, the simple and the
    normal loop filter, no filter, sharpness, sharp YUV, and odd sizes. libwebp 1.3.2's encoder ignores
    config.partitions (it writes one token partition whatever is asked), so files with 2 to 8 partitions are made by Egami's
    encoder in the tests and checked against dwebp there.
  - Lossy stills with alpha (ALPH): raw and lossless-compressed alpha, each with no, horizontal-or-vertical ("fast") and best
    filtering, and quantized alpha.
  - Animations by img2webp and gif2webp from the 1.4 GIF and APNG corpus: lossless, lossy, mixed, with loop counts, blending and
    disposal as libwebp's muxer chooses them.
  - Metadata by webpmux: ICC, EXIF (with and without the "Exif\\0\\0" prefix some writers add) and XMP chunks on lossless and lossy
    images.

References (tests/reference/webp), all from tools/webpref/webpref.c, a small libwebp program compiled inside the container:
  <file>.rgba.gz           WebPDecode to RGBA (libwebp's default fancy upsampling for lossy), rows packed, gzipped
  <file>.nofancy.rgba.gz   some lossy files: the same with no_fancy_upsampling (replicated chroma), for FastUpsampling
  <file>.yuv.gz            lossy files: WebPDecodeYUV, the Y plane then U then V at (w + 1) / 2 × (h + 1) / 2, packed
  <file>.frames.rgba.gz    animations: every canvas WebPAnimDecoder composes (transparent start, background color ignored)
  manifest.json            per file: what made it (tool and settings), WebPGetFeatures and WebPDemux's reading (size, alpha,
                           format, frames, loop, background, VP8X flags), animation timestamps, which reference files hold its
                           pixels (files that decode alike share one), and the tool versions

Usage, from the repo root with Docker running:
  python tools/make-webp-references.py
The script runs alpine:3.20, installs libwebp 1.3.2 (libwebp-dev, libwebp-tools), gcc, musl-dev and Pillow inside it with apk,
and compiles tools/webpref/webpref.c. libwebp's own test-data files are not used until their license is checked.
"""
import gzip
import hashlib
import json
import os
import shutil
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow gcc musl-dev libwebp-dev libwebp-tools"
CORPUS = "tests/images/webp"
TARGET = "tests/reference/webp"
TOOL = "/tmp/webpref"
WORK = "/tmp/webpwork"

# Lossy files that also get a no-fancy-upsampling reference.
NOFANCY = {"lossy-q75", "lossy-egret-odd", "lossy-odd", "lossy-tall", "lossy-alpha-m1-f1", "lossy-large"}


def run(*args, **kwargs):
    return subprocess.run(list(map(str, args)), check=True, capture_output=True, text=True, **kwargs).stdout


def webpref(*args):
    return json.loads(run(TOOL, *args))


def write_gz(path, data):
    # No name and a zero time in the header, so regenerating gives identical files.
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def sources():
    """The pictures the corpus is encoded from, as Pillow RGBA images, by name."""
    from PIL import Image

    duck = Image.open("tests/images/ops/duck.png").convert("RGBA")
    egret = Image.open("tests/images/ops/egret.png").convert("RGBA")
    shapes = Image.open("tests/images/ops/shapes.png").convert("RGBA")
    zone = Image.open("tests/images/ops/zone.png").convert("RGBA")
    result = {
        "duck": duck.resize((160, 120), Image.LANCZOS),
        "duck-large": duck.resize((320, 240), Image.LANCZOS),
        "egret-odd": egret.resize((97, 61), Image.LANCZOS),
        "shapes": shapes,
        "zone": zone,
        "rgba32": Image.open("tests/images/pngsuite/basn6a08.png").convert("RGBA"),
        "graya32": Image.open("tests/images/pngsuite/basn4a08.png").convert("RGBA"),
        "gray": duck.resize((160, 120), Image.LANCZOS).convert("L").convert("RGBA"),
        "one": duck.resize((1, 1), Image.LANCZOS),
        "tall": duck.resize((3, 300), Image.LANCZOS),
        "wide": duck.resize((300, 1), Image.LANCZOS),
        "odd-17x33": egret.resize((17, 33), Image.LANCZOS),
    }
    for colors in (2, 4, 16, 128):
        result[f"colors-{colors}"] = Image.open(f"tests/images/gif/colors-{colors}.gif").convert("RGBA")

    # Alpha that is neither all opaque nor all clear, with colors under fully transparent pixels.
    soft = duck.resize((160, 120), Image.LANCZOS)
    alpha = Image.linear_gradient("L").resize((160, 120))
    soft.putalpha(alpha)
    result["duck-alpha"] = soft
    return result


# Each still: (file name, source, webpref encode options).
STILLS = (
    [(f"lossless-z{z}", "duck", ["z=%d" % z]) for z in range(10)]
    + [
        ("lossless-shapes-z6", "shapes", ["z=6"]),
        ("lossless-shapes-exact", "shapes", ["z=6", "exact=1"]),
        ("lossless-zone", "zone", ["z=6", "exact=1"]),
        ("lossless-rgba32", "rgba32", ["z=6", "exact=1"]),
        ("lossless-graya32", "graya32", ["z=6", "exact=1"]),
        ("lossless-alpha-exact", "duck-alpha", ["z=6", "exact=1"]),
        ("lossless-alpha-cleared", "duck-alpha", ["z=6"]),
        ("lossless-gray", "gray", ["z=6"]),
        ("lossless-near60", "duck", ["lossless=1", "near=60"]),
        ("lossless-one", "one", ["z=6"]),
        ("lossless-tall", "tall", ["z=6"]),
        ("lossless-wide", "wide", ["z=6"]),
        ("lossless-odd", "odd-17x33", ["z=6"]),
        ("lossless-egret-odd", "egret-odd", ["z=3"]),
    ]
    + [(f"lossless-colors-{c}", f"colors-{c}", ["z=6"]) for c in (2, 4, 16, 128)]
    + [(f"lossless-colors-{c}-z0", f"colors-{c}", ["z=0"]) for c in (2, 16)]
    + [(f"lossy-q{q}", "duck", ["q=%d" % q]) for q in (0, 10, 50, 75, 90, 100)]
    + [(f"lossy-m{m}", "duck", ["q=75", "m=%d" % m]) for m in range(7)]
    + [
        ("lossy-segments1", "duck", ["segments=1"]),
        ("lossy-segments2", "duck", ["segments=2"]),
        ("lossy-sns0", "duck", ["sns=0"]),
        ("lossy-sns100", "duck", ["sns=100"]),
        ("lossy-nofilter", "duck", ["f=0"]),
        ("lossy-simple", "duck", ["strong=0", "f=60"]),
        ("lossy-strong-sharp7", "duck", ["strong=1", "sharpness=7"]),
        ("lossy-autofilter", "duck", ["autofilter=1"]),
        ("lossy-sharpyuv", "duck", ["sharp_yuv=1"]),
        ("lossy-large", "duck-large", ["q=80"]),
        ("lossy-gray", "gray", ["q=75"]),
        ("lossy-egret-odd", "egret-odd", ["q=75"]),
        ("lossy-one", "one", ["q=75"]),
        ("lossy-tall", "tall", ["q=75"]),
        ("lossy-wide", "wide", ["q=75"]),
        ("lossy-odd", "odd-17x33", ["q=75"]),
    ]
    + [(f"lossy-alpha-m{m}-f{f}", "duck-alpha", ["q=75", "alpha_method=%d" % m, "alpha_filter=%d" % f])
       for m in (0, 1) for f in (0, 1, 2)]
    + [
        ("lossy-alpha-q50", "duck-alpha", ["q=75", "alpha_q=50"]),
        ("lossy-alpha-shapes", "shapes", ["q=75"]),
        ("lossy-alpha-rgba32", "rgba32", ["q=90", "alpha_filter=2"]),
        ("lossy-alpha-exact", "duck-alpha", ["q=75", "exact=1"]),
    ]
)

# Each animation: (file name, tool, arguments before the frames, source animation, per-frame options).
ANIMATIONS = [
    ("anim-lossless", "img2webp", ["-loop", "3"], "tests/images/gif/sprite-dispose2.gif", ["-lossless"]),
    ("anim-lossy", "img2webp", ["-loop", "0"], "tests/images/gif/sprite-dispose2.gif", ["-lossy", "-q", "70"]),
    ("anim-mixed", "img2webp", ["-mixed", "-loop", "0"], "tests/images/apng/pan-rgb.png", []),
    ("anim-pan-lossless", "img2webp", ["-loop", "0"], "tests/images/apng/pan-rgb.png", ["-lossless"]),
    ("anim-sprite-alpha", "img2webp", ["-loop", "0"], "tests/images/apng/sprite-over-background.png", ["-lossy", "-q", "80"]),
    ("anim-gif2webp", "gif2webp", [], "tests/images/gif/page-dispose-background.gif", []),
    ("anim-gif2webp-lossy", "gif2webp", ["-lossy", "-q", "60"], "tests/images/gif/pan-optimized.gif", []),
    ("anim-gif2webp-mixed", "gif2webp", ["-mixed"], "tests/images/gif/sprite-dispose3.gif", []),
    ("anim-gif2webp-loop3", "gif2webp", [], "tests/images/gif/loop-3.gif", []),
]

XMP = (b'<?xpacket begin="\xef\xbb\xbf" id="W5M0MpCehiHzreSzNTczkc9d"?>'
       b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
       b'<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator><rdf:Seq><rdf:li>Egami</rdf:li>'
       b'</rdf:Seq></dc:creator></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>')


def frames_of(path):
    """Writes each composed frame of a GIF or APNG as PNG and returns the paths with their delays in ms."""
    from PIL import Image, ImageSequence

    out = []
    with Image.open(path) as im:
        for i, frame in enumerate(ImageSequence.Iterator(im)):
            name = os.path.join(WORK, f"frame-{i:03}.png")
            frame.convert("RGBA").save(name)
            out.append((name, frame.info.get("duration", 100)))
    return out


def inside():
    from PIL import Image

    subprocess.run(["gcc", "-O2", "-Wall", "-o", TOOL, "tools/webpref/webpref.c", "-lwebp", "-lwebpdemux"], check=True)
    for folder in (CORPUS, TARGET):
        if os.path.isdir(folder):
            shutil.rmtree(folder)
        os.makedirs(folder)
    os.makedirs(WORK, exist_ok=True)
    pictures = sources()
    entries = []
    first = {}  # sha256 of a reference -> the file that holds it

    def keep(name, data):
        """Writes a reference, or returns the earlier file with the same bytes (lossless files of one source decode alike)."""
        digest = hashlib.sha256(data).hexdigest()
        if digest not in first:
            write_gz(os.path.join(TARGET, name), data)
            first[digest] = name
        return first[digest]

    def references(name, entry):
        path = os.path.join(CORPUS, name + ".webp")
        info = webpref("info", path)
        entry.update(info=info)
        raw = os.path.join(WORK, "out.raw")
        if info["animation"]:
            entry["timestamps"] = webpref("anim", path, raw)["timestamps"]
            with open(raw, "rb") as f:
                entry["frames"] = keep(name + ".frames.rgba.gz", f.read())
        else:
            webpref("decode", path, raw)
            with open(raw, "rb") as f:
                entry["rgba"] = keep(name + ".rgba.gz", f.read())
            if info["format"] == 1:
                if name in NOFANCY:
                    webpref("decode", path, raw, "nofancy")
                    with open(raw, "rb") as f:
                        entry["nofancy"] = keep(name + ".nofancy.rgba.gz", f.read())
                webpref("yuv", path, raw)
                with open(raw, "rb") as f:
                    entry["yuv"] = keep(name + ".yuv.gz", f.read())
        entry["size"] = os.path.getsize(path)
        entries.append(entry)

    for name, source, options in STILLS:
        picture = pictures[source]
        raw = os.path.join(WORK, "in.rgba")
        with open(raw, "wb") as f:
            f.write(picture.tobytes())
        result = webpref("encode", raw, picture.width, picture.height, os.path.join(CORPUS, name + ".webp"), *options)
        references(name, {"file": name + ".webp", "source": source, "tool": "webpref encode", "options": options,
                          "config": result["config"]})

    for name, tool, args, source, per_frame in ANIMATIONS:
        out = os.path.join(CORPUS, name + ".webp")
        if tool == "gif2webp":
            run("gif2webp", "-quiet", *args, source, "-o", out)
        else:
            frames = frames_of(source)
            command = ["img2webp", *args]
            for path, delay in frames:
                command += ["-d", str(delay), *per_frame, path]
            run(*command, "-o", out)
            for path, _ in frames:
                os.remove(path)
        references(name, {"file": name + ".webp", "source": source, "tool": tool, "options": args + per_frame})

    # Metadata: chunks added to finished files by webpmux.
    icc = "tests/images/icc/compact/DisplayP3-v4.icc"
    with Image.open("tests/images/jpeg/exif-tags.jpg") as im:
        exif = im.info["exif"]
    if not exif.startswith(b"Exif\x00\x00"):
        raise RuntimeError("Pillow's EXIF is expected to start with the Exif prefix.")
    exif_files = {"plain": exif[6:], "prefixed": exif}
    for kind, data in exif_files.items():
        with open(os.path.join(WORK, f"exif-{kind}.bin"), "wb") as f:
            f.write(data)
    with open(os.path.join(WORK, "meta.xmp"), "wb") as f:
        f.write(XMP)
    METADATA = [
        ("meta-icc-lossless", "lossless-z6", [("icc", icc)]),
        ("meta-icc-lossy", "lossy-q75", [("icc", icc)]),
        ("meta-exif", "lossless-z6", [("exif", os.path.join(WORK, "exif-plain.bin"))]),
        ("meta-exif-prefixed", "lossy-q75", [("exif", os.path.join(WORK, "exif-prefixed.bin"))]),
        ("meta-xmp", "lossy-alpha-m1-f1", [("xmp", os.path.join(WORK, "meta.xmp"))]),
        ("meta-all", "lossless-alpha-exact", [("icc", icc), ("exif", os.path.join(WORK, "exif-plain.bin")),
                                              ("xmp", os.path.join(WORK, "meta.xmp"))]),
        ("meta-anim-xmp", "anim-lossless", [("xmp", os.path.join(WORK, "meta.xmp"))]),
    ]
    for name, base, chunks in METADATA:
        current = os.path.join(CORPUS, base + ".webp")
        for i, (kind, path) in enumerate(chunks):
            out = os.path.join(WORK, f"meta-{i}.webp")
            run("webpmux", "-set", kind, path, current, "-o", out)
            current = out
        shutil.copy(current, os.path.join(CORPUS, name + ".webp"))
        references(name, {"file": name + ".webp", "source": base + ".webp", "tool": "webpmux",
                          "options": [f"-set {kind} {os.path.basename(path)}" for kind, path in chunks]})

    package = run("apk", "list", "--installed", "libwebp").split()[0]
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": f"libwebp {webpref('version')['version']}; {package}; {IMAGE}", "files": entries}, f, indent=1)
        f.write("\n")
    shutil.rmtree(WORK)
    print(f"Wrote {len(entries)} WebP files and their references.")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-webp-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
