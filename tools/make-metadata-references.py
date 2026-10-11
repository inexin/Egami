"""Builds the Phase 2.1.3 metadata corpus and ExifTool's reading of it.

Files (tests/images/metadata):
  synthetic/*          written here with ExifTool 12 and ImageMagick 7 from ops/duck.png (public domain): IPTC in UTF-8 and
                       Latin-1, IPTC large enough to span APP13 segments, Photoshop resources beside IPTC, XMP in every RDF
                       form (hand-written packets), language alternatives and structures, extended XMP, XMP in PNG and GIF,
                       Windows XP tags, UserComment and GPS text in each encoding, a phone-style photo with orientation 6,
                       SubjectArea, SubjectLocation and an IFD1 thumbnail, and PNGs ImageMagick wrote from those JPEGs, with
                       its "Raw profile type" text chunks
  outside/*            files fetched by Lars, each listed with its license in tests/images/LICENSES (none yet)

References (tests/reference/metadata):
  <file>.json          exiftool -j -a -G1 -struct -n output (values as stored, not print-converted) for the file, without the System and ExifTool groups: every tag,
                       grouped by family 1 (IPTC, XMP-dc, IFD0, ExifIFD, IFD1, Photoshop, PNG ...), structures as JSON
                       objects, binary values as a length note

Usage, from the repo root with Docker running:
  python tools/make-metadata-references.py
The script runs alpine:3.20 and installs exiftool and imagemagick inside it with apk.
"""
import json
import os
import shutil
import struct
import subprocess
import zlib

IMAGE = "alpine:3.20"
PACKAGES = "exiftool imagemagick imagemagick-jpeg"
IMAGES = "tests/images/metadata"
REF = "tests/reference/metadata"
WORK = ".cache/refs-metadata"

# Hand-written XMP packets: one per RDF form the parser must read.
XMP = {
    # Properties as attributes of one rdf:Description, simple values only.
    "attributes": """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
 xmp:CreatorTool="Egami corpus" xmp:Rating="4" xmp:Label="Green" photoshop:City="Bergen" photoshop:Country="Norway"
 photoshop:Credit="Egami" photoshop:Source="Synthetic" photoshop:Headline="Attributes form"/>
</rdf:RDF></x:xmpmeta>""",
    # Several rdf:Description blocks, element form, arrays of every kind, language alternatives.
    "elements": """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
 <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Mandarin duck</rdf:li><rdf:li xml:lang="nb-NO">Mandarinand</rdf:li><rdf:li xml:lang="de">Mandarinente</rdf:li></rdf:Alt></dc:title>
 <dc:description><rdf:Alt><rdf:li xml:lang="x-default">A duck on water, æøå — 水</rdf:li></rdf:Alt></dc:description>
 <dc:rights><rdf:Alt><rdf:li xml:lang="x-default">Public domain</rdf:li></rdf:Alt></dc:rights>
 <dc:creator><rdf:Seq><rdf:li>Adrian Pingstone</rdf:li><rdf:li>Egami</rdf:li></rdf:Seq></dc:creator>
 <dc:subject><rdf:Bag><rdf:li>duck</rdf:li><rdf:li>bird</rdf:li><rdf:li>water</rdf:li></rdf:Bag></dc:subject>
</rdf:Description>
<rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/">
 <xmp:CreateDate>2026-10-11T09:30:15+02:00</xmp:CreateDate>
 <xmp:ModifyDate>2026-10-11T10:00</xmp:ModifyDate>
 <tiff:Orientation>1</tiff:Orientation>
</rdf:Description>
</rdf:RDF></x:xmpmeta>""",
    # Structures three ways: rdf:parseType="Resource", a nested rdf:Description, and attributes on the property element;
    # an rdf:resource value; an array of structures; a namespace Egami does not know.
    "structures": """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:Iptc4xmpCore="http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/"
 xmlns:Iptc4xmpExt="http://iptc.org/std/Iptc4xmpExt/2008-02-29/" xmlns:xmpRights="http://ns.adobe.com/xap/1.0/rights/"
 xmlns:egami="http://egami.example/ns/1.0/">
 <Iptc4xmpCore:CreatorContactInfo rdf:parseType="Resource">
  <Iptc4xmpCore:CiAdrCity>Bergen</Iptc4xmpCore:CiAdrCity>
  <Iptc4xmpCore:CiEmailWork>photo@example.com</Iptc4xmpCore:CiEmailWork>
 </Iptc4xmpCore:CreatorContactInfo>
 <Iptc4xmpExt:LocationCreated><rdf:Bag><rdf:li><rdf:Description>
  <Iptc4xmpExt:City>Bergen</Iptc4xmpExt:City><Iptc4xmpExt:CountryName>Norway</Iptc4xmpExt:CountryName>
 </rdf:Description></rdf:li><rdf:li rdf:parseType="Resource">
  <Iptc4xmpExt:City>Oslo</Iptc4xmpExt:City>
 </rdf:li></rdf:Bag></Iptc4xmpExt:LocationCreated>
 <Iptc4xmpExt:ArtworkOrObject><rdf:Bag><rdf:li Iptc4xmpExt:AOTitle="Duck study" Iptc4xmpExt:AOCreator="Egami"/></rdf:Bag></Iptc4xmpExt:ArtworkOrObject>
 <xmpRights:WebStatement rdf:resource="https://creativecommons.org/publicdomain/zero/1.0/"/>
 <egami:Note>Unknown namespace, kept through edits</egami:Note>
 <egami:Point egami:X="0.25" egami:Y="0.75"/>
</rdf:Description>
</rdf:RDF></x:xmpmeta>""",
}

# Keywords enough to push the IPTC block past one 64 KB APP13 segment.
MANY_KEYWORDS = " ".join(f"-IPTC:Keywords=keyword-{i:05d}-{'x' * 40}" for i in range(1400))

SCRIPT = r"""
set -e
apk add -q {packages} >/dev/null
cd /work
S=/out/synthetic
mkdir -p $S
X="exiftool -q -overwrite_original -m"

# Base images: the duck, 160 pixels wide, as JPEG, PNG and GIF; a 640 x 480 "phone" frame and its 160 x 120 thumbnail.
magick /duck.png -resize 160x -strip -quality 90 base.jpg
magick /duck.png -resize 160x -strip -define png:exclude-chunks=date,time base.png
magick /duck.png -resize 160x -strip -colors 64 base.gif
magick /duck.png -resize 640x480! -strip -quality 85 phone.jpg
magick /duck.png -resize 160x120! -strip -quality 75 thumb.jpg

# IPTC, UTF-8 (1:90 = ESC % G), the common datasets, repeated keywords and bylines.
cp base.jpg $S/iptc-utf8.jpg
$X -charset iptc=UTF8 -IPTC:CodedCharacterSet=UTF8 -IPTC:ObjectName="Mandarin duck" -IPTC:Headline="Duck on water" \
  -IPTC:Caption-Abstract="A mandarin duck, æøå — 水鳥" -IPTC:By-line="Adrian Pingstone" -IPTC:By-line="Egami" \
  -IPTC:By-lineTitle="Photographer" -IPTC:Credit="Egami" -IPTC:Source="Synthetic" -IPTC:CopyrightNotice="Public domain" \
  -IPTC:City="Bergen" -IPTC:Sub-location="Bryggen" -IPTC:Province-State="Vestland" -IPTC:Country-PrimaryLocationName="Norway" \
  -IPTC:Country-PrimaryLocationCode="NOR" -IPTC:Keywords="duck" -IPTC:Keywords="bird" -IPTC:Keywords="vann" \
  -IPTC:DateCreated=2026:10:11 -IPTC:TimeCreated=09:30:15+02:00 -IPTC:Urgency=5 -IPTC:Category=NAT \
  -IPTC:SupplementalCategories="Birds" -IPTC:SpecialInstructions="None" -IPTC:Writer-Editor="Egami" \
  -IPTC:OriginalTransmissionReference="EGAMI-1" $S/iptc-utf8.jpg

# IPTC with no character set: Latin-1 bytes, as old news software wrote.
cp base.jpg $S/iptc-latin1.jpg
$X -charset iptc=Latin -IPTC:Caption-Abstract="Fjord, æøå ÆØÅ" -IPTC:City="Tromsø" -IPTC:Keywords="sjø" $S/iptc-latin1.jpg

# IPTC over 64 KB: Photoshop resources split across APP13 segments.
cp base.jpg $S/iptc-large.jpg
$X -charset iptc=UTF8 -IPTC:CodedCharacterSet=UTF8 {many_keywords} $S/iptc-large.jpg

# Photoshop resources beside IPTC: resolution info and the IPTC digest, as Photoshop saves them.
cp base.jpg $S/photoshop.jpg
$X -Photoshop:XResolution=300 -Photoshop:YResolution=300 -Photoshop:DisplayedUnitsX=inches -Photoshop:DisplayedUnitsY=inches \
  -Photoshop:CopyrightFlag=True -Photoshop:URL=https://example.com/duck \
  -charset iptc=UTF8 -IPTC:CodedCharacterSet=UTF8 -IPTC:ObjectName="With resources" -IPTC:Keywords="photoshop" -Photoshop:IPTCDigest=new $S/photoshop.jpg

# XMP in each RDF form, from the packets above.
for f in attributes elements structures; do
  cp base.jpg $S/xmp-$f.jpg
  $X "-xmp<=/work/$f.xmp" $S/xmp-$f.jpg
done

# Extended XMP: a packet over 64 KB, which ExifTool splits into the standard and extended parts.
cp base.jpg $S/xmp-extended.jpg
$X -XMP-dc:Description="$(head -c 90000 /dev/zero | tr '\0' 'd')" -XMP-dc:Title="Extended" $S/xmp-extended.jpg

# XMP in PNG (iTXt XML:com.adobe.xmp) and GIF (application extension).
cp base.png $S/xmp.png
$X "-xmp<=/work/elements.xmp" $S/xmp.png
cp base.gif $S/xmp.gif
$X "-xmp<=/work/elements.xmp" $S/xmp.gif

# Windows XP tags (UTF-16LE in IFD0), as Explorer writes them.
cp base.jpg $S/xp-tags.jpg
$X -XPTitle="Duck title æø" -XPComment="Comment — 水" -XPAuthor="Egami; Adrian Pingstone" -XPKeywords="duck;bird" \
  -XPSubject="Subject" $S/xp-tags.jpg

# UserComment and GPS text in each encoding ExifTool writes: ASCII, UNICODE (UTF-16, file byte order), and an undefined
# (all zero) prefix written raw.
cp base.jpg $S/usercomment-ascii.jpg
$X -UserComment="Plain ASCII comment" -GPSProcessingMethod=GPS -GPSAreaInformation="Bergen" $S/usercomment-ascii.jpg
cp base.jpg $S/usercomment-unicode.jpg
$X -UserComment="Unicode comment æøå 水" -GPSProcessingMethod="NETWORK ø" $S/usercomment-unicode.jpg
cp base.jpg $S/usercomment-unicode-motorola.jpg
$X -ExifByteOrder=Big-endian -UserComment="Big-endian ø" $S/usercomment-unicode-motorola.jpg

# A phone-style photo: orientation 6, SubjectArea as a rectangle, SubjectLocation, an IFD1 thumbnail, XMP copies.
cp phone.jpg $S/phone-orientation6.jpg
$X -Orientation#=6 -ExifIFD:SubjectArea="320 240 200 120" -ExifIFD:SubjectLocation="300 200" \
  -ExifIFD:ExifImageWidth=640 -ExifIFD:ExifImageHeight=480 -Make=Egami -Model="Corpus Phone" \
  "-ThumbnailImage<=/work/thumb.jpg" -IFD1:Orientation#=6 \
  -XMP-tiff:Orientation#=6 -XMP-exif:ExifImageWidth=640 -XMP-exif:ExifImageHeight=480 \
  -XMP-exif:SubjectArea="320, 240, 200, 120" $S/phone-orientation6.jpg
# SubjectArea as a point and as a circle.
cp phone.jpg $S/subject-point.jpg
$X -ExifIFD:SubjectArea="100 50" -Orientation#=1 $S/subject-point.jpg
cp phone.jpg $S/subject-circle.jpg
$X -ExifIFD:SubjectArea="500 400 60" -Orientation#=8 $S/subject-circle.jpg

# PNGs ImageMagick writes from the JPEGs: EXIF, IPTC and XMP as "Raw profile type" text chunks.
magick $S/iptc-utf8.jpg -define png:exclude-chunks=date,time $S/im-iptc.png
magick $S/phone-orientation6.jpg -resize 160x -define png:exclude-chunks=date,time $S/im-exif.png
magick $S/xmp-elements.jpg -define png:exclude-chunks=date,time $S/im-xmp.png

"""

# ExifTool's reading of every file, after the legacy PNGs are added.
DUMP = r"""
set -e
apk add -q {packages} >/dev/null
S=/out/synthetic
mkdir -p /ref
for f in $S/*; do
  exiftool -j -a -G1 -struct -n -charset iptc=Latin --System:all --ExifTool:all "$f" | sed "s#$S/##" > /ref/$(basename "$f").json
done
exiftool -ver > /ref/exiftool-version.txt
magick -version | head -1 > /ref/imagemagick-version.txt
"""


def raw_profile(kind, payload):
    """A zTXt "Raw profile type <kind>" chunk as ImageMagick and ExifTool wrote them: a newline, the kind, the length right
    aligned in 8 columns, then the bytes in hex, 36 a line."""
    hexed = payload.hex()
    lines = "\n".join(hexed[i:i + 72] for i in range(0, len(hexed), 72))
    text = f"\n{kind}\n{len(payload):8d}\n{lines}\n".encode("latin-1")
    body = f"Raw profile type {kind}".encode("latin-1") + b"\0\0" + zlib.compress(text)
    return struct.pack(">I", len(body)) + b"zTXt" + body + struct.pack(">I", zlib.crc32(b"zTXt" + body))


def app_segment(jpeg, marker, prefix):
    """The payload of the first APPn segment that starts with prefix."""
    i = 2
    while i < len(jpeg):
        m, n = jpeg[i + 1], struct.unpack(">H", jpeg[i + 2:i + 4])[0]
        if m == marker and jpeg[i + 4:i + 4 + len(prefix)] == prefix:
            return jpeg[i + 4:i + 2 + n]
        i += 2 + n
    raise ValueError("segment not found")


def with_chunks(png, chunks):
    """The PNG with chunks inserted after IHDR."""
    return png[:33] + b"".join(chunks) + png[33:]


def legacy_pngs():
    """PNGs as tools wrote before eXIf (2017): EXIF as "Raw profile type exif" (ImageMagick) and "APP1" (ExifTool), both
    starting with the Exif header."""
    with open(f"{IMAGES}/synthetic/phone-orientation6.jpg", "rb") as f:
        exif = app_segment(f.read(), 0xE1, b"Exif\0\0")
    with open(f"{WORK}/base.png", "rb") as f:
        base = f.read()
    with open(f"{IMAGES}/synthetic/legacy-raw-exif.png", "wb") as f:
        f.write(with_chunks(base, [raw_profile("exif", exif)]))
    with open(f"{IMAGES}/synthetic/legacy-raw-app1.png", "wb") as f:
        f.write(with_chunks(base, [raw_profile("APP1", exif)]))


def main():
    os.makedirs(WORK, exist_ok=True)
    for name, text in XMP.items():
        with open(f"{WORK}/{name}.xmp", "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    shutil.copy("tests/images/ops/duck.png", f"{WORK}/duck.png")
    script = SCRIPT.format(packages=PACKAGES, many_keywords=MANY_KEYWORDS)
    with open(f"{WORK}/run.sh", "w", encoding="utf-8", newline="\n") as f:
        f.write(script)
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(REF, exist_ok=True)
    root = os.path.abspath(".").replace("\\", "/")
    subprocess.run(["docker", "run", "--rm",
                    "-v", f"{root}/{WORK}:/work", "-v", f"{root}/{WORK}/duck.png:/duck.png:ro",
                    "-v", f"{root}/{IMAGES}:/out", "-v", f"{root}/{REF}:/ref",
                    IMAGE, "sh", "/work/run.sh"], check=True)
    legacy_pngs()
    with open(f"{WORK}/dump.sh", "w", encoding="utf-8", newline="\n") as f:
        f.write(DUMP.format(packages=PACKAGES))
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}/{WORK}:/work", "-v", f"{root}/{IMAGES}:/out",
                    "-v", f"{root}/{REF}:/ref", IMAGE, "sh", "/work/dump.sh"], check=True)
    files = sorted(os.listdir(f"{IMAGES}/synthetic"))
    for name in files:
        with open(f"{REF}/{name}.json", encoding="utf-8") as f:
            json.load(f)
    print(f"{len(files)} files; ExifTool {open(f'{REF}/exiftool-version.txt').read().strip()}")


if __name__ == "__main__":
    main()
