"""Generates the AV1 decoder's constant tables from libaom 3.15.2's sources (BSD 2-clause, AOM Patent License 1.0; see
THIRD-PARTY-NOTICES), in .cache/avif/aom-3.15.2 (python tools/make-avif-references.py fetch clones it).

  src/Egami/Formats/Av1/Av1DefaultCdfs.cs   every default CDF of av1/common/entropymode.c and token_cdfs.h, each table a list
                                            of CDFs in libaom's array order, each CDF as libaom stores it (AOM_ICDF): 32768
                                            minus the cumulative probabilities (times 32768) of all but the last symbol, then 0,
                                            then the adaptation counter (0)
  src/Egami/Formats/Av1/Av1Tables.Data.cs   the numeric tables named in NUMERIC below (scan orders, quantizer lookups and so on),
                                            flattened in array order
  src/Egami/Formats/Av1/Av1InverseTransform.OneD.cs
                                            av1/common/av1_inv_txfm1d.c's one-dimensional inverse transforms, translated
                                            line by line, each stage clamped to the one range the decoder uses
  src/Egami/Formats/Av1/Av1InverseTransform.Vector.cs
                                            the same DCTs of 8 to 64 points and ADSTs of 8 and 16 on eight transforms at
                                            once, a lane each
  src/Egami/Formats/Av1/Av1ForwardTransform.OneD.cs
                                            av1/encoder/av1_fwd_txfm1d.c's one-dimensional forward transforms, for the
                                            encoder, translated line by line

Usage, from the repo root: python tools/make-av1-tables.py
"""
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AOM = os.path.join(ROOT, ".cache", "avif", "aom-3.15.2")
OUT = os.path.join(ROOT, "src", "Egami", "Formats", "Av1")

def pascal(name):
    name = re.sub(r"^(av1_)?default_", "", name)
    name = re.sub(r"_cdfs?$", "", name)
    return "".join(part[:1].upper() + part[1:] for part in name.split("_"))


# (C# name, file, libaom array name, C# element type). Enum names (BLOCK_*, TX_*, ...) become their values.
CD = "av1/common/common_data.h"
NUMERIC = [
    ("MiSizeWideLog2", CD, "mi_size_wide_log2", "byte"),
    ("MiSizeHighLog2", CD, "mi_size_high_log2", "byte"),
    ("MiSizeWide", CD, "mi_size_wide", "byte"),
    ("MiSizeHigh", CD, "mi_size_high", "byte"),
    ("BlockSizeWide", CD, "block_size_wide", "byte"),
    ("BlockSizeHigh", CD, "block_size_high", "byte"),
    ("SizeGroupLookup", CD, "size_group_lookup", "byte"),
    ("NumPelsLog2Lookup", CD, "num_pels_log2_lookup", "byte"),
    ("SubsizeLookup", CD, "subsize_lookup", "byte"),
    ("MaxTxSizeLookup", CD, "max_txsize_lookup", "byte"),
    ("MaxTxSizeRectLookup", CD, "max_txsize_rect_lookup", "byte"),
    ("VtxTab", CD, "vtx_tab", "byte"),
    ("HtxTab", CD, "htx_tab", "byte"),
    ("SubTxSizeMap", CD, "sub_tx_size_map", "byte"),
    ("TxSizeWide", CD, "tx_size_wide", "short"),
    ("TxSizeHigh", CD, "tx_size_high", "short"),
    ("TxSizeWideUnit", CD, "tx_size_wide_unit", "byte"),
    ("TxSizeHighUnit", CD, "tx_size_high_unit", "byte"),
    ("TxSizeWideLog2", CD, "tx_size_wide_log2", "byte"),
    ("TxSizeHighLog2", CD, "tx_size_high_log2", "byte"),
    ("TxSizeToBlockSize", CD, "txsize_to_bsize", "byte"),
    ("TxSizeSqrMap", CD, "txsize_sqr_map", "byte"),
    ("TxSizeSqrUpMap", CD, "txsize_sqr_up_map", "byte"),
    ("TxSizeLog2Minus4", CD, "txsize_log2_minus4", "sbyte"),
    ("IntraModeContext", CD, "intra_mode_context", "byte"),
    ("SsSizeLookup", "av1/common/common_data.c", "av1_ss_size_lookup", "byte"),
    ("EobGroupStart", "av1/common/txb_common.c", "av1_eob_group_start", "short"),
    ("EobOffsetBits", "av1/common/txb_common.c", "av1_eob_offset_bits", "short"),
    ("DcQLookup8", "av1/common/quant_common.c", "dc_qlookup_QTX", "short"),
    ("DcQLookup10", "av1/common/quant_common.c", "dc_qlookup_10_QTX", "short"),
    ("DcQLookup12", "av1/common/quant_common.c", "dc_qlookup_12_QTX", "short"),
    ("AcQLookup8", "av1/common/quant_common.c", "ac_qlookup_QTX", "short"),
    ("AcQLookup10", "av1/common/quant_common.c", "ac_qlookup_10_QTX", "short"),
    ("AcQLookup12", "av1/common/quant_common.c", "ac_qlookup_12_QTX", "short"),
    ("InverseQuantizerMatrices", "av1/common/quant_common.c", "iwt_matrix_ref", "byte"),
] + [
    (f"NzMapCtxOffset{size}", "av1/common/txb_common.c", f"av1_nz_map_ctx_offset_{size}", "sbyte")
    for size in ("4x4", "8x8", "16x16", "32x32", "4x8", "8x16", "16x32", "32x16", "32x64", "64x32", "4x16", "16x4", "8x32", "32x8")
] + [
    (f"{kind.capitalize()}Scan{size}", "av1/common/scan.c", f"{kind}_scan_{size}", "short")
    for size in ("4x4", "8x8", "16x16", "32x32", "4x8", "8x4", "8x16", "16x8", "16x32", "32x16", "4x16", "16x4", "8x32", "32x8")
    for kind in ("default", "mrow", "mcol")
] + [
    ("SmoothWeights", "aom_dsp/intrapred_common.h", "smooth_weights", "byte"),
    ("DrIntraDerivative", "av1/common/reconintra.h", "dr_intra_derivative", "short"),
    ("FilterIntraTaps", "av1/common/reconintra.c", "av1_filter_intra_taps", "sbyte"),
    ("CospiData", "av1/common/av1_txfm.c", "av1_cospi_arr_data", "int"),
    ("SinpiData", "av1/common/av1_txfm.c", "av1_sinpi_arr_data", "int"),
    ("ResizeFilterNormative", "av1/common/resize.c", "av1_resize_filter_normative", "short"),
    ("XByXPlus1", "av1/common/restoration.c", "av1_x_by_xplus1", "short"),
    ("OneByX", "av1/common/restoration.c", "av1_one_by_x", "short"),
    ("GaussianSequence", "av1/decoder/grain_synthesis.c", "gaussian_sequence", "short"),
] + [
    (pascal(f"has_{kind}_{size}"), "av1/common/reconintra.c", f"has_{kind}_{size}", "byte")
    for kind in ("tr", "bl")
    for size in ("4x4", "4x8", "8x4", "8x8", "8x16", "16x8", "16x16", "16x32", "32x16", "32x32", "32x64", "64x32", "64x64",
                 "64x128", "128x64", "128x128", "4x16", "16x4", "8x32", "32x8", "16x64", "64x16",
                 "vert_8x8", "vert_16x16", "vert_32x32", "vert_64x64")
]

# Tables of other tables (reconintra.c's has_tr_tables and the like), after the tables they name.
POINTERS = [
    ("HasTrTables", "av1/common/reconintra.c", "has_tr_tables"),
    ("HasTrVertTables", "av1/common/reconintra.c", "has_tr_vert_tables"),
    ("HasBlTables", "av1/common/reconintra.c", "has_bl_tables"),
    ("HasBlVertTables", "av1/common/reconintra.c", "has_bl_vert_tables"),
]


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def initializer(text, name):
    """Returns the declaration (up to '=') and the brace-enclosed initializer of the array called name."""
    match = re.search(r"\b" + re.escape(name) + r"\s*(\[[^=]*)=\s*\{", text)
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
                return match.group(1).strip(), text[start:i + 1]
    raise ValueError(name)


def cdf_tables():
    tables = []
    for file in ("av1/common/entropymode.c", "av1/common/token_cdfs.h"):
        text = strip_comments(open(os.path.join(AOM, file)).read())
        for match in re.finditer(r"static const aom_cdf_prob\s+(\w+)", text):
            name = match.group(1)
            dims, body = initializer(text, name)
            cdfs = []
            for cdf in re.finditer(r"AOM_CDF(\d+)\s*\(([^)]*)\)", body):
                count = int(cdf.group(1))
                values = [int(eval(v.strip(), {}, {})) for v in cdf.group(2).split(",") if v.strip()]
                if len(values) != count - 1:
                    raise ValueError(f"{name}: AOM_CDF{count} with {len(values)} values")
                cdfs.append([32768 - v for v in values] + [0, 0])
            tables.append((pascal(name), name, " ".join(dims.split()), cdfs))
    return tables


ENUMS = {}


def load_enums():
    """Reads the enum constants of libaom's enums.h and txfm_common.h (BLOCK_SIZE, TX_SIZE, TX_TYPE and the rest)."""
    for file in ("av1/common/enums.h", "aom_dsp/txfm_common.h"):
        text = strip_comments(open(os.path.join(AOM, file)).read())
        for body in re.findall(r"enum[^{;]*\{([^}]*)\}", text):
            value = -1
            for entry in body.split(","):
                entry = entry.strip()
                if not entry:
                    continue
                name, _, expr = entry.partition("=")
                name = name.strip()
                if expr.strip():
                    try:
                        value = int(eval(re.sub(r"[A-Za-z_]\w*", lambda m: str(ENUMS.get(m.group(0), 0)), expr), {}, {}))
                    except Exception:
                        continue
                else:
                    value += 1
                ENUMS[name] = value


def numeric(file, name):
    text = strip_comments(open(os.path.join(AOM, file)).read())
    # Keep the first branch of a preprocessor conditional inside an initializer (resize.c's filter table).
    text = re.sub(r"#else.*?#endif", "", text, flags=re.S)
    text = re.sub(r"^#if.*$", "", text, flags=re.M)
    dims, body = initializer(text, name)
    values = []
    for token in re.findall(r"(?:-\s*)?(?:0x[0-9a-fA-F]+|\d+|[A-Za-z_]\w*)", body):
        token = "".join(token.split())
        sign = -1 if token.startswith("-") else 1
        token = token.lstrip("-")
        values.append(sign * (int(token, 0) if token[0].isdigit() else ENUMS[token]))
    return " ".join(dims.split()), values


def scan_orders():
    """libaom's av1_scan_orders[TX_SIZES_ALL][TX_TYPES]: the scan array each pair names, as C# names."""
    text = strip_comments(open(os.path.join(AOM, "av1/common/scan.c")).read())
    _, body = initializer(text, "av1_scan_orders")
    names = re.findall(r"\{\s*(\w+)\s*,\s*\w+\s*\}", body)
    return ["".join(part.capitalize() for part in n.split("_")[:-1]) + n.split("_")[-1] for n in names]


def wrap(values, indent, width=128):
    lines, line = [], indent
    for i, v in enumerate(values):
        piece = str(v) + ("," if i < len(values) - 1 else "")
        if len(line) + len(piece) + 1 > width:
            lines.append(line.rstrip())
            line = indent
        line += piece + " "
    lines.append(line.rstrip())
    return "\n".join(lines)


HEADER = """// <auto-generated>
// Generated by tools/make-av1-tables.py from libaom 3.15.2 (BSD 2-clause, AOM Patent License 1.0; see THIRD-PARTY-NOTICES).
// Do not edit; run the script again.
// </auto-generated>
#nullable enable
"""


def main():
    out = [HEADER, "namespace Egami.Formats.Av1;", "",
           "/// <summary>The default CDFs of the AV1 specification, as libaom's entropymode.c and token_cdfs.h hold them (inverse form).</summary>",
           "internal static class Av1DefaultCdfs", "{"]
    for csharp, name, dims, cdfs in cdf_tables():
        out.append(f"    /// <summary>libaom's {name}{dims}: {len(cdfs)} CDFs.</summary>")
        out.append(f"    internal static readonly ushort[][] {csharp} =")
        out.append("    [")
        for cdf in cdfs:
            out.append(f"        [{', '.join(map(str, cdf))}],")
        out.append("    ];")
        out.append("")
    out[-1] = "}"
    open(os.path.join(OUT, "Av1DefaultCdfs.cs"), "w", newline="\n").write("\n".join(out) + "\n")

    load_enums()
    out = [HEADER, "namespace Egami.Formats.Av1;", "", "/// <summary>Numeric tables of libaom, flattened in array order.</summary>",
           "internal static partial class Av1Tables", "{"]
    for csharp, file, name, kind in NUMERIC:
        dims, values = numeric(file, name)
        dims = dims.replace("<", "&lt;").replace(">", "&gt;")
        out.append(f"    /// <summary>libaom's {name}{dims} ({file}).</summary>")
        out.append(f"    internal static readonly {kind}[] {csharp} =")
        out.append("    [")
        out.append(wrap(values, "        "))
        out.append("    ];")
        out.append("")

    for csharp, file, name in POINTERS:
        text = strip_comments(open(os.path.join(AOM, file)).read())
        dims, body = initializer(text, name)
        names = ["null" if n == "NULL" else pascal(n) for n in re.findall(r"\w+", body)]
        out.append(f"    /// <summary>libaom's {name}{' '.join(dims.split())} ({file}).</summary>")
        out.append(f"    internal static readonly byte[]?[] {csharp} =")
        out.append("    [")
        out.append(wrap(names, "        "))
        out.append("    ];")
        out.append("")

    # After the scans it names: static fields initialize in textual order.
    out.append("    /// <summary>libaom's av1_scan_orders[TX_SIZES_ALL][TX_TYPES]: the scan of each transform size and type.</summary>")
    out.append("    internal static readonly short[][] ScanOrders =")
    out.append("    [")
    out.append(wrap(scan_orders(), "        "))
    out.append("    ];")
    out.append("}")
    open(os.path.join(OUT, "Av1Tables.Data.cs"), "w", newline="\n").write("\n".join(out) + "\n")



TRANSFORM_NAMES = {
    "av1_idct4": "Dct4", "av1_idct8": "Dct8", "av1_idct16": "Dct16", "av1_idct32": "Dct32", "av1_idct64": "Dct64",
    "av1_iadst4": "Adst4", "av1_iadst8": "Adst8", "av1_iadst16": "Adst16",
    "av1_iidentity4_c": "Identity4", "av1_iidentity8_c": "Identity8", "av1_iidentity16_c": "Identity16",
    "av1_iidentity32_c": "Identity32",
}

TRANSFORM_HEADER = """// <auto-generated>
// Translated by tools/make-av1-tables.py from libaom 3.15.2's av1/common/av1_inv_txfm1d.c (BSD 2-clause, AOM Patent License
// 1.0; see THIRD-PARTY-NOTICES): the one-dimensional inverse transforms, every stage clamped to the one range the decoder uses.
// Do not edit; run the script again.
// </auto-generated>
namespace Egami.Formats.Av1;

/// <summary>The one-dimensional inverse transforms of libaom's av1_inv_txfm1d.c.</summary>
internal static partial class Av1InverseTransform
{
"""


def translate_line(name, s):
    s = s.replace("const int32_t *cospi = cospi_arr(cos_bit);", "ReadOnlySpan<int> cospi = Cospi;")
    s = s.replace("const int32_t *sinpi = sinpi_arr(bit);", "ReadOnlySpan<int> sinpi = Sinpi;")
    s = s.replace("int bit = cos_bit;", "const int bit = CosBit;")
    s = s.replace("int32_t *bf0, *bf1;", "scoped Span<int> bf0, bf1;")
    s = re.sub(r"int32_t step\[(\d+)\];", r"Span<int> step = stackalloc int[\1];", s)
    s = re.sub(r"clamp_value\((.*), stage_range\[stage\]\)", r"Clamp(\1, range)", s)
    s = re.sub(r"range_check_value64\((.*), stage_range\[\d\]( \+ bit)?\)", r"\1", s)
    s = re.sub(r"if \(!\((.*)\)\) \{", r"if ((\1) == 0) {", s)
    s = s.replace("half_btf(", "HalfBtf(").replace("round_shift(", "RoundShift(").replace("cos_bit", "CosBit")
    s = s.replace("int64_t", "long").replace("int32_t", "int")
    if "stage_range" in s or "->" in s or "memcpy" in s or "!(" in s:
        raise ValueError(f"{name}: {s}")
    return s


VECTOR_HEADER = """// <auto-generated>
// Translated by tools/make-av1-tables.py from libaom 3.15.2's av1/common/av1_inv_txfm1d.c (BSD 2-clause, AOM Patent License
// 1.0; see THIRD-PARTY-NOTICES): the DCTs and ADSTs of Av1InverseTransform.OneD.cs on eight transforms at once, one in each lane.
// Do not edit; run the script again.
// </auto-generated>
using System.Runtime.Intrinsics;

namespace Egami.Formats.Av1;

/// <summary>The DCTs and ADSTs of libaom's av1_inv_txfm1d.c, eight at a time, with the butterfly <c>T</c> chooses for the bit depth.</summary>
internal static partial class Av1InverseTransform
{
"""

VECTOR_TRANSFORMS = ("av1_idct8", "av1_idct16", "av1_idct32", "av1_idct64", "av1_iadst8", "av1_iadst16")

# The transforms of the blocks four samples on a side, on Vector128 lanes at 8 bits (av1_iadst4 is written by hand).
NARROW_TRANSFORMS = ("av1_idct4", "av1_idct8", "av1_idct16", "av1_iadst8", "av1_iadst16")


def vector_lines(lines):
    """The lines of a scalar transform on Vector256 lanes: HalfBtf and Clamp have vector overloads taking the same arguments."""
    out = []
    for s in lines:
        s = s.replace("scoped Span<int> bf0, bf1;", "scoped Span<Vector256<int>> bf0, bf1;")
        s = re.sub(r"Span<int> step = stackalloc int\[(\d+)\];", r"Span<Vector256<int>> step = stackalloc Vector256<int>[\1];", s)
        s = s.replace(", range)", ", lo, hi)").replace("HalfBtf(", "T.HalfBtf(")
        if "range" in s or "long" in s or "RoundShift" in s:
            raise ValueError(f"not vectorized: {s}")
        out.append(s)
    return out


def translate_inverse_transforms():
    """Translates libaom's av1_inv_txfm1d.c into Av1InverseTransform.OneD.cs and its DCTs into Av1InverseTransform.Vector.cs."""
    text = strip_comments(open(os.path.join(AOM, "av1/common/av1_inv_txfm1d.c")).read())
    out = []
    vector = []
    pattern = r"void (\w+)\(const int32_t \*input, int32_t \*output, int8_t cos_bit,\s*const int8_t \*stage_range\) \{"
    for m in re.finditer(pattern, text):
        name = m.group(1)
        depth, i = 1, m.end()
        while depth:
            depth += {"{": 1, "}": -1}.get(text[i], 0)
            i += 1
        lines = []
        for line in text[m.end():i - 1].split("\n"):
            s = line.strip()
            if not s or s.startswith(("assert(", "(void)", "av1_range_check_buf(")) or s in ("stage++;", "int32_t stage = 0;"):
                continue
            if re.match(r"const int32_t size = \d+;", s):
                continue
            lines.append(translate_line(name, s))

        # Braces on their own lines, and braces around a loop's single statement.
        split = []
        k = 0
        while k < len(lines):
            s = lines[k]
            if s.endswith("{"):
                split += [s[:-1].rstrip(), "{"]
            elif s.startswith("for (") and s.endswith(")"):
                split += [s, "{", lines[k + 1], "}"]
                k += 1
            elif s.startswith("for (") and s.endswith(";"):
                head = s[:s.index(") ") + 1]
                split += [head, "{", s[len(head):].strip(), "}"]
            else:
                split.append(s)
            k += 1
        emit(out, f"    /// <summary>libaom's {name}.</summary>",
             f"    internal static void {TRANSFORM_NAMES[name]}(ReadOnlySpan<int> input, Span<int> output, int range)", split)
        if name in VECTOR_TRANSFORMS:
            emit(vector, f"    /// <summary>libaom's {name} on each lane.</summary>",
                 f"    internal static void {TRANSFORM_NAMES[name]}<T>(ReadOnlySpan<Vector256<int>> input, Span<Vector256<int>> output, "
                 "Vector256<int> lo, Vector256<int> hi)\n        where T : IButterfly", vector_lines(split))
        if name in NARROW_TRANSFORMS:
            emit(vector, f"    /// <summary>libaom's {name} on each of four lanes, at 8 bits.</summary>",
                 f"    internal static void {TRANSFORM_NAMES[name]}(ReadOnlySpan<Vector128<int>> input, Span<Vector128<int>> output, "
                 "Vector128<int> lo, Vector128<int> hi)",
                 [line.replace("Vector256", "Vector128").replace("T.HalfBtf(", "HalfBtf(") for line in vector_lines(split)])
    out[-1] = "}"
    open(os.path.join(OUT, "Av1InverseTransform.OneD.cs"), "w", newline="\n").write(TRANSFORM_HEADER + "\n".join(out) + "\n")
    vector[-1] = "}"
    open(os.path.join(OUT, "Av1InverseTransform.Vector.cs"), "w", newline="\n").write(VECTOR_HEADER + "\n".join(vector) + "\n")


def emit(out, summary, signature, lines):
    indent = 2
    out += [summary, signature, "    {"]
    for s in lines:
        if s.startswith("}"):
            indent -= 1
        out.append("    " * indent + s)
        if s == "{":
            indent += 1
    out += ["    }", ""]


FORWARD_NAMES = {
    "av1_fdct4": "Dct4", "av1_fdct8": "Dct8", "av1_fdct16": "Dct16", "av1_fdct32": "Dct32", "av1_fdct64": "Dct64",
    "av1_fadst4": "Adst4", "av1_fadst8": "Adst8", "av1_fadst16": "Adst16",
    "av1_fidentity4_c": "Identity4", "av1_fidentity8_c": "Identity8", "av1_fidentity16_c": "Identity16",
    "av1_fidentity32_c": "Identity32",
}

FORWARD_HEADER = """// <auto-generated>
// Translated by tools/make-av1-tables.py from libaom 3.15.2's av1/encoder/av1_fwd_txfm1d.c (BSD 2-clause, AOM Patent License
// 1.0; see THIRD-PARTY-NOTICES): the one-dimensional forward transforms, without the range checks.
// Do not edit; run the script again.
// </auto-generated>
namespace Egami.Formats.Av1;

/// <summary>The one-dimensional forward transforms of libaom's av1_fwd_txfm1d.c.</summary>
internal static partial class Av1ForwardTransform
{
"""


def translate_forward_line(name, s):
    s = s.replace("const int32_t *cospi;", "ReadOnlySpan<int> cospi;")
    s = s.replace("cospi = cospi_arr(cos_bit);", "cospi = Cospi(cosBit);")
    s = s.replace("const int32_t *sinpi = sinpi_arr(bit);", "ReadOnlySpan<int> sinpi = Sinpi(bit);")
    s = s.replace("int bit = cos_bit;", "int bit = cosBit;")
    s = s.replace("int32_t *bf0, *bf1;", "scoped Span<int> bf0, bf1;")
    s = re.sub(r"int32_t step\[(\d+)\];", r"Span<int> step = stackalloc int[\1];", s)
    s = re.sub(r"range_check_value\((.*), (bit \+ )?stage_range\[\d\]\)", r"\1", s)
    s = re.sub(r"if \(!\((.*)\)\) \{", r"if ((\1) == 0) {", s)
    s = s.replace("half_btf(", "HalfBtf(").replace("round_shift(", "RoundShift(").replace("cos_bit", "cosBit")
    s = s.replace("int64_t", "long").replace("int32_t", "int")
    if "stage_range" in s or "->" in s or "memcpy" in s or "!(" in s or "range_check" in s:
        raise ValueError(f"{name}: {s}")
    return s


def translate_forward_transforms():
    """Translates libaom's av1_fwd_txfm1d.c into Av1ForwardTransform.OneD.cs."""
    text = strip_comments(open(os.path.join(AOM, "av1/encoder/av1_fwd_txfm1d.c")).read())
    out = []
    pattern = r"void (\w+)\(const int32_t \*input, int32_t \*output, int8_t cos_bit,\s*const int8_t \*stage_range\) \{"
    for m in re.finditer(pattern, text):
        name = m.group(1)
        depth, i = 1, m.end()
        while depth:
            depth += {"{": 1, "}": -1}.get(text[i], 0)
            i += 1
        raw = []
        for line in text[m.end():i - 1].split("\n"):
            s = line.strip()
            if s:
                raw.append(s)

        # Statements split over lines are joined first, so a range check spread over two lines is recognised whole.
        joined = []
        for s in raw:
            if joined and not joined[-1].endswith((";", "{", "}", ")")):
                joined[-1] += " " + s
            else:
                joined.append(s)
        lines = []
        for s in joined:
            if s.startswith(("assert(", "(void)", "av1_range_check_buf(")) or s in ("stage++;", "int32_t stage = 0;"):
                continue
            if re.match(r"const int32_t size = \d+;", s):
                continue
            lines.append(translate_forward_line(name, s))

        # Braces on their own lines, and braces around a loop's single statement.
        split = []
        k = 0
        while k < len(lines):
            s = lines[k]
            if s.endswith("{"):
                split += [s[:-1].rstrip(), "{"]
            elif s.startswith("for (") and s.endswith(")"):
                split += [s, "{", lines[k + 1], "}"]
                k += 1
            elif s.startswith("for (") and s.endswith(";"):
                head = s[:s.index(") ") + 1]
                split += [head, "{", s[len(head):].strip(), "}"]
            else:
                split.append(s)
            k += 1
        emit(out, f"    /// <summary>libaom's {name}.</summary>",
             f"    internal static void {FORWARD_NAMES[name]}(ReadOnlySpan<int> input, Span<int> output, int cosBit)", split)
    out[-1] = "}"
    open(os.path.join(OUT, "Av1ForwardTransform.OneD.cs"), "w", newline="\n").write(FORWARD_HEADER + "\n".join(out) + "\n")


main()
translate_inverse_transforms()
translate_forward_transforms()
