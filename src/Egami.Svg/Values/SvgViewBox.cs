using System.Numerics;

using Egami.Drawing;

namespace Egami.Svg.Values;

/// <summary>How preserveAspectRatio aligns a viewBox in a viewport.</summary>
internal enum SvgAlign : byte
{
    None,
    XMinYMin,
    XMidYMin,
    XMaxYMin,
    XMinYMid,
    XMidYMid,
    XMaxYMid,
    XMinYMax,
    XMidYMax,
    XMaxYMax,
}

/// <summary>The preserveAspectRatio attribute: an alignment and meet or slice (SVG 1.1 section 7.8).</summary>
internal readonly record struct SvgAspectRatio(SvgAlign Align, bool Slice)
{
    /// <summary>The default, <c>xMidYMid meet</c>.</summary>
    public static SvgAspectRatio Default { get; } = new(SvgAlign.XMidYMid, false);

    /// <summary>Reads the attribute; <c>defer</c> is accepted and ignored, as SVG 2 drops it.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out SvgAspectRatio value)
    {
        value = Default;
        Span<Range> parts = stackalloc Range[4];
        int count = text.Trim().SplitAny(parts, " \t\r\n", StringSplitOptions.RemoveEmptyEntries);
        ReadOnlySpan<char> trimmed = text.Trim();
        int i = 0;
        if (count > i && trimmed[parts[i]].SequenceEqual("defer"))
        {
            i++;
        }

        if (count <= i)
        {
            return false;
        }

        SvgAlign align;
        switch (trimmed[parts[i]])
        {
            case "none": align = SvgAlign.None; break;
            case "xMinYMin": align = SvgAlign.XMinYMin; break;
            case "xMidYMin": align = SvgAlign.XMidYMin; break;
            case "xMaxYMin": align = SvgAlign.XMaxYMin; break;
            case "xMinYMid": align = SvgAlign.XMinYMid; break;
            case "xMidYMid": align = SvgAlign.XMidYMid; break;
            case "xMaxYMid": align = SvgAlign.XMaxYMid; break;
            case "xMinYMax": align = SvgAlign.XMinYMax; break;
            case "xMidYMax": align = SvgAlign.XMidYMax; break;
            case "xMaxYMax": align = SvgAlign.XMaxYMax; break;
            default: return false;
        }

        i++;
        bool slice = false;
        if (count > i)
        {
            switch (trimmed[parts[i]])
            {
                case "meet": break;
                case "slice": slice = true; break;
                default: return false;
            }

            i++;
        }

        if (count > i)
        {
            return false;
        }

        value = new SvgAspectRatio(align, slice);
        return true;
    }

    /// <summary>The transform that maps a viewBox onto a viewport rectangle (SVG 2's "equivalent transform of a viewBox").</summary>
    public Matrix3x2 Transform(RectF viewBox, RectF viewport)
    {
        float sx = viewport.Width / viewBox.Width;
        float sy = viewport.Height / viewBox.Height;
        if (Align != SvgAlign.None)
        {
            float s = Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = s;
            sy = s;
        }

        float tx = viewport.X - (viewBox.X * sx);
        float ty = viewport.Y - (viewBox.Y * sy);
        float freeX = viewport.Width - (viewBox.Width * sx);
        float freeY = viewport.Height - (viewBox.Height * sy);
        switch (Align)
        {
            case SvgAlign.XMidYMin or SvgAlign.XMidYMid or SvgAlign.XMidYMax:
                tx += freeX / 2;
                break;
            case SvgAlign.XMaxYMin or SvgAlign.XMaxYMid or SvgAlign.XMaxYMax:
                tx += freeX;
                break;
        }

        switch (Align)
        {
            case SvgAlign.XMinYMid or SvgAlign.XMidYMid or SvgAlign.XMaxYMid:
                ty += freeY / 2;
                break;
            case SvgAlign.XMinYMax or SvgAlign.XMidYMax or SvgAlign.XMaxYMax:
                ty += freeY;
                break;
        }

        return new Matrix3x2(sx, 0, 0, sy, tx, ty);
    }
}

/// <summary>The viewBox attribute.</summary>
internal static class SvgViewBox
{
    /// <summary>
    /// Reads <c>min-x min-y width height</c>. A negative width or height is an error; a zero one is valid but disables rendering
    /// of the element (the caller checks <see cref="RectF.IsEmpty"/>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out RectF viewBox)
    {
        viewBox = default;
        Span<double> v = stackalloc double[4];
        int i = 0;
        SvgNumbers.SkipSpace(text, ref i);
        for (int n = 0; n < 4; n++)
        {
            if (!SvgNumbers.TryRead(text, ref i, out v[n]))
            {
                return false;
            }

            SvgNumbers.SkipSeparator(text, ref i);
        }

        if (i != text.Length || v[2] < 0 || v[3] < 0)
        {
            return false;
        }

        viewBox = new RectF((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
        return viewBox.IsFinite;
    }
}
