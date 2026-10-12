using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>How deep SVG images may hold SVG images; deeper ones are not drawn.</summary>
    private const int MaxImageDepth = 8;

    /// <summary>
    /// An image: a raster through Egami's decoders, or an SVG document drawn as a group (question 6: data: URIs always, other
    /// URIs only through the resolver). A missing width or height takes the image's own size, keeping its aspect ratio; the
    /// image is placed by preserveAspectRatio and clipped to its viewport.
    /// </summary>
    private SvgImage? Image(SvgElement element, SvgStyle style, in SvgContext context)
    {
        if (!style.Visible)
        {
            return null;
        }

        string? href = element.Attributes["href"] ?? element.Attributes.Get("href", SvgNamespaces.XLink);
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        byte[]? data = SvgResources.Open(href, _options, out Uri? location, out string? problem);
        if (data is null)
        {
            _report.Warn("Svg.ImageNotLoaded", $"The image \"{SvgGeometry.Clip(href)}\" was not loaded: {problem}", element);
            return null;
        }

        EncodedImage? raster = null;
        SvgDocument? document = null;
        RectF view;
        if (SvgResources.LooksLikeSvg(data))
        {
            if (_options.ImageDepth >= MaxImageDepth)
            {
                _report.Warn("Svg.ImageNotLoaded", $"SVG images nest more than {MaxImageDepth} deep; the image \"{SvgGeometry.Clip(href)}\" is not drawn.", element);
                return null;
            }

            List<Uri> chain = [.. _options.ImageChain];
            if (_options.BaseUri is Uri self)
            {
                chain.Add(self);
            }

            if (location is not null && chain.Contains(location))
            {
                _report.Warn("Svg.ImageNotLoaded", $"The SVG image \"{SvgGeometry.Clip(href)}\" is a document that holds it; it is not drawn again.", element);
                return null;
            }

            try
            {
                document = SvgDocument.Load(new MemoryStream(data), _options with { BaseUri = location, ImageDepth = _options.ImageDepth + 1, ImageChain = chain, Strict = false });
            }
            catch (EgamiException e)
            {
                _report.Warn("Svg.ImageNotLoaded", $"The SVG image \"{SvgGeometry.Clip(href)}\" cannot be read: {e.Message}", element);
                return null;
            }

            view = document.ViewBox ?? new RectF(0, 0, document.Size.X, document.Size.Y);
        }
        else
        {
            try
            {
                raster = EncodedImage.FromBytes(data, _options.EffectiveContext);
            }
            catch (EgamiException e)
            {
                _report.Warn("Svg.ImageNotLoaded", $"The image \"{SvgGeometry.Clip(href)}\" cannot be decoded: {e.Message}", element);
                return null;
            }

            view = new RectF(0, 0, raster.Width, raster.Height);
        }

        if (!(view.Width > 0 && view.Height > 0))
        {
            return null;
        }

        Vector2 intrinsic = document is null ? view.Size : document.Size;
        float x = _geometry.Length(element, "x", context.Lengths, SvgAxis.X, 0);
        float y = _geometry.Length(element, "y", context.Lengths, SvgAxis.Y, 0);
        float? w = _geometry.Auto(element, "width", context.Lengths, SvgAxis.X);
        float? h = _geometry.Auto(element, "height", context.Lengths, SvgAxis.Y);
        float width = w ?? (h is float hh ? hh * intrinsic.X / intrinsic.Y : intrinsic.X);
        float height = h ?? (w is float ww ? ww * intrinsic.Y / intrinsic.X : intrinsic.Y);
        if (!(width > 0 && height > 0))
        {
            return null;
        }

        RectF viewport = new(x, y, width, height);
        SvgAspectRatio aspect = Aspect(element);
        Matrix3x2 fit = aspect.Transform(view, viewport);
        RectF destination = new(fit.M31 + (view.X * fit.M11), fit.M32 + (view.Y * fit.M22), view.Width * fit.M11, view.Height * fit.M22);
        SvgImage image = new()
        {
            Raster = raster,
            Document = document,
            Source = view,
            Destination = destination,
            ClipRect = aspect.Slice || document is not null ? viewport : null,
            Sampling = style.ImageRendering == SvgStyle.SamplingKind.Pixelated ? new SamplingOptions { Filter = ResampleFilter.Nearest } : null,
            Bounds = viewport,
        };
        return Decorate(image with { Transform = ElementTransform(element, style, viewport, context.Lengths) }, element, style, context);
    }
}
