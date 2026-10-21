using System.Globalization;
using System.Text;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Colors;
using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Cli.Commands;

/// <summary>Writes what an image holds, as text and as JSON, for <c>info</c> and <c>metadata show</c>.</summary>
internal static class MetadataReport
{
    /// <summary>Loads an image's metadata, decoding as little as the format allows (one pixel of the first frame).</summary>
    public static RasterMetadata Load(CliSession session, Pipeline.InputSource input)
    {
        ReaderOptions small = new() { Frames = ..1, Region = new Rect(0, 0, 1, 1) };
        try
        {
            using Raster raster = input.Load(session, small);
            return raster.Metadata;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException)
        {
            using Raster raster = input.Load(session, new ReaderOptions { Frames = ..1 });
            return raster.Metadata;
        }
    }

    /// <summary>The kebab-case name of an orientation.</summary>
    public static string Orientation(ImageOrientation orientation) => Steps.ValueParsers.Kebab(orientation.ToString());

    /// <summary>The pixel layout in words: <c>RGBA 8-bit</c>, <c>gray 16-bit</c>, <c>RGB 32-bit float, premultiplied</c>.</summary>
    public static string Pixels(PixelLayoutInfo layout)
    {
        string order = layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => "gray",
            PixelChannelOrder.GrayAlpha => "gray+alpha",
            PixelChannelOrder.Other => $"{layout.ChannelCount} channels",
            _ => layout.ChannelOrder.ToString().ToUpperInvariant(),
        };
        string type = layout.ChannelType is PixelChannelType.Float16 or PixelChannelType.Float32 ? " float" : string.Empty;
        string alpha = layout.Alpha == AlphaKind.Premultiplied ? ", premultiplied" : string.Empty;
        return $"{order} {layout.BitsPerChannel}-bit{type}{alpha}";
    }

    /// <summary>The color profile's name and size, or <see langword="null"/>.</summary>
    public static (string Name, int Bytes)? Profile(RasterMetadata metadata)
    {
        if (metadata.IccProfile.IsEmpty)
        {
            return null;
        }

        try
        {
            return (ColorProfile.FromIcc(metadata.IccProfile.Span).Name, metadata.IccProfile.Length);
        }
        catch (EgamiException)
        {
            return ("(unreadable ICC profile)", metadata.IccProfile.Length);
        }
    }

    /// <summary>One EXIF entry's value as text.</summary>
    public static string Value(ExifEntry entry)
    {
        if (entry.Type == ExifType.Ascii)
        {
            return entry.AsString();
        }

        if (entry.Type is ExifType.Undefined or ExifType.Byte or ExifType.SByte && entry.Count > 8)
        {
            return $"({entry.Count} bytes)";
        }

        int shown = Math.Min(entry.Count, 16);
        StringBuilder text = new();
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(entry.Type is ExifType.Rational or ExifType.SRational
                ? entry.AsFraction(i).ToString()
                : entry.AsDouble(i).ToString(CultureInfo.InvariantCulture));
        }

        return shown < entry.Count ? text.Append(" ...").ToString() : text.ToString();
    }

    /// <summary>Writes the metadata block names an image has: <c>icc exif xmp</c>.</summary>
    public static IReadOnlyList<string> Blocks(RasterMetadata metadata)
    {
        List<string> blocks = [];
        if (!metadata.IccProfile.IsEmpty)
        {
            blocks.Add("icc");
        }

        if (metadata.Exif is { Entries.Count: > 0 })
        {
            blocks.Add("exif");
        }

        if (!metadata.Xmp.IsEmpty)
        {
            blocks.Add("xmp");
        }

        if (metadata.Iptc is { Entries.Count: > 0 })
        {
            blocks.Add("iptc");
        }

        if (metadata.Text.Count > 0)
        {
            blocks.Add("text");
        }

        if (metadata.GainMap is not null)
        {
            blocks.Add("gain-map");
        }

        return blocks;
    }

    /// <summary>Writes the metadata as indented text lines.</summary>
    public static void WriteText(TextWriter output, RasterMetadata metadata, bool everything)
    {
        if (Profile(metadata) is { } profile)
        {
            output.WriteLine($"  color profile  {profile.Name} (ICC, {profile.Bytes} bytes)");
        }
        else if (metadata.CodePoints is { } points)
        {
            output.WriteLine($"  color          {points}");
        }

        if (metadata.Resolution is { } resolution)
        {
            output.WriteLine($"  resolution     {resolution.X.ToString(CultureInfo.InvariantCulture)} x {resolution.Y.ToString(CultureInfo.InvariantCulture)} {Steps.ValueParsers.Kebab(resolution.Unit.ToString())}");
        }

        if (metadata.Gamma is { } gamma)
        {
            output.WriteLine($"  gamma          {gamma.ToString(CultureInfo.InvariantCulture)}");
        }

        if (metadata.Exif is { Entries.Count: > 0 } exif)
        {
            output.WriteLine($"  exif           {exif.Entries.Count} entries" + (exif.CameraModel is { } model ? $", {exif.CameraMake} {model}".TrimEnd() : string.Empty)
                + (exif.DateTaken is { } taken ? $", taken {taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}" : string.Empty));
            if (everything)
            {
                foreach (ExifEntry entry in exif.Entries)
                {
                    output.WriteLine($"    {entry.Key,-28} {Value(entry)}");
                }
            }
        }

        if (!metadata.Xmp.IsEmpty)
        {
            output.WriteLine($"  xmp            {metadata.Xmp.Length} bytes");
        }

        if (metadata.Iptc is { Entries.Count: > 0 } iptc)
        {
            output.WriteLine($"  iptc           {iptc.Entries.Count} entries");
            if (everything)
            {
                foreach (IptcEntry entry in iptc.Entries)
                {
                    output.WriteLine($"    {entry.Key,-28} {entry.AsString()}");
                }
            }
        }

        foreach (TextEntry entry in metadata.Text)
        {
            output.WriteLine($"  text           {entry.Key}: {Shorten(entry.Value, everything ? 2000 : 80)}");
        }

        if (metadata.Hdr is not null)
        {
            output.WriteLine("  hdr            yes");
        }

        if (metadata.GainMap is not null)
        {
            output.WriteLine("  gain map       yes");
        }
    }

    /// <summary>Writes the metadata as a JSON object property named <c>metadata</c>.</summary>
    public static void WriteJson(Utf8JsonWriter json, RasterMetadata metadata, bool everything)
    {
        json.WriteStartObject("metadata");
        json.WriteStartArray("blocks");
        foreach (string block in Blocks(metadata))
        {
            json.WriteStringValue(block);
        }

        json.WriteEndArray();
        if (Profile(metadata) is { } profile)
        {
            json.WriteStartObject("colorProfile");
            json.WriteString("name", profile.Name);
            json.WriteNumber("bytes", profile.Bytes);
            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("colorProfile");
        }

        if (metadata.Resolution is { } resolution)
        {
            json.WriteStartObject("resolution");
            json.WriteNumber("x", resolution.X);
            json.WriteNumber("y", resolution.Y);
            json.WriteString("unit", Steps.ValueParsers.Kebab(resolution.Unit.ToString()));
            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("resolution");
        }

        json.Number("gamma", metadata.Gamma);
        if (metadata.Exif is { } exif)
        {
            json.WriteStartObject("exif");
            json.Text("cameraMake", exif.CameraMake);
            json.Text("cameraModel", exif.CameraModel);
            json.Text("dateTaken", exif.DateTaken?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            json.WriteString("orientation", Orientation(exif.Orientation));
            if (exif.Location is { } location)
            {
                json.WriteStartObject("location");
                json.WriteNumber("latitude", location.Latitude);
                json.WriteNumber("longitude", location.Longitude);
                json.WriteEndObject();
            }

            json.WriteNumber("count", exif.Entries.Count);
            if (everything)
            {
                json.WriteStartArray("entries");
                foreach (ExifEntry entry in exif.Entries)
                {
                    json.WriteStartObject();
                    json.WriteString("ifd", Steps.ValueParsers.Kebab(entry.Key.Ifd.ToString()));
                    json.WriteNumber("tag", entry.Key.Tag);
                    json.WriteString("name", entry.Key.ToString());
                    json.WriteString("type", Steps.ValueParsers.Kebab(entry.Type.ToString()));
                    json.WriteString("value", Value(entry));
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("exif");
        }

        if (!metadata.Xmp.IsEmpty)
        {
            json.WriteStartObject("xmp");
            json.WriteNumber("bytes", metadata.Xmp.Length);
            if (everything)
            {
                json.WriteString("packet", Encoding.UTF8.GetString(metadata.Xmp.Span));
            }

            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("xmp");
        }

        if (metadata.Iptc is { Entries.Count: > 0 } iptc)
        {
            json.WriteStartArray("iptc");
            foreach (IptcEntry entry in iptc.Entries)
            {
                json.WriteStartObject();
                json.WriteString("name", entry.Key.ToString());
                json.WriteString("value", entry.AsString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }
        else
        {
            json.WriteNull("iptc");
        }

        json.WriteStartArray("text");
        foreach (TextEntry entry in metadata.Text)
        {
            json.WriteStartObject();
            json.WriteString("key", entry.Key);
            json.Text("language", entry.Language);
            json.WriteString("value", entry.Value);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteBoolean("hdr", metadata.Hdr is not null);
        json.WriteBoolean("gainMap", metadata.GainMap is not null);
        json.WriteEndObject();
    }

    private static string Shorten(string text, int length)
    {
        string line = text.ReplaceLineEndings(" ");
        return line.Length <= length ? line : line[..length] + "...";
    }
}
