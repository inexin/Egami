using Egami.Cli.Steps;

namespace Egami.Cli.Pipeline;

/// <summary>Where and how a pipeline writes: <c>save out.webp --quality 82</c>, or the command's <c>-o</c>.</summary>
/// <param name="Path">The output path or template, <c>-</c> for standard output, or <see langword="null"/> for the command's <c>-o</c>.</param>
/// <param name="Format">The format name or extension from <c>--format</c>, or <see langword="null"/> for the path's extension.</param>
/// <param name="Settings">Writer settings from <c>--set</c>, by key with or without the format prefix: <c>png.compression-level</c>.</param>
/// <param name="Common">Settings from the common flags (<c>--quality</c>, <c>--lossless</c> …), skipped with a warning where the format lacks them.</param>
internal sealed record SaveSpec(string? Path, string? Format, IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Common)
{
    /// <summary>Gets a save with no path, format or settings.</summary>
    public static SaveSpec Default { get; } = new(null, null, new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>Gets a value indicating whether the save sets anything about the writer.</summary>
    public bool HasSettings => Settings.Count > 0 || Common.Count > 0;
}

/// <summary>One item of a pipeline: a step, or a save.</summary>
internal abstract record PipelineItem;

/// <summary>A step of a pipeline.</summary>
/// <param name="Step">The bound step.</param>
internal sealed record StepItem(BoundStep Step) : PipelineItem;

/// <summary>An edit of the whole image outside RasterOps, such as its metadata; it keeps a plan from streaming.</summary>
/// <param name="Name">The edit's name, for <c>--dry-run</c>.</param>
/// <param name="Edit">Changes the image.</param>
internal sealed record EditItem(string Name, Action<Raster> Edit) : PipelineItem;

/// <summary>A save in a pipeline.</summary>
/// <param name="Save">Where and how to write.</param>
internal sealed record SaveItem(SaveSpec Save) : PipelineItem;

/// <summary>
/// A plan for one image: the steps and saves in order, plus how to read. Every single command becomes one of these, as do
/// <c>run</c> and workflow files, so there is one code path to test.
/// </summary>
/// <param name="Items">The steps and saves, in order. A plan whose last item is not a save gets one to the command's <c>-o</c>.</param>
/// <param name="Read">How to read.</param>
internal sealed record Plan(IReadOnlyList<PipelineItem> Items, ReadSpec Read)
{
    /// <summary>Gets the plan's items with a final save added when it has none at the end.</summary>
    public IReadOnlyList<PipelineItem> WithFinalSave()
    {
        return Items.Count > 0 && Items[^1] is SaveItem ? Items : [.. Items, new SaveItem(SaveSpec.Default)];
    }
}

/// <summary>How to read the input.</summary>
/// <param name="ConvertTo">A color profile to convert to while decoding (<c>--to</c>), or <see langword="null"/>.</param>
/// <param name="FirstFrameOnly">Whether to read only the first frame.</param>
internal sealed record ReadSpec(Colors.ColorProfile? ConvertTo = null, bool FirstFrameOnly = false)
{
    /// <summary>Gets the defaults.</summary>
    public static ReadSpec Default { get; } = new();
}
