"""Builds tests/svg/corpus: 200 real SVG files with clear licenses, for Phase 2.10's load, save and speed checks.

The corpus is made once and committed with corpus.json, which records each file's source, license and author. Run again
only to rebuild it from scratch:

    python tools/fetch-svg-corpus.py --repos ../svg-refs

--repos is a folder holding shallow clones of the icon repositories listed in REPOS (their svg folders only) and the
Tango 0.8.90 release unpacked as tango/. Wikimedia Commons files come from its API, keeping only public domain, CC0 and
CC BY files (no share-alike, no GNU licenses), at most 1 MB each (3 MB for maps). Choice inside each source is by a hash
of the name, so a rebuild picks the same files while the sources do not change.
"""

import argparse
import hashlib
import html
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(ROOT, "tests", "svg", "corpus")
UA = {"User-Agent": "EgamiSvgCorpus/1.0 (https://github.com/inexin/Egami; test corpus builder)"}

# (folder in --repos, glob root, kind, count, license, source url)
REPOS = [
    ("bootstrap-icons/icons", "icons", 15, "MIT", "https://github.com/twbs/icons"),
    ("tabler/icons/outline", "icons", 15, "MIT", "https://github.com/tabler/tabler-icons"),
    ("fontawesome/svgs/solid", "icons", 10, "CC-BY-4.0", "https://github.com/FortAwesome/Font-Awesome"),
    ("weather-icons/svg", "icons-illustrator", 10, "OFL-1.1", "https://github.com/erikflowers/weather-icons"),
    ("twemoji/assets/svg", "emoji", 25, "CC-BY-4.0", "https://github.com/jdecked/twemoji"),
    ("tango/tango-icon-theme-0.8.90/scalable", "inkscape", 25, "Public domain", "http://tango.freedesktop.org/"),
]

# (Commons category, kind, count, size limit)
COMMONS = [
    ("Valid SVG created with Inkscape", "inkscape", 30, 1 << 20),
    ("Valid SVG created with Adobe Illustrator", "illustrator", 25, 1 << 20),
    ("Valid SVG created with Matplotlib", "chart", 10, 1 << 20),
    ("Valid SVG created with Gnuplot", "chart", 5, 1 << 20),
    ("SVG blank maps of the world with national borders", "map", 5, 2 << 20),
    ("flags", "flag", 25, 1 << 20),
]

# National flags, named (their category holds only subcategories); all public domain on Commons.
FLAGS = ["Brazil", "Mexico", "Spain", "Portugal", "Nepal", "Canada", "Japan", "Norway", "the United Kingdom",
         "the United States", "South Africa", "Sri Lanka", "Bhutan", "Ecuador", "Belize", "Saudi Arabia", "Turkey",
         "Kazakhstan", "Mozambique", "Guatemala", "South Korea", "Israel", "Kenya", "Papua New Guinea", "Dominica"]


def key(name):
    return hashlib.sha1(name.encode("utf-8")).hexdigest()


def api(**params):
    params.update(format="json", action="query")
    url = "https://commons.wikimedia.org/w/api.php?" + urllib.parse.urlencode(params)
    for attempt in range(5):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60) as r:
                return json.load(r)
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("Commons API failed: " + url)


def download(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=120) as r:
                return r.read()
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("download failed: " + url)


def clear_license(name):
    n = name.lower()
    if "sa" in re.split(r"[^a-z]+", n) or "gfdl" in n or "gpl" in n or "nc" in re.split(r"[^a-z]+", n):
        return False
    return n.startswith("public domain") or n.startswith("pd") or n.startswith("cc0") or n.startswith("cc by")


def safe_name(prefix, title):
    stem = re.sub(r"[^A-Za-z0-9._-]+", "-", title)[:60].strip("-.")
    return f"{prefix}-{stem}"


def from_repos(repos, entries):
    for folder, kind, count, lic, source in REPOS:
        base = os.path.join(repos, folder)
        files = []
        for dirpath, _, names in os.walk(base):
            files += [os.path.join(dirpath, n) for n in names if n.endswith(".svg")]
        files.sort(key=lambda p: key(os.path.relpath(p, base).replace(os.sep, "/")))
        for path in files[:count]:
            rel = os.path.relpath(path, base).replace(os.sep, "/")
            data = open(path, "rb").read()
            name = safe_name(kind, folder.split("/")[0] + "-" + rel.replace("/", "-"))
            if not name.endswith(".svg"):
                name += ".svg"
            entries.append(write(name, data, kind, lic, source + " (" + rel + ")", ""))


def from_commons(entries):
    for category, kind, count, limit in COMMONS:
        titles, cont = [], {}
        if category == "flags":
            titles = ["File:Flag of " + f + ".svg" for f in FLAGS]
        while category != "flags" and len(titles) < 3000:
            r = api(list="categorymembers", cmtitle="Category:" + category, cmtype="file", cmlimit=500, **cont)
            titles += [m["title"] for m in r["query"]["categorymembers"] if m["title"].lower().endswith(".svg")]
            if "continue" not in r:
                break
            cont = {"cmcontinue": r["continue"]["cmcontinue"]}
        titles.sort(key=key)
        taken = 0
        for i in range(0, len(titles), 50):
            if taken >= count:
                break
            batch = titles[i:i + 50]
            r = api(titles="|".join(batch), prop="imageinfo", iiprop="url|size|extmetadata|sha1",
                    iiextmetadatafilter="LicenseShortName|Artist")
            pages = {p["title"]: p for p in r["query"]["pages"].values()}
            for t in batch:
                if taken >= count:
                    break
                info = pages.get(t, {}).get("imageinfo", [None])[0]
                if info is None or info["size"] > limit:
                    continue
                meta = info.get("extmetadata", {})
                lic = meta.get("LicenseShortName", {}).get("value", "")
                if not clear_license(lic):
                    continue
                artist = re.sub(r"<[^>]+>", "", html.unescape(meta.get("Artist", {}).get("value", ""))).strip()
                name = safe_name(kind, t[5:])
                if not name.endswith(".svg"):
                    name += ".svg"
                if any(e["file"] == name for e in entries):
                    continue
                data = download(info["url"])
                entries.append(write(name, data, kind, lic, info["descriptionurl"], artist))
                taken += 1
        print(f"{category}: {taken}", file=sys.stderr)


def write(name, data, kind, lic, source, author):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)
    return {"file": name, "kind": kind, "license": lic, "source": source, "author": author,
            "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repos", required=True)
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    for name in os.listdir(OUT):
        os.remove(os.path.join(OUT, name))
    entries = []
    from_repos(args.repos, entries)
    from_commons(entries)
    entries.sort(key=lambda e: e["file"])
    with open(os.path.join(OUT, "corpus.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(entries, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print(f"{len(entries)} files, {sum(e['bytes'] for e in entries) / 1e6:.1f} MB", file=sys.stderr)


if __name__ == "__main__":
    main()
