"""Builds the Zstandard corpus for the managed decoder (src/Egami/Compression/Zstd) with the zstd CLI in Docker.

Inputs, all made here: "text" (48 KB of words picked by a fixed generator), "egret" and "duck" (raw RGB and RGBA samples of 64 by 64 crops of
tests/images/ops/egret.png and duck.png, decoded with Pillow), "random" (8 KB from the generator, which does not compress),
"zeros" (300,000 zero bytes), "mixed" (160 KB of all of these in pieces, with repeats at long distances, so it spans two blocks),
"empty" and "one" (a single byte).

Committed corpus (tests/images/zstd; references in tests/reference/zstd):
  <name>.zst              made by zstd 1.5.6 from one input with the arguments in manifest.json: levels 1 to 22, --fast, --ultra,
                          --long, --no-check, --no-compress-literals, and from standard input (no content size, a window
                          descriptor); "multi" is two frames, and "skippable" puts skippable frames (written here byte by byte,
                          since the CLI does not make them) around two frames
  <input>.bin.gz          the bytes each file must decode to
  manifest.json           per file: its input or inputs, the arguments, and the decoded length and SHA-256

Local corpus (never committed): `python tools/make-zstd-references.py --local` writes larger files to .cache/zstd: 4 MB of text
and 3 MB of photo samples at every level from 1 to 22 (with and without the checksum), 8 MB of mixed data with --long=27 and with
-T4, its inputs, a manifest.json like the committed one, and native.json: zstd -b -d's decode speed for every file with a content size, the native
side of the throughput comparison (benchmarks/Egami.Benchmarks -- --time-zstd .cache/zstd).

Usage, from the repo root with Docker running:
  python tools/make-zstd-references.py [--local]
The script runs alpine:3.20 and installs zstd (1.5.6), Python and Pillow inside it with apk.
"""
import gzip
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow zstd"
CORPUS = "tests/images/zstd"
TARGET = "tests/reference/zstd"
LOCAL = ".cache/zstd"
WORK = "/tmp/zstdwork"

WORDS = ("the of and to in is that it for as with was on by be at this from or an are which but not have one had all were when "
         "there can image pixel strip tile sample frame block literal sequence offset match length window table stream byte bit "
         "decoder encoder compression Huffman entropy state symbol weight checksum header magic level ratio speed memory").split()


class Generator:
    """A 64-bit linear congruential generator (Knuth's MMIX constants), so the inputs are the same on every run."""

    def __init__(self, seed):
        self.state = seed

    def next(self):
        self.state = (self.state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        return self.state >> 33

    def bytes(self, count):
        return bytes(self.next() & 255 for _ in range(count))

    def text(self, count):
        out = []
        size = 0
        while size < count:
            word = WORDS[self.next() % len(WORDS)]
            pick = self.next() % 16
            word += ". " if pick == 0 else ",\n" if pick == 1 else " "
            out.append(word)
            size += len(word)
        return "".join(out).encode()[:count]


def run(*args, **kwargs):
    result = subprocess.run(list(map(str, args)), capture_output=True, **kwargs)
    if result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, args, result.stdout, result.stderr)
    return result.stdout


def write_gz(path, data):
    # No name and a zero time in the header, so regenerating gives identical files.
    with open(path, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
        f.write(data)


def samples(name, mode, box):
    from PIL import Image
    with Image.open(f"tests/images/ops/{name}.png") as image:
        return image.convert(mode).crop(box).tobytes()


def mixed(count, seed, text, photo):
    """Pieces of text, photo samples, noise and runs, and copies of earlier pieces from far back."""
    generator = Generator(seed)
    out = bytearray()
    while len(out) < count:
        kind = generator.next() % 6
        size = 512 + generator.next() % 6000
        if kind == 0:
            at = generator.next() % (len(text) - size)
            out += text[at:at + size]
        elif kind == 1:
            at = generator.next() % (len(photo) - size)
            out += photo[at:at + size // 4]
        elif kind == 2:
            out += generator.bytes(size // 16)
        elif kind == 3:
            out += bytes([generator.next() & 255]) * size
        elif len(out) > size:
            at = generator.next() % (len(out) - size)
            out += out[at:at + size]
    return bytes(out[:count])


def inputs():
    generator = Generator(8878)
    text = generator.text(48 * 1024)
    egret = samples("egret", "RGB", (140, 240, 204, 304))
    return {
        "text": text,
        "egret": egret,
        "duck": samples("duck", "RGBA", (320, 220, 384, 284)),
        "random": generator.bytes(8 * 1024),
        "zeros": bytes(300000),
        "mixed": mixed(160 * 1024, 1, text, egret),
        "empty": b"",
        "one": b"a",
    }


def local_inputs():
    generator = Generator(1950)
    text = generator.text(4 * 1024 * 1024)
    photo = b"".join(samples(name, "RGB", (0, 0, 640, 480) if name == "duck" else (0, 0, 480, 640)) for name in ("duck", "egret"))
    photo += b"".join(samples(name, "RGBA", (0, 0, 480, 480)) for name in ("duck", "egret"))
    return {"text-big": text, "photo-big": photo, "mixed-big": mixed(8 * 1024 * 1024, 2, text, photo)}


# Committed files: name, input, zstd arguments ("stdin" pipes the input, so the frame has no content size).
COMMITTED = [
    ("text-1", "text", ["-1"]), ("text-3", "text", ["-3"]), ("text-9", "text", ["-9"]), ("text-19", "text", ["-19"]),
    ("text-22", "text", ["--ultra", "-22"]), ("text-fast5", "text", ["--fast=5"]), ("text-nocheck", "text", ["-3", "--no-check"]),
    ("text-stdin", "text", ["-3", "stdin"]), ("text-rawliterals", "text", ["-3", "--no-compress-literals"]),
    ("egret-1", "egret", ["-1"]), ("egret-19", "egret", ["-19"]), ("egret-22", "egret", ["--ultra", "-22"]),
    ("duck-3", "duck", ["-3"]), ("duck-12", "duck", ["-12"]),
    ("random-3", "random", ["-3"]), ("zeros-3", "zeros", ["-3"]), ("zeros-19", "zeros", ["-19"]),
    ("mixed-1", "mixed", ["-1"]), ("mixed-3", "mixed", ["-3"]), ("mixed-7", "mixed", ["-7"]),
    ("mixed-16", "mixed", ["-16"]), ("mixed-22", "mixed", ["--ultra", "-22"]), ("mixed-long", "mixed", ["-19", "--long=24"]),
    ("mixed-stdin", "mixed", ["-5", "stdin"]),
    ("empty-3", "empty", ["-3"]), ("empty-stdin", "empty", ["-3", "stdin"]), ("one-3", "one", ["-3"]),
]


def zstd(data, arguments):
    """Compresses with the zstd CLI, from a file (so the frame records the size) or from standard input."""
    if "stdin" in arguments:
        return run("zstd", "-q", "-c", *[a for a in arguments if a != "stdin"], input=data)
    path = f"{WORK}/input"
    with open(path, "wb") as f:
        f.write(data)
    return run("zstd", "-q", "-c", *arguments, path)


def skippable(number, payload):
    return struct.pack("<II", 0x184D2A50 + number, len(payload)) + payload


def entry(sources, arguments, decoded):
    return {"inputs": sources, "arguments": arguments, "length": len(decoded), "sha256": hashlib.sha256(decoded).hexdigest()}


def check(data, decoded):
    # zstd itself must decode every file to its input.
    path = f"{WORK}/check.zst"
    with open(path, "wb") as f:
        f.write(data)
    if run("zstd", "-q", "-d", "-c", path) != decoded:
        raise RuntimeError("zstd does not decode a corpus file to its input")


def committed():
    data = inputs()
    for folder in (CORPUS, TARGET):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    manifest = {}
    for name, source, arguments in COMMITTED:
        packed = zstd(data[source], arguments)
        check(packed, data[source])
        with open(f"{CORPUS}/{name}.zst", "wb") as f:
            f.write(packed)
        manifest[name] = entry([source], arguments, data[source])

    multi = zstd(data["text"], ["-1"]) + zstd(data["random"], ["-3"])
    check(multi, data["text"] + data["random"])
    with open(f"{CORPUS}/multi.zst", "wb") as f:
        f.write(multi)
    manifest["multi"] = entry(["text", "random"], ["-1", "+", "-3"], data["text"] + data["random"])

    skip = skippable(0, b"Egami") + zstd(data["one"], ["-3"]) + skippable(15, bytes(40)) + zstd(data["duck"], ["-3"]) + skippable(7, b"")
    check(skip, data["one"] + data["duck"])
    with open(f"{CORPUS}/skippable.zst", "wb") as f:
        f.write(skip)
    manifest["skippable"] = entry(["one", "duck"], ["skippable", "-3", "skippable", "-3", "skippable"], data["one"] + data["duck"])

    for source in sorted({s for e in manifest.values() for s in e["inputs"]}):
        write_gz(f"{TARGET}/{source}.bin.gz", data[source])
    manifest = {"zstd": run("zstd", "--version").decode().strip(), "files": manifest}
    with open(f"{TARGET}/manifest.json", "w", newline="\n") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")


def local():
    data = local_inputs()
    shutil.rmtree(LOCAL, ignore_errors=True)
    os.makedirs(f"{LOCAL}/inputs")
    for source, content in data.items():
        with open(f"{LOCAL}/inputs/{source}.bin", "wb") as f:
            f.write(content)
    jobs = []
    for source in ("text-big", "photo-big"):
        for level in range(1, 23):
            jobs.append((f"{source}-{level}", source, (["--ultra"] if level > 19 else []) + [f"-{level}"]))
        jobs.append((f"{source}-3-nocheck", source, ["-3", "--no-check"]))
        jobs.append((f"{source}-19-nocheck", source, ["-19", "--no-check"]))
    jobs += [("mixed-big-3", "mixed-big", ["-3"]), ("mixed-big-long", "mixed-big", ["-19", "--long=27"]),
             ("mixed-big-t4", "mixed-big", ["-5", "-T4"]), ("mixed-big-stdin", "mixed-big", ["-9", "stdin"])]
    manifest = {}
    native = {}
    for name, source, arguments in jobs:
        packed = zstd(data[source], arguments)
        check(packed, data[source])
        path = f"{LOCAL}/{name}.zst"
        with open(path, "wb") as f:
            f.write(packed)
        manifest[name] = entry([source], arguments, data[source])
        if "stdin" in arguments:
            # zstd -b -d needs the content size in the frame header.
            continue
        bench = subprocess.run(["zstd", "-b", "-d", "-i3", path], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=True).stdout.decode()
        native[name] = float(re.findall(r"([\d.]+) MB/s", bench)[-1])
        print(f"{name}: {len(packed)} bytes, zstd decodes {native[name]} MB/s", flush=True)
    version = run("zstd", "--version").decode().strip()
    with open(f"{LOCAL}/manifest.json", "w", newline="\n") as f:
        json.dump({"zstd": version, "files": manifest}, f, indent=1, sort_keys=True)
    with open(f"{LOCAL}/native.json", "w", newline="\n") as f:
        json.dump({"zstd": version, "unit": "MB/s of decoded data, zstd -b -d -i3, one thread", "speeds": native}, f, indent=1, sort_keys=True)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        os.makedirs(WORK, exist_ok=True)
        local() if "--local" in sys.argv else committed()
        return
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    extra = " --local" if "--local" in sys.argv else ""
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-zstd-references.py --inside{extra}"],
                   check=True)


if __name__ == "__main__":
    main()
