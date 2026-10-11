"""Checks the documentation is complete. Fails (exit 1) when:

- a documented member has an empty summary;
- a public type, or a public method that takes options or limits, has no <example>;
- an <example> points at a snippet file or region that does not exist;
- a public type is mentioned in no guide, cookbook page or getting-started page, and is not listed in REFERENCE_ONLY below;
- a guide links a sample in samples/ that does not exist.

Usage: python tools/check-docs.py [src/Egami.Drawing]   (from the repo root; builds src/Egami, or the package given, in Release first)
"""
import glob
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

FOLDER = sys.argv[1].rstrip("/\\") if len(sys.argv) > 1 else "src/Egami"
NAME = os.path.basename(FOLDER)
PROJECT = f"{FOLDER}/{NAME}.csproj"
XML = f"{FOLDER}/bin/Release/net10.0/{NAME}.xml"
API_FILES = [f"{FOLDER}/PublicAPI.Shipped.txt", f"{FOLDER}/PublicAPI.Unshipped.txt"]

# Types the API reference covers alone: details a guide would only list, not explain.
REFERENCE_ONLY = {
    "ClearMode", "RowBandAction", "SignatureMatcher", "PixelMemoryLeakEventArgs", "GifApplicationExtension", "JpegComponent",
    "PngChunkPosition", "PngTextSource", "ExifIfd", "ExifType", "ExifFraction", "GeoPosition", "AlphaKind", "PixelChannelOrder",
    "PixelChannelType", "PixelLayoutInfo", "TgaAlphaType", "TgaOrigin", "BmpCompression", "JpegColorSpace", "RowAction",
}

problems = []


def public_api():
    """Returns the public types and the public methods that take options, from the declared API."""
    types, methods = set(), set()
    for line in (line for f in API_FILES for line in open(f, encoding="utf-8")):
        line = re.sub(r"^((abstract|sealed|static|override|readonly|virtual|const) )*", "", line.strip())
        if not line or line.startswith("#") or line.startswith("*REMOVED*"):
            continue
        if " -> " not in line and "(" not in line:
            types.add(line)
        elif re.search(r"\(.*(Options|SafetyLimits)[!?]? \w+", line) and not re.search(r"\.(Equals|operator|\w+Options\(|SafetyLimits\()", line):
            methods.add(re.sub(r"\(.*", "", line))
    return types, methods


def doc_id(type_name):
    """Egami.Raster<TPixel> -> Egami.Raster`1."""
    m = re.match(r"(.+?)<(.+)>$", type_name)
    return f"{m.group(1)}`{m.group(2).count(',') + 1}" if m else type_name


def main():
    build = subprocess.run(["dotnet", "build", PROJECT, "-c", "Release"], capture_output=True, text=True)
    if build.returncode != 0:
        print(build.stdout[-3000:])
        sys.exit("build failed")

    members = {m.get("name"): m for m in ET.parse(XML).getroot().iter("member")}
    for name, member in members.items():
        summary = member.find("summary")
        if member.find("inheritdoc") is None and (summary is None or not "".join(summary.itertext()).strip()):
            problems.append(f"{name}: no summary")

    types, methods = public_api()
    for t in sorted(types):
        member = members.get("T:" + doc_id(t))
        if member is None:
            problems.append(f"{t}: not in the XML docs")
        elif member.find("example") is None:
            problems.append(f"{t}: no <example>")

    for method in sorted(methods):
        owner, name = method.rsplit(".", 1)
        name = re.sub(r"<.*", "", name)
        if name == re.sub(r"<.*", "", owner.rsplit(".", 1)[1]):
            name = "#ctor"
        prefix = f"M:{doc_id(owner)}.{name}"
        overloads = [m for k, m in members.items() if k == prefix or k.startswith(prefix + "(") or k.startswith(prefix + "``")]
        overloads = [m for m in overloads if re.search(r"Options|SafetyLimits", m.get("name"))]
        if not overloads:
            problems.append(f"{method}: no documented overload found")
        for m in overloads:
            if m.find("example") is None:
                problems.append(f"{m.get('name')}: no <example>")

    for source in glob.glob(f"{FOLDER}/**/*.cs", recursive=True):
        try:
            text = open(source, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        for path, region in re.findall(r'<code language="csharp" source="([^"]+)" region="([^"]+)"', text):
            target = os.path.normpath(os.path.join(os.path.dirname(source), path))
            if not os.path.exists(target):
                problems.append(f"{source}: example file {path} does not exist")
            elif f"#region {region}\n" not in open(target, encoding="utf-8").read().replace("\r\n", "\n"):
                problems.append(f"{source}: region {region} not found in {path}")

    pages = ""
    for page in glob.glob("docs/**/*.md", recursive=True):
        text = open(page, encoding="utf-8").read()
        pages += text
        # Code a page includes counts as the page's own: [!code-csharp[](path#region)].
        for path, region in re.findall(r"\[!code-csharp\[\]\(([^)#]+)#([\w-]+)\)\]", text):
            target = os.path.normpath(os.path.join(os.path.dirname(page), path))
            if not os.path.exists(target):
                problems.append(f"{page}: includes {path}, which does not exist")
                continue
            m = re.search(r"#region " + re.escape(region) + r"\n(.*?)#endregion", open(target, encoding="utf-8").read().replace("\r\n", "\n"), re.S)
            if m is None:
                problems.append(f"{page}: includes region {region}, not found in {path}")
            else:
                pages += m.group(1)
    for t in sorted(types):
        short = re.sub(r"<.*", "", t.rsplit(".", 1)[1] if "<" not in t else t[:t.index("<")].rsplit(".", 1)[1])
        if short not in REFERENCE_ONLY and not re.search(r"\b" + short + r"\b", pages):
            problems.append(f"{t}: no guide mentions it (add it to a guide, or to REFERENCE_ONLY)")

    for sample in set(re.findall(r"samples/(\w+)", pages)):
        if not os.path.exists(f"samples/{sample}/{sample}.csproj"):
            problems.append(f"docs link samples/{sample}, which does not exist")

    for p in problems:
        print(p)
    print(f"{len(types)} types, {len(methods)} methods with options, {len(members)} documented members: {len(problems)} problems")
    sys.exit(1 if problems else 0)


main()
