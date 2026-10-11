"""Times PDFium's and jbig2dec's JBIG2 decoding of the same pages Egami's compare benchmark decodes: run in the
egami-codecref container (tools/time-jbig2-native.sh). For each file: the first page as an embedded stream with its
globals, decoded through a one-image PDF by PDFium (pypdfium2, render=False, which decodes the image and nothing else) and
by jbig2dec (the standalone file, first page); prints the median of the given runs in milliseconds.

Usage: python3 time_jbig2.py <runs> <file.jb2>...
"""
import os
import statistics
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import pypdfium2 as pdfium  # noqa: E402
import pypdfium2.raw as raw  # noqa: E402

from pdfium_jbig2 import pdf, split  # noqa: E402


def pdfium_ms(width, height, stream, glob, runs):
    document = pdfium.PdfDocument(pdf(width, height, stream, glob))
    page = document[0]
    image = next(o for o in page.get_objects() if o.type == raw.FPDF_PAGEOBJ_IMAGE)
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        image.get_bitmap(render=False)
        times.append((time.perf_counter() - start) * 1000)
    return statistics.median(times)


def jbig2dec_ms(path, runs):
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        subprocess.run(["jbig2dec", "-t", "pbm", "-o", "/dev/null", path], capture_output=True)
        times.append((time.perf_counter() - start) * 1000)
    return statistics.median(times)


def main():
    runs = int(sys.argv[1])
    for path in sys.argv[2:]:
        data = open(path, "rb").read()
        glob, pages = split(data)
        _, width, height, stream = pages[0]
        print(f"{os.path.basename(path)}\t{width}x{height}\tPDFium {pdfium_ms(width, height, stream, glob, runs):.2f} ms"
              f"\tjbig2dec (process, all pages) {jbig2dec_ms(path, max(3, runs // 4)):.2f} ms")


if __name__ == "__main__":
    main()
