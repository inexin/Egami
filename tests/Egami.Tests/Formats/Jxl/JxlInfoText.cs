using System.Globalization;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Formats.Jxl;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// What jxlinfo -v (libjxl 0.12.0) prints about a file, split into the box list, the basic information and color lines, and
/// the frame lines, so Egami's reading of the headers can be compared part by part. <see cref="Of(JxlFile)"/> writes the same
/// text from Egami's headers; <see cref="Parse(string)"/> reads jxlinfo's.
/// </summary>
internal sealed partial record JxlInfoText(List<string> Boxes, List<string> Info, List<string> Frames)
{
    private static readonly string[] ExtraChannelNames =
        ["Alpha", "Depth", "Spotcolor", "Selection", "Black", "CFA", "Thermal", "Reserved0", "Reserved1", "Reserved2", "Reserved3", "Reserved4", "Reserved5", "Reserved6", "Reserved7", "Unknown", "Optional"];

    private static readonly string[] OrientationNames =
        ["Normal", "Flipped horizontally", "Upside down", "Flipped vertically", "Transposed", "90 degrees clockwise", "Anti-Transposed", "90 degrees counter-clockwise"];

    /// <summary>Reads a file's headers with Egami, walking every box.</summary>
    internal static JxlFile Read(byte[] data)
    {
        using MemoryStream stream = new(data);
        return JxlFile.Read(new EgamiInput(stream, EgamiContext.Default, TestContext.Current.CancellationToken), allBoxes: true);
    }

    /// <summary>Splits jxlinfo -v's output.</summary>
    internal static JxlInfoText Parse(string text)
    {
        JxlInfoText parts = new([], [], []);
        bool gainMap = false;
        string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == "Box:")
            {
                parts.Boxes.Add($"{lines[i + 1].Trim()} {lines[i + 2].Trim()} {lines[i + 3].Trim()}");
                i += 3;
            }
            else if (line.StartsWith("Brotli-compressed ", StringComparison.Ordinal))
            {
                parts.Boxes[^1] += " " + line;
            }
            else if (line.StartsWith("Frame: ", StringComparison.Ordinal) || line.StartsWith("Layer: ", StringComparison.Ordinal) || line.StartsWith("Animation length", StringComparison.Ordinal))
            {
                parts.Frames.Add(line);
            }
            else if (line.StartsWith("Gain map", StringComparison.Ordinal))
            {
                gainMap = true;
            }
            else if (gainMap && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // The gain map's own lines, read in task 14.
            }
            else if (!line.StartsWith("JPEG XL file format container", StringComparison.Ordinal) && !line.StartsWith("Uncompressed ", StringComparison.Ordinal)
                && !line.StartsWith("JPEG bitstream reconstruction", StringComparison.Ordinal) && !line.StartsWith("Frame index box", StringComparison.Ordinal))
            {
                gainMap = false;
                parts.Info.Add(line);
            }
        }

        return parts;
    }

    /// <summary>Writes what jxlinfo would print, from Egami's reading.</summary>
    internal static JxlInfoText Of(JxlFile file)
    {
        JxlInfoText parts = new([], [], []);
        foreach (JxlBox box in file.Container.Boxes)
        {
            string line = $"type: \"{box.Type}\" size: {box.Size} contents size: {box.ContentSize}";
            parts.Boxes.Add(box.CompressedType is string inner ? $"{line} Brotli-compressed {inner} metadata: {box.Size} compressed bytes" : line);
        }

        JxlImageHeader h = file.Header;
        List<string> info = parts.Info;
        bool cmyk = h.ExtraChannels.Any(c => c.Type == JxlExtraChannelType.Black);
        string channels = h.ColorChannels == 1 ? "Grayscale" : cmyk ? "CMY" : "RGB";
        string extras = string.Concat(h.ExtraChannels.Select(c => "+" + ExtraChannelNames[(int)c.Type]));
        string sample = h.BitDepth.IsFloat ? $"float ({h.BitDepth.ExponentBits} exponent bits) " : string.Empty;
        info.Add($"JPEG XL {(h.Animation is null ? "image" : "animation")}, {h.Width}x{h.Height}, {(h.XybEncoded ? "lossy" : "(possibly) lossless")}, {h.BitDepth.BitsPerSample}-bit {sample}{channels}{extras}");
        info.Add($"Number of color channels: {h.ColorChannels}");
        info.Add($"Number of extra channels: {h.ExtraChannels.Count}");
        for (int i = 0; i < h.ExtraChannels.Count; i++)
        {
            JxlExtraChannelInfo c = h.ExtraChannels[i];
            info.Add($"Extra channel {i}:");
            info.Add($"  type: {ExtraChannelNames[(int)c.Type]}");
            info.Add($"  bits per sample: {c.BitDepth.BitsPerSample}");
            if (c.BitDepth.IsFloat)
            {
                info.Add($"  float, with exponent_bits_per_sample: {c.BitDepth.ExponentBits}");
            }

            if (c.DimShift > 0)
            {
                info.Add($"  upsampled {1 << c.DimShift}x");
            }

            if (c.Name.Length > 0)
            {
                info.Add($"  name: {c.Name}");
            }

            if (c.Type == JxlExtraChannelType.Alpha)
            {
                info.Add($"  alpha premultiplied: {(c.AlphaAssociated ? 1 : 0)} ({(c.AlphaAssociated ? "Premultiplied" : "Non-premultiplied")})");
            }

            if (c.SpotColor is float[] spot)
            {
                info.Add($"  spot color: ({F(spot[0])}, {F(spot[1])}, {F(spot[2])}) with opacity {F(spot[3])}");
            }

            if (c.Type == JxlExtraChannelType.Cfa)
            {
                info.Add($"  cfa_channel: {c.CfaChannel}");
            }
        }

        JxlToneMapping tone = h.ToneMapping;
        if (tone.IntensityTarget != 255 || tone.MinNits != 0 || tone.RelativeToMaxDisplay)
        {
            info.Add($"Intensity target: {F(tone.IntensityTarget)} nits");
            info.Add($"Min nits: {F(tone.MinNits)}");
            info.Add($"Relative to max display: {(tone.RelativeToMaxDisplay ? 1 : 0)}");
            info.Add($"Linear below: {F(tone.LinearBelow)}");
        }

        info.Add($"Have preview: {(h.Preview is null ? 0 : 1)}");
        if (h.Preview is (int pw, int ph))
        {
            info.Add($"Preview image: {pw}x{ph}");
        }

        info.Add($"Have animation: {(h.Animation is null ? 0 : 1)}");
        if (h.Animation is JxlAnimation a)
        {
            info.Add($"Ticks per second (numerator / denominator): {a.TicksNumerator} / {a.TicksDenominator}");
            info.Add($"Num loops: {a.Loops}");
            info.Add($"Have timecodes: {(a.HaveTimecodes ? 1 : 0)}");
        }

        (int iw, int ih) = h.IntrinsicSize ?? (h.Width, h.Height);
        info.Add($"Intrinsic dimensions: {iw}x{ih}");
        info.Add($"Orientation: {h.Orientation} ({OrientationNames[h.Orientation - 1]})");
        if (!h.Color.WantIcc)
        {
            Color(h.Color, info);
        }

        if (file.FramesKnown)
        {
            FrameLines(file, parts.Frames);
        }

        return parts;
    }

    /// <summary>Gets jxlinfo's info lines without the ICC profile line, which needs the decoded profile (task 3).</summary>
    internal List<string> InfoWithoutIcc() => [.. Info.Where(l => !IccLine().IsMatch(l))];

    private static void Color(JxlColorEncoding c, List<string> info)
    {
        info.Add($"Color space: {c.ColorSpace switch { JxlColorSpace.Rgb => "RGB", JxlColorSpace.Gray => "Grayscale", JxlColorSpace.Xyb => "XYB", _ => "Unknown" }}");
        string white = c.WhitePoint switch { 1 => "D65", 2 => "Custom", 10 => "E", _ => "P3" };
        info.Add(c.WhiteXy is (double wx, double wy) ? $"White point: {white} (x={F(wx)},y={F(wy)})" : $"White point: {white}");
        if (c.ColorSpace is JxlColorSpace.Rgb or JxlColorSpace.Unknown)
        {
            string primaries = c.Primaries switch { 1 => "sRGB", 2 => "Custom", 9 => "Rec.2100", _ => "P3" };
            info.Add(c.PrimariesXy is { } xy
                ? $"Primaries: {primaries} - red(x={F(xy[0].X)},y={F(xy[0].Y)}), green(x={F(xy[1].X)},y={F(xy[1].Y)}), blue(x={F(xy[2].X)},y={F(xy[2].Y)})"
                : $"Primaries: {primaries}");
        }

        string tf = c.Gamma != 0 ? $"gamma({F(c.Gamma / 1e7)})" : c.TransferFunction switch { 1 => "709", 8 => "Linear", 13 => "sRGB", 16 => "PQ", 17 => "DCI", 18 => "HLG", _ => "Unknown" };
        info.Add($"Transfer function: {tf}");
        info.Add($"Rendering intent: {c.RenderingIntent switch { 0 => "Perceptual", 1 => "Relative", 2 => "Saturation", _ => "Absolute" }}");
    }

    private static void FrameLines(JxlFile file, List<string> frames)
    {
        JxlImageHeader h = file.Header;
        int count = 0;
        double total = 0;
        foreach (JxlFrameHeader f in file.Frames.Where(f => f.IsNormal))
        {
            if (f.Duration == 0 && f.IsLast && count == 0 && f.Name.Length == 0)
            {
                continue;
            }

            count++;
            string line = (f.Duration == 0 ? "Layer: " : "Frame: ") + (f.HaveCrop ? $"{f.Width}x{f.Height} at position ({f.X0},{f.Y0})" : "Full image size");
            if (h.Animation is JxlAnimation a)
            {
                float ms = f.Duration * 1000f * a.TicksDenominator / a.TicksNumerator;
                total += ms;
                line += $", duration: {ms.ToString("F1", CultureInfo.InvariantCulture)} ms";
                if (a.HaveTimecodes)
                {
                    line += $", time code: {f.Timecode:X}";
                }
            }

            if (f.Name.Length > 0)
            {
                line += $", name: \"{f.Name}\"";
            }

            frames.Add(line);
        }

        if (h.Animation is JxlAnimation animation)
        {
            if (animation.Loops > 1)
            {
                total *= animation.Loops;
            }

            frames.Add($"Animation length: {(total * 0.001).ToString("F3", CultureInfo.InvariantCulture)} seconds{(animation.Loops == 0 ? " (looping)" : string.Empty)}");
        }
    }

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d+-byte ICC profile")]
    private static partial Regex IccLine();
}
