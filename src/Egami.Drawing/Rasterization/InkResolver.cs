using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Turns paint colours into tints for an ink canvas's planes, one value 0 to 1 per plane (disabled spots have none): an ink colour
/// by name, its inks the canvas lacks drawn through their appearance into the process inks (combined with the named process
/// inks in complement space), and RGB colours through a transform from the canvas's RGB encoding into its process profile.
/// Inks a colour does not name get 0, which is what a fill without overprint leaves.
/// </summary>
internal sealed class InkResolver
{
    private readonly InkSet _inks;
    private readonly int[] _planeOf;          // set index to plane index, -1 for a disabled spot
    private readonly int _process;
    private readonly ColorTransform _fromRgb;
    private readonly ColorTransformOptions _options;
    private readonly Dictionary<InkAppearanceKind, ColorTransform> _appearance = [];

    internal InkResolver(InkSet inks, ColorProfile rgbSource, ColorTransformOptions options)
    {
        _inks = inks;
        _options = options;
        _planeOf = new int[inks.Count];
        for (int i = 0, plane = 0; i < inks.Count; i++)
        {
            _planeOf[i] = inks.IsDisabled(i) ? -1 : plane++;
        }

        Planes = _planeOf.Count(p => p >= 0);
        _process = inks.ProcessCount;
        _fromRgb = ColorTransform.Create(rgbSource, inks.Profile, options);
    }

    /// <summary>Gets the number of planes, alpha not counted.</summary>
    internal int Planes { get; }

    /// <summary>Writes a solid paint's tints; returns its alpha (an ink colour is opaque, an RGB one has its own).</summary>
    internal float Resolve(SolidPaint paint, Span<float> tints)
    {
        tints.Clear();
        if (paint.Ink is not InkColor ink)
        {
            Span<float> process = stackalloc float[_process];
            ResolveRgb(paint.Color, process);
            for (int i = 0; i < _process; i++)
            {
                tints[_planeOf[i]] = process[i];
            }

            return Math.Clamp(paint.Color.A, 0, 1);
        }

        Resolve(ink, tints);
        return 1;
    }

    /// <summary>Writes an ink colour's tints, its missing inks through their appearance.</summary>
    internal void Resolve(InkColor ink, Span<float> tints)
    {
        tints.Clear();
        List<InkTint>? missing = null;
        foreach (InkTint t in ink.Inks)
        {
            int index = _inks.IndexOf(t.Colorant.Name);
            if (index >= 0 && _planeOf[index] >= 0)
            {
                tints[_planeOf[index]] = t.Tint;
            }
            else
            {
                (missing ??= []).Add(t);
            }
        }

        if (missing is null || InkColor.Mix([.. missing]).Appearance is not InkAppearance appearance)
        {
            return;
        }

        Span<float> process = stackalloc float[_process];
        ResolveAppearance(appearance, process);
        for (int i = 0; i < _process; i++)
        {
            int plane = _planeOf[i];
            tints[plane] = 1 - ((1 - tints[plane]) * (1 - process[i]));
        }
    }

    /// <summary>Converts straight RGB colours to tints, interleaved per colour over every plane; spots get 0.</summary>
    internal void ResolveRgb(ReadOnlySpan<RgbaF32> colors, Span<float> tints)
    {
        int n = colors.Length;
        Span<float> rgb = n <= 256 ? stackalloc float[n * 3] : new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            (rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2]) = (colors[i].R, colors[i].G, colors[i].B);
        }

        Span<float> process = n * _process <= 2048 ? stackalloc float[n * _process] : new float[n * _process];
        _fromRgb.Apply(rgb, process);
        tints[..(n * Planes)].Clear();
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < _process; c++)
            {
                tints[(i * Planes) + _planeOf[c]] = Math.Clamp(process[(i * _process) + c], 0, 1);
            }
        }
    }

    private void ResolveRgb(RgbaF32 color, Span<float> process)
    {
        _fromRgb.Apply([color.R, color.G, color.B], process);
        Clamp(process);
    }

    /// <summary>
    /// An appearance as process tints: CMYK as it is when the canvas prints CMYK, else through FOGRA39 into the process profile;
    /// sRGB through the profile; Lab through sRGB values outside 0 to 1 where it must.
    /// </summary>
    private void ResolveAppearance(InkAppearance appearance, Span<float> process)
    {
        System.Numerics.Vector4 v = appearance.Values;
        if (appearance.Kind == InkAppearanceKind.Cmyk && _inks.Profile.Model == ColorModel.Cmyk)
        {
            (process[0], process[1], process[2], process[3]) = (v.X, v.Y, v.Z, v.W);
            return;
        }

        if (appearance.Kind == InkAppearanceKind.Cmyk)
        {
            if (!_appearance.TryGetValue(InkAppearanceKind.Cmyk, out ColorTransform? cmyk))
            {
                cmyk = ColorTransform.Create(ColorProfile.Fogra39, _inks.Profile, _options);
                _appearance[InkAppearanceKind.Cmyk] = cmyk;
            }

            cmyk.Apply([v.X, v.Y, v.Z, v.W], process);
            Clamp(process);
            return;
        }

        if (!_appearance.TryGetValue(InkAppearanceKind.Rgb, out ColorTransform? srgb))
        {
            srgb = ColorTransform.Create(ColorProfile.Srgb, _inks.Profile, _options);
            _appearance[InkAppearanceKind.Rgb] = srgb;
        }

        RgbaF32 c = appearance.Kind == InkAppearanceKind.Rgb ? new RgbaF32(v.X, v.Y, v.Z, 1) : appearance.ToLab().ToRgbaF32();
        srgb.Apply([c.R, c.G, c.B], process);
        Clamp(process);
    }

    private static void Clamp(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Math.Clamp(values[i], 0, 1);
        }
    }
}
