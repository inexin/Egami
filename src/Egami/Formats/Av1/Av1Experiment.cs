namespace Egami.Formats.Av1;

/// <summary>Temporary experiment switches (EGAMI_AV1X=name,name=value).</summary>
internal static class Av1Experiment
{
    private static readonly Dictionary<string, double> Flags = Parse();

    internal static bool On(string name) => Flags.ContainsKey(name);

    internal static double Value(string name, double fallback) => Flags.TryGetValue(name, out double v) && !double.IsNaN(v) ? v : fallback;

    private static Dictionary<string, double> Parse()
    {
        Dictionary<string, double> flags = [];
        foreach (string part in (Environment.GetEnvironmentVariable("EGAMI_AV1X") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split('=');
            flags[kv[0]] = kv.Length > 1 ? double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
        }

        return flags;
    }
}
