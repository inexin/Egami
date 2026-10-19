"""Makes the barcode references in tests/reference/barcodes, in Docker.

The image egami-barcoderef:2.11 (tools/barcoderef/Dockerfile: zint 2.16.0's backend library, BSD-3, driven by our own
zintdump.c; zxing-cpp 3.1.1, Apache-2.0) is built if needed. Two steps, both by default:

  grids     zint encodes every case in tests/reference/barcodes/cases.json; its module grids go to zint-grids.json, which
            Egami.Barcodes.Tests compares Egami's grids with.
  readback  Egami.Barcodes.Tests renders every case to a PNG in .cache/barcodes (ReferenceTests.WriteImages, run with
            EGAMI_BARCODE_IMAGES set), and zxing-cpp reads each back; what it read goes to zxing-readback.json with a hash of
            Egami's grid, so the tests notice when a grid changes without a new read-back. Fails if any image does not read
            back as the case's text (or its 'read' text).

Usage, from the repo root with Docker running:
  python tools/make-barcode-references.py [grids|readback]
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys

IMAGE = "egami-barcoderef:2.11"
REFERENCES = "tests/reference/barcodes"
IMAGES = ".cache/barcodes"
ENV = {**os.environ, "MSYS_NO_PATHCONV": "1"}


def load_cases():
    with open(f"{REFERENCES}/cases.json", encoding="utf-8") as f:
        return json.load(f)["cases"]


def write_json(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1, ensure_ascii=True, sort_keys=True)
        f.write("\n")


def grids(cases):
    lines = []
    for case in cases:
        z = case["zint"]
        data = z["input"].encode("utf-8" if z.get("mode", 0) in (1, 2) else "latin-1").hex()
        lines.append(f"{case['name']}\t{z['symbology']}\t0\t{z.get('option2', 0)}\t0\t{z.get('mode', 0)}\t{data}\n")
    out = subprocess.run(["docker", "run", "--rm", "-i", IMAGE, "zintdump"], input="".join(lines), capture_output=True,
                         text=True, check=True, env=ENV).stdout
    result = {}
    for line in out.splitlines():
        entry = json.loads(line)
        if entry["error"] >= 5:
            sys.exit(f"zint failed on {entry['name']}: {entry['message']}")
        result[entry["name"]] = {"width": entry["width"], "text": entry["text"], "modules": entry["modules"]}
    write_json(f"{REFERENCES}/zint-grids.json", result)
    print(f"{len(result)} zint grids written")


def readback(cases):
    root = os.getcwd()
    shutil.rmtree(IMAGES, ignore_errors=True)
    os.makedirs(IMAGES)
    subprocess.run(["dotnet", "test", "tests/Egami.Barcodes.Tests", "--filter", "FullyQualifiedName~ReferenceTests.WriteImages"],
                   check=True, env={**ENV, "EGAMI_BARCODE_IMAGES": os.path.abspath(IMAGES)})
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python3", "/w/tools/barcoderef/readback.py",
                          f"/w/{IMAGES}"], capture_output=True, text=True, check=True, env=ENV).stdout
    reads = {entry["name"]: entry for entry in map(json.loads, out.splitlines())}
    result, failures = {}, []
    for case in cases:
        if not os.path.exists(f"{IMAGES}/{case['name']}.grid"):
            continue
        with open(f"{IMAGES}/{case['name']}.grid", encoding="ascii") as f:
            grid = hashlib.sha256(f.read().encode("ascii")).hexdigest()[:16]
        read = reads.get(case["name"], {"format": None, "text": None})
        result[case["name"]] = {"format": read["format"], "text": read["text"], "grid": grid}
        expected = case.get("read", case["text"])
        if read["text"] != expected:
            failures.append(f"{case['name']}: read {read['text']!r} as {read['format']}, expected {expected!r}")
    write_json(f"{REFERENCES}/zxing-readback.json", result)
    print(f"{len(result) - len(failures)} of {len(result)} read back")
    if failures:
        sys.exit("\n".join(failures))


def main():
    steps = sys.argv[1:] or ["grids", "readback"]
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/barcoderef"], check=True, env=ENV)
    cases = load_cases()
    if "grids" in steps:
        grids(cases)
    if "readback" in steps:
        readback(cases)


if __name__ == "__main__":
    main()
