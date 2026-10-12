"""Builds the shaping references of Phase 2.8.1 (question 18): exact expectations from a pinned HarfBuzz.

1. Downloads HarfBuzz 14.5.1's release tarball into .cache/harfbuzz (SHA-256 checked) and builds the egami-shaperef image from
   tools/shaperef (hb-shape, plus hb-guess for the script and direction HarfBuzz guesses).
2. Copies HarfBuzz's in-house and AOTS shaping tests and their fonts into tests/fonts/harfbuzz (MIT, with HarfBuzz's COPYING),
   leaving out the tests for Apple's AAT tables, Graphite and system shapers, which Egami does not implement (question 3).
3. Downloads the corpus fonts (pinned releases, SHA-256 checked) into tests/fonts/shaping and the corpus texts (the opening of
   the Universal Declaration of Human Rights per language, from the UDHR in XML project at a pinned commit) into
   tests/reference/shaping/texts.
4. Runs tools/shaperef/shape.py in the image, which writes tests/reference/shaping/harfbuzz and tests/reference/shaping/corpus.

Usage, from the repo root with Docker running:
  python tools/make-shaping-references.py
"""
import hashlib
import io
import os
import re
import shutil
import subprocess
import tarfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

IMAGE = "egami-shaperef:2.8.1"
HB_VERSION = "14.5.1"
HB_URL = f"https://github.com/harfbuzz/harfbuzz/releases/download/{HB_VERSION}/harfbuzz-{HB_VERSION}.tar.xz"
HB_SHA256 = "7e2fa4e8c7c98e8d8140671f5772542afaaa6acccfbd746506886b6d85f7f8d6"

# HarfBuzz tests for what Egami leaves out: AAT tables, Graphite, the system shapers and fonts that are not in the tarball.
SKIPPED_TESTS = {"aat-morx.tests", "aat-trak.tests", "coretext.tests", "directwrite.tests", "uniscribe.tests", "macos.tests",
                 "graphite2-clusters.tests", "kbts.tests"}

# (file in tests/fonts/shaping, archive URL, archive SHA-256, member, license file in tests/fonts/LICENSES)
FONTS = [
    ("NotoSansArabic-Regular.ttf", "https://github.com/notofonts/arabic/releases/download/NotoSansArabic-v2.013/NotoSansArabic-v2.013.zip",
     "1301aceaea84c501cf2e6dcfb3182e2328c8eae5725817fcb239672bda7154f1", "NotoSansArabic/unhinted/ttf/NotoSansArabic-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansHebrew-Regular.ttf", "https://github.com/notofonts/hebrew/releases/download/NotoSansHebrew-v3.001/NotoSansHebrew-v3.001.zip",
     "df0a71814b4e63644cf40fcc4529111b61266b7a2dafbe95068b29a7520cc3cb", "NotoSansHebrew/unhinted/ttf/NotoSansHebrew-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansDevanagari-Regular.ttf", "https://github.com/notofonts/devanagari/releases/download/NotoSansDevanagari-v2.007/NotoSansDevanagari-v2.007.zip",
     "820c7da45b1e63562cb41c0a8cac5d9a4202312043a3a040ed1325857ef469b1", "NotoSansDevanagari/unhinted/ttf/NotoSansDevanagari-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansBengali-Regular.ttf", "https://github.com/notofonts/bengali/releases/download/NotoSansBengali-v3.011/NotoSansBengali-v3.011.zip",
     "2aca24bf71665e66c32e14862ee58857dd8b8b07b6321ec7c96f3121ad6683b1", "NotoSansBengali/unhinted/ttf/NotoSansBengali-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansTamil-Regular.ttf", "https://github.com/notofonts/tamil/releases/download/NotoSansTamil-v2.004/NotoSansTamil-v2.004.zip",
     "f8284e0f200a7f29a439b4ec88280d864b2b31f8479111c5b658ba6da38b3005", "NotoSansTamil/unhinted/ttf/NotoSansTamil-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansThai-Regular.ttf", "https://github.com/notofonts/thai/releases/download/NotoSansThai-v2.002/NotoSansThai-v2.002.zip",
     "af889cc673fc714060ce5e4e088fbad32aa4c0571a19958efeaff128a22da485", "NotoSansThai/unhinted/ttf/NotoSansThai-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansLao-Regular.ttf", "https://github.com/notofonts/lao/releases/download/NotoSansLao-v2.003/NotoSansLao-v2.003.zip",
     "5a87c31b1a40ef8147c1e84437e5f0ceba2d4dbbfc0b56a65821ad29870da8c0", "NotoSansLao/unhinted/ttf/NotoSansLao-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansKhmer-Regular.ttf", "https://github.com/notofonts/khmer/releases/download/NotoSansKhmer-v2.004/NotoSansKhmer-v2.004.zip",
     "19382ca97d62febea1c735ebee35a5aa4f03beca9b6ea6f6d86b7a7a0025a688", "NotoSansKhmer/unhinted/ttf/NotoSansKhmer-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansMyanmar-Regular.ttf", "https://github.com/notofonts/myanmar/releases/download/NotoSansMyanmar-v2.107/NotoSansMyanmar-v2.107.zip",
     "c4995ee97f1f267b46cf83734dbf18a3cfd431e387b6fe38e90279546f260c4b", "NotoSansMyanmar/unhinted/ttf/NotoSansMyanmar-Regular.ttf", "Noto-OFL.txt"),
    ("NotoSansSinhala-Regular.ttf", "https://github.com/notofonts/sinhala/releases/download/NotoSansSinhala-v3.000/NotoSansSinhala-v3.000.zip",
     "25b2c787e0b34b82ccedce026e6519cc535394a6bd8520d5a1df45c233807d8a", "NotoSansSinhala/unhinted/ttf/NotoSansSinhala-Regular.ttf", "Noto-OFL.txt"),
    ("Amiri-Regular.ttf", "https://github.com/aliftype/amiri/releases/download/1.003/Amiri-1.003.zip",
     "81af0aff7d2086d8af24cea7202f7546130997982534691373485cd96744d05e", "Amiri-1.003/Amiri-Regular.ttf", "Amiri-OFL.txt"),
    ("ScheherazadeNew-Regular.ttf", "https://github.com/silnrsi/font-scheherazade/releases/download/v4.500/ScheherazadeNew-4.500.zip",
     "49c789c21305bed13d90d7500877b50d62db1e8e4d6f7715178e556319ddbda9", "ScheherazadeNew-4.500/ScheherazadeNew-Regular.ttf", "ScheherazadeNew-OFL.txt"),
    ("Mukta-Regular.ttf", "https://raw.githubusercontent.com/google/fonts/9d451f695f894391bc997052eec5931f08dc6a3d/ofl/mukta/Mukta-Regular.ttf",
     "2958e4af564507df2a856164df6f9978dacb03f999a4f34a0c269dc8a4de9688", None, "Mukta-OFL.txt"),
]

# Where each license file comes from: (archive URL, member) or a plain URL.
LICENSES = {
    "Amiri-OFL.txt": ("https://github.com/aliftype/amiri/releases/download/1.003/Amiri-1.003.zip", "Amiri-1.003/OFL.txt"),
    "ScheherazadeNew-OFL.txt": ("https://github.com/silnrsi/font-scheherazade/releases/download/v4.500/ScheherazadeNew-4.500.zip", "ScheherazadeNew-4.500/OFL.txt"),
    "Mukta-OFL.txt": ("https://raw.githubusercontent.com/google/fonts/9d451f695f894391bc997052eec5931f08dc6a3d/ofl/mukta/OFL.txt", None),
}

UDHR_COMMIT = "588b3f4b2d0467aff54842a4b926551b69d5a66a"
UDHR_URL = f"https://raw.githubusercontent.com/eric-muller/udhr/{UDHR_COMMIT}/data/udhr/udhr_{{0}}.xml"
# Corpus text name: UDHR file code. Each text is the declaration's opening paragraphs, one per line.
TEXTS = {"english": "eng", "greek": "ell_monotonic", "russian": "rus", "arabic": "arb", "persian": "pes_1", "urdu": "urd",
         "hebrew": "heb", "hindi": "hin", "bengali": "ben", "tamil": "tam", "thai": "tha", "lao": "lao", "khmer": "khm",
         "burmese": "mya", "sinhala": "sin"}
LINES_PER_TEXT = 24


def download(url, sha256=None):
    name = re.sub(r"[^A-Za-z0-9._-]", "_", url.split("://", 1)[1])
    path = os.path.join(".cache", "shaping", name)
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        print(f"Downloading {url}")
        with urllib.request.urlopen(url) as response:
            data = response.read()
        with open(path, "wb") as f:
            f.write(data)
    with open(path, "rb") as f:
        data = f.read()
    digest = hashlib.sha256(data).hexdigest()
    if sha256 and digest != sha256:
        raise SystemExit(f"{url}: SHA-256 {digest} does not match the pinned {sha256}")
    return data, digest


def member(archive, name):
    return zipfile.ZipFile(io.BytesIO(archive)).read(name) if name else archive


def write_bytes(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def harfbuzz():
    data, _ = download(HB_URL, HB_SHA256)
    context = os.path.join(".cache", "shaperef")
    os.makedirs(context, exist_ok=True)
    write_bytes(os.path.join(context, f"harfbuzz-{HB_VERSION}.tar.xz"), data)
    for name in ("Dockerfile", "hb-guess.c"):
        shutil.copyfile(os.path.join("tools", "shaperef", name), os.path.join(context, name))
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, context], check=True)

    tar = tarfile.open(fileobj=io.BytesIO(data))
    prefix = f"harfbuzz-{HB_VERSION}/test/shape/data/"
    members = {m.name[len(prefix):]: m for m in tar.getmembers() if m.name.startswith(prefix) and m.isfile()}
    out = os.path.join("tests", "fonts", "harfbuzz")
    for suite in ("in-house", "aots"):
        fonts = set()
        for name, m in sorted(members.items()):
            if name.startswith(f"{suite}/tests/") and name.endswith(".tests") and os.path.basename(name) not in SKIPPED_TESTS:
                text = tar.extractfile(m).read()
                write_bytes(os.path.join(out, name), text)
                fonts.update(re.findall(r"^\.\./fonts/([^;]+);", text.decode("utf-8"), re.MULTILINE))
        for font in sorted(fonts):
            key = f"{suite}/fonts/{font}"
            if key in members:
                write_bytes(os.path.join(out, key), tar.extractfile(members[key]).read())
    write_bytes(os.path.join("tests", "fonts", "LICENSES", "HarfBuzz-COPYING.txt"),
                tar.extractfile(f"harfbuzz-{HB_VERSION}/COPYING").read())
    write_bytes(os.path.join("tests", "fonts", "LICENSES", "HarfBuzz-AOTS-COPYING.txt"), tar.extractfile(members["aots/COPYING"]).read())


def fonts():
    for file, url, sha256, name, license_file in FONTS:
        archive, _ = download(url, sha256)
        write_bytes(os.path.join("tests", "fonts", "shaping", file), member(archive, name))
    for license_file, (url, name) in LICENSES.items():
        archive, _ = download(url)
        write_bytes(os.path.join("tests", "fonts", "LICENSES", license_file), member(archive, name))


def texts():
    for text, code in TEXTS.items():
        data, _ = download(UDHR_URL.format(code))
        root = ET.fromstring(data)
        lines = []
        for element in root.iter():
            if element.tag.split("}")[-1] in ("title", "para", "listitem") and element.text:
                line = " ".join("".join(element.itertext()).split())
                if line:
                    lines.append(line)
            if len(lines) == LINES_PER_TEXT:
                break
        path = os.path.join("tests", "reference", "shaping", "texts", f"{text}.txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")


harfbuzz()
fonts()
texts()
root = os.getcwd()
subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python", "/w/tools/shaperef/shape.py", "/w"], check=True,
               env={**os.environ, "MSYS_NO_PATHCONV": "1"})
