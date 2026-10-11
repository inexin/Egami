"""Decodes JBIG2 with PDFium (pypdfium2), the way a PDF holds it: run inside the egami-codecref image.

Usage: python3 pdfium_jbig2.py <file.jb2 | page-stream> [--globals <globals-stream>] <out-prefix>

A standalone file (with the T.88 file header) is split into embedded streams, one per page, with its page-0 segments as
the globals; each page is wrapped in a one-image PDF with /Filter /JBIG2Decode and decoded by PDFium. Writes
<out-prefix>.<page>.pbm (P4, 1 = black) for each page.
"""
import struct
import sys

import pypdfium2 as pdfium
import pypdfium2.raw as raw

ID = bytes([0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A])


def segments(data, at, random_access):
    """Yields (number, type, page, header bytes, data bytes) for each segment."""
    headers = []
    while at < len(data):
        start = at
        number, flags = struct.unpack(">IB", data[at:at + 5])
        at += 5
        count = data[at] >> 5
        if count == 7:
            count = struct.unpack(">I", data[at:at + 4])[0] & 0x1FFFFFFF
            at += 4 + (count + 8) // 8
        else:
            at += 1
        size = 1 if number <= 256 else 2 if number <= 65536 else 4
        at += count * size
        if flags & 0x40:
            page = struct.unpack(">I", data[at:at + 4])[0]
            at += 4
        else:
            page = data[at]
            at += 1
        length = struct.unpack(">I", data[at:at + 4])[0]
        at += 4
        header = data[start:at]
        if random_access:
            headers.append((number, flags & 63, page, header, length))
            if flags & 63 == 51:
                break
            continue
        if length == 0xFFFFFFFF:
            raise SystemExit("unknown segment length is not supported by this script")
        yield number, flags & 63, page, header, data[at:at + length]
        at += length
        if flags & 63 == 51:
            return
    for number, kind, page, header, length in headers:
        yield number, kind, page, header, data[at:at + length]
        at += length


def split(data):
    """Returns (globals bytes, [(page number, width, height, page stream bytes)])."""
    if data[:8] == ID:
        flags = data[8]
        at = 13 if not flags & 2 else 9
        segs = list(segments(data, at, not flags & 1))
    else:
        segs = list(segments(data, 0, False))
    glob = b"".join(h + d for n, k, p, h, d in segs if p == 0 and k not in (51,))
    pages = []
    for n, k, p, h, d in segs:
        if k == 48:
            w, hgt = struct.unpack(">II", d[:8])
            pages.append((p, w, hgt))
    out = []
    for p, w, hgt in pages:
        stream = b"".join(hh + dd for n, k, pp, hh, dd in segs if pp == p)
        if hgt == 0xFFFFFFFF:
            hgt = 0
            for n, k, pp, hh, dd in segs:
                if pp == p and k == 50:
                    hgt = max(hgt, struct.unpack(">I", dd[:4])[0] + 1)
        out.append((p, w, hgt, stream))
    return glob, out


def pdf(width, height, stream, glob):
    objects = []
    objects.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objects.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objects.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>" % (width, height))
    content = b"q %d 0 0 %d 0 0 cm /Im0 Do Q" % (width, height)
    objects.append(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    parms = b" /DecodeParms << /JBIG2Globals 6 0 R >>" if glob else b""
    objects.append(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode%s /Length %d >>\nstream\n" % (width, height, parms, len(stream)) + stream + b"\nendstream")
    if glob:
        objects.append(b"<< /Length %d >>\nstream\n" % len(glob) + glob + b"\nendstream")
    out = bytearray(b"%PDF-1.5\n")
    offsets = []
    for i, body in enumerate(objects):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % (i + 1) + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for o in offsets:
        out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    return bytes(out)


def decode(width, height, stream, glob):
    doc = pdfium.PdfDocument(pdf(width, height, stream, glob))
    page = doc[0]
    for obj in page.get_objects():
        if obj.type == raw.FPDF_PAGEOBJ_IMAGE:
            image = obj.get_bitmap(render=False).to_pil().convert("L")
            return image
    raise SystemExit("PDFium found no image")


def main():
    args = sys.argv[1:]
    glob = b""
    if "--globals" in args:
        i = args.index("--globals")
        glob = open(args[i + 1], "rb").read()
        del args[i:i + 2]
    data = open(args[0], "rb").read()
    prefix = args[1]
    if data[:8] == ID or not glob:
        file_glob, pages = split(data)
        glob = glob or file_glob
    else:
        _, pages = split(data)
    for index, (p, w, h, stream) in enumerate(pages):
        image = decode(w, h, stream, glob)
        with open("%s.%d.pbm" % (prefix, index), "wb") as f:
            f.write(b"P4\n%d %d\n" % image.size)
            f.write(image.point(lambda v: 255 if v < 128 else 0).convert("1").tobytes())


if __name__ == "__main__":
    main()
