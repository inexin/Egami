namespace Egami.Metafiles.Tests;

/// <summary>Inputs the fuzzers found (task 17), run through the fuzz target: none may throw anything but what it allows.</summary>
public class FuzzFindingsTests
{
    public static TheoryData<string> Findings =>
        [.. Directory.EnumerateFiles(System.IO.Path.Combine(TestFiles.Root, "tests", "images", "fuzz", "metafile-findings")).Select(f => System.IO.Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Findings))]
    public async Task FindingsDoNotCrashOrHang(string name)
    {
        byte[] data = File.ReadAllBytes(System.IO.Path.Combine(TestFiles.Root, "tests", "images", "fuzz", "metafile-findings", name));
        Task run = Task.Run(() =>
        {
            if (name.EndsWith(".emfplus", StringComparison.Ordinal))
            {
                Egami.Fuzz.MetafileFuzzTarget.RunPlus(data);
            }
            else
            {
                Egami.Fuzz.MetafileFuzzTarget.Run(data);
            }
        }, TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); // a TimeoutException fails the test
    }
}
