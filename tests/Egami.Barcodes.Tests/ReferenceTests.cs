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
    private static readonly HashSet<string> s_calls = ["Code128", "Gs1_128", "Code39", "Itf", "Itf14"];

    public static TheoryData<string> Cases =>
        [.. s_cases.Value.Where(c => s_calls.Contains(c.GetProperty("call").GetString()!)).Select(c => c.GetProperty("name").GetString()!)];

    private static string Folder => System.IO.Path.Combine(TestFiles.Root, "tests", "reference", "barcodes");

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModulesEqualZints(string name)
    {
        Barcode barcode = Make(Case(name));
        JsonElement zint = Load("zint-grids.json").GetProperty(name);
        Assert.Equal(zint.GetProperty("width").GetInt32(), barcode.Modules.Width);
        Assert.Equal(zint.GetProperty("modules")[0].GetString(), Row(barcode.Modules));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ZxingReadTheseModules(string name)
    {
        JsonElement test = Case(name);
        JsonElement read = Load("zxing-readback.json").GetProperty(name);
        Assert.Equal(Hash(Make(test).Modules), read.GetProperty("grid").GetString());
        Assert.Equal(test.GetProperty("text").GetString(), read.GetProperty("text").GetString());
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
            "Gs1_128" => Barcode.Gs1_128(data),
            "Code39" => Barcode.Code39(data, new Code39Options { CheckDigit = Flag(options, "checkDigit") }),
            "Itf" => Barcode.Itf(data),
            "Itf14" => Barcode.Itf14(data),
            string call => throw new NotSupportedException(call),
            _ => throw new NotSupportedException(),
        };
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

    private static bool Flag(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out JsonElement value) && value.GetBoolean();
}
