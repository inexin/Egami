"""Makes Egami.Formats.Extended's camera table: the colour matrices of each camera whose RAW files Egami reads.

RAW Build Plan task 6 (decision Q6). For one CC0 sample file per camera from raw.pixls.us, Adobe DNG Converter (free, run as
a black box) writes a DNG, and the script reads the tags Adobe put in it: ColorMatrix1 and 2, ForwardMatrix1 and 2, their
calibration illuminants and BaselineExposure from IFD0, and from the raw IFD the sensor size, ActiveArea, default crop, black
levels and white level (maker formats keep those in maker notes, which have no public specification). Those are measurements of the camera, the same data every RAW converter's
table comes from; no other converter's table is read. The result is src/Egami.Formats.Extended/Raw/RawCameras.txt, embedded
in the package: one line per camera, tab-separated:

  Make  Model  UniqueCameraModel  Illuminant1  ColorMatrix1 (9)  Illuminant2  ColorMatrix2 (9 or -)  ForwardMatrix1 (9 or -)
  ForwardMatrix2 (9 or -)  BaselineExposure  Width Height  ActiveArea (top left bottom right, or -)
  DefaultCrop (x y width height, or -)  BlackRepeat (rows columns)  BlackLevels  WhiteLevel  ExposureBias (or -)
  ReducedWhite (or -)

ExposureBias is, for RAF samples only, the RawExposureBias the sample's RAF header records (meta tag 0x9650, as ExifTool's
FujiFilm tag page names it, read as a fact): Adobe's BaselineExposure follows the camera's dynamic-range setting, so Egami
moves the table's value by the difference between a file's bias and the measured sample's. Only the RAF's bytes up to its meta
directory are fetched for it.

ReducedWhite is, for Canon bodies with a CC0 sRAW or mRAW sample, the white level of Adobe's LinearRaw DNG of that sample:
Egami's sRAW decode gives the same values Adobe stores, whose saturation is not the sensor's white level. Full-resolution
samples are always the ones measured for the rest of the line.

Make and Model are the IFD0 strings of the DNG, which Adobe copies from the original file, trimmed. Matrices are row-major,
rounded to 4 decimals (Adobe stores them in units of 1/10000).

Usage, from the repo root on a PC with Adobe DNG Converter installed:
  python tools/make-raw-camera-table.py                 every CC0 camera of the maker formats Egami reads
  python tools/make-raw-camera-table.py --formats cr2,nef --limit 5
  python tools/make-raw-camera-table.py --keep          keep the downloads and DNGs in .cache/raw-cameras

The downloads (one file at a time, SHA-256 checked) and the DNGs go to .cache/raw-cameras and are deleted once read unless
--keep is given. Progress is kept in .cache/raw-cameras/progress.json, so a run that stops carries on where it left off.
"""
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import sys
import urllib.parse
import urllib.request

REPOSITORY = "https://raw.pixls.us/json/getrepository.php?set=all"
CONVERTER = r"C:\Program Files\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe"
FORMATS = ["cr2", "nef", "nrw", "pef", "arw", "sr2", "orf", "rw2", "raf"]
CACHE = os.path.join(".cache", "raw-cameras")
OUTPUT = os.path.join("src", "Egami.Formats.Extended", "Raw", "RawCameras.txt")

TAG_MAKE, TAG_MODEL, TAG_SUBIFDS = 271, 272, 330
TAG_UNIQUE = 50708
TAG_CM1, TAG_CM2 = 50721, 50722
TAG_ILLUM1, TAG_ILLUM2 = 50778, 50779
TAG_FM1, TAG_FM2 = 50964, 50965
TAG_BASELINE = 50730
TAG_NEWSUBFILE, TAG_WIDTH, TAG_HEIGHT = 254, 256, 257
TAG_BLACK_REPEAT, TAG_BLACK, TAG_WHITE = 50713, 50714, 50717
TAG_CROP_ORIGIN, TAG_CROP_SIZE, TAG_ACTIVE = 50719, 50720, 50829
VERSION = 2


def reduced(row):
    """Gets whether a listing row is a reduced-resolution mode (Canon sRAW and mRAW, Nikon sNEF), whose DNG is LinearRaw."""
    return re.search(r"\b(sRAW|mRAW|sNEF)", row[2]) is not None


def samples(repository, formats, reduced_only=False):
    """Returns the smallest full-resolution (or, with reduced_only, reduced) CC0 file of each camera in the given formats."""
    cameras = {}
    for row in repository["data"]:
        if "zero" not in row[5] or reduced(row) != reduced_only:
            continue
        m = re.search(r"href='([^']+)'>([^<]+)</a>.*?sha256 Checksum'>([0-9a-f]+)</span>&nbsp;\(([\d.]+)MB\)", row[7])
        if not m:
            continue
        url, name, sha, mb = m.groups()
        ext = name.rsplit(".", 1)[-1].lower()
        if ext not in formats:
            continue
        key = (row[0].strip(), row[1].strip())
        if key not in cameras or float(mb) < cameras[key]["mb"]:
            cameras[key] = {"make": key[0], "model": key[1], "url": url, "name": name, "sha": sha, "mb": float(mb), "ext": ext,
                            "id": int(re.search(r"getfile.php/(\d+)/", url).group(1))}
    return sorted(cameras.values(), key=lambda c: (c["make"].lower(), c["model"].lower()))


def download(sample):
    path = os.path.join(CACHE, "src", f"{sample['id']}.{sample['ext']}")
    if os.path.exists(path) and hashlib.sha256(open(path, "rb").read()).hexdigest() == sample["sha"]:
        return path
    base, rest = sample["url"].split("/nice/", 1)
    data = urllib.request.urlopen(base + "/nice/" + urllib.parse.quote(rest), timeout=600).read()
    if hashlib.sha256(data).hexdigest() != sample["sha"]:
        raise ValueError(f"SHA-256 of {sample['name']} does not match the repository's")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    return path


def fetch(sample, start, length):
    """Reads bytes [start, start + length) of a sample, streaming from its start (the server ignores range requests)."""
    base, rest = sample["url"].split("/nice/", 1)
    with urllib.request.urlopen(base + "/nice/" + urllib.parse.quote(rest), timeout=120) as response:
        data = bytearray()
        while len(data) < start + length:
            chunk = response.read(min(1 << 20, start + length - len(data)))
            if not chunk:
                return None
            data += chunk
    return bytes(data[start:start + length])


def raf_bias(sample):
    """Reads a RAF's RawExposureBias (meta tag 0x9650: two signed 16-bit values, a ratio) in EV, or None."""
    header = fetch(sample, 0, 108)
    if not header or not header.startswith(b"FUJIFILM"):
        return None
    offset, length = struct.unpack(">II", header[92:100])
    meta = fetch(sample, offset, min(length, 1 << 20)) if 4 <= length else None
    if not meta:
        return None
    at = 4
    for _ in range(struct.unpack(">I", meta[:4])[0]):
        if at + 4 > len(meta):
            break
        tag, n = struct.unpack(">HH", meta[at:at + 4])
        if tag == 0x9650 and n >= 4:
            num, den = struct.unpack(">hh", meta[at + 4:at + 8])
            return round(num / den, 4) if den else None
        at += 4 + n
    return None


def convert(source):
    """Runs Adobe DNG Converter on one file: lossless, no preview, into .cache/raw-cameras/dng. Returns the DNG's path."""
    out = os.path.abspath(os.path.join(CACHE, "dng"))
    os.makedirs(out, exist_ok=True)
    dng = os.path.join(out, os.path.splitext(os.path.basename(source))[0] + ".dng")
    if os.path.exists(dng):
        os.remove(dng)
    subprocess.run([CONVERTER, "-c", "-p0", "-d", out, os.path.abspath(source)], check=True, timeout=600)
    if not os.path.exists(dng):
        raise FileNotFoundError(f"Adobe DNG Converter wrote no DNG for {source}")
    return dng


class Tiff:
    """Just enough TIFF (TIFF 6.0) to read IFD0's tags: byte order, entries, and values of every type DNG uses."""

    SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8}

    def __init__(self, data):
        self.data = data
        self.e = "<" if data[:2] == b"II" else ">"
        first = struct.unpack(self.e + "I", data[4:8])[0]
        self.ifd0 = self.read_ifd(first)

    def read_ifd(self, at):
        count = struct.unpack(self.e + "H", self.data[at:at + 2])[0]
        entries = {}
        for i in range(count):
            p = at + 2 + i * 12
            tag, typ, n = struct.unpack(self.e + "HHI", self.data[p:p + 8])
            size = self.SIZES.get(typ, 0) * n
            where = p + 8 if size <= 4 else struct.unpack(self.e + "I", self.data[p + 8:p + 12])[0]
            entries[tag] = (typ, n, where)
        return entries

    def raw_ifd(self):
        """Returns the raw IFD: the sub-IFD (or IFD0) whose NewSubFileType is 0."""
        subs = self.values(TAG_SUBIFDS, self.ifd0) or []
        for at in subs:
            ifd = self.read_ifd(at)
            if (self.values(TAG_NEWSUBFILE, ifd) or [0])[0] == 0:
                return ifd
        return self.ifd0

    def values(self, tag, ifd=None):
        ifd = self.ifd0 if ifd is None else ifd
        if tag not in ifd:
            return None
        typ, n, at = ifd[tag]
        out = []
        for i in range(n):
            p = at + i * self.SIZES[typ]
            if typ == 2:
                return self.data[at:at + n].split(b"\0")[0].decode("latin-1").strip()
            if typ in (5, 10):
                a, b = struct.unpack(self.e + ("II" if typ == 5 else "ii"), self.data[p:p + 8])
                out.append(a / b if b else 0.0)
            else:
                fmt = {1: "B", 3: "H", 4: "I", 6: "b", 7: "B", 8: "h", 9: "i", 11: "f", 12: "d"}[typ]
                out.append(struct.unpack(self.e + fmt, self.data[p:p + self.SIZES[typ]])[0])
        return out


def colour(dng):
    """Reads the camera's colour tags from the DNG's IFD0."""
    t = Tiff(open(dng, "rb").read())

    def matrix(tag):
        v = t.values(tag)
        return [round(x, 4) for x in v] if v and len(v) == 9 else None

    baseline = t.values(TAG_BASELINE)
    raw = t.raw_ifd()

    def ints(tag):
        v = t.values(tag, raw)
        return [int(round(x)) for x in v] if v else None

    black = t.values(TAG_BLACK, raw)
    return {
        "version": VERSION,
        "size": [ints(TAG_WIDTH)[0], ints(TAG_HEIGHT)[0]],
        "active": ints(TAG_ACTIVE),
        "crop": (ints(TAG_CROP_ORIGIN) or []) + (ints(TAG_CROP_SIZE) or []) or None,
        "blackRepeat": ints(TAG_BLACK_REPEAT) or [1, 1],
        "black": [round(x, 2) for x in black] if black else [0],
        "white": (ints(TAG_WHITE) or [65535])[0],
        "dngMake": t.values(TAG_MAKE) or "",
        "dngModel": t.values(TAG_MODEL) or "",
        "unique": t.values(TAG_UNIQUE) or "",
        "illuminant1": (t.values(TAG_ILLUM1) or [0])[0],
        "cm1": matrix(TAG_CM1),
        "illuminant2": (t.values(TAG_ILLUM2) or [0])[0],
        "cm2": matrix(TAG_CM2),
        "fm1": matrix(TAG_FM1),
        "fm2": matrix(TAG_FM2),
        "baseline": round(baseline[0], 4) if baseline else 0.0,
    }


def line(entry):
    def m(v):
        return " ".join(f"{x:g}" for x in v) if v else "-"

    fields = [entry["dngMake"], entry["dngModel"], entry["unique"], str(entry["illuminant1"]), m(entry["cm1"]),
              str(entry["illuminant2"]), m(entry["cm2"]), m(entry["fm1"]), m(entry["fm2"]), f"{entry['baseline']:g}",
              m(entry["size"]), m(entry["active"]), m(entry["crop"] if entry["crop"] and len(entry["crop"]) == 4 else None),
              m(entry["blackRepeat"]), m(entry["black"]), str(entry["white"]),
              f"{entry['rafBias']:g}" if entry.get("rafBias") is not None else "-",
              str(entry["reducedWhite"]) if entry.get("reducedWhite") else "-"]
    return "\t".join(f.replace("\t", " ") for f in fields)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--formats", default=",".join(FORMATS), help="comma-separated extensions")
    parser.add_argument("--limit", type=int, default=0, help="at most this many cameras (for a trial run)")
    parser.add_argument("--keep", action="store_true", help="keep downloads and DNGs")
    args = parser.parse_args()
    if not os.path.exists(CONVERTER):
        sys.exit(f"Adobe DNG Converter is not at {CONVERTER}; install it (free) from Adobe.")
    os.makedirs(CACHE, exist_ok=True)
    listing = os.path.join(CACHE, "repository.json")
    if not os.path.exists(listing):
        with open(listing, "wb") as f:
            f.write(urllib.request.urlopen(REPOSITORY, timeout=600).read())
    todo = samples(json.load(open(listing, encoding="utf-8")), args.formats.split(","))
    if args.limit:
        todo = todo[:args.limit]
    progress_path = os.path.join(CACHE, "progress.json")
    progress = json.load(open(progress_path, encoding="utf-8")) if os.path.exists(progress_path) else {}
    for i, sample in enumerate(todo):
        key = str(sample["id"])
        if key in progress and progress[key].get("version") == VERSION:
            continue
        print(f"[{i + 1}/{len(todo)}] {sample['make']} {sample['model']} ({sample['ext']}, {sample['mb']} MB)", flush=True)
        source = dng = None
        try:
            source = download(sample)
            dng = convert(source)
            entry = colour(dng)
            if entry["cm1"] is None:
                raise ValueError("the DNG has no ColorMatrix1")
            progress[key] = {**entry, "make": sample["make"], "model": sample["model"], "ext": sample["ext"], "sha": sample["sha"]}
        except Exception as e:  # noqa: BLE001 - a camera Adobe cannot convert is noted and skipped
            print(f"   skipped: {e}", flush=True)
            progress[key] = {"error": str(e), "make": sample["make"], "model": sample["model"], "ext": sample["ext"], "version": VERSION}
        finally:
            if not args.keep:
                for path in (dng, source):
                    if path and os.path.exists(path):
                        os.remove(path)
        with open(progress_path, "w", encoding="utf-8") as f:
            json.dump(progress, f, indent=1)

    # Canon sRAW and mRAW: the white level Adobe gives the reduced-resolution data, which differs from the sensor's.
    for sample in samples(json.load(open(listing, encoding="utf-8")), [f for f in args.formats.split(",") if f == "cr2"], True):
        key = str(sample["id"])
        if key in progress and progress[key].get("version") == VERSION:
            continue
        print(f"[reduced] {sample['make']} {sample['model']} ({sample['mb']} MB)", flush=True)
        source = dng = None
        try:
            source = download(sample)
            dng = convert(source)
            progress[key] = {**colour(dng), "make": sample["make"], "model": sample["model"], "ext": sample["ext"], "sha": sample["sha"]}
        except Exception as e:  # noqa: BLE001 - a sample Adobe cannot convert is noted and skipped
            print(f"   skipped: {e}", flush=True)
            progress[key] = {"error": str(e), "make": sample["make"], "model": sample["model"], "ext": sample["ext"], "version": VERSION}
        finally:
            if not args.keep:
                for path in (dng, source):
                    if path and os.path.exists(path):
                        os.remove(path)
        with open(progress_path, "w", encoding="utf-8") as f:
            json.dump(progress, f, indent=1)

    # RAF samples: the exposure bias of the sample each line was measured on.
    for sample in todo:
        entry = progress.get(str(sample["id"]))
        if sample["ext"] == "raf" and entry and "error" not in entry and "rafBias" not in entry:
            try:
                entry["rafBias"] = raf_bias(sample)
            except Exception as e:  # noqa: BLE001 - a bias that cannot be read leaves the column empty
                print(f"   no exposure bias for {sample['model']}: {e}", flush=True)
                entry["rafBias"] = None
            with open(progress_path, "w", encoding="utf-8") as f:
                json.dump(progress, f, indent=1)

    # Reduced-resolution samples measured by earlier runs do not describe the sensor; they are left out.
    skip = set()
    for row in json.load(open(listing, encoding="utf-8"))["data"]:
        m = re.search(r"getfile.php/(\d+)/", row[7])
        if m and reduced(row):
            skip.add(m.group(1))

    # One line per Make and Model as the files say them; when two samples name the same camera, the first in id order wins.
    seen = {}
    reduced_white = {}
    for key in sorted(progress, key=int):
        entry = progress[key]
        if key in skip and "error" not in entry and entry.get("version") == VERSION:
            reduced_white.setdefault((entry["dngMake"].lower(), entry["dngModel"].lower()), entry["white"])
    for key in sorted(progress, key=int):
        entry = progress[key]
        if "error" in entry or entry.get("version") != VERSION or key in skip:
            continue
        name = (entry["dngMake"].lower(), entry["dngModel"].lower())
        seen.setdefault(name, {**entry, "reducedWhite": reduced_white.get(name)})
    lines = sorted((line(e) for e in seen.values()), key=str.lower)
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("# Egami camera colour table, made by tools/make-raw-camera-table.py from Adobe DNG Converter output on CC0 samples\n")
        f.write("# from raw.pixls.us. Columns: Make, Model, UniqueCameraModel, Illuminant1, ColorMatrix1, Illuminant2,\n")
        f.write("# ColorMatrix2, ForwardMatrix1, ForwardMatrix2, BaselineExposure, Width Height, ActiveArea, DefaultCrop, BlackRepeat,\n")
        f.write("# BlackLevels, WhiteLevel, ExposureBias, ReducedWhite. Do not edit by hand; run the script.\n")
        for text in lines:
            f.write(text + "\n")
    errors = sum(1 for e in progress.values() if "error" in e)
    print(f"Wrote {len(lines)} cameras to {OUTPUT}; {errors} samples skipped.")


if __name__ == "__main__":
    main()
