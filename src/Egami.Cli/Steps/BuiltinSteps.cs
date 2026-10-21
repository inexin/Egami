using Egami.Operations;
using Egami.Pixels;

namespace Egami.Cli.Steps;

/// <summary>The tool's own steps: <c>resize</c> with the size syntax and fit modes (Q7) and <c>flip</c> with a direction.</summary>
internal static class BuiltinSteps
{
    /// <summary>The fit modes, as <c>--fit</c> spells them.</summary>
    public static readonly string[] FitNames = ["contain", "cover", "fill", "pad"];

    private static readonly string[] FlipNames = ["horizontal", "vertical", "both"];

    /// <summary>Creates the built-in steps; <paramref name="generated"/> holds the generated ones, for their choice parsers.</summary>
    public static IEnumerable<StepInfo> Create(IReadOnlyDictionary<string, StepInfo> generated)
    {
        _ = generated;
        yield return Resize();
        yield return Flip();
    }

    /// <summary>The parameters of <c>resize</c>, shared with the resize and thumbnail commands.</summary>
    public static ParamInfo[] ResizeParameters { get; } =
    [
        new("size", "size", static text => SizeSpec.Parse(text), true, null, "The size: W, xH, WxH or P%, optionally followed by ! (ignore the aspect ratio), ^ (fill the box), > (only shrink) or < (only enlarge).", null),
        new("fit", "choice", static text => ValueParsers.Enum<FitMode>(text), false, "contain", "How the image fills a WxH box: inside it (contain), filling and cropped (cover), stretched (fill) or inside and padded (pad).", FitNames),
        new("gravity", "choice", static text => ValueParsers.Enum<Anchor>(text), false, "center", "Which part stays for cover, and where the image sits for pad.", Enum.GetNames<Anchor>().Select(ValueParsers.Kebab).ToArray()),
        new("filter", "choice", static text => ValueParsers.Enum<ResampleFilter>(text), false, "bicubic", "The resampling kernel; bicubic is Catmull-Rom.", Enum.GetNames<ResampleFilter>().Select(ValueParsers.Kebab).ToArray()),
        new("linear", "bool", ValueParsers.Bool, false, "false", "Blend pixels in linear light rather than as encoded.", null),
        new("background", "color", ValueParsers.Color, false, "transparent", "The color pad fills with.", null),
    ];

    /// <summary>Adds a resize to a chain: the library's Fit, Cover, Pad or Resize, as the size and fit mode ask.</summary>
    public static void AddResize(RasterOps ops, SizeSpec spec, FitMode fit, Anchor gravity, ResampleFilter filter, bool linear, Rgba8 background, Size source)
    {
        ColorSpace space = linear ? ColorSpace.Linear : ColorSpace.Encoded;
        bool box = spec.Width is not null && spec.Height is not null && !spec.WidthPercent && !spec.HeightPercent;
        if (box && !spec.Exact && !spec.Minimum && !spec.OnlyEnlarge)
        {
            int w = (int)spec.Width!.Value, h = (int)spec.Height!.Value;
            switch (fit)
            {
                case FitMode.Contain:
                    ops.Fit(w, h, filter, space, enlarge: !spec.OnlyShrink);
                    return;
                case FitMode.Cover:
                    if (!spec.OnlyShrink || source.Width > w || source.Height > h)
                    {
                        ops.Cover(w, h, gravity, filter, space);
                    }

                    return;
                case FitMode.Pad:
                    ops.Fit(w, h, filter, space, enlarge: !spec.OnlyShrink);
                    ops.Pad(w, h, background, gravity);
                    return;
            }
        }

        Size target = spec.Target(source, fit);
        if (target != source)
        {
            ops.Resize(target.Width, target.Height, filter, space);
        }
    }

    private static StepInfo Resize()
    {
        return new StepInfo(
            "resize",
            "Resize",
            "Resizes to a size in the size syntax (W, xH, WxH, P%, with !, ^, > or <) with a fit mode.",
            [new OverloadInfo(ResizeParameters)],
            static (_, _, _) => throw new InvalidOperationException("resize needs the image's size."))
        {
            Sized = static (ops, args, size) => AddResize(
                ops,
                (SizeSpec)args[0]!,
                args[1] is FitMode fit ? fit : FitMode.Contain,
                args[2] is Anchor gravity ? gravity : Anchor.Center,
                args[3] is ResampleFilter filter ? filter : ResampleFilter.Bicubic,
                args[4] is true,
                args[5] is Rgba8 background ? background : default,
                size),
            Streams = true,
        };
    }

    private static StepInfo Flip()
    {
        return new StepInfo(
            "flip",
            "Flip",
            "Mirrors the image: horizontal (left to right, the default), vertical (top to bottom) or both.",
            [new OverloadInfo([new("direction", "choice", static text => FlipNames[(int)ValueParsers.Choice(text, FlipNames, [0, 1, 2])], false, "horizontal", "Which way to mirror.", FlipNames)])],
            static (ops, _, args) =>
            {
                string direction = args[0] as string ?? "horizontal";
                if (direction is "horizontal" or "both")
                {
                    ops.FlipHorizontal();
                }

                if (direction is "vertical" or "both")
                {
                    ops.FlipVertical();
                }
            })
        {
            KeepsSize = true,
        };
    }
}
