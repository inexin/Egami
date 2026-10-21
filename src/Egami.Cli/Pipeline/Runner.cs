using Egami.Cli.Host;
using Egami.Cli.Steps;
using Egami.Formats;
using Egami.Metadata;
using Egami.Operations;

namespace Egami.Cli.Pipeline;

/// <summary>
/// Runs a plan over one input. A plan of steps that can stream, ending in one save, goes through <c>Raster.Transcode</c> a band
/// of rows at a time (which also shrinks on decode); anything else loads the image, runs the steps in chains and saves.
/// Either way the result is the one the same <c>RasterOps</c> chain gives in C#, at any <c>--jobs</c>.
/// </summary>
internal static class Runner
{
    /// <summary>Runs a plan over one input; <paramref name="defaultOutput"/> is the command's <c>-o</c> for saves without a path.</summary>
    /// <returns>The paths written.</returns>
    public static IReadOnlyList<string> Run(CliSession session, InputSource input, Plan plan, OutputTemplate? defaultOutput)
    {
        IReadOnlyList<PipelineItem> items = plan.WithFinalSave();
        RasterInfo info = input.Probe(session);
        List<(SaveSpec Save, string Path, FormatInfo Format)> targets = [];
        foreach (SaveItem save in items.OfType<SaveItem>())
        {
            targets.Add(Resolve(session, input, save.Save, defaultOutput, info.Format));
        }

        // A leading auto-orient of an upright image changes nothing, and dropping it lets the plan stream.
        List<PipelineItem> work = [.. items];
        while (work.Count > 0 && work[0] is StepItem { Step.Info.Name: "auto-orient" } && info.Orientation is ImageOrientation.Normal or ImageOrientation.Unknown)
        {
            work.RemoveAt(0);
        }

        ReaderOptions read = new()
        {
            ConvertColorTo = plan.Read.ConvertTo,
            Frames = plan.Read.FirstFrameOnly ? ..1 : ..,
            MinimumSize = DecodeSize(work, info),
        };

        if (CanStream(work))
        {
            try
            {
                Stream(session, input, work, read, new Size(info.Width, info.Height), targets[0]);
                return [targets[0].Path];
            }
            catch (NotSupportedException error)
            {
                session.Info($"{input.File.Display}: not streaming ({error.Message})");
            }
        }

        InMemory(session, input, work, read, info, targets);
        return targets.Select(t => t.Path).ToList();
    }

    /// <summary>Finds a format by its name or one of its extensions, among those the tool can read or write.</summary>
    public static FormatInfo? FindFormat(CliSession session, string nameOrExtension)
    {
        string key = WriterInfo.Normalize(nameOrExtension.TrimStart('.'));
        return session.Formats.FirstOrDefault(f => WriterInfo.Normalize(f.Name) == key)
            ?? session.Formats.FirstOrDefault(f => f.Extensions.Any(e => WriterInfo.Normalize(e) == key));
    }

    /// <summary>The writer options a save asks for, for its format.</summary>
    public static WriterOptions Options(CliSession session, SaveSpec save, FormatInfo format)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        WriterInfo? writer = WriterCatalog.Find(format);
        foreach ((string key, string value) in save.Common)
        {
            if (writer?.Settings.Any(s => s.Key == key) == true || (writer is null && key == "strip-metadata"))
            {
                values[key] = value;
            }
            else if (key == "strip-metadata")
            {
                values[key] = value;
            }
            else
            {
                session.Warn($"{format.Name} has no '{key}' setting; --{key} is ignored.");
            }
        }

        foreach ((string key, string value) in save.Settings)
        {
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && WriterCatalog.All.FirstOrDefault(w => w.Prefix == WriterInfo.Normalize(key[..dot])) is { } owner)
            {
                if (owner != writer)
                {
                    session.Info($"--set {key} is for {owner.Prefix}, not {format.Name}; ignored.");
                    continue;
                }

                values[key[(dot + 1)..]] = value;
            }
            else if (dot > 0 && FindFormat(session, key[..dot]) is { } other)
            {
                if (other != format)
                {
                    session.Info($"--set {key} is for {other.Name}, not {format.Name}; ignored.");
                    continue;
                }

                values[key[(dot + 1)..]] = value;
            }
            else
            {
                values[key] = value;
            }
        }

        return WriterCatalog.Build(format, values);
    }

    /// <summary>Where a save writes and in which format.</summary>
    public static (SaveSpec Save, string Path, FormatInfo Format) Resolve(CliSession session, InputSource input, SaveSpec save, OutputTemplate? defaultOutput, FormatInfo inputFormat)
    {
        OutputTemplate template = save.Path is not null
            ? OutputTemplate.Parse(save.Path)
            : defaultOutput
                ?? (session.InPlace && input.File.Path is { } own ? OutputTemplate.Parse(own.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)) : null)
                ?? throw new UsageException("No output: give -o with a file, a folder, a template or - for standard output.");
        FormatInfo? format = save.Format is not null
            ? FindFormat(session, save.Format) ?? throw new UsageException($"'{save.Format}' is not a format egami knows; 'egami formats' lists them.")
            : null;
        string path;
        if (template.IsStdout)
        {
            path = "-";
            format ??= inputFormat;
        }
        else
        {
            string text = template.Text;
            bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
            string extension = format is { Extensions.Count: > 0 } ? format.Extensions[0] : input.File.Extension.Length > 0 ? input.File.Extension : inputFormat.Extensions[0];
            path = folder
                ? Path.Combine(session.ResolvePath(text), $"{input.File.Name}.{extension}")
                : session.ResolvePath(template.Resolve(input.File, extension));
            if (format is null)
            {
                string ext = Path.GetExtension(path);
                format = ext.Length > 1
                    ? FindFormat(session, ext) ?? throw new UsageException($"'{ext}' is not an extension egami knows; give --format or see 'egami formats'.")
                    : throw new UsageException($"The output '{path}' has no extension; give --format.");
            }
        }

        if (!session.InPlace && input.File.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"{input.File.Display}: the output would replace the input; give another -o, or --in-place to replace it.");
        }

        if (format.Writer is null)
        {
            throw new UsageException($"egami can read {format.Name} but not write it; 'egami formats' lists what it writes.");
        }

        return (save, path, format);
    }

    private static bool CanStream(List<PipelineItem> work)
    {
        if (work.Count == 0 || work[^1] is not SaveItem || work.Take(work.Count - 1).Any(i => i is not StepItem))
        {
            return false;
        }

        bool sizeChanged = false;
        foreach (StepItem item in work.Take(work.Count - 1).Cast<StepItem>())
        {
            StepInfo info = item.Step.Info;
            if (!StepCatalog.Streams(info) || (info.Sized is not null && sizeChanged))
            {
                return false;
            }

            sizeChanged |= !StepCatalog.KeepsSize(info);
        }

        return true;
    }

    private static void Stream(CliSession session, InputSource input, List<PipelineItem> work, ReaderOptions read, Size size, (SaveSpec Save, string Path, FormatInfo Format) target)
    {
        List<BoundStep> steps = work.OfType<StepItem>().Select(s => s.Step).ToList();
        WriterOptions options = Options(session, target.Save, target.Format);
        Action<RasterOps>? operations = steps.Count == 0 ? null : ops =>
        {
            foreach (BoundStep step in steps)
            {
                step.AddTo(ops, size);
            }
        };

        Output.Write(session, target.Path, output =>
        {
            using Stream source = input.Open(session);
            Raster.Transcode(source, output, target.Format, operations, read, options, session.Context);
        });
    }

    /// <summary>
    /// The size to decode at when the plan starts with a resize (after an auto-orient): at least one and a half times the result,
    /// so a JPEG is decoded at 1/2, 1/4 or 1/8 of its size only where that still leaves the resize filter real work. The
    /// result stays within a few levels of a full decode, as ImageMagick and libvips resize it, at a fraction of the work. When
    /// no reduction is that safe, the stored size itself is asked for, which keeps Transcode from reducing to the result.
    /// </summary>
    private static Size? DecodeSize(List<PipelineItem> work, RasterInfo info)
    {
        int at = work.Count > 1 && work[0] is StepItem { Step.Info.Name: "auto-orient" } ? 1 : 0;
        if (work.Count <= at || work[at] is not StepItem { Step: var step })
        {
            return null;
        }

        // An auto-orient first means the box is measured against the upright image, and the decoder shrinks the stored one.
        bool swaps = at == 1 && info.Orientation >= ImageOrientation.Transpose;
        Size upright = swaps ? new Size(info.Height, info.Width) : new Size(info.Width, info.Height);
        Size? target = step switch
        {
            { Info.Name: "resize", Args: [SizeSpec spec, var fit, ..] } => spec.Target(upright, fit is FitMode mode ? mode : FitMode.Contain),
            { Info.Name: "fit", Args: [int w, int h, ..] } => new SizeSpec(w, false, h, false, false, false, false, false).Target(upright, FitMode.Contain),
            { Info.Name: "cover", Args: [int w, int h, ..] } => new SizeSpec(w, false, h, false, false, true, false, false).Target(upright, FitMode.Contain),
            { Info.Name: "resize", Args: [int w, int h, ..] } => new Size(w > 0 ? w : upright.Width * h / Math.Max(1, upright.Height), h > 0 ? h : upright.Height * w / Math.Max(1, upright.Width)),
            _ => null,
        };
        if (target is not { } result)
        {
            return null;
        }

        Size margin = new((result.Width * 3 + 1) / 2, (result.Height * 3 + 1) / 2);
        Size stored = swaps ? new Size(margin.Height, margin.Width) : margin;
        return stored.Width < info.Width && stored.Height < info.Height ? stored : new Size(info.Width, info.Height);
    }

    private static void InMemory(CliSession session, InputSource input, List<PipelineItem> work, ReaderOptions read, RasterInfo info, List<(SaveSpec Save, string Path, FormatInfo Format)> targets)
    {
        using Raster image = input.Load(session, read);
        List<(BoundStep Step, Size Size)> pending = [];
        int saveIndex = 0;
        foreach (PipelineItem item in work)
        {
            switch (item)
            {
                case StepItem { Step: var step }:
                    if (step.Info.Sized is not null && pending.Any(p => !StepCatalog.KeepsSize(p.Step.Info)))
                    {
                        Flush(image, pending);
                    }

                    pending.Add((step, new Size(image.Width, image.Height)));
                    break;
                case EditItem edit:
                    Flush(image, pending);
                    edit.Edit(image);
                    break;
                case SaveItem:
                    Flush(image, pending);
                    (SaveSpec save, string path, FormatInfo format) = targets[saveIndex++];
                    WriterOptions options = Options(session, save, format);
                    Output.Write(session, path, output => image.Save(output, format, options));
                    break;
            }
        }
    }

    private static void Flush(Raster image, List<(BoundStep Step, Size Size)> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        (BoundStep Step, Size Size)[] chain = [.. pending];
        pending.Clear();
        image.Apply(ops =>
        {
            foreach ((BoundStep step, Size size) in chain)
            {
                step.AddTo(ops, size);
            }
        });
    }
}
