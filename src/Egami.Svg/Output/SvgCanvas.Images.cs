using System.Numerics;

using Egami.Drawing;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Svg.Output;

namespace Egami.Svg;

public sealed partial class SvgCanvas
{
    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Recording())
        {
            _recorder!.DrawImage(image, source, destination, sampling, paint);
        }
        else if (_quiet == 0)
        {
            PlaceImage(() => ImageDefinition(image), image.Width, image.Height, source, destination, sampling, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Recording())
        {
            _recorder!.DrawImage(image, source, destination, sampling, paint);
        }
        else if (_quiet == 0)
        {
            PlaceImage(() => EncodedDefinition(image), image.Width, image.Height, source, destination, sampling, paint);
        }
    }

    /// <summary>An image in defs, written once per raster.</summary>
    private string ImageDefinition(Raster image)
        => _out.DefineOnce(image, "i", id => ImageElement(id, image.Width, image.Height, _out.ImageUri(image, allowJpeg: true)));

    /// <summary>An encoded image in defs: a PNG's or JPEG's own bytes, other formats encoded again.</summary>
    private string EncodedDefinition(EncodedImage image)
        => _out.DefineOnce(image, "i", id =>
        {
            // A file's own bytes, unless an EXIF orientation would turn it in a browser: Egami draws the pixels as stored.
            string format = image.Info.Orientation == Egami.Metadata.ImageOrientation.Normal ? image.Info.Format.Name : string.Empty;
            string uri = format == PngFormat.Info.Name ? SvgOutput.DataUri("image/png", image.Data.Span)
                : format == JpegFormat.Info.Name ? SvgOutput.DataUri("image/jpeg", image.Data.Span)
                : _out.ImageUri(image.Decode(), allowJpeg: true);
            return ImageElement(id, image.Width, image.Height, uri);
        });

    private string ImageElement(string id, int width, int height, string uri)
        => SvgMarkup.Element("image", new SvgAttrs()
            .Add("id", id)
            .Add("width", Num.F(width)).Add("height", Num.F(height))
            .Add("preserveAspectRatio", "none")
            .Add("xlink:href", uri), null, _out.Compact, SvgOutput.DefDepth);

    /// <summary>Places part of an image defined in defs: the whole image as a use, a part clipped to its destination.</summary>
    private void PlaceImage(Func<string> define, int width, int height, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        (bool skip, string? blend) = Blend(paint?.BlendMode ?? BlendMode.SourceOver, null);
        if (skip)
        {
            return;
        }

        string id = define();
        bool nearest = sampling.Filter == ResampleFilter.Nearest;
        float opacity = paint?.Opacity ?? 1;
        string? style = Style(BlendDeclaration(blend), nearest ? "image-rendering:pixelated" : null);
        float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        Matrix3x2 place = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(destination.X - (source.X * sx), destination.Y - (source.Y * sy));
        bool clipped = OpenClip();
        SvgAttrs Use(Matrix3x2 transform) => new SvgAttrs()
            .Add("xlink:href", "#" + id)
            .Add("transform", Num.Transform(transform))
            .Add("opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null)
            .Add("image-rendering", nearest ? "optimizeSpeed" : null)
            .Add("style", style);
        if (source == new RectF(0, 0, width, height))
        {
            _body.Empty("use", Use(place * Matrix));
        }
        else
        {
            // A part of the image: the whole of it placed so the part fills the destination, clipped to the destination.
            string crop = _out.Define("c", "clipPath", new SvgAttrs().Add("clipPathUnits", "userSpaceOnUse"), Child(SvgOutput.DefDepth + 1, "path", new SvgAttrs().Add("d", Path.Rect(destination).ToSvg(_out.PathFormat))));
            _body.Open("g", new SvgAttrs().Add("transform", Num.Transform(Matrix)).Add("clip-path", $"url(#{crop})"));
            _body.Empty("use", Use(place));
            _body.Close();
        }

        CloseClip(clipped);
    }
}
