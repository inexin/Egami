using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Svg.Rendering;

namespace Egami.Svg;

/// <summary>
/// Draws the glyphs of an OpenType SVG table (question 20): each glyph's document is read once per font and kept, and the
/// element <c>glyph</c><i>id</i> is drawn in the font's units, its origin on the baseline. Set it on
/// <see cref="CanvasOptions.ColorGlyphs"/>; <see cref="SvgDocument.Rasterize{TPixel}(SvgRenderOptions?)"/> uses it already.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-glyphs"/>
/// </example>
public sealed class SvgGlyphRenderer : IColorGlyphRenderer
{
    private readonly ConditionalWeakTable<Typeface, ConcurrentDictionary<ushort, Glyphs?>> _documents = [];

    private SvgGlyphRenderer()
    {
    }

    /// <summary>Gets the renderer.</summary>
    public static SvgGlyphRenderer Instance { get; } = new();

    /// <inheritdoc/>
    public ColorGlyphFormats Formats => ColorGlyphFormats.Svg;

    /// <inheritdoc/>
    public bool TryDraw(Canvas canvas, Font font, ushort glyph, Matrix3x2 transform, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Typeface face = font.EffectiveTypeface;
        if (!face.TryGetSvgDocument(glyph, out ReadOnlyMemory<byte> data, out ushort first, out _))
        {
            return false;
        }

        Glyphs? document = _documents.GetValue(face, _ => new ConcurrentDictionary<ushort, Glyphs?>()).GetOrAdd(first, _ => Read(data));
        if (document is null || !document.Nodes.TryGetValue("glyph" + glyph, out (SvgNode Node, Matrix3x2 Transform) found))
        {
            return false;
        }

        float scale = font.Size / face.UnitsPerEm;
        int count = canvas.Save();
        try
        {
            canvas.Concat(found.Transform * Matrix3x2.CreateScale(scale) * transform);
            SvgRenderer.Draw(canvas, found.Node, SvgRenderOptions.Default);
        }
        finally
        {
            canvas.RestoreToCount(count);
        }

        return true;
    }

    /// <summary>A glyph document's rendered elements by id, with the transforms of the groups around them (not the root's viewport).</summary>
    private sealed record Glyphs(Dictionary<string, (SvgNode Node, Matrix3x2 Transform)> Nodes);

    private static Glyphs? Read(ReadOnlyMemory<byte> data)
    {
        try
        {
            SvgDocument document = SvgDocument.Load(new MemoryStream(data.ToArray()), new SvgLoadOptions { Fonts = new SvgFontOptions { UseSystemFonts = false } });
            Dictionary<string, (SvgNode, Matrix3x2)> nodes = new(StringComparer.Ordinal);
            if (document.RenderTree is SvgGroup root)
            {
                foreach (SvgNode child in root.Children)
                {
                    Collect(child, Matrix3x2.Identity, nodes);
                }
            }

            return new Glyphs(nodes);
        }
        catch (EgamiException)
        {
            return null;
        }
    }

    private static void Collect(SvgNode node, Matrix3x2 outer, Dictionary<string, (SvgNode, Matrix3x2)> nodes)
    {
        if (node.Id is string id && id.StartsWith("glyph", StringComparison.Ordinal))
        {
            nodes.TryAdd(id, (node, outer));
        }

        if (node is SvgGroup group)
        {
            Matrix3x2 inner = node.Transform * outer;
            foreach (SvgNode child in group.Children)
            {
                Collect(child, inner, nodes);
            }
        }
    }
}
