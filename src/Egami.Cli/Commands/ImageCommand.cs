using System.CommandLine;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;

namespace Egami.Cli.Commands;

/// <summary>Shared plumbing of the commands that read images and write images: inputs, outputs, plans and batches.</summary>
internal static class ImageCommand
{
    /// <summary>Binds a step by name to named argument text, skipping <see langword="null"/> values.</summary>
    public static BoundStep Step(string name, params (string Key, string? Value)[] named)
    {
        StepInfo step = StepCatalog.Find(name) ?? throw new InvalidOperationException($"No step {name}.");
        return StepCatalog.Bind(step, [], named.Where(n => n.Value is not null).Select(n => new KeyValuePair<string, string>(n.Key, n.Value!)).ToList());
    }

    /// <summary>
    /// Runs an image command: configures the session, expands the inputs, checks the output, builds the plan once (so option
    /// mistakes stop before any file is touched) and runs it over every input; <paramref name="special"/> may handle an input
    /// another way (a lossless JPEG transform) and return its outputs, or <see langword="null"/> to run the plan.
    /// </summary>
    public static int Run(
        CliSession session,
        ParseResult result,
        ImageOptions options,
        Func<Plan> plan,
        Func<InputSource, OutputTemplate?, IReadOnlyList<string>?>? special = null)
    {
        BatchOptions batch = options.Batch(session, result);
        List<InputFile> inputs = options.ExpandInputs(session, result);
        GlobalOptions.Configure(session, result, inputs.Count > 1 && batch.Jobs != 1 ? 1 : 0);
        OutputTemplate? output = options.OutputTemplate(session, result, inputs);
        Plan built = plan();
        return Batch.Run(session, inputs, batch, input => special?.Invoke(input, output) ?? Runner.Run(session, input, built, output));
    }

    /// <summary>The steps a plan starts with for <c>--auto-orient</c>.</summary>
    public static List<PipelineItem> Leading(ParseResult result, ImageOptions options)
    {
        return result.GetValue(options.AutoOrient) ? [new StepItem(Step("auto-orient"))] : [];
    }
}
