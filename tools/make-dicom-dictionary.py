"""Makes Egami.Formats.Extended's DICOM data dictionary from the standard's own XML.

DICOM Build Plan task 16 (decision Q6). PS3.6, the Data Dictionary, is published by NEMA as DocBook XML. This script reads its
registry tables and writes two tab-separated files embedded in the package:

  src/Egami.Formats.Extended/Dicom/DicomDictionary.txt   one line per public data element (tables 6-1, 7-1, 8-1 and 9-1):
      Tag  VR  VM  Retired  Keyword  Name
    Tag is 8 hex digits, group then element, with x where the standard writes one (60xx0010 is every overlay group). VR is as
    PS3.6 lists it, alternatives joined by | (US|SS), or - when it has none (the item and delimitation tags). Retired is R or -.
  src/Egami.Formats.Extended/Dicom/DicomUids.txt         one line per UID of table A-1:
      UID  Type  Keyword  Name

The first line of each file names the edition read, such as "# DICOM PS3.6 2026d". Keywords lose the zero-width spaces the
XML puts in them to allow line breaks.

Usage, from the repo root:
  python tools/make-dicom-dictionary.py                     fetches the current part06.xml from dicom.nema.org
  python tools/make-dicom-dictionary.py --xml part06.xml    reads a copy already on disk
"""

import argparse
import os
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

URL = "https://dicom.nema.org/medical/dicom/current/source/docbook/part06/part06.xml"
NS = {"d": "http://docbook.org/ns/docbook"}
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Egami.Formats.Extended", "Dicom")


def text(cell):
    """Returns a cell's text, whitespace collapsed and zero-width spaces dropped."""
    value = "".join(cell.itertext()).replace("\u200b", "").replace("\u00a0", " ")
    return re.sub(r"\s+", " ", value).strip()


def rows(root, table_id):
    table = root.find(f".//d:table[@{{http://www.w3.org/XML/1998/namespace}}id='{table_id}']", NS)
    if table is None:
        sys.exit(f"{table_id} is missing from the XML")
    for tr in table.findall("d:tbody/d:tr", NS):
        yield [text(td) for td in tr.findall("d:td", NS)]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--xml", help="a part06.xml already downloaded")
    args = parser.parse_args()
    if args.xml:
        with open(args.xml, "rb") as f:
            data = f.read()
    else:
        with urllib.request.urlopen(URL) as response:
            data = response.read()
    root = ET.fromstring(data)
    edition = text(root.find("d:subtitle", NS))
    edition = edition.split(" - ")[0]

    elements = {}
    for table in ("table_6-1", "table_7-1", "table_8-1", "table_9-1"):
        for cells in rows(root, table):
            if len(cells) < 5:
                continue
            match = re.fullmatch(r"\(([0-9A-Fa-fx]{4}),([0-9A-Fa-fx]{4})\)", cells[0])
            if not match:
                continue
            tag = (match.group(1) + match.group(2)).upper().replace("X", "x")
            name, keyword, vr, vm = cells[1], cells[2], cells[3], cells[4]
            retired = "R" if len(cells) > 5 and "RET" in cells[5] else "-"
            vr = "|".join(part.strip() for part in vr.split(" or ")) if vr and "See Note" not in vr else "-"
            if not keyword:
                continue
            elements[tag] = (vr, vm or "-", retired, keyword, name)

    uids = []
    for cells in rows(root, "table_A-1"):
        if len(cells) < 4 or not re.fullmatch(r"[0-9.]+", cells[0]):
            continue
        uid, name, keyword, kind = cells[0], cells[1], cells[2], cells[3]
        uids.append((uid, kind, keyword or "-", name.replace(" (Retired)", "")))

    def sort_key(tag):
        return tag.replace("x", "0")

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "DicomDictionary.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# {edition}, tables 6-1, 7-1, 8-1 and 9-1: Tag VR VM Retired Keyword Name\n")
        for tag in sorted(elements, key=sort_key):
            f.write(tag + "\t" + "\t".join(elements[tag]) + "\n")
    with open(os.path.join(OUT, "DicomUids.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# {edition}, table A-1: UID Type Keyword Name\n")
        for row in uids:
            f.write("\t".join(row) + "\n")
    print(f"{edition}: {len(elements)} elements, {len(uids)} UIDs")


if __name__ == "__main__":
    main()
