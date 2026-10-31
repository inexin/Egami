"""Builds the xz corpus for the managed LZMA decoder (src/Egami/Compression/Lzma) with the xz CLI in Docker.

Inputs are the ones tools/make-zstd-references.py makes (generated text, photo samples of tests/images/ops, noise, zeros, a
mixture, empty, one byte), so both codecs are tested and timed on the same bytes.

Committed corpus (tests/images/xz; references in tests/reference/xz):
  <name>.xz               made by xz (XZ Utils 5.8) from one input with the arguments in manifest.json: presets 0 to 9 and
                          -6e and -9e, the delta filter before LZMA2 (as libtiff writes), every check type (none, CRC32, CRC64,
                          SHA-256), several blocks (--block-size), and from standard input (no sizes in the block headers);
                          "multi" is two streams with stream padding between them
  <input>.bin.gz          the bytes each file must decode to
  manifest.json           per file: its input or inputs, the arguments, and the decoded length and SHA-256

Local corpus (never committed): `python tools/make-xz-references.py --local` writes 4 MB of text, 3 MB of photo samples and
8 MB of mixed data at presets 0 to 9 and -9e, with the delta filter and the TIFF-style "no check" stream, to .cache/xz, with a
manifest.json like the committed one. Native speeds are timed live, next to Egami's, by benchmarks/Egami.Benchmarks
(-- --time-xz .cache/xz --live).

Usage, from the repo root with Docker running:
  python tools/make-xz-references.py [--local]
The script runs alpine:3.20 and installs xz (5.8), Python and Pillow inside it with apk.
"""
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow xz"
CORPUS = "tests/images/xz"
TARGET = "tests/reference/xz"
LOCAL = ".cache/xz"
WORK = "/tmp/xzwork"


def zstd_script():
    spec = importlib.util.spec_from_file_location("zstdrefs", os.path.join(os.path.dirname(os.path.abspath(__file__)), "make-zstd-references.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(*args, **kwargs):
    result = subprocess.run(list(map(str, args)), capture_output=True, **kwargs)
    if result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, args, result.stdout, result.stderr)
    return result.stdout


def xz(data, arguments):
    """Compresses with the xz CLI, from a file (so block headers record the sizes) or from standard input."""
    if "stdin" in arguments:
        return run("xz", "-q", "-c", *[a for a in arguments if a != "stdin"], input=data)
    path = f"{WORK}/input"
    with open(path, "wb") as f:
        f.write(data)
    return run("xz", "-q", "-c", "-k", *arguments, path)


def check(data, decoded):
    path = f"{WORK}/check.xz"
    with open(path, "wb") as f:
        f.write(data)
    if run("xz", "-q", "-d", "-c", path) != decoded:
        raise RuntimeError("xz does not decode a corpus file to its input")


def entry(sources, arguments, decoded):
    return {"inputs": sources, "arguments": arguments, "length": len(decoded), "sha256": hashlib.sha256(decoded).hexdigest()}


# Committed files: name, input, xz arguments ("stdin" pipes the input, so the block headers hold no sizes).
TIFF = ["--format=xz", "--check=none"]
COMMITTED = [
    *[(f"text-{p}", "text", [f"-{p}"]) for p in (0, 1, 3, 6, 9)],
    ("text-9e", "text", ["-9e"]), ("text-crc32", "text", ["-6", "--check=crc32"]), ("text-sha256", "text", ["-6", "--check=sha256"]),
    ("text-none", "text", ["-6", "--check=none"]), ("text-stdin", "text", ["-6", "stdin"]), ("text-blocks", "text", ["-6", "--block-size=8KiB"]),
    ("egret-1", "egret", ["-1"]), ("egret-6", "egret", ["-6"]), ("egret-delta3", "egret", [*TIFF, "--delta=dist=3", "--lzma2=preset=6"]),
    ("duck-delta4", "duck", [*TIFF, "--delta=dist=4", "--lzma2=preset=6"]), ("duck-6e", "duck", ["-6e"]),
    ("random-6", "random", ["-6"]), ("zeros-6", "zeros", ["-6"]), ("zeros-0", "zeros", ["-0"]),
    ("mixed-0", "mixed", ["-0"]), ("mixed-6", "mixed", ["-6"]), ("mixed-9", "mixed", ["-9"]), ("mixed-lc0lp2pb0", "mixed", ["--lzma2=preset=6,lc=0,lp=2,pb=0"]),
    ("mixed-lc4", "mixed", ["--lzma2=preset=6,lc=4"]), ("mixed-delta1", "mixed", [*TIFF, "--delta=dist=1", "--lzma2=preset=6"]),
    ("empty-6", "empty", ["-6"]), ("one-6", "one", ["-6"]),
]


def committed():
    refs = zstd_script()
    data = refs.inputs()
    for folder in (CORPUS, TARGET):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    manifest = {}
    for name, source, arguments in COMMITTED:
        packed = xz(data[source], arguments)
        check(packed, data[source])
        with open(f"{CORPUS}/{name}.xz", "wb") as f:
            f.write(packed)
        manifest[name] = entry([source], arguments, data[source])

    multi = xz(data["text"], ["-1"]) + bytes(8) + xz(data["random"], ["-6", "--check=crc32"])
    check(multi, data["text"] + data["random"])
    with open(f"{CORPUS}/multi.xz", "wb") as f:
        f.write(multi)
    manifest["multi"] = entry(["text", "random"], ["-1", "+ 8 bytes of stream padding +", "-6", "--check=crc32"], data["text"] + data["random"])

    for source in sorted({s for e in manifest.values() for s in e["inputs"]}):
        refs.write_gz(f"{TARGET}/{source}.bin.gz", data[source])
    with open(f"{TARGET}/manifest.json", "w", newline="\n") as f:
        json.dump({"xz": run("xz", "--version").decode().strip().splitlines()[0], "files": manifest}, f, indent=1, sort_keys=True)
        f.write("\n")


def local():
    refs = zstd_script()
    data = refs.local_inputs()
    shutil.rmtree(LOCAL, ignore_errors=True)
    os.makedirs(f"{LOCAL}/inputs")
    for source, content in data.items():
        with open(f"{LOCAL}/inputs/{source}.bin", "wb") as f:
            f.write(content)
    jobs = []
    for source in data:
        for preset in range(10):
            jobs.append((f"{source}-{preset}", source, [f"-{preset}"]))
        jobs.append((f"{source}-9e", source, ["-9e"]))
        jobs.append((f"{source}-tiff", source, [*TIFF, "--delta=dist=1", "--lzma2=preset=6"]))
    manifest = {}
    for name, source, arguments in jobs:
        packed = xz(data[source], arguments)
        check(packed, data[source])
        with open(f"{LOCAL}/{name}.xz", "wb") as f:
            f.write(packed)
        manifest[name] = entry([source], arguments, data[source])
        print(f"{name}: {len(packed)} bytes", flush=True)
    with open(f"{LOCAL}/manifest.json", "w", newline="\n") as f:
        json.dump({"xz": run("xz", "--version").decode().strip().splitlines()[0], "files": manifest}, f, indent=1, sort_keys=True)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        os.makedirs(WORK, exist_ok=True)
        local() if "--local" in sys.argv else committed()
        return
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    extra = " --local" if "--local" in sys.argv else ""
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-xz-references.py --inside{extra}"],
                   check=True)


if __name__ == "__main__":
    main()
