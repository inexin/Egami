using Egami.Colors;
using Egami.Drawing.Shading;
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

    // A layer that blends in an RGB or gray space: the canvas's resolver, and the transforms between its inks and that space.
    private readonly InkResolver? _canvas;
    private readonly ColorProfile? _space;
    private readonly ColorTransform? _toSpace;
    private readonly ColorTransform? _fromSpace;
    private readonly ColorTransform? _rgbToSpace;

    internal InkResolver(InkSet inks, ColorProfile rgbSource, ColorTransformOptions options)
    {
        _inks = inks;
        _options = options;
        RgbSource = rgbSource;
        _planeOf = new int[inks.Count];
        for (int i = 0, plane = 0; i < inks.Count; i++)
        {
            _planeOf[i] = inks.IsDisabled(i) ? -1 : plane++;
        }

        Planes = _planeOf.Count(p => p >= 0);
        _process = inks.ProcessCount;
        int c = ProcessPlane("Cyan"), m = ProcessPlane("Magenta"), y = ProcessPlane("Yellow");
        CmyPlanes = c >= 0 && m >= 0 && y >= 0 ? [c, m, y] : null;
        BlackPlane = ProcessPlane("Black");
        _fromRgb = ColorTransform.Create(rgbSource, inks.Profile, options);
    }

    /// <summary>
    /// A resolver for a layer that blends in <paramref name="space"/>: planes 0 to 2 hold its colour as the complements of red,
    /// green and blue (of gray thrice), so blending on complements blends the colour itself; spots keep their own planes after
    /// a fourth, unused one.
    /// </summary>
    private InkResolver(InkResolver canvas, ColorProfile space, InkSet layout)
        : this(layout, canvas.RgbSource, canvas.Options)
    {
        _canvas = canvas;
        _space = space;
        _toSpace = ColorTransform.Create(canvas.Profile, space, canvas.Options);
        _fromSpace = ColorTransform.Create(space, canvas.Profile, canvas.Options);
        _rgbToSpace = ColorTransform.Create(canvas.RgbSource, space, canvas.Options);
        CmyPlanes = [0, 1, 2];
        BlackPlane = -1;
    }

    /// <summary>Gets the space a group layer blends in, or <see langword="null"/> for the canvas's inks.</summary>
    internal ColorProfile? Space => _space;

    /// <summary>Returns the resolver and the layout of a layer that blends in <paramref name="space"/>.</summary>
    internal (InkResolver Resolver, InkSet Layout) ForSpace(ColorProfile space)
    {
        InkSet layout = InkSet.FromProfile(ColorProfile.Fogra39);
        for (int i = _process; i < _inks.Count; i++)
        {
            if (!_inks.IsDisabled(i))
            {
                layout = layout.WithSpot(_inks[i]);
            }
        }

        return (new InkResolver(this, space, layout), layout);
    }

    /// <summary>Turns a canvas colour (straight tints on the canvas's planes) into this group layer's planes.</summary>
    internal void FromCanvas(ReadOnlySpan<float> canvas, Span<float> layer)
    {
        InkResolver c = _canvas!;
        Span<float> process = stackalloc float[c._process];
        for (int i = 0; i < c._process; i++)
        {
            process[i] = canvas[c._planeOf[i]];
        }

        Span<float> colour = stackalloc float[_space!.ChannelCount];
        _toSpace!.Apply(process, colour);
        SetColour(colour, layer);
        for (int j = 0; j < Planes - 4; j++)
        {
            layer[4 + j] = canvas[c._process + j];
        }
    }

    /// <summary>Turns this group layer's straight planes into a canvas colour.</summary>
    internal void ToCanvas(ReadOnlySpan<float> layer, Span<float> canvas)
    {
        InkResolver c = _canvas!;
        Span<float> colour = stackalloc float[_space!.ChannelCount];
        for (int k = 0; k < colour.Length; k++)
        {
            colour[k] = 1 - layer[k];
        }

        Span<float> process = stackalloc float[c._process];
        _fromSpace!.Apply(colour, process);
        canvas.Clear();
        for (int i = 0; i < c._process; i++)
        {
            canvas[c._planeOf[i]] = Math.Clamp(process[i], 0, 1);
        }

        for (int j = 0; j < Planes - 4; j++)
        {
            canvas[c._process + j] = layer[4 + j];
        }
    }

    // A colour in the group's space as complements on planes 0 to 2.
    private static void SetColour(ReadOnlySpan<float> colour, Span<float> layer)
    {
        for (int k = 0; k < 3; k++)
        {
            layer[k] = 1 - Math.Clamp(colour[colour.Length == 1 ? 0 : k], 0, 1);
        }

        layer[3] = 0;
    }

    /// <summary>Gets the number of planes, alpha not counted.</summary>
    internal int Planes { get; }

    /// <summary>Gets the process profile.</summary>
    internal ColorProfile Profile => _inks.Profile;

    /// <summary>Gets what untagged RGB means.</summary>
    internal ColorProfile RgbSource { get; }

    /// <summary>Gets the intent and black point compensation of conversions into the process inks.</summary>
    internal ColorTransformOptions Options => _options;



    /// <summary>Gets the number of process planes, which come first.</summary>
    internal int ProcessPlanes => _process;

    /// <summary>Gets the planes of Cyan, Magenta and Yellow, when the canvas has all three as process inks, for the non-separable blend modes.</summary>
    internal int[]? CmyPlanes { get; private set; }

    /// <summary>Gets the plane of Black, or −1.</summary>
    internal int BlackPlane { get; private set; } = -1;

    /// <summary>
    /// Returns the planes a paint paints, as PDF's overprint has it: every plane with overprint off (an empty array, which means
    /// all). With it on, the planes of the inks its colours name, and every process plane for an RGB colour, an image or an ink
    /// the canvas lacks (which prints through the process inks); NonZero leaves out the zero components of a CMYK colour.
    /// </summary>
    internal bool[] Painted(Paint paint)
    {
        if (paint.Overprint == Overprint.Off)
        {
            return [];
        }

        if (_canvas is InkResolver canvas)
        {
            bool[] own = canvas.Painted(paint), mapped = new bool[Planes];
            mapped[0] = mapped[1] = mapped[2] = own.Take(canvas._process).Any(p => p);
            for (int j = 0; j < Planes - 4; j++)
            {
                mapped[4 + j] = own[canvas._process + j];
            }

            return mapped;
        }

        bool[] painted = new bool[Planes];
        switch (paint)
        {
            case SolidPaint { Ink: InkColor ink }:
                Name(ink, painted, paint.Overprint == Overprint.NonZero && ink.IsCmyk);
                break;
            case Gradient gradient when gradient.Stops.Any(s => s.Ink is not null):
                foreach (GradientStop stop in gradient.Stops)
                {
                    if (stop.Ink is InkColor stopInk)
                    {
                        Name(stopInk, painted, false);
                    }
                    else
                    {
                        AllProcess(painted);
                    }
                }

                break;
            case MeshShadingPaint { Shader.Inks: InkColor[] inks }:
                foreach (InkColor vertex in inks)
                {
                    Name(vertex, painted, false);
                }

                break;
            case InkImagePaint image:
                foreach (InkPlate plate in image.Image.Plates)
                {
                    int plane = PlaneOf(plate.Name);
                    if (plane >= 0)
                    {
                        painted[plane] = true;
                    }
                    else
                    {
                        AllProcess(painted);
                    }
                }

                break;
            default:
                AllProcess(painted);
                break;
        }

        return painted;
    }

    private void Name(InkColor ink, bool[] painted, bool skipZero)
    {
        foreach (InkTint t in ink.Inks)
        {
            int plane = PlaneOf(t.Colorant.Name);
            if (plane < 0)
            {
                AllProcess(painted);
            }
            else if (!(skipZero && t.Tint == 0))
            {
                painted[plane] = true;
            }
        }
    }

    private void AllProcess(bool[] painted)
    {
        for (int i = 0; i < _process; i++)
        {
            painted[_planeOf[i]] = true;
        }
    }

    /// <summary>Returns the plane of the ink named <paramref name="name"/>, or −1 when the canvas has none (absent or disabled).</summary>
    internal int PlaneOf(string name) => _inks.IndexOf(name) is int i and >= 0 ? _planeOf[i] : -1;

    /// <summary>Writes a solid paint's tints; returns its alpha (an ink colour is opaque, an RGB one has its own).</summary>
    internal float Resolve(SolidPaint paint, Span<float> tints)
    {
        tints.Clear();
        if (_canvas is not null && paint.Ink is null)
        {
            Span<float> colour = stackalloc float[_space!.ChannelCount];
            _rgbToSpace!.Apply([paint.Color.R, paint.Color.G, paint.Color.B], colour);
            SetColour(colour, tints);
            return Math.Clamp(paint.Color.A, 0, 1);
        }

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
        if (_canvas is InkResolver canvas)
        {
            Span<float> own = stackalloc float[canvas.Planes];
            canvas.Resolve(ink, own);
            FromCanvas(own, tints);
            return;
        }

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
        if (_canvas is not null)
        {
            Span<float> colour = stackalloc float[_space!.ChannelCount];
            tints[..(n * Planes)].Clear();
            for (int i = 0; i < n; i++)
            {
                _rgbToSpace!.Apply([colors[i].R, colors[i].G, colors[i].B], colour);
                SetColour(colour, tints.Slice(i * Planes, Planes));
            }

            return;
        }

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

    private int ProcessPlane(string name) => _inks.IndexOf(name) is int i and >= 0 && i < _inks.ProcessCount ? _planeOf[i] : -1;

    private static void Clamp(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Math.Clamp(values[i], 0, 1);
        }
    }
}
