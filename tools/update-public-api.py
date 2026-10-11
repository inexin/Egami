"""Brings PublicAPI.Unshipped.txt in line with the code (PublicAPI.Shipped.txt is the frozen 1.0 API; edit it by hand only at a release): adds every symbol the public API analyzer reports as undeclared (RS0016)
and removes every declared one it cannot find (RS0017), such as a renamed or internalized member.

Usage: python tools/update-public-api.py [src/Egami.Drawing]   (from the repo root; src/Egami by default)
"""
import os
import re
import subprocess
import sys

FOLDER = sys.argv[1].rstrip("/\\") if len(sys.argv) > 1 else "src/Egami"
PROJECT = f"{FOLDER}/{os.path.basename(FOLDER)}.csproj"
API_FILE = f"{FOLDER}/PublicAPI.Unshipped.txt"

out = subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "--no-incremental"], capture_output=True, text=True).stdout
symbols = set(re.findall(r"error RS0016: Symbol '(.+?)' is not part of the declared public API", out))
removed = set(re.findall(r"error RS0017: Symbol '(.+?)' is part of the declared API, but is either not public or could not be found", out))

with open(API_FILE, encoding="utf-8") as f:
    lines = [line.rstrip("\n") for line in f if line.strip()]
header = [line for line in lines if line.startswith("#")]
entries = (set(line for line in lines if not line.startswith("#")) - removed) | symbols

with open(API_FILE, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(header + sorted(entries, key=str.lower)) + "\n")
print(f"Added {len(symbols)} symbols, removed {len(removed)}.")
