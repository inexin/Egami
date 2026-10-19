"""Reads every PNG in a folder with zxing-cpp and prints one JSON object per image: name, format and text.

Run by tools/make-barcode-references.py inside egami-barcoderef. Add-ons of EAN and UPC are read too and joined to the main
digits with a space.
"""
import json
import os
import sys

import zxingcpp
from PIL import Image


def main():
    folder = sys.argv[1]
    for file in sorted(os.listdir(folder)):
        if not file.endswith(".png"):
            continue
        image = Image.open(os.path.join(folder, file)).convert("L")
        results = zxingcpp.read_barcodes(image, ean_add_on_symbol=zxingcpp.EanAddOnSymbol.Read, try_rotate=False)
        entry = {"name": file[:-4], "format": None, "text": None}
        if results:
            result = results[0]
            # GS1 data in its human-readable form with AIs in parentheses; anything else as the bytes it holds (ISO 8859-1).
            text = result.text if result.content_type == zxingcpp.ContentType.GS1 else bytes(result.bytes).decode("latin-1")
            if result.format in (zxingcpp.BarcodeFormat.EAN13, zxingcpp.BarcodeFormat.EAN8, zxingcpp.BarcodeFormat.UPCA,
                                 zxingcpp.BarcodeFormat.UPCE) and len(text) > {"EAN8": 8, "UPCE": 8, "UPCA": 12}.get(result.format.name, 13):
                main_length = {"EAN8": 8, "UPCE": 8, "UPCA": 12}.get(result.format.name, 13)
                text = text[:main_length] + " " + text[main_length:]
            entry = {"name": file[:-4], "format": result.format.name, "text": text}
        print(json.dumps(entry))


if __name__ == "__main__":
    main()
