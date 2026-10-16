"""The Phase 2.9.3 JPEG recompression gate (task 18): every JPEG cjxl (libjxl 0.12.0) recompresses, Egami recompresses too;
djxl decodes Egami's file to the same pixels as cjxl's and rebuilds the original JPEG from it byte for byte, as Egami's
ReconstructJpeg does; and Egami's files are within 5% of cjxl's and about a fifth smaller than the JPEGs in total.

The sources are the corpus's JPEGs (tests/images/jpeg) and libjxl's test JPEGs and the conformance JPEGs in .cache/jxl, with
a few photos from .cache/avif. They are copied to .cache/interop/jxl-jpeg/src; the JxlRecompressInteropFiles test
(EGAMI_INTEROP_DIR=.cache/interop) writes Egami's files and rebuilt JPEGs to .cache/interop/jxl-jpeg/out; cjxl and djxl run
in the egami-jxlref:2.9.3 image.

Usage, from the repo root with Docker running:
  python tools/check-jxl-recompress.py
"""
import glob
import os
import shutil
import subprocess
import sys

IMAGE = "egami-jxlref:2.9.3"
ROOT = ".cache/interop/jxl-jpeg"
SOURCES = [
    "tests/images/jpeg/**/*.jpg",
    ".cache/jxl/testdata/jxl/flower/*.jpg",
    ".cache/jxl/testdata/jxl/jpeg_reconstruction/*.jpg",
    ".cache/jxl/conformance/testcases/*/reconstructed.jpg",
    ".cache/avif/av1-avif-bf4c18d/testFiles/Link-U/*.jpg",
]
SIZE_GATE = 1.05
TOTAL_GATE = 0.85


def docker(script):
    # .cache may be a link to another checkout's, which Docker does not follow: mount its targets too.
    cache = [arg for d in (".cache/jxl", ".cache/interop") if os.path.isdir(d) for arg in ("-v", f"{os.path.realpath(d)}:/w/{d}")]
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", *cache, "-w", "/w", IMAGE, "sh", "-c", script],
                         capture_output=True, text=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})
    return out.stdout + out.stderr


def main():
    src = f"{ROOT}/src"
    out = f"{ROOT}/out"
    shutil.rmtree(ROOT, ignore_errors=True)
    os.makedirs(src)
    os.makedirs(out)
    names = []
    for pattern in SOURCES:
        for path in sorted(glob.glob(pattern, recursive=True)):
            rel = os.path.relpath(path).replace("\\", "/")
            if rel.endswith("/reconstructed.jpg"):
                name = "conf-" + rel.split("/")[-2]
            else:
                name = os.path.splitext(os.path.basename(rel))[0].replace(".", "-")
            if name in names:
                continue
            names.append(name)
            shutil.copyfile(path, f"{src}/{name}.jpg")
    env = {**os.environ, "EGAMI_INTEROP_DIR": os.path.abspath(".cache/interop")}
    test = subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter",
                           "FullyQualifiedName~JxlRecompressInteropFiles"], capture_output=True, text=True, env=env)
    if test.returncode != 0:
        sys.exit(test.stdout + test.stderr)
    results = {}
    for line in open(f"{out}/results.txt"):
        name, jpeg, jxl, ms, status = line.split()
        results[name] = (int(jpeg), int(jxl), float(ms), status)

    script = []
    for n in names:
        script.append(
            f"echo @{n}; "
            f"if cjxl {src}/{n}.jpg {out}/{n}.cjxl.jxl --num_threads=0 >/dev/null 2>&1; then echo cjxl-ok; stat -c %s {out}/{n}.cjxl.jxl; "
            f"djxl {out}/{n}.cjxl.jxl {out}/{n}.cjxl.png >/dev/null 2>&1; else echo cjxl-refused; fi; "
            f"if [ -f {out}/{n}.jxl ]; then "
            f"djxl {out}/{n}.jxl {out}/{n}.djxl.jpg >/dev/null 2>&1 && cmp -s {out}/{n}.djxl.jpg {src}/{n}.jpg && echo djxl-jpeg-same; "
            f"djxl {out}/{n}.jxl {out}/{n}.djxl.png >/dev/null 2>&1 && cmp -s {out}/{n}.djxl.png {out}/{n}.cjxl.png && echo djxl-pixels-same; "
            f"fi")
    # Too long for a command line: run it from a file in the mounted folder.
    with open(f"{ROOT}/check.sh", "w", newline="\n") as f:
        f.write("\n".join(script) + "\n")
    report = docker(f"sh {ROOT}/check.sh")
    seen = {}
    current = None
    for line in report.splitlines():
        if line.startswith("@"):
            current = line[1:]
            seen[current] = []
        elif current:
            seen[current].append(line.strip())

    failed = False
    total_jpeg = total_egami = total_cjxl = 0
    print(f"{'file':44} {'JPEG':>9} {'cjxl':>9} {'Egami':>9} {'E/c':>6} {'E/J':>6}  checks")
    for n in names:
        jpeg, jxl, ms, status = results.get(n, (0, 0, 0, "missing"))
        lines = seen.get(n, [])
        cjxl_ok = "cjxl-ok" in lines
        cjxl_size = int(lines[lines.index("cjxl-ok") + 1]) if cjxl_ok else 0
        checks = []
        bad = False
        if cjxl_ok and status != "same":
            bad = True
            checks.append(f"Egami {status}")
        if status == "same":
            for want in ("djxl-jpeg-same", "djxl-pixels-same"):
                if want in lines:
                    checks.append(want)
                elif cjxl_ok or want == "djxl-jpeg-same":
                    bad = True
                    checks.append("NOT " + want)
            if cjxl_ok:
                total_jpeg += jpeg
                total_egami += jxl
                total_cjxl += cjxl_size
                if jxl > cjxl_size * SIZE_GATE:
                    bad = True
                    checks.append("too large")
        if not cjxl_ok:
            checks.append("cjxl refuses")
        failed |= bad
        ec = f"{jxl / cjxl_size:.3f}" if cjxl_size and jxl else "-"
        ej = f"{jxl / jpeg:.3f}" if jxl else "-"
        print(f"{n:44} {jpeg:9} {cjxl_size:9} {jxl:9} {ec:>6} {ej:>6}  {'FAIL ' if bad else ''}{', '.join(checks)}")
    if total_jpeg:
        print(f"total: Egami {total_egami / total_jpeg:.3f} of the JPEGs, cjxl {total_cjxl / total_jpeg:.3f}, Egami/cjxl {total_egami / total_cjxl:.4f}")
        if total_egami > total_jpeg * TOTAL_GATE:
            failed = True
            print("FAIL: the files are not about a fifth smaller than the JPEGs")
    print("FAIL" if failed else "PASS")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
