using Egami.Fuzz;

namespace Egami.Cli.Tests;

/// <summary>The CLI fuzz targets on their seeds and on random mutations of them, so a crash shows up without AFL++.</summary>
public class FuzzTests
{
    public static TheoryData<string, string> Seeds()
    {
        TheoryData<string, string> seeds = [];
        foreach (string target in (string[])["size", "template", "workflow", "run", "mcp"])
        {
            foreach (string file in Directory.GetFiles(Path.Combine(Tool.Images, "fuzz", $"cli-{target}-seeds")))
            {
                seeds.Add(target, Path.GetFileName(file));
            }
        }

        return seeds;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SeedsAndTheirMutationsOnlyFailWithUsageErrors(string target, string seed)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Tool.Images, "fuzz", $"cli-{target}-seeds", seed));
        Action<byte[]> run = target switch
        {
            "size" => CliFuzzTarget.Size,
            "template" => CliFuzzTarget.Template,
            "workflow" => CliFuzzTarget.Workflow,
            "mcp" => CliFuzzTarget.Mcp,
            _ => CliFuzzTarget.Run,
        };
        run(data);
        Random random = new(seed.GetHashCode(StringComparison.Ordinal) ^ target.Length);
        byte[] alphabet = "0123456789x%!^<>{}:,.-+= \n\"[]abcdefghijklmnopqrstuvwxyz"u8.ToArray();
        for (int i = 0; i < 2000; i++)
        {
            byte[] mutated = [.. data];
            int edits = 1 + random.Next(4);
            for (int e = 0; e < edits && mutated.Length > 0; e++)
            {
                int at = random.Next(mutated.Length);
                switch (random.Next(3))
                {
                    case 0: mutated[at] = alphabet[random.Next(alphabet.Length)]; break;
                    case 1: mutated = [.. mutated[..at], .. mutated[(at + 1)..]]; break;
                    default: mutated = [.. mutated[..at], alphabet[random.Next(alphabet.Length)], .. mutated[at..]]; break;
                }
            }

            run(mutated);
        }
    }
}
