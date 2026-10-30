using System.CommandLine;
using System.Text;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Formats.Png;
using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Cli.Commands;

/// <summary><c>egami layers</c>: a Photoshop document's layers as PNG files and a JSON list.</summary>
internal static class LayersCommand
{
    public static Command Create(CliSession session)
    {
        Argument<string> input = new("input") { Description = "The PSD or PSB file, or - for standard input." };
        Argument<string> folder = new("folder") { Description = "The folder to write the layers and layers.json into; made when missing." };
        Command command = new("layers", "Write each layer of a PSD or PSB file as a PNG at its own size, its mask as a gray PNG, and layers.json listing every layer and group: name, place, size, blend mode, opacity, visibility and group.");
        command.Arguments.Add(input);
        command.Arguments.Add(folder);
        command.SetAction(result =>
        {
            InputFile file = Globber.Expand(session, [result.GetValue(input)!], recursive: false)[0];
            string outputFolder = session.ResolvePath(result.GetValue(folder)!);
            Directory.CreateDirectory(outputFolder);
            using Stream stream = new InputSource(file).Open(session);
            using PsdDocument document = Read(stream, session);
            using MemoryStream json = new();
            using (Utf8JsonWriter writer = new(json, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("width", document.Width);
                writer.WriteNumber("height", document.Height);
                writer.WriteString("colorMode", document.ColorMode.ToString());
                writer.WriteNumber("bitsPerChannel", document.BitsPerChannel);
                writer.WriteStartArray("layers");
                int written = 0;
                foreach (PsdLayerImage layer in document.Layers)
                {
                    string? image = null, mask = null;
                    if (layer.Pixels is Raster pixels)
                    {
                        image = $"{layer.Index:000}-{Safe(layer.Info.Name)}.png";
                        Output.Write(session, Path.Combine(outputFolder, image), s => Save(pixels, s));
                        written++;
                    }

                    if (layer.Mask is PsdMask m)
                    {
                        mask = $"{layer.Index:000}-{Safe(layer.Info.Name)}-mask.png";
                        Output.Write(session, Path.Combine(outputFolder, mask), s => Save(m.Pixels, s));
                    }

                    Write(writer, layer, image, mask);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                session.Info($"{file.Display}: {document.Layers.Count} layers and groups, {written} images, in {outputFolder}");
            }

            Output.Write(session, Path.Combine(outputFolder, "layers.json"), s => s.Write(json.GetBuffer().AsSpan(0, (int)json.Length)));
            foreach (EgamiDiagnostic warning in document.Metadata.Warnings)
            {
                session.Warn($"{file.Display}: {warning.Message}");
            }

            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>Reads the layers; CMYK ones as RGBA by the plain formula, since PNG has no inks.</summary>
    private static PsdDocument Read(Stream stream, CliSession session)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        copy.Position = 0;

        // The color mode is the header's last field: 4 is CMYK.
        bool cmyk = copy.Length >= 26 && copy.GetBuffer()[24] == 0 && copy.GetBuffer()[25] == 4;
        return cmyk ? PsdImage.ReadLayers<Rgba16>(copy, null, session.Context) : PsdImage.ReadLayers(copy, null, session.Context);
    }

    /// <summary>Saves a layer as PNG: 8 or 16 bits as it is, a float layer as 16 bits.</summary>
    private static void Save(Raster raster, Stream stream)
    {
        if (raster is Raster<RgbaF32> or Raster<GrayF32>)
        {
            using Raster<Rgba16> deep = raster.As<Rgba16>();
            deep.Save(stream, PngFormat.Info);
            return;
        }

        raster.Save(stream, PngFormat.Info);
    }

    private static void Write(Utf8JsonWriter json, PsdLayerImage layer, string? image, string? mask)
    {
        PsdLayer info = layer.Info;
        json.WriteStartObject();
        json.WriteNumber("index", layer.Index);
        json.WriteString("name", info.Name);
        json.WriteString("kind", info.Kind == PsdLayerKind.Layer ? "layer" : "group");
        json.WriteString("type", info.LayerType.ToString());
        json.WriteNumber("parent", layer.ParentIndex);
        json.WriteNumber("x", layer.X);
        json.WriteNumber("y", layer.Y);
        json.WriteNumber("width", layer.Pixels?.Width ?? layer.Ink?.Width ?? 0);
        json.WriteNumber("height", layer.Pixels?.Height ?? layer.Ink?.Height ?? 0);
        json.WriteString("blendMode", info.BlendModeKey.Trim());
        if (info.BlendMode is { } mode)
        {
            json.WriteString("egamiBlendMode", mode.ToString());
        }

        json.WriteNumber("opacity", Math.Round(info.Opacity, 4));
        json.WriteNumber("fillOpacity", Math.Round(info.FillOpacity, 4));
        json.WriteBoolean("visible", info.IsVisible);
        json.WriteBoolean("clipped", info.IsClipped);
        if (image is not null)
        {
            json.WriteString("image", image);
        }

        if (layer.Mask is PsdMask m)
        {
            json.WriteStartObject("mask");
            json.WriteString("image", mask);
            json.WriteNumber("x", m.Bounds.X);
            json.WriteNumber("y", m.Bounds.Y);
            json.WriteNumber("defaultColor", m.DefaultColor);
            json.WriteBoolean("disabled", m.IsDisabled);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    /// <summary>Returns a layer name fit for a file name: letters, digits, '-' and '_', at most 40 of them.</summary>
    private static string Safe(string name)
    {
        StringBuilder safe = new();
        foreach (char c in name)
        {
            if (safe.Length >= 40)
            {
                break;
            }

            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return safe.Length == 0 ? "layer" : safe.ToString();
    }
}
