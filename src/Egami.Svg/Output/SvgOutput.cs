using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Svg.Output;

/// <summary>
/// What every canvas writing one SVG file shares: the options, the <c>defs</c> (each definition written once, equal ones shared),
/// the ids, the images, glyphs and pictures already defined, the fonts to embed and the diagnostics.
/// </summary>
internal sealed class SvgOutput
{
    /// <summary>The depth of a definition inside the root's <c>defs</c>.</summary>
    public const int DefDepth = 2;

    private readonly List<string> _defs = [];
    private readonly Dictionary<string, string> _byContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _byReference = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Typeface Face, ushort Glyph, FontSynthesis Synthesis, float Size), string?> _glyphs = [];
    private readonly Dictionary<Typeface, EmbeddedFace> _faces = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets each clipPath written so far: the one it is clipped by, its area in device coordinates, and how many clipPaths its chain holds.</summary>
    public Dictionary<string, (string? Parent, Path Device, int Depth)> Clips { get; } = new(StringComparer.Ordinal);
    private readonly List<EgamiDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public SvgOutput(SvgWriteOptions options)
    {
        Options = options;
        Numbers = new SvgNumbers(options.Precision);
        PathFormat = new SvgPathFormat { Precision = options.Precision, Compact = options.Compact };
    }

    public SvgWriteOptions Options { get; }

    public SvgNumbers Numbers { get; }

    public SvgPathFormat PathFormat { get; }

    public bool Compact => Options.Compact;

    public IReadOnlyList<string> Definitions => _defs;

    public IEnumerable<EmbeddedFace> Faces => _faces.Values;

    public string NewId(string prefix)
    {
        _counters.TryGetValue(prefix, out int n);
        _counters[prefix] = ++n;
        return prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Defines an element once: an equal definition written before is shared. Returns its id.</summary>
    public string Define(string prefix, string name, SvgAttrs attrs, string? inner)
    {
        string key = name + attrs.ToString() + "|" + inner;
        if (_byContent.TryGetValue(key, out string? id))
        {
            return id;
        }

        id = NewId(prefix);
        SvgAttrs withId = new() { new("id", id) };
        withId.AddRange(attrs);
        _defs.Add(SvgMarkup.Element(name, withId, inner, Compact, DefDepth));
        _byContent[key] = id;
        return id;
    }

    /// <summary>Returns the id an object (an image, a picture) was defined with, or defines it with <paramref name="define"/>.</summary>
    public string DefineOnce(object key, string prefix, Func<string, string> define)
    {
        if (_byReference.TryGetValue(key, out string? id))
        {
            return id;
        }

        id = NewId(prefix);
        _byReference[key] = id;
        _defs.Add(define(id));
        return id;
    }

    /// <summary>Returns the id of a glyph's outline in defs, or <see langword="null"/> for a glyph without ink.</summary>
    public string? Glyph(Typeface face, ushort glyph, FontSynthesis synthesis, float size, Func<Path> outline)
    {
        var key = (face, glyph, synthesis, size);
        if (_glyphs.TryGetValue(key, out string? id))
        {
            return id;
        }

        Path path = outline();
        id = path.IsEmpty ? null : Define("gl", "path", new SvgAttrs().Add("d", path.ToSvg(PathFormat)), null);
        _glyphs[key] = id;
        return id;
    }

    /// <summary>The face's entry for font embedding: its family name in the file and the glyphs and text it draws.</summary>
    public EmbeddedFace Face(Typeface face)
    {
        if (!_faces.TryGetValue(face, out EmbeddedFace? entry))
        {
            entry = new EmbeddedFace(face, "egami-font-" + (_faces.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _faces[face] = entry;
        }

        return entry;
    }

    /// <summary>An image's bytes as a data URI: PNG, or JPEG when asked and the image is opaque.</summary>
    public string ImageUri(Raster raster, bool allowJpeg)
    {
        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        bool jpeg = allowJpeg && Options.ImageFormat == SvgImageFormat.Jpeg && IsOpaque(rgba);
        using MemoryStream stream = new();
        if (jpeg)
        {
            rgba.Save(stream, JpegFormat.Info);
        }
        else
        {
            rgba.Save(stream, PngFormat.Info, new PngWriterOptions { FirstFrameOnly = true });
        }

        return DataUri(jpeg ? "image/jpeg" : "image/png", stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    public static string DataUri(string mime, ReadOnlySpan<byte> data) => "data:" + mime + ";base64," + Convert.ToBase64String(data);

    /// <summary>Reports a diagnostic once per message.</summary>
    public void Report(DiagnosticLevel level, string id, string message)
    {
        if (_reported.Add(id + "|" + message))
        {
            _diagnostics.Add(new EgamiDiagnostic(level, "Svg", id, message, null, null));
        }
    }

    public void Deliver()
    {
        EgamiContext context = Options.Context ?? EgamiContext.Default;
        if (_diagnostics.Count == 0 || context.Diagnostics is null)
        {
            return;
        }

        using DiagnosticReporter reporter = context.StartDiagnostics();
        foreach (EgamiDiagnostic diagnostic in _diagnostics)
        {
            reporter.Report(diagnostic);
        }
    }

    private static bool IsOpaque(Raster<Rgba8> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                if (p.A != 255)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>A face drawn as text: the family name it has in the file, and the glyphs and characters to keep in its subset.</summary>
internal sealed class EmbeddedFace(Typeface face, string family)
{
    public Typeface Face { get; } = face;

    public string Family { get; } = family;

    /// <summary>Gets a value indicating whether the face's license (OS/2 fsType) allows embedding a subset, and it has outlines.</summary>
    public bool Embeddable { get; } = face.Outlines != FontOutlines.None
        && (face.Embedding & (FontEmbedding.NoSubsetting | FontEmbedding.BitmapOnly)) == 0
        && (face.Embedding & (FontEmbedding.Restricted | FontEmbedding.PreviewAndPrint | FontEmbedding.Editable)) != FontEmbedding.Restricted;

    public HashSet<ushort> Glyphs { get; } = [0];

    public StringBuilder Text { get; } = new();
}
