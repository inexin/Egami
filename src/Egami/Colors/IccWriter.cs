using System.Buffers.Binary;
using System.Text;

namespace Egami.Colors;

/// <summary>
/// Writes small version 4 ICC profiles for RGB and gray encodings: a matrix-shaper (or gray curve) with para curves where the
/// curve is a formula and curv tables otherwise, version 4.3 for SDR and 4.4 with a cicp tag for PQ and HLG, whose curves are
/// written as tables that roll highlights off toward reference white, for readers that ignore cicp.
/// </summary>
internal static class IccWriter
{
    private const int TableSize = 1024;

    public static byte[] Write(ColorProfile profile)
    {
        TransferFunction curve = profile.Transfer ?? throw new InvalidOperationException($"'{profile.Name}' has no single curve to write.");
        bool hdr = curve.IsHdr;
        Vector3d white = profile.White.ToXyz();
        Matrix3d adapt = Matrix3d.Bradford(white, Cie.D50);
        List<(string Signature, byte[] Data)> tags =
        [
            ("desc", Mluc(profile.Name)),
            ("cprt", Mluc("No copyright, use freely")),
            ("wtpt", Xyz(Cie.D50)),
            ("chad", Sf32(adapt)),
        ];
        byte[] trc = Curve(curve);
        if (profile.Model == ColorModel.Gray)
        {
            tags.Add(("kTRC", trc));
        }
        else
        {
            Matrix3d colorants = adapt * profile.ToXyz();
            tags.Add(("rXYZ", Xyz(new Vector3d(colorants[0, 0], colorants[1, 0], colorants[2, 0]))));
            tags.Add(("gXYZ", Xyz(new Vector3d(colorants[0, 1], colorants[1, 1], colorants[2, 1]))));
            tags.Add(("bXYZ", Xyz(new Vector3d(colorants[0, 2], colorants[1, 2], colorants[2, 2]))));
            tags.Add(("rTRC", trc));
            tags.Add(("gTRC", trc));
            tags.Add(("bTRC", trc));
        }

        if (hdr && profile.ToCodePoints() is ColorCodePoints codes)
        {
            tags.Add(("cicp", [.. "cicp"u8, 0, 0, 0, 0, codes.Primaries, codes.TransferCharacteristics, codes.MatrixCoefficients, (byte)(codes.FullRange ? 1 : 0)]));
        }

        return Assemble(hdr ? 0x04400000u : 0x04300000u, profile.Model == ColorModel.Gray ? "GRAY" : "RGB ", tags);
    }

    private static byte[] Assemble(uint version, string space, List<(string Signature, byte[] Data)> tags)
    {
        int offset = 128 + 4 + (12 * tags.Count);
        using MemoryStream body = new();
        byte[] table = new byte[4 + (12 * tags.Count)];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Count);
        Dictionary<string, int> shared = [];
        for (int i = 0; i < tags.Count; i++)
        {
            byte[] data = tags[i].Data;
            string key = Convert.ToBase64String(data);
            if (!shared.TryGetValue(key, out int at))
            {
                at = offset + (int)body.Length;
                shared[key] = at;
                body.Write(data);
                while (body.Length % 4 != 0)
                {
                    body.WriteByte(0);
                }
            }

            Encoding.ASCII.GetBytes(tags[i].Signature, table.AsSpan(4 + (i * 12)));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + (i * 12)), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + (i * 12)), (uint)data.Length);
        }

        byte[] profile = new byte[offset + body.Length];
        Span<byte> h = profile;
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], version);
        "mntr"u8.CopyTo(h[12..]);
        Encoding.ASCII.GetBytes(space, h[16..]);
        "XYZ "u8.CopyTo(h[20..]);

        // A fixed creation date keeps the bytes the same from run to run.
        BinaryPrimitives.WriteUInt16BigEndian(h[24..], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(h[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(h[28..], 1);
        "acsp"u8.CopyTo(h[36..]);
        S15(h[68..], Cie.D50.X);
        S15(h[72..], Cie.D50.Y);
        S15(h[76..], Cie.D50.Z);
        table.CopyTo(h[128..]);
        body.ToArray().CopyTo(h[offset..]);

        // The profile ID stays zero, which ICC allows and means "not computed".
        return profile;
    }

    private static byte[] Curve(TransferFunction curve)
    {
        (double G, double A, double B, double C, double D, double E, double F)? p = curve.ParametricValues;
        if (curve.Shape is TransferFunction.Kind.Linear or TransferFunction.Kind.Gamma)
        {
            return Para(0, p!.Value.G);
        }

        if (p is (double g, double a, double b, double c, double d, double e, double f))
        {
            return e == 0 && f == 0 ? Para(3, g, a, b, c, d) : Para(4, g, a, b, c, d, e, f);
        }

        // Tables; and for PQ and HLG, for readers that ignore cicp, the curve up to reference white with highlights rolled off
        // smoothly toward 1 above three quarters of it, so it stays increasing and invertible.
        int n = curve.Shape == TransferFunction.Kind.Table ? curve.TableValues.Length : TableSize;
        byte[] data = new byte[12 + (2 * n)];
        "curv"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)n);
        for (int i = 0; i < n; i++)
        {
            double v = curve.Shape == TransferFunction.Kind.Table ? curve.TableValues[i] : RollOff(curve.ToLinear(i / (double)(n - 1)));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + (2 * i)), (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535));
        }

        return data;
    }

    private static double RollOff(double linear)
    {
        const double Knee = 0.75;
        return linear <= Knee ? linear : Knee + ((1 - Knee) * Math.Tanh((linear - Knee) / (1 - Knee)));
    }

    private static byte[] Para(ushort function, params ReadOnlySpan<double> values)
    {
        byte[] data = new byte[12 + (4 * values.Length)];
        "para"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), function);
        for (int i = 0; i < values.Length; i++)
        {
            S15(data.AsSpan(12 + (4 * i)), values[i]);
        }

        return data;
    }

    private static byte[] Xyz(Vector3d v)
    {
        byte[] data = new byte[20];
        "XYZ "u8.CopyTo(data);
        S15(data.AsSpan(8), v.X);
        S15(data.AsSpan(12), v.Y);
        S15(data.AsSpan(16), v.Z);
        return data;
    }

    private static byte[] Sf32(Matrix3d m)
    {
        byte[] data = new byte[44];
        "sf32"u8.CopyTo(data);
        for (int i = 0; i < 9; i++)
        {
            S15(data.AsSpan(8 + (4 * i)), m[i / 3, i % 3]);
        }

        return data;
    }

    private static byte[] Mluc(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        byte[] data = new byte[28 + utf16.Length];
        "mluc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12);
        "enUS"u8.CopyTo(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        utf16.CopyTo(data, 28);
        return data;
    }

    private static void S15(Span<byte> destination, double v) => BinaryPrimitives.WriteInt32BigEndian(destination, (int)Math.Round(v * 65536));
}
