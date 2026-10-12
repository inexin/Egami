"""Runs hb-shape (HarfBuzz 14.5.1) in the egami-shaperef image and writes the shaping references under tests/reference/shaping.

  harfbuzz/<suite>.json.gz  every case of HarfBuzz's in-house and AOTS shaping tests whose font is here: the test's options and
                            input, its expected text, hb-shape's text output (which must equal it) and hb-shape's JSON output
  corpus/<name>.json.gz     every line of a corpus text in a corpus font: the script and direction HarfBuzz guesses, and the
                            glyphs (id, cluster, flags, advances, offsets) with default settings, no language and cluster level 0

Usage (inside the image, from tools/make-shaping-references.py): python /w/tools/shaperef/shape.py /w
"""
import glob
import gzip
import json
import os
import shlex
import subprocess
import sys

ROOT = sys.argv[1]
HB = os.path.join(ROOT, "tests", "fonts", "harfbuzz")
OUT = os.path.join(ROOT, "tests", "reference", "shaping")
VERSION = subprocess.run(["hb-shape", "--version"], capture_output=True, text=True, check=True).stdout.split()[2]


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = json.dumps(data, ensure_ascii=False, indent=None, separators=(",", ":"), sort_keys=False)
    with open(path, "wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as f:
            f.write(text.encode("utf-8"))


def shape(font, args):
    result = subprocess.run(["hb-shape", font, *args], capture_output=True, text=True)
    return result.returncode, result.stdout.rstrip("\n"), result.stderr.strip()


def filter_shapers(args):
    """Only HarfBuzz's own OpenType shaper is a reference: --shapers keeps "ot", and a case for other shapers alone is left out."""
    result = []
    for arg in args:
        if arg.startswith("--shapers="):
            if "ot" not in arg[len("--shapers="):].split(","):
                return None
            arg = "--shapers=ot"
        result.append(arg)
    return result


def harfbuzz_suite(suite):
    cases = []
    for path in sorted(glob.glob(os.path.join(HB, suite, "tests", "*.tests"))):
        name = os.path.basename(path)
        defaults = []
        for number, raw in enumerate(open(path, encoding="utf-8").read().splitlines(), 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("@"):
                # Directives such as @font-funcs=ot,ft or @shaper=ot apply to the lines after them; Egami shapes with its own
                # OpenType tables, which matches the "ot" font functions.
                defaults = [f"--{d}" for d in line[1:].split("#", 1)[0].split() if not d.startswith("font-funcs")]
                continue
            font, options, unicodes, expected = (line.split(";") + ["", "", "", ""])[:4]
            font_path = os.path.normpath(os.path.join(os.path.dirname(path), font))
            if not os.path.exists(font_path):
                continue
            args = []
            for token in shlex.split(options):
                if token.startswith(("#", "--#")):
                    break  # a comment ends the options
                args.append(token)
            args = filter_shapers([a for a in args + defaults if not a.startswith("--font-funcs")])
            if args is None:
                continue
            args.append("--font-funcs=ot")
            unicodes_arg = f"--unicodes={unicodes}"
            code, text, error = shape(font_path, [*args, unicodes_arg])
            jcode, jtext, _ = shape(font_path, [*args, unicodes_arg, "--output-format=json", "--no-glyph-names", "--show-flags"])
            cases.append({
                "file": name,
                "line": number,
                "font": os.path.relpath(font_path, ROOT).replace(os.sep, "/"),
                "options": args,
                "unicodes": unicodes,
                "expected": expected,
                "hb": text if code == 0 else None,
                "matches": code == 0 and text == expected,
                "glyphs": json.loads(jtext) if jcode == 0 and jtext.startswith("[") else None,
                "error": error if code != 0 else None,
            })
    write(os.path.join(OUT, "harfbuzz", f"{suite}.json.gz"), {"harfbuzz": VERSION, "cases": cases})
    matched = sum(1 for c in cases if c["matches"])
    print(f"{suite}: {len(cases)} cases, {matched} match HarfBuzz's own expectations")


def corpus():
    manifest = json.load(open(os.path.join(ROOT, "tests", "reference", "shaping", "corpus.json"), encoding="utf-8"))
    for entry in manifest["runs"]:
        font = os.path.join(ROOT, entry["font"])
        lines = [l for l in open(os.path.join(OUT, "texts", entry["text"] + ".txt"), encoding="utf-8").read().split("\n") if l]
        guess = subprocess.run(["hb-guess"], input="\n".join(lines) + "\n", capture_output=True, text=True, check=True).stdout.split("\n")
        args = ["--no-glyph-names", "--show-flags", "--output-format=json", "--cluster-level=0", *entry.get("options", [])]
        result = []
        for i, text in enumerate(lines):
            code, out, error = shape(font, [*args, f"--text={text}"])
            if code != 0:
                sys.exit(f"{entry['name']} line {i + 1}: {error}")
            script, direction = guess[i].split()
            result.append({"text": text, "script": script, "direction": direction, "glyphs": json.loads(out)})
        write(os.path.join(OUT, "corpus", entry["name"] + ".json.gz"),
              {"harfbuzz": VERSION, "font": entry["font"], "options": args, "lines": result})
        print(f"{entry['name']}: {len(result)} lines")


harfbuzz_suite("in-house")
harfbuzz_suite("aots")
corpus()
