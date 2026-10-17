using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Operations;
using Egami.Svg.Output;

namespace Egami.Svg;

/// <summary>
/// A canvas that writes SVG: every drawing call becomes SVG elements, so anything drawn on a <see cref="Canvas"/> (shapes,
/// strokes, gradients, images, text, clips, layers, recorded pictures) can be saved as a vector file that browsers, Inkscape and
/// Egami.Svg all read. Gradients, patterns, clips, masks, images, glyph outlines and pictures are written once in <c>defs</c> and
/// shared. What SVG cannot express (sweep gradients, noise and composed paints, meshes, layers with filters) is drawn by Egami
/// into an image at <see cref="SvgWriteOptions.FallbackScale"/> and embedded as PNG.
/// <para>
/// The output is SVG 1.1 with CSS <c>mix-blend-mode</c> and <c>isolation</c> for blend modes and layers (and SVG 2's <c>fr</c>
/// for a conical gradient whose start circle has a radius). Disposing the canvas finishes the file and writes it to the stream,
/// which is left open. An image or picture drawn several times is stored once, as it was when first drawn.
/// </para>
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Svg.cs" region="svg-canvas"/>
/// </example>
public sealed partial class SvgCanvas : Canvas
{
    /// <summary>The most clipPaths a chain holds before it is written as one.</summary>
    private const int MaxClipChain = 16;

    private readonly Stream? _stream;
    private readonly SvgOutput _out;
    private readonly SvgMarkup _body;
    private readonly List<int> _groups = [0];
    private readonly List<string?> _clips = [null];
    private readonly float _fallbackScale;
    private int _quiet;

    /// <summary>Initializes a new instance of the <see cref="SvgCanvas"/> class that writes one SVG file to a stream when disposed.</summary>
    /// <param name="stream">Where the file goes, from its current position; left open.</param>
    /// <param name="width">The drawing's width: the root's width and its viewBox's, in device units (CSS pixels).</param>
    /// <param name="height">The drawing's height.</param>
    /// <param name="options">How to write the file, or <see langword="null"/> for <see cref="SvgWriteOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Svg.cs" region="svg-canvas"/>
    /// </example>
    public SvgCanvas(Stream stream, float width, float height, SvgWriteOptions? options = null)
        : base(width, height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _stream = stream;
        _out = new SvgOutput(options ?? SvgWriteOptions.Default);
        _body = new SvgMarkup(_out.Compact, 1);
        _fallbackScale = _out.Options.FallbackScale;
    }

    /// <summary>A canvas writing a fragment of the same file: a picture's, a pattern's or a mask's content.</summary>
    private SvgCanvas(SvgOutput output, float width, float height, int depth, float fallbackScale)
        : base(width, height)
    {
        _out = output;
        _body = new SvgMarkup(output.Compact, depth);
        _fallbackScale = fallbackScale;
    }

    /// <summary>Gets the options the file is written with.</summary>
    public SvgWriteOptions Options => _out.Options;

    private SvgNumbers Num => _out.Numbers;

    /// <inheritdoc/>
    protected override void OnSave()
    {
        PushLevel();
        if (_recorder is not null)
        {
            _recorder.Save();
            _recordDepth++;
        }
    }

    /// <inheritdoc/>
    protected override void OnRestore()
    {
        CloseLevel();
        if (_recorder is not null)
        {
            _recorder.Restore();
            if (--_recordDepth == 0)
            {
                FinishRecording();
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipRect(rect, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(Path.Rect(rect), op, antialias);
        }
    }

    /// <inheritdoc/>
    protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipRoundRect(roundRect, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(Path.RoundRect(roundRect), op, antialias);
        }
    }

    /// <inheritdoc/>
    protected override void OnClipPath(Path path, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipPath(path, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(path, op, antialias);
        }
    }

    /// <inheritdoc path="/*[not(self::example)]"/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Drawing.cs" region="backend"/>
    /// </example>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
    {
        if (Recording())
        {
            if (stroke is null)
            {
                _recorder!.FillPath(path, paint);
            }
            else
            {
                _recorder!.StrokePath(path, stroke, paint);
            }
        }
        else if (_quiet == 0)
        {
            DrawShape(path, stroke, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawAnnotation(RectF area, Annotation annotation)
    {
        if (_quiet > 0 || annotation.Kind != AnnotationKind.Url || area.IsEmpty)
        {
            return;
        }

        _body.Open("a", new SvgAttrs().Add("xlink:href", annotation.Value).Add("clip-path", ClipRef));
        _body.Empty("path", new SvgAttrs()
            .Add("d", Path.Rect(area).ToSvg(_out.PathFormat))
            .Add("transform", Num.Transform(Matrix))
            .Add("fill", "#000")
            .Add("fill-opacity", "0"));
        _body.Close();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        while (_groups[0]-- > 0)
        {
            _body.Close();
        }

        if (_stream is not null)
        {
            WriteFile(_stream);
        }

        base.Dispose(disposing);
    }

    private void WriteFile(Stream stream)
    {
        StringBuilder sb = new();
        string nl = _out.Compact ? string.Empty : "\n";
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(nl);
        sb.Append("<svg xmlns=\"").Append(SvgNamespaces.Svg).Append("\" xmlns:xlink=\"").Append(SvgNamespaces.XLink).Append('"');
        sb.Append(" width=\"").Append(Num.F(Width)).Append("\" height=\"").Append(Num.F(Height)).Append('"');
        sb.Append(" viewBox=\"0 0 ").Append(Num.F(Width)).Append(' ').Append(Num.F(Height)).Append("\">");
        string? fonts = FontFaces();
        if (fonts is not null || _out.Definitions.Count > 0)
        {
            string inner = _out.Compact ? string.Empty : "\n    ";
            sb.Append(nl).Append(_out.Compact ? string.Empty : "  ").Append("<defs>");
            if (fonts is not null)
            {
                sb.Append(inner).Append("<style type=\"text/css\">");
                SvgMarkup.Escape(sb, fonts, attribute: false);
                sb.Append("</style>");
            }

            foreach (string definition in _out.Definitions)
            {
                sb.Append(inner).Append(definition);
            }

            sb.Append(nl).Append(_out.Compact ? string.Empty : "  ").Append("</defs>");
        }

        sb.Append(_body.ToString()).Append(nl).Append("</svg>").Append(nl);
        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        stream.Write(bytes);
        stream.Flush();
        _out.Deliver();
    }

    /// <summary>Finishes a fragment canvas and returns its markup.</summary>
    private string Finish()
    {
        Dispose();
        return _body.ToString();
    }

    private SvgCanvas Fragment(float width, float height, int depth, float fallbackScale) => new(_out, width, height, depth, fallbackScale);

    /// <summary>Gets the clip-path value of the current clip, or <see langword="null"/> when nothing is clipped in this group.</summary>
    private string? ClipRef => _clips[^1] is string id ? $"url(#{id})" : null;

    /// <summary>
    /// Opens a group clipped by the current clip around an element with a transform: a clip-path on the element itself would be
    /// read in its transformed coordinates, and clips are in device coordinates.
    /// </summary>
    private bool OpenClip()
    {
        if (ClipRef is not string clip)
        {
            return false;
        }

        _body.Open("g", new SvgAttrs().Add("clip-path", clip));
        return true;
    }

    private void CloseClip(bool opened)
    {
        if (opened)
        {
            _body.Close();
        }
    }

    private void PushLevel()
    {
        _groups.Add(0);
        _clips.Add(_clips[^1]);
    }

    private void OpenGroup(SvgAttrs attrs)
    {
        _body.Open("g", attrs);
        _groups[^1]++;
    }

    private void CloseLevel()
    {
        for (int n = _groups[^1]; n > 0; n--)
        {
            _body.Close();
        }

        _groups.RemoveAt(_groups.Count - 1);
        _clips.RemoveAt(_clips.Count - 1);
    }

    private void WriteClip(Path path, ClipOp op, bool antialias)
    {
        Path clip = path;
        if (op == ClipOp.Difference)
        {
            if (path.IsEmpty)
            {
                return;
            }

            clip = Outside(path);
        }

        Matrix3x2 matrix = Matrix;
        string? parent = _clips[^1];
        Path device = clip.IsEmpty ? Path.Empty : clip.Transform(matrix);
        int depth = parent is null ? 1 : _out.Clips[parent].Depth + 1;
        if (depth > MaxClipChain)
        {
            // A long chain of clipPaths is cut: readers limit how deep references nest (Egami.Svg at 32, resvg lower), so the
            // whole chain is written as one clipPath of the areas' intersection.
            for (string? up = parent; up is not null && !device.IsEmpty; up = _out.Clips[up].Parent)
            {
                device = device.Intersect(_out.Clips[up].Device);
            }

            clip = device;
            matrix = Matrix3x2.Identity;
            parent = null;
            depth = 1;
        }
        else if (parent is not null)
        {
            // The clip before is named on the path, whose own user space must then be device space: Inkscape ignores a
            // clip-path on the clipPath element itself.
            clip = device;
            matrix = Matrix3x2.Identity;
        }

        string? inner = null;
        if (!clip.IsEmpty)
        {
            inner = Child(SvgOutput.DefDepth + 1, "path", new SvgAttrs()
                .Add("d", clip.ToSvg(_out.PathFormat))
                .Add("transform", Num.Transform(matrix))
                .Add("clip-rule", clip.FillRule == FillRule.EvenOdd ? "evenodd" : null)
                .Add("shape-rendering", antialias ? null : "crispEdges")
                .Add("clip-path", parent is null ? null : $"url(#{parent})"));
        }

        // Each clip is a clipPath whose path is clipped by the one before, and every element names the last: clips add no nesting.
        string id = _out.Define("c", "clipPath", new SvgAttrs().Add("clipPathUnits", "userSpaceOnUse"), inner);
        _out.Clips.TryAdd(id, (parent, device, depth));
        _clips[^1] = id;
    }

    /// <summary>The area outside a path, within a rectangle that covers the clip and the path: a difference clip as an ordinary one.</summary>
    private Path Outside(Path path)
    {
        RectF area = LocalClipBounds;
        RectF bounds = path.Bounds;
        RectF big = (area.IsEmpty ? bounds : RectF.FromEdges(
            MathF.Min(area.Left, bounds.Left),
            MathF.Min(area.Top, bounds.Top),
            MathF.Max(area.Right, bounds.Right),
            MathF.Max(area.Bottom, bounds.Bottom))).Inflate(1, 1);
        try
        {
            return Path.Rect(big).Difference(path);
        }
        catch (ArgumentException)
        {
            // Even-odd of the rectangle and the path: right for a path whose contours do not overlap.
            return new PathBuilder().AddPath(Path.Rect(big)).AddPath(path).ToPath(FillRule.EvenOdd);
        }
    }

    /// <summary>A child element's markup, on its own line at a depth.</summary>
    private string Child(int depth, string name, SvgAttrs attrs, string? inner = null)
        => (_out.Compact ? string.Empty : "\n" + new string(' ', depth * 2)) + SvgMarkup.Element(name, attrs, inner, _out.Compact, depth);

    private static RectF MapRect(RectF rect, Matrix3x2 m)
        => RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(rect.Left, rect.Top), m),
            Vector2.Transform(new Vector2(rect.Right, rect.Top), m),
            Vector2.Transform(new Vector2(rect.Right, rect.Bottom), m),
            Vector2.Transform(new Vector2(rect.Left, rect.Bottom), m),
        ]);

    /// <summary>What a blend mode becomes: drawn as a CSS mix-blend-mode (or plain), or left out.</summary>
    private (bool Skip, string? Css) Blend(BlendMode mode, Paint? paint)
    {
        switch (mode)
        {
            case BlendMode.SourceOver:
                return (false, null);
            case BlendMode.Destination:
                return (true, null);
            case BlendMode.Clear:
                Approximated("The Clear blend mode cannot erase in SVG; the call was left out.");
                return (true, null);
            case BlendMode.Source:
                if (paint is not SolidPaint solid || solid.Color.A * solid.Opacity < 1)
                {
                    Approximated("The Source blend mode was drawn as source-over: SVG cannot replace what lies below.");
                }

                return (false, null);
        }

        string? css = CssBlend(mode);
        if (css is null)
        {
            Approximated($"The {mode} blend mode has no SVG form; it was drawn as source-over.");
        }

        return (false, css);
    }

    private static string? CssBlend(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        BlendMode.Overlay => "overlay",
        BlendMode.Darken => "darken",
        BlendMode.Lighten => "lighten",
        BlendMode.ColorDodge => "color-dodge",
        BlendMode.ColorBurn => "color-burn",
        BlendMode.HardLight => "hard-light",
        BlendMode.SoftLight => "soft-light",
        BlendMode.Difference => "difference",
        BlendMode.Exclusion => "exclusion",
        BlendMode.Hue => "hue",
        BlendMode.Saturation => "saturation",
        BlendMode.Color => "color",
        BlendMode.Luminosity => "luminosity",
        BlendMode.Plus => "plus-lighter",
        _ => null,
    };

    private static string? Style(params ReadOnlySpan<string?> declarations)
    {
        StringBuilder? sb = null;
        foreach (string? d in declarations)
        {
            if (d is not null)
            {
                (sb ??= new()).Append(sb.Length > 0 ? ";" : string.Empty).Append(d);
            }
        }

        return sb?.ToString();
    }

    private static string? BlendDeclaration(string? css) => css is null ? null : "mix-blend-mode:" + css;

    private void Approximated(string message) => _out.Report(DiagnosticLevel.Warning, "Svg.Approximated", message);
}
