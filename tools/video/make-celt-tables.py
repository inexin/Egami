"""Generates the CELT decoder's constant tables from libopus 1.5.2's sources (BSD-3; see THIRD-PARTY-NOTICES), the float build
without custom modes:

  src/Egami.Video/Audio/Codecs/Opus/Celt/CeltTables.Data.cs   the static 48 kHz mode of celt/static_modes_float.h (window,
                                                              logN, pulse cache, FFT twiddles and bit-reverse tables, MDCT
                                                              trig), the band edges and allocation vectors of celt/modes.c,
                                                              the PVQ codeword counts of celt/cwrs.c and the coarse-energy
                                                              model and band means of celt/quant_bands.c.

Usage, from the repo root: python tools/video/make-celt-tables.py [path to the libopus 1.5.2 checkout]
(default .cache/audio/opus: git clone --depth 1 --branch v1.5.2 https://github.com/xiph/opus .cache/audio/opus).
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OPUS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, ".cache", "audio", "opus")
OUT = os.path.join(ROOT, "src", "Egami.Video", "Audio", "Codecs", "Opus", "Celt", "CeltTables.Data.cs")

# (C# name, file, libopus name, C# element type, flatten complex pairs)
TABLES = [
    ("Window120", "celt/static_modes_float.h", "window120", "float"),
    ("LogN400", "celt/static_modes_float.h", "logN400", "short"),
    ("CacheIndex50", "celt/static_modes_float.h", "cache_index50", "short"),
    ("CacheBits50", "celt/static_modes_float.h", "cache_bits50", "byte"),
    ("CacheCaps50", "celt/static_modes_float.h", "cache_caps50", "byte"),
    ("FftTwiddles48000_960", "celt/static_modes_float.h", "fft_twiddles48000_960", "float"),
    ("FftBitrev480", "celt/static_modes_float.h", "fft_bitrev480", "short"),
    ("FftBitrev240", "celt/static_modes_float.h", "fft_bitrev240", "short"),
    ("FftBitrev120", "celt/static_modes_float.h", "fft_bitrev120", "short"),
    ("FftBitrev60", "celt/static_modes_float.h", "fft_bitrev60", "short"),
    ("MdctTwiddles960", "celt/static_modes_float.h", "mdct_twiddles960", "float"),
    ("EBand5ms", "celt/modes.c", "eband5ms", "short"),
    ("BandAllocation", "celt/modes.c", "band_allocation", "byte"),
    ("PvqUData", "celt/cwrs.c", "CELT_PVQ_U_DATA", "uint"),
    ("EProbModel", "celt/quant_bands.c", "e_prob_model", "byte"),
    ("EMeans", "celt/quant_bands.c", "eMeans", "float"),
]


def preprocess(text):
    """Keeps the lines of the float, non-custom, non-small-footprint build."""
    defined = set()
    out, stack = [], []
    for line in text.split("\n"):
        s = line.strip()
        m = re.match(r"#\s*(ifdef|ifndef|if|elif|else|endif)\b(.*)", s)
        if not m:
            if all(stack):
                out.append(line)
            continue
        kind, cond = m.group(1), m.group(2).strip()
        if kind == "ifdef":
            stack.append(cond in defined)
        elif kind == "ifndef":
            stack.append(cond not in defined)
        elif kind == "if":
            stack.append(evaluate(cond, defined))
        elif kind == "else":
            stack[-1] = not stack[-1]
        elif kind == "endif":
            stack.pop()
        else:
            raise ValueError("#elif not handled")
    return "\n".join(out)


def evaluate(cond, defined):
    cond = re.sub(r"defined\s*\(\s*(\w+)\s*\)", lambda m: str(m.group(1) in defined), cond)
    cond = cond.replace("&&", " and ").replace("||", " or ").replace("!", " not ")
    cond = re.sub(r"\b(?!True\b|False\b|and\b|or\b|not\b)[A-Za-z_]\w*", "0", cond)
    return bool(eval(cond, {}, {}))


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def initializer(text, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*(\[[^=\n]*)?=\s*\{", text)
    if not match:
        raise KeyError(name)
    start = match.end() - 1
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    raise ValueError(name)


def values(body, kind):
    tokens = re.findall(r"[-+]?[0-9][0-9.eE+-]*f?|[-+]?\.[0-9][0-9eE+-]*f?", body)
    if kind == "float":
        out = []
        for t in tokens:
            t = t.rstrip("f")
            if "." not in t and "e" not in t.lower():
                t += ".0"
            out.append(t + "f")
        return out
    return [str(int(t)) for t in tokens]


def wrap(items, indent, width=128):
    lines, line = [], indent
    for i, v in enumerate(items):
        piece = v + ("," if i < len(items) - 1 else "")
        if len(line) + len(piece) + 1 > width:
            lines.append(line.rstrip())
            line = indent
        line += piece + " "
    lines.append(line.rstrip())
    return "\n".join(lines)


HEADER = """// <auto-generated>
// Generated by tools/video/make-celt-tables.py from libopus 1.5.2 (BSD-3; see THIRD-PARTY-NOTICES).
// Do not edit; run the script again.
// </auto-generated>
#nullable enable

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>Numeric tables of libopus's float CELT decoder for the static 48 kHz mode, flattened in array order.</summary>
internal static partial class CeltTables
{
"""


def main():
    cache = {}
    parts = []
    for cs_name, file, c_name, kind in TABLES:
        if file not in cache:
            with open(os.path.join(OPUS, file)) as f:
                cache[file] = strip_comments(preprocess(f.read()))
        items = values(initializer(cache[file], c_name), kind)
        parts.append(f"    /// <summary>libopus's {c_name}[{len(items)}] ({file}).</summary>\n"
                     f"    internal static ReadOnlySpan<{kind}> {cs_name} =>\n    [\n{wrap(items, '        ')}\n    ];\n")
    rows = re.findall(r"CELT_PVQ_U_DATA\s*\+\s*(\d+)", initializer(cache["celt/cwrs.c"], "CELT_PVQ_U_ROW"))
    parts.append("    /// <summary>The row offsets of libopus's CELT_PVQ_U_ROW[15] into PvqUData (celt/cwrs.c).</summary>\n"
                 f"    internal static ReadOnlySpan<short> PvqURow =>\n    [\n{wrap(rows, '        ')}\n    ];\n")
    with open(OUT, "w", newline="\n") as f:
        f.write(HEADER + "\n".join(parts) + "}\n")
    print("wrote", OUT)


if __name__ == "__main__":
    main()
