using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Pixels;

namespace Egami.Fonts.Color;

/// <summary>The color palette table ('CPAL', versions 0 and 1): palettes of BGRA colors, their types and name IDs.</summary>
internal sealed class CpalTable
{
    private CpalTable(Rgba8[][] palettes, int[] types, int[] labels)
    {
        Palettes = palettes;
        Types = types;
        Labels = labels;
    }

    public static CpalTable Empty { get; } = new([], [], []);

    public Rgba8[][] Palettes { get; }

    public int[] Types { get; }

    public int[] Labels { get; }

    public static CpalTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        int version = r.ReadUInt16();
        int entries = r.ReadUInt16();
        int palettes = r.ReadUInt16();
        int records = r.ReadUInt16();
        int recordsOffset = (int)r.ReadUInt32();
        var firstIndex = new int[palettes];
        for (int i = 0; i < palettes; i++)
        {
            firstIndex[i] = r.ReadUInt16();
        }

        int typesOffset = 0, labelsOffset = 0;
        if (version >= 1)
        {
            typesOffset = (int)r.ReadUInt32();
            labelsOffset = (int)r.ReadUInt32();
            r.Skip(4);
        }

        ReadOnlySpan<byte> colors = BigEndian.Slice(data, recordsOffset, (long)records * 4);
        var result = new Rgba8[palettes][];
        for (int p = 0; p < palettes; p++)
        {
            if (firstIndex[p] + entries > records)
            {
                throw FontErrors.Corrupt(offset, $"CPAL palette {p} runs past the color records.");
            }

            var palette = new Rgba8[entries];
            for (int e = 0; e < entries; e++)
            {
                int at = (firstIndex[p] + e) * 4;
                palette[e] = new Rgba8(colors[at + 2], colors[at + 1], colors[at], colors[at + 3]);
            }

            result[p] = palette;
        }

        var types = new int[palettes];
        var labels = new int[palettes];
        Array.Fill(labels, 0xFFFF);
        if (typesOffset > 0 && (long)typesOffset + (palettes * 4L) <= data.Length)
        {
            for (int p = 0; p < palettes; p++)
            {
                types[p] = (int)BigEndian.UInt32(data, typesOffset + (p * 4));
            }
        }

        if (labelsOffset > 0 && (long)labelsOffset + (palettes * 2L) <= data.Length)
        {
            for (int p = 0; p < palettes; p++)
            {
                labels[p] = BigEndian.UInt16(data, labelsOffset + (p * 2));
            }
        }

        return new CpalTable(result, types, labels);
    }

    /// <summary>Picks the palette a <see cref="FontPalette"/> names: by background flag first when it asks, else its index, else the first.</summary>
    public int Choose(FontPalette palette)
    {
        if (Palettes.Length == 0)
        {
            return -1;
        }

        int flag = palette.Background switch
        {
            FontPaletteBackground.Light => 1,
            FontPaletteBackground.Dark => 2,
            _ => 0,
        };
        if (flag != 0)
        {
            for (int p = 0; p < Types.Length; p++)
            {
                if ((Types[p] & flag) != 0)
                {
                    return p;
                }
            }
        }

        return palette.Index < Palettes.Length ? palette.Index : 0;
    }

    public FontPaletteInfo[] Describe(NameTable name)
        => [.. Palettes.Select((colors, p) => new FontPaletteInfo(
            Labels[p] is > 0 and < 0xFFFF ? name.GetEnglish(Labels[p]) : null,
            (Types[p] & 1) != 0,
            (Types[p] & 2) != 0,
            colors))];
}
