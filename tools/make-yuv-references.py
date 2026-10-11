"""Builds the planar YUV references in tests/reference/yuv from the JPEG corpus (tests/images/jpeg), with libjpeg-turbo's TurboJPEG
API in Docker.

Corpus: every JPEG in tests/images/jpeg (made by tools/make-jpeg-references.py). Files TurboJPEG cannot decode to planes are
listed under "skipped" in the manifest with the reason: CMYK, YCCK and RGB files, arithmetic-coded and 12-bit files.

References (tests/reference/yuv), all from tools/yuvref/yuvref.c, a small TurboJPEG 3 program compiled inside the container
(accurate integer IDCT, the default):
  <file>.yuv.gz                 tj3DecompressToYUVPlanes8 with no padding (each stride is the plane width): the Y plane, then
                                U, then V (gray: Y only), each row by row, 8-bit, packed, gzipped. 4:4:0 and 4:1:1 files keep
                                their native planes.
  <file>.s2/.s4/.s8.yuv.gz      the same at scaling factors 1/2, 1/4 and 1/8, for the files with scaled references in
                                tests/reference/jpeg/manifest.json
  encode/<source>-q<n>.jpg      tj3CompressFromYUVPlanes8 from the decoded planes of <source>.jpg at quality n, with the
                                source's subsampling: baseline, spec Huffman tables (no optimization), no restart markers
  manifest.json                 per file: width and height (of the scaled output), TurboJPEG's subsampling name (444, 422,
                                420, gray, 440, 411, 441) and each plane's width and height (tj3YUVPlaneWidth/Height);
                                "encodes" with the exact TurboJPEG parameters; "markersExample", the segments of one encode;
                                "skipped"; and the tool version. Files with identical planes share one reference ("reference").

TurboJPEG's plane sizes pad to whole chroma samples: a 4:2:0 plane set for 17 × 9 has a Y plane of 18 × 10 and chroma of 9 × 5.

Usage, from the repo root with Docker running:
  python tools/make-yuv-references.py
The script runs alpine:3.20, installs libjpeg-turbo-dev, gcc and musl-dev inside it with apk, and compiles tools/yuvref/yuvref.c.
"""
import gzip
import hashlib
import json
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 gcc musl-dev libjpeg-turbo-dev"
CORPUS = "tests/images/jpeg"
JPEG_MANIFEST = "tests/reference/jpeg/manifest.json"
TARGET = "tests/reference/yuv"
TOOL = "/tmp/yuvref"

# (source in the corpus, qualities) for the encoder references.
ENCODES = [("duck-420", (75, 90)), ("duck-422", (75, 90)), ("duck-444", (75, 90)), ("duck-gray", (75, 90)),
           ("odd-17x9", (75, 90)), ("odd-31x17", (75, 90))]

MARKERS = {0xC0: "SOF0", 0xC1: "SOF1", 0xC2: "SOF2", 0xC4: "DHT", 0xD8: "SOI", 0xD9: "EOI", 0xDA: "SOS", 0xDB: "DQT",
           0xDD: "DRI", 0xFE: "COM"}


def yuvref(*args):
    result = subprocess.run([TOOL, *map(str, args)], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip())
    return json.loads(result.stdout)


def write_gz(path, data):
    # No name and a zero time in the header, so regenerating gives identical files.
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def markers(path):
    """The segments of a JPEG up to EOI, in order: marker name, length field (none for SOI and EOI), APPn identifier."""
    with open(path, "rb") as f:
        data = f.read()
    result = []
    i = 0
    while i < len(data):
        assert data[i] == 0xFF, f"no marker at {i}"
        code = data[i + 1]
        name = MARKERS.get(code) or (f"APP{code - 0xE0}" if 0xE0 <= code <= 0xEF else f"0x{code:02X}")
        if code in (0xD8, 0xD9):
            result.append({"marker": name})
            i += 2
            if code == 0xD9:
                break
            continue
        length = int.from_bytes(data[i + 2:i + 4], "big")
        segment = {"marker": name, "length": length}
        if 0xE0 <= code <= 0xEF:
            segment["identifier"] = data[i + 4:i + 2 + length].split(b"\0", 1)[0].decode("latin-1")
        i += 2 + length
        if code == 0xDA:
            # Skip the entropy-coded data to the next marker that is not a stuffed zero or a restart marker.
            start = i
            while not (data[i] == 0xFF and data[i + 1] != 0 and not 0xD0 <= data[i + 1] <= 0xD7):
                i += 1
            segment["entropyBytes"] = i - start
        result.append(segment)
    return result


def inside():
    subprocess.run(["gcc", "-O2", "-Wall", "-o", TOOL, "tools/yuvref/yuvref.c", "-lturbojpeg"], check=True)
    os.makedirs(os.path.join(TARGET, "encode"), exist_ok=True)
    for old in os.listdir(TARGET):
        if old.endswith(".yuv.gz"):
            os.remove(os.path.join(TARGET, old))
    with open(JPEG_MANIFEST) as f:
        scaled = {e["file"] for e in json.load(f)["files"] if "scaled" in e}

    entries, skipped = [], []
    first = {}  # sha256 of the planes -> the reference that holds them

    def write(name, data):
        digest = hashlib.sha256(data).hexdigest()
        if digest in first:
            return first[digest]
        write_gz(os.path.join(TARGET, name), data)
        first[digest] = name
        return name

    # Baseline files first, so a progressive file with the same planes points at the baseline reference.
    for file in sorted((f for f in os.listdir(CORPUS) if f.lower().endswith(".jpg")), key=lambda f: ("progressive" in f, f)):
        path = os.path.join(CORPUS, file)
        try:
            info = yuvref("info", path)
        except RuntimeError as e:
            skipped.append({"file": file, "reason": str(e)})
            continue
        reason = None
        if info["colorspace"] not in ("ycbcr", "gray"):
            reason = f"Color space {info['colorspace']}"
        elif info["arithmetic"]:
            reason = "Arithmetic coding"
        elif info["precision"] != 8:
            reason = f"{info['precision']}-bit"
        elif info["subsampling"] == "unknown":
            reason = "Subsampling TurboJPEG does not name"
        if reason:
            skipped.append({"file": file, "reason": reason})
            continue
        base = os.path.splitext(file)[0]
        for denominator in (1, 2, 4, 8) if file in scaled else (1,):
            suffix = "" if denominator == 1 else f".s{denominator}"
            raw = f"/tmp/{base}{suffix}.yuv"
            result = yuvref("decode", path, denominator, raw)
            with open(raw, "rb") as f:
                data = f.read()
            os.remove(raw)
            entry = {"file": file, "scale": f"1/{denominator}", "width": result["width"], "height": result["height"],
                     "subsampling": result["subsampling"], "planes": result["planes"], "progressive": info["progressive"],
                     "reference": write(f"{base}{suffix}.yuv.gz", data)}
            entries.append(entry)

    encodes = []
    for source, qualities in ENCODES:
        entry = next(e for e in entries if e["file"] == f"{source}.jpg" and e["scale"] == "1/1")
        raw = "/tmp/source.yuv"
        with gzip.open(os.path.join(TARGET, entry["reference"]), "rb") as f, open(raw, "wb") as out:
            out.write(f.read())
        for quality in qualities:
            name = f"encode/{source}-q{quality}.jpg"
            result = yuvref("encode", raw, entry["width"], entry["height"], entry["subsampling"], quality,
                            os.path.join(TARGET, name))
            encodes.append({"file": name, "source": entry["reference"], "width": entry["width"], "height": entry["height"],
                            "subsampling": entry["subsampling"], "quality": quality, "size": result["size"],
                            "function": "tj3CompressFromYUVPlanes8", "params": result["params"]})
        os.remove(raw)

    example = "encode/duck-420-q75.jpg"
    version = yuvref("version")["version"]
    package = subprocess.run(["apk", "list", "--installed", "libjpeg-turbo"], capture_output=True, text=True).stdout.split()[0]
    with open(os.path.join(TARGET, "manifest.json"), "w", newline="\n") as f:
        json.dump({"tools": f"{version}; {package}; {IMAGE}", "files": entries, "skipped": skipped, "encodes": encodes,
                   "markersExample": {"file": example, "segments": markers(os.path.join(TARGET, example))}}, f, indent=1)
        f.write("\n")
    print(f"Wrote {len(entries)} plane references and {len(encodes)} encodes; skipped {len(skipped)}.")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-yuv-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
