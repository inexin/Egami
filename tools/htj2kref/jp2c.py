"""Copies the first contiguous codestream box (jp2c) of a JP2 or JPH file to a raw codestream file: jp2c.py <in> <out>."""
import struct
import sys


def main(source, target):
    with open(source, "rb") as f:
        data = f.read()
    at = 0
    while at + 8 <= len(data):
        length, kind = struct.unpack(">I4s", data[at:at + 8])
        head = 8
        if length == 1:
            length = struct.unpack(">Q", data[at + 8:at + 16])[0]
            head = 16
        elif length == 0:
            length = len(data) - at
        if kind == b"jp2c":
            with open(target, "wb") as f:
                f.write(data[at + head:at + length])
            return
        at += length
    raise SystemExit(f"{source} has no jp2c box")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
