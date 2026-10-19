using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>
/// The cases in tests/reference/barcodes/cases.json: Egami's modules equal zint's (zint-grids.json), and zxing-cpp read Egami's
/// rendering of the same modules back as the case's text (zxing-readback.json). tools/make-barcode-references.py makes both.
/// </summary>
public class ReferenceTests
{
    private static readonly Lazy<JsonElement[]> s_cases = new(() =>
        [.. Load("cases.json").GetProperty("cases").EnumerateArray()]);

    /// <summary>The factories the cases may name; cases for others are not run yet.</summary>
    private static readonly HashSet<string> s_calls = ["Code128", "Gs1_128", "Code39", "Itf", "Itf14", "Ean13", "Ean8", "UpcA", "UpcE", "Qr", "QrGs1", "DataMatrix", "DataMatrixGs1", "Pdf417", "Aztec", "AztecRune"];

    public static TheoryData<string> Cases =>
        [.. s_cases.Value.Where(c => s_calls.Contains(c.GetProperty("call").GetString()!)).Select(c => c.GetProperty("name").GetString()!)];

    /// <summary>The cases zint encodes too; for the others only the read-back is checked.</summary>
    public static TheoryData<string> ZintCases =>
        [.. s_cases.Value.Where(c => s_calls.Contains(c.GetProperty("call").GetString()!) && c.TryGetProperty("zint", out _)).Select(c => c.GetProperty("name").GetString()!)];

    private static string Folder => System.IO.Path.Combine(TestFiles.Root, "tests", "reference", "barcodes");

    [Theory]
    [MemberData(nameof(ZintCases))]
    public void ModulesEqualZints(string name)
    {
        JsonElement test = Case(name);
        Barcode barcode = Make(test);
        JsonElement zint = Load("zint-grids.json").GetProperty(name);
        if (zint.TryGetProperty("versions", out JsonElement versions))
        {
            // QR: masks may differ, so the version and level are compared. Without a level, Egami takes M's version and raises
            // the level while that version holds (design question 9); zint, given a level, does not raise it.
            (int version, char level) = QrVersionAndLevel(barcode.Modules);
            string? asked = test.TryGetProperty("options", out JsonElement o) ? Text(o, "level") : null;
            char expectedLevel = asked?[0] ?? "HQM".First(l => versions.GetProperty(l.ToString()).GetInt32() == versions.GetProperty("M").GetInt32());
            Assert.Equal(versions.GetProperty((asked ?? "M")).GetInt32(), version);
            Assert.Equal(expectedLevel, level);
            return;
        }

        Assert.Equal(zint.GetProperty("width").GetInt32(), barcode.Modules.Width);
        if (!barcode.Modules.IsLinear)
        {
            Assert.Equal(string.Join('\n', zint.GetProperty("modules").EnumerateArray().Select(r => r.GetString())), Grid(barcode.Modules).TrimEnd('\n'));
            return;
        }

        Assert.Equal(zint.GetProperty("modules")[0].GetString(), Row(barcode.Modules));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ZxingReadTheseModules(string name)
    {
        JsonElement test = Case(name);
        JsonElement read = Load("zxing-readback.json").GetProperty(name);
        Assert.Equal(Hash(Make(test).Modules), read.GetProperty("grid").GetString());
        string expected = (test.TryGetProperty("read", out JsonElement r) ? r : test.GetProperty("text")).GetString()!;
        Assert.Equal(expected, read.GetProperty("text").GetString());
    }

    /// <summary>Writes each case as a PNG and its grid as text, for tools/make-barcode-references.py; does nothing otherwise.</summary>
    [Fact]
    public void WriteImages()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_BARCODE_IMAGES");
        if (folder is null)
        {
            return;
        }

        foreach (JsonElement test in s_cases.Value.Where(c => s_calls.Contains(c.GetProperty("call").GetString()!)))
        {
            string name = test.GetProperty("name").GetString()!;
            Barcode barcode = Make(test);
            using Raster<Gray8> image = barcode.ToRaster(3);
            image.Save(System.IO.Path.Combine(folder, name + ".png"));
            File.WriteAllText(System.IO.Path.Combine(folder, name + ".grid"), Grid(barcode.Modules));
        }
    }

    /// <summary>Makes a case's barcode with the factory and options it names.</summary>
    internal static Barcode Make(JsonElement test)
    {
        string data = test.GetProperty("data").GetString()!;
        JsonElement options = test.TryGetProperty("options", out JsonElement o) ? o : default;
        return test.GetProperty("call").GetString() switch
        {
            "Code128" => Barcode.Code128(data),
            "Gs1_128" => Barcode.Gs1Code128(data),
            "Code39" => Barcode.Code39(data, new Code39Options { CheckDigit = Flag(options, "checkDigit") }),
            "Itf" => Barcode.Itf(data),
            "Itf14" => Barcode.Itf14(data),
            "Ean13" => Barcode.Ean13(data, new EanUpcOptions { AddOn = Text(options, "addOn") }),
            "Ean8" => Barcode.Ean8(data, new EanUpcOptions { AddOn = Text(options, "addOn") }),
            "UpcA" => Barcode.UpcA(data, new EanUpcOptions { AddOn = Text(options, "addOn") }),
            "UpcE" => Barcode.UpcE(data, new EanUpcOptions { AddOn = Text(options, "addOn") }),
            "Qr" => Barcode.Qr(data, new QrOptions { ErrorCorrection = Text(options, "level") is string level ? Enum.Parse<QrErrorCorrection>(level) : null }),
            "QrGs1" => Barcode.Qr(Gs1.Parse(data)),
            "DataMatrix" => Barcode.DataMatrix(data, Text(options, "shape") switch
            {
                "Any" => new DataMatrixOptions { Shape = DataMatrixShape.Any },
                "AnyWithDmre" => new DataMatrixOptions { Shape = DataMatrixShape.Any, AllowDmre = true },
                _ => null,
            }),
            "DataMatrixGs1" => Barcode.DataMatrix(Gs1.Parse(data)),
            "Pdf417" => Barcode.Pdf417(data, new Pdf417Options
            {
                Compact = Flag(options, "compact"),
                Columns = Number(options, "columns"),
                ErrorLevel = Number(options, "level"),
            }),
            "Aztec" => Barcode.Aztec(data, new AztecOptions
            {
                Compact = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("compact", out JsonElement compact) ? compact.GetBoolean() : null,
                Layers = Number(options, "layers"),
                ErrorCorrectionPercent = Number(options, "percent"),
            }),
            "AztecRune" => Barcode.AztecRune(int.Parse(data, System.Globalization.CultureInfo.InvariantCulture)),
            string call => throw new NotSupportedException(call),
            _ => throw new NotSupportedException(),
        };
    }

    /// <summary>Reads a QR Code's version from its size and its level from the format bits next to the top-left finder.</summary>
    internal static (int Version, char Level) QrVersionAndLevel(BarcodeModules modules)
    {
        int bits = 0;
        foreach ((int x, int y) in (ReadOnlySpan<(int, int)>)[(0, 8), (1, 8), (2, 8), (3, 8), (4, 8), (5, 8), (7, 8), (8, 8), (8, 7), (8, 5), (8, 4), (8, 3), (8, 2), (8, 1), (8, 0)])
        {
            bits = (bits << 1) | (modules[x, y] ? 1 : 0);
        }

        return ((modules.Width - 17) / 4, "MLHQ"[((bits ^ 0x5412) >> 13) & 3]);
    }

    internal static JsonElement Case(string name) => s_cases.Value.Single(c => c.GetProperty("name").GetString() == name);

    internal static string Grid(BarcodeModules modules)
    {
        var text = new StringBuilder();
        for (int y = 0; y < (modules.IsLinear ? 1 : modules.Height); y++)
        {
            for (int x = 0; x < modules.Width; x++)
            {
                text.Append(modules[x, y] ? '1' : '0');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string Row(BarcodeModules modules) => Grid(modules).TrimEnd('\n');

    private static string Hash(BarcodeModules modules) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Grid(modules))))[..16];

    private static JsonElement Load(string file)
    {
        using var stream = File.OpenRead(System.IO.Path.Combine(Folder, file));
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static string? Text(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static int? Number(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : null;

    private static bool Flag(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out JsonElement value) && value.GetBoolean();
}
