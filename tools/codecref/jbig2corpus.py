"""Builds the JBIG2 corpus and its references inside the egami-codecref image; run by tools/make-jbig2-references.py.

Sources are drawn here (text set with Pillow's bundled font, the repo's duck and shapes images dithered or thresholded, and
noise at odd sizes) and encoded with jbig2enc 0.30 in every mode it has: generic, generic with TPGD (-d), symbol (-s),
lossy symbol (-s -t 0.8), multi-page symbol, and PDF mode (-p: embedded page streams plus a
globals stream). jbig2enc's refinement (-r) is broken in 0.30 and refuses to run. Each page is decoded by jbig2dec 0.20 and by PDFium (pypdfium2); the reference is their output when they
agree, and the manifest records any disagreement.

Usage (in the container, repo mounted at /w): python3 /w/tools/codecref/jbig2corpus.py
       python3 /w/tools/codecref/jbig2corpus.py --local <dir>
The second form writes references for the files already in <dir> (third-party streams kept out of the repo) to <dir>/ref.
"""
import glob
import gzip
import hashlib
import json
import os
import random
import shutil
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

REPO = "/w"
CORPUS = f"{REPO}/tests/images/jbig2"
TARGET = f"{REPO}/tests/reference/jbig2"
WORK = "/tmp/jb2work"
PDFIUM = f"{REPO}/tools/codecref/pdfium_jbig2.py"

WORDS = ("JBIG2 codes bi-level images such as scanned pages of text: symbols are matched, kept in a dictionary and "
         "placed by text regions, while halftones and line art use generic regions. Egami decodes every region type "
         "the standard defines except the colour extension. The quick brown fox jumps over the lazy dog 0123456789 "
         "!?;:() [] {} <> @#%& ").split()


def run(*args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, **kwargs)


def text_page(width, height, seed, sizes=(14, 18, 24, 36)):
    random.seed(seed)
    image = Image.new("L", (width, height), 255)
    draw = ImageDraw.Draw(image)
    y = 10
    while y < height - 40:
        size = random.choice(sizes)
        font = ImageFont.load_default(size)
        x = 12
        line = []
        while x < width - 60:
            word = random.choice(WORDS)
            line.append(word)
            x += int(font.getlength(word + " "))
        draw.text((12, y), " ".join(line[:-1]), font=font, fill=0)
        y += int(size * 1.4)
    return image.point(lambda v: 0 if v < 140 else 255).convert("1")


def sources():
    os.makedirs(f"{WORK}/src", exist_ok=True)
    made = {}

    def save(name, image):
        path = f"{WORK}/src/{name}.png"
        image.save(path)
        made[name] = path

    save("text-small", text_page(320, 200, 1, (12, 16)))
    save("text-page", text_page(1275, 1650, 2))
    for i in range(3):
        save(f"book-{i}", text_page(850, 1100, 10 + i))
    duck = Image.open(f"{REPO}/tests/images/ops/duck.png").convert("L")
    save("duck-dither", duck.convert("1"))
    save("duck-threshold", duck.point(lambda v: 0 if v < 120 else 255).convert("1"))
    shapes = Image.open(f"{REPO}/tests/images/ops/shapes.png").convert("L")
    save("shapes", shapes.point(lambda v: 0 if v < 128 else 255).convert("1"))
    random.seed(7)
    for w, h in ((67, 1), (1, 53), (129, 65), (64, 64), (65, 3)):
        noise = Image.new("1", (w, h))
        noise.putdata([random.choice((0, 255)) for _ in range(w * h)])
        save(f"noise-{w}x{h}", noise)
    return made


def jbig2(*args, cwd):
    return subprocess.run(["jbig2", *args], check=True, capture_output=True, cwd=cwd).stdout


def encode(made):
    os.makedirs(f"{WORK}/enc", exist_ok=True)
    enc = f"{WORK}/enc"
    files = {}

    def write(name, data, how):
        with open(f"{enc}/{name}", "wb") as f:
            f.write(data)
        files[name] = {"how": how}

    for name, path in made.items():
        if name.startswith("book-") and name != "book-0":
            continue
        write(f"{name}-generic.jb2", jbig2(path, cwd=enc), "jbig2 (generic region, template 0)")
        write(f"{name}-tpgd.jb2", jbig2("-d", path, cwd=enc), "jbig2 -d (generic region with TPGD)")
        if name.startswith(("text", "book", "shapes")):
            write(f"{name}-symbol.jb2", jbig2("-s", path, cwd=enc), "jbig2 -s (symbol dictionary and text region)")
            write(f"{name}-lossy.jb2", jbig2("-s", "-t", "0.8", path, cwd=enc), "jbig2 -s -t 0.8 (lossy symbol matching)")
    books = [made[f"book-{i}"] for i in range(3)]
    write("book-3pages-symbol.jb2", jbig2("-s", *books, cwd=enc), "jbig2 -s, three pages")
    write("book-3pages-generic.jb2", jbig2(*books, cwd=enc), "jbig2, three generic pages")
    # PDF mode: embedded page streams and one globals stream, as a PDF holds them.
    jbig2("-s", "-p", "-b", "book-pdf", *books, cwd=enc)
    for page in sorted(glob.glob(f"{enc}/book-pdf.0*")):
        files[os.path.basename(page)] = {"how": "jbig2 -s -p (embedded page stream)", "globals": "book-pdf.sym"}
    files["book-pdf.sym"] = {"how": "jbig2 -s -p (globals stream)", "globals_only": True}
    write("duck-pdf.0000", jbig2("-p", made["duck-dither"], cwd=enc), "jbig2 -p (embedded generic page stream)")
    return files


def split_pbms(path):
    """Returns ((width, height), packed rows) for each PBM in a file of concatenated binary PBMs (jbig2dec writes pages so)."""
    data = open(path, "rb").read()
    pages = []
    at = 0
    while at < len(data):
        fields = []
        while len(fields) < 3:
            while data[at:at + 1].isspace():
                at += 1
            start = at
            while not data[at:at + 1].isspace():
                at += 1
            fields.append(data[start:at])
        at += 1
        width, height = int(fields[1]), int(fields[2])
        size = (width + 7) // 8 * height
        pages.append(((width, height), data[at:at + size]))
        at += size
    return pages


def references(files, enc=f"{WORK}/enc", target=TARGET):
    os.makedirs(f"{WORK}/ref", exist_ok=True)
    manifest = {}
    for name, entry in sorted(files.items()):
        if entry.get("globals_only"):
            manifest[name] = entry
            continue
        path = f"{enc}/{name}"
        globals_path = f"{enc}/{entry['globals']}" if "globals" in entry else None
        embedded = not open(path, "rb").read(8).startswith(bytes([0x97, 0x4A, 0x42, 0x32]))
        # jbig2dec: one PBM per page (pages are written to <out>.<n>.pbm by -o with %d isn't supported; decode per page).
        jd = f"{WORK}/ref/{name}.jbig2dec"
        args = ["jbig2dec", "-t", "pbm", "-o", jd + ".pbm"]
        if embedded:
            args += ["-e"]
            args += [globals_path, path] if globals_path else [path]
        else:
            args += [path]
        jd_out = subprocess.run(args, capture_output=True, text=True)
        pf = f"{WORK}/ref/{name}.pdfium"
        pargs = ["python3", PDFIUM, path, pf]
        if globals_path:
            pargs[3:3] = ["--globals", globals_path]
        pf_out = subprocess.run(pargs, capture_output=True, text=True)
        pages = sorted(glob.glob(pf + ".*.pbm"), key=lambda p: int(p.split(".")[-2]))
        entry["pages"] = []
        jd_pages = split_pbms(jd + ".pbm") if os.path.exists(jd + ".pbm") else []
        for index, page in enumerate(pages):
            size, pixels = split_pbms(page)[0]
            agree = None
            if index < len(jd_pages):
                agree = jd_pages[index][0] == size and jd_pages[index][1] == pixels
            reference = f"{name}.{index}.pbm.gz"
            with open(page, "rb") as f, open(f"{target}/{reference}", "wb") as g:
                g.write(gzip.compress(f.read(), mtime=0))
            page_entry = {
                "width": size[0],
                "height": size[1],
                "reference": reference,
                "sha1": hashlib.sha1(pixels).hexdigest(),
                "jbig2dec_agrees": agree,
            }
            if agree is False:
                # Kept so a test can follow T.88 where the two references part ways.
                other = f"{name}.{index}.jbig2dec.pbm.gz"
                (w, h), bits = jd_pages[index]
                with open(f"{target}/{other}", "wb") as g:
                    g.write(gzip.compress(b"P4 %d %d\n" % (w, h) + bits, mtime=0))
                page_entry["jbig2dec_reference"] = other
            entry["pages"].append(page_entry)
        entry["jbig2dec_warnings"] = jd_out.stderr.strip()[:500]
        entry["pdfium_error"] = pf_out.stderr.strip()[-500:]
        manifest[name] = entry
    return manifest


def local(folder):
    target = f"{folder}/ref"
    os.makedirs(target, exist_ok=True)
    for old in glob.glob(f"{target}/*"):
        os.remove(old)
    files = {os.path.basename(p): {"how": "third-party"} for p in sorted(glob.glob(f"{folder}/*.jb2"))}
    manifest = references(files, enc=folder, target=target)
    with open(f"{target}/manifest.json", "w") as f:
        json.dump({"files": manifest}, f, indent=1, sort_keys=True)
    print(f"{len(manifest)} local files referenced")


def main():
    if sys.argv[1:2] == ["--local"]:
        local(sys.argv[2])
        return
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(CORPUS, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    for old in glob.glob(f"{TARGET}/*") + glob.glob(f"{CORPUS}/*"):
        os.remove(old)
    made = sources()
    files = encode(made)
    for name in files:
        shutil.copy(f"{WORK}/enc/{name}", f"{CORPUS}/{name}")
    manifest = references(files)
    versions = {
        "jbig2dec": subprocess.run(["jbig2dec", "--version"], capture_output=True, text=True).stdout.strip(),
        "jbig2enc": subprocess.run(["jbig2", "-V"], capture_output=True, text=True).stderr.strip().splitlines()[0],
        "pypdfium2": subprocess.run(["pip", "show", "pypdfium2"], capture_output=True, text=True).stdout.split("\n")[1],
    }
    with open(f"{TARGET}/manifest.json", "w") as f:
        json.dump({"files": manifest, "tools": versions}, f, indent=1, sort_keys=True)
    disagree = [n for n, e in manifest.items() for p in e.get("pages", []) if p["jbig2dec_agrees"] is False]
    print(f"{len(manifest)} files; jbig2dec disagrees with PDFium on: {disagree}")


main()
