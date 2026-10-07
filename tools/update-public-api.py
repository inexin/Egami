"""Adds every symbol the public API analyzer reports as undeclared (RS0016) to PublicAPI.Unshipped.txt.

Usage: python tools/update-public-api.py   (from the repo root)
"""
import re
import subprocess

PROJECT = "src/Egami/Egami.csproj"
API_FILE = "src/Egami/PublicAPI.Unshipped.txt"

out = subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "--no-incremental"], capture_output=True, text=True).stdout
symbols = set(re.findall(r"error RS0016: Symbol '(.+?)' is not part of the declared public API", out))

with open(API_FILE, encoding="utf-8") as f:
    lines = [line.rstrip("\n") for line in f if line.strip()]
header = [line for line in lines if line.startswith("#")]
entries = set(line for line in lines if not line.startswith("#")) | symbols

with open(API_FILE, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(header + sorted(entries, key=str.lower)) + "\n")
print(f"Added {len(symbols)} symbols.")
