"""Reads the ZXing blackbox test images with zxing-cpp and prints, per set, how many it reads as expected.

Run by tools/make-barcode-references.py (blackbox step) inside egami-barcoderef, with the corpus folder as argument. Each image
counts once, with the set's format only and zxing-cpp's default search (rotations included): the same rule as
Egami.Barcodes.Tests' BlackboxTests, so the numbers compare. The expected text is the image's .txt (an EAN or UPC add-on is not
part of it); UPC-A may come back as a GTIN-13 and UPC-E expanded to one.
"""
import json
import os
import sys

import zxingcpp
from PIL import Image

SETS = {
    "aztec": zxingcpp.BarcodeFormat.Aztec, "code128": zxingcpp.BarcodeFormat.Code128, "code39": zxingcpp.BarcodeFormat.Code39,
    "datamatrix": zxingcpp.BarcodeFormat.DataMatrix, "ean13": zxingcpp.BarcodeFormat.EAN13, "ean8": zxingcpp.BarcodeFormat.EAN8,
    "itf": zxingcpp.BarcodeFormat.ITF, "pdf417": zxingcpp.BarcodeFormat.PDF417, "qrcode": zxingcpp.BarcodeFormat.QRCode,
    "upca": zxingcpp.BarcodeFormat.UPCA, "upce": zxingcpp.BarcodeFormat.UPCE, "upcean": zxingcpp.BarcodeFormat.EAN13,
}


def upce_to_gtin(digits):
    """The GTIN-13 a UPC-E number (number system, six digits, check digit) stands for."""
    system, d, check = digits[0], digits[1:7], digits[7]
    if d[5] in "012":
        body = d[:2] + d[5] + "0000" + d[2:5]
    elif d[5] == "3":
        body = d[:3] + "00000" + d[3:5]
    elif d[5] == "4":
        body = d[:4] + "00000" + d[4]
    else:
        body = d[:5] + "0000" + d[5]
    return "0" + system + body + check


def main():
    root = sys.argv[1]
    summary = {}
    for folder in sorted(os.listdir(root)):
        kind = folder.split("-")[0]
        if kind not in SETS:
            continue
        passed = total = 0
        path = os.path.join(root, folder)
        for file in sorted(os.listdir(path)):
            stem, extension = os.path.splitext(file)
            if extension.lower() not in (".png", ".jpg", ".jpeg", ".gif") or not os.path.exists(os.path.join(path, stem + ".txt")):
                continue
            with open(os.path.join(path, stem + ".txt"), encoding="utf-8") as f:
                expected = f.read()
            total += 1
            formats = [SETS[kind]] + ([zxingcpp.BarcodeFormat.UPCA] if kind in ("ean13", "upcean") else [])
            results = zxingcpp.read_barcodes(Image.open(os.path.join(path, file)).convert("L"), formats=formats)
            accepted = {expected, "0" + expected} | ({upce_to_gtin(expected)} if kind == "upce" and len(expected) == 8 else set())
            if any(r.text in accepted for r in results):
                passed += 1
        summary[folder] = [passed, total]
    print(json.dumps(summary, indent=1, sort_keys=True))


if __name__ == "__main__":
    main()
