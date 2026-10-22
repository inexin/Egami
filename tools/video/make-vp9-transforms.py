"""Translates libvpx 1.14.1's one-dimensional VP9 inverse transforms (vpx_dsp/inv_txfm.c, BSD-3; see THIRD-PARTY-NOTICES) into
C#, line by line, from .cache/libvpx-1.14.1 (python tools/video/make-vp9-tables.py clones it).

  src/Egami.Video/Codecs/Vp9/Vp9Transform.OneD.cs   the high bit depth versions of idct4, iadst4, idct8, iadst8, idct16, iadst16
                                                    and idct32, which hold intermediates in 32 bits and products in 64. For valid
                                                    streams every intermediate fits 16 bits at 8 bit (libvpx asserts it), so the
                                                    8-bit transforms give the same values. The non-normative check that zeroes
                                                    out-of-range high bit depth input is left out.

  src/Egami.Video/Codecs/Vp9/Vp9TransformSimd.OneD.cs
                                                    the same transforms on vectors of 32-bit lanes (Vector128 and Vector256
                                                    overloads), one column per lane, for 8-bit frames: products of 16-bit
                                                    intermediates and 14-bit constants fit 32 bits, so the values are the same.

Usage, from the repo root: python tools/video/make-vp9-transforms.py
"""
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE = os.path.join(ROOT, ".cache", "libvpx-1.14.1", "vpx_dsp", "inv_txfm.c")
OUT = os.path.join(ROOT, "src", "Egami.Video", "Codecs", "Vp9", "Vp9Transform.OneD.cs")
SIMD_OUT = os.path.join(ROOT, "src", "Egami.Video", "Codecs", "Vp9", "Vp9TransformSimd.OneD.cs")

FUNCTIONS = [
    ("vpx_highbd_idct4_c", "Idct4"),
    ("vpx_highbd_iadst4_c", "Iadst4"),
    ("vpx_highbd_idct8_c", "Idct8"),
    ("vpx_highbd_iadst8_c", "Iadst8"),
    ("vpx_highbd_idct16_c", "Idct16"),
    ("vpx_highbd_iadst16_c", "Iadst16"),
    ("highbd_idct32_c", "Idct32"),
]
NAMES = dict(FUNCTIONS)


def body_of(text, name):
    match = re.search(r"^(?:static )?void " + name + r"\(const tran_low_t \*input, tran_low_t \*output,\s*int bd\) \{", text, flags=re.M)
    start = match.end()
    depth = 1
    i = start
    while depth:
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    return text[start:i - 1]


def unwrap(text, macro, replacement, drop_last_argument):
    """Replaces macro(args) by replacement(args), dropping the macro's last argument when asked."""
    out = []
    i = 0
    while True:
        j = text.find(macro + "(", i)
        if j < 0:
            out.append(text[i:])
            return "".join(out)
        out.append(text[i:j])
        k = j + len(macro) + 1
        depth = 1
        last_comma = -1
        while depth:
            c = text[k]
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            elif c == "," and depth == 1:
                last_comma = k
            k += 1
        inner = text[j + len(macro) + 1:k - 1]
        if drop_last_argument:
            inner = text[j + len(macro) + 1:last_comma]
        out.append(replacement + "(" + unwrap(inner, macro, replacement, drop_last_argument) + ")")
        i = k


def translate(body):
    body = re.sub(r"/\*.*?\*/", "", body, flags=re.S)
    body = re.sub(r"//[^\n]*", "", body)
    body = re.sub(r"\n\s*\(void\)bd;", "", body)
    body = re.sub(r"\n\s*if \(detect_invalid_highbd_input\(input, \d+\)\) \{.*?\n  \}", "", body, flags=re.S)
    body = unwrap(body, "HIGHBD_WRAPLOW", "(int)", True)
    body = body.replace("dct_const_round_shift(", "Rs(")
    body = body.replace("(tran_high_t)", "(long)")
    body = re.sub(r"cospi_(\d+)_64", r"C\1", body)
    body = re.sub(r"sinpi_(\d)_9", r"S\1", body)
    body = re.sub(r"memset\(output, 0, (?:(\d+) \* sizeof\(\*output\)|sizeof\(\*output\) \* (\d+))\);",
                  lambda m: f"output[..{m.group(1) or m.group(2)}].Clear();", body)
    body = re.sub(r"if \(!\(([^;{]*?)\)\) \{", r"if ((\1) == 0) {", body, flags=re.S)
    body = re.sub(r"tran_low_t (\w+)\[(\d+)\], (\w+)\[(\d+)\];",
                  r"Span<int> \1 = stackalloc int[\2];\n  Span<int> \3 = stackalloc int[\4];", body)
    body = re.sub(r"tran_low_t (\w+)\[(\d+)\];", r"Span<int> \1 = stackalloc int[\2];", body)
    body = body.replace("tran_high_t ", "long ").replace("tran_low_t ", "int ")
    for c_name, cs_name in FUNCTIONS:
        body = re.sub(c_name + r"\((\w+), (\w+), bd\);", cs_name + r"(\1, \2);", body)
    # Join the statements libvpx's formatter wrapped, keep blank lines single, and reindent: libvpx uses two spaces per level,
    # this file four inside a method of a class, with opening braces on their own line.
    body = re.sub(r"=\s*\n\s+", "= ", body)
    body = re.sub(r"\|\s*\n\s+", "| ", body)
    body = re.sub(r"\n(\s*\n)+", "\n\n", body)
    lines = []
    for line in body.strip("\n").split("\n"):
        stripped = line.strip()
        if not stripped:
            if lines and lines[-1] != "" and not lines[-1].endswith("{"):
                lines.append("")
            continue
        indent = "    " * (((len(line) - len(line.lstrip(" "))) // 2) + 1)
        if stripped.endswith("{"):
            lines.append(indent + stripped[:-1].rstrip())
            lines.append(indent + "{")
        else:
            lines.append(indent + stripped)
    while lines and lines[-1] == "":
        lines.pop()
    return "\n".join(lines)


HEADER = """// <auto-generated>
// Generated by tools/video/make-vp9-transforms.py from libvpx 1.14.1 (BSD-3, Google patent grant; see THIRD-PARTY-NOTICES).
// Do not edit; run the script again.
// </auto-generated>
#nullable enable
"""


def vectorize(code, vector):
    """The scalar C# of one transform on vectors: every int and long becomes the vector type, the casts go."""
    code = code.replace("(long)(int)", "").replace("(long)", "").replace("(int)(", "(")
    code = re.sub(r"\blong (\w)", vector + r" \1", code)
    code = re.sub(r"\bint (x\d+ =)", vector + r" \1", code)
    code = code.replace("Span<int>", f"Span<{vector}>").replace("stackalloc int[", f"stackalloc {vector}[")
    code = re.sub(r"if \((\(.*?\)) == 0\)", lambda m: f"if ({m.group(1)} == {vector}.Zero)", code)
    return code


def main():
    text = open(SOURCE).read()
    out = [HEADER, "namespace Egami.Video.Codecs.Vp9;", "",
           "/// <summary>libvpx's one-dimensional inverse transforms (high bit depth versions), translated line by line.</summary>",
           "internal static partial class Vp9Transform", "{"]
    for c_name, cs_name in FUNCTIONS:
        out.append(f"    /// <summary>libvpx's {c_name}.</summary>")
        out.append(f"    internal static void {cs_name}(ReadOnlySpan<int> input, Span<int> output)")
        out.append("    {")
        out.append(translate(body_of(text, c_name)))
        out.append("    }")
        out.append("")
    out[-1] = "}"
    open(OUT, "w", newline="\n").write("\n".join(out) + "\n")
    print(f"{len(FUNCTIONS)} transforms written to {os.path.relpath(OUT, ROOT)}")

    simd = [HEADER, "using System.Runtime.Intrinsics;", "", "namespace Egami.Video.Codecs.Vp9;", "",
            "/// <summary>libvpx's one-dimensional inverse transforms on vectors, one column per lane (8-bit frames only).</summary>",
            "internal static partial class Vp9TransformSimd", "{"]
    for vector in ("Vector128<int>", "Vector256<int>"):
        for c_name, cs_name in FUNCTIONS:
            simd.append(f"    /// <summary>libvpx's {c_name}, a column per lane.</summary>")
            simd.append(f"    internal static void {cs_name}(ReadOnlySpan<{vector}> input, Span<{vector}> output)")
            simd.append("    {")
            simd.append(vectorize(translate(body_of(text, c_name)), vector))
            simd.append("    }")
            simd.append("")
    simd[-1] = "}"
    open(SIMD_OUT, "w", newline="\n").write("\n".join(simd) + "\n")
    print(f"{2 * len(FUNCTIONS)} vector transforms written to {os.path.relpath(SIMD_OUT, ROOT)}")


if __name__ == "__main__":
    main()
