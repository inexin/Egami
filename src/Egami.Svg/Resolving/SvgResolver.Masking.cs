using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>
    /// A clip path for an element with these bounds. A reference to a missing element is reported and ignored (the element is
    /// drawn unclipped, as resvg and browsers do); a valid clip path that clips everything sets <paramref name="hidden"/>.
    /// </summary>
    private SvgClip? ClipPath(SvgElement element, string url, RectF bounds, in SvgContext context, ref bool hidden)
    {
        SvgElement? target = SvgStyleValues.LocalId(url) is string id ? _document.FindById(id) : null;
        if (target is null || target.Name != "clipPath" || !target.IsSvgOrBare(_document))
        {
            _report.Warn("Svg.BadReference", $"The clip-path \"url({SvgGeometry.Clip(url)})\" names no clipPath; it is ignored.", element);
            return null;
        }

        SvgClip? clip = ClipOf(target, element, bounds, context, out bool cyclic);
        if (clip is null && !cyclic)
        {
            hidden = true;
        }

        return clip;
    }

    /// <summary>Builds a clip path, or null when it clips everything away.</summary>
    private SvgClip? ClipOf(SvgElement clipPath, SvgElement user, RectF bounds, in SvgContext context, out bool cyclic)
    {
        cyclic = false;
        if (!Enter(clipPath, user))
        {
            cyclic = true;
            return null;
        }

        try
        {
            (SvgStyle clipStyle, CssComputedStyle clipCss) = OriginalStyle(clipPath);
            Matrix3x2 transform = ElementTransform(clipPath, clipStyle, bounds, context.Lengths);
            if (clipPath.Attributes["clipPathUnits"] == "objectBoundingBox")
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                transform = new Matrix3x2(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y) * transform;
            }

            SvgContext inner = new(clipStyle, clipCss, context.Lengths, context.Depth + 1, null, InClip: true);
            List<SvgNode> children = [];
            foreach (SvgElement child in clipPath.Elements())
            {
                if (!child.IsSvgOrBare(_document) || child.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "text" or "use"))
                {
                    continue;
                }

                if (child.Name == "use" && Reference(child, "use") is SvgElement used && used.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "text"))
                {
                    continue;
                }

                SvgNode? node = Element(child, inner);
                if (node is not null && ClipChildClip(child, node, inner) is { Opacity: > 0 } clipped)
                {
                    children.Add(clipped);
                }
            }

            SvgClip? own = null;
            if (clipStyle.ClipPath is string ownUrl)
            {
                bool hidden = false;
                own = ClipPath(clipPath, ownUrl, bounds, context, ref hidden);
                if (hidden)
                {
                    return null;
                }
            }

            if (children.Count == 0)
            {
                return null;
            }

            if (own is null && children.Count == 1 && children[0] is SvgPath { Clip: null, Mask: null } single)
            {
                return new SvgClip(single.Path.Transform(single.Transform * transform));
            }

            SvgGroup content = new() { Children = children, Transform = transform, Bounds = Bounds(children), Clip = own };
            return new SvgClip(Path.Rect(Transformed(content))) { Content = content };
        }
        finally
        {
            Leave(clipPath);
        }
    }

    /// <summary>A clip path child keeps its own clip-path, the only one of its effects that applies inside a clip path.</summary>
    private SvgNode ClipChildClip(SvgElement child, SvgNode node, in SvgContext inner)
    {
        (SvgStyle style, _) = OriginalStyle(child);
        if (style.ClipPath is not string url)
        {
            return node;
        }

        bool hidden = false;
        SvgClip? clip = ClipPath(child, url, node.Bounds, inner with { InClip = false }, ref hidden);
        return hidden ? node with { Opacity = 0 } : node with { Clip = clip };
    }

    /// <summary>
    /// A mask for an element with these bounds. A reference to a missing element is reported and ignored; a valid mask whose
    /// region is empty sets <paramref name="hidden"/>.
    /// </summary>
    private SvgMask? Mask(SvgElement element, string url, RectF bounds, in SvgContext context, ref bool hidden)
    {
        SvgElement? target = SvgStyleValues.LocalId(url) is string id ? _document.FindById(id) : null;
        if (target is null || target.Name != "mask" || !target.IsSvgOrBare(_document))
        {
            _report.Warn("Svg.BadReference", $"The mask \"url({SvgGeometry.Clip(url)})\" names no mask; it is ignored.", element);
            return null;
        }

        SvgMask? mask = MaskOf(target, element, bounds, context, out bool cyclic);
        if (mask is null && !cyclic)
        {
            hidden = true;
        }

        return mask;
    }

    private SvgMask? MaskOf(SvgElement mask, SvgElement user, RectF bounds, in SvgContext context, out bool cyclic)
    {
        cyclic = false;
        bool boundingBox = mask.Attributes["maskUnits"] is not "userSpaceOnUse";
        if (boundingBox && (bounds.Width <= 0 || bounds.Height <= 0))
        {
            return null;
        }

        SvgLengthContext lengths = context.Lengths;
        float Region(string name, string fallback, SvgAxis axis)
        {
            if (!(mask.Attributes[name] is string text && SvgLength.TryParse(text, out SvgLength l)))
            {
                _ = SvgLength.TryParse(fallback, out l);
            }

            if (!boundingBox)
            {
                return l.ToPixels(lengths, axis);
            }

            float f = l.ToFraction();
            return name switch
            {
                "x" => bounds.X + (f * bounds.Width),
                "y" => bounds.Y + (f * bounds.Height),
                "width" => f * bounds.Width,
                _ => f * bounds.Height,
            };
        }

        RectF region = new(Region("x", "-10%", SvgAxis.X), Region("y", "-10%", SvgAxis.Y), Region("width", "120%", SvgAxis.X), Region("height", "120%", SvgAxis.Y));
        if (!(region.Width > 0 && region.Height > 0))
        {
            return null;
        }

        if (!Enter(mask, user))
        {
            cyclic = true;
            return null;
        }

        try
        {
            (SvgStyle style, CssComputedStyle css) = OriginalStyle(mask);
            Matrix3x2 content = Matrix3x2.Identity;
            if (mask.Attributes["maskContentUnits"] == "objectBoundingBox")
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                content = new Matrix3x2(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y);
            }

            SvgContext inner = new(style, css, lengths, context.Depth + 1, null, false);
            List<SvgNode> children = Children(mask, inner);
            SvgMask? own = null;
            if (style.Mask is string ownUrl)
            {
                bool hidden = false;
                own = Mask(mask, ownUrl, bounds, context, ref hidden);
                if (hidden)
                {
                    return null;
                }
            }

            return new SvgMask(new SvgGroup { Children = children, Transform = content, Bounds = Bounds(children) })
            {
                Region = region,
                Kind = style.MaskType,
                Mask = own,
            };
        }
        finally
        {
            Leave(mask);
        }
    }
}
