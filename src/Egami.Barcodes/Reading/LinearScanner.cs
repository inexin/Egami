using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds linear symbols by scanning lines across the image: rows, and at higher efforts columns and diagonals. Each line is
/// thresholded by its own histogram, cut into runs and read both ways; a symbol counts once two lines agree on it.
/// </summary>
internal static class LinearScanner
{
    /// <summary>
    /// Scans the image; with <paramref name="inverted"/> each line is also read with light and dark swapped (its runs shifted by
    /// one), for light bars on a dark background, and those reads come back apart.
    /// </summary>
    public static (List<Found> Normal, List<Found> Inverted) Scan(LuminanceImage image, BitImage? local, BarcodeFormat formats, int effort, bool inverted)
    {
        formats &= BarcodeFormat.Linear;
        if (formats == 0)
        {
            return ([], []);
        }

        var normal = new Reads(false);
        Reads? swapped = inverted ? new Reads(true) : null;
        var line = new LineBuffers();

        // Angles in degrees: rows always, columns from effort 3, diagonals from effort 7.
        int[] angles = effort < 3 ? [0] : effort < 7 ? [0, 90] : [0, 90, 45, 135];
        foreach (int angle in angles)
        {
            foreach ((Vector2 start, Vector2 step, int length) in Lines(image.Width, image.Height, angle, effort))
            {
                ReadOnlySpan<byte> samples = Sample(image, start, step, length, line);
                ReadLine(samples, start, step, formats, line, normal, swapped);
                if (local is not null && angle == 0 && effort >= 6)
                {
                    // The local binarization of the same row, for uneven light.
                    Binarizer.BitRuns(local.Row((int)start.Y), line.Runs, line.Offsets);
                    ReadRuns(line, length, start, step, formats, normal, swapped);
                }
            }
        }

        // A symbol read on one line only: the lines next to it, where small or glary symbols often read, get a look before it
        // is given up (the scan lines can be further apart than such a symbol is tall).
        foreach (Reads reads in swapped is null ? [normal] : (Reads[])[normal, swapped])
        {
            foreach (Candidate candidate in reads.Votes.Values.Where(c => !c.Reported).ToList())
            {
                var across = new Vector2(-candidate.LineStep.Y, candidate.LineStep.X);
                for (int d = 1; d <= 16 && !candidate.Reported; d++)
                {
                    Vector2 start = candidate.LineStart + (across * ((d + 1) / 2 * (d % 2 == 0 ? -1 : 1)));
                    if (start.X < 0 || start.Y < 0 || start.X >= image.Width || start.Y >= image.Height)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> samples = Sample(image, start, candidate.LineStep, candidate.LineLength, line);
                    ReadLine(samples, start, candidate.LineStep, formats, line, reads.Swapped ? null : reads, reads.Swapped ? reads : null);
                }
            }
        }

        // Offset to the source image.
        var offset = new Vector2(image.Left, image.Top);
        return (Shift(normal.Results, offset), swapped is null ? [] : Shift(swapped.Results, offset));
    }

    private static List<Found> Shift(List<Found> results, Vector2 offset) => [.. results.Select(f => f with { Corners = [.. f.Corners.Select(c => c + offset)] })];

    /// <summary>The votes and results of one way of reading the colors: as seen, or with light and dark swapped.</summary>
    private sealed class Reads(bool swapped)
    {
        public bool Swapped { get; } = swapped;

        public Dictionary<(BarcodeFormat, string), Candidate> Votes { get; } = [];

        public List<Found> Results { get; } = [];
    }

    /// <summary>Buffers reused from line to line.</summary>
    private sealed class LineBuffers
    {
        public byte[] Samples = [];
        public float[] Work = [];
        public int[] Prefix = [];
        public readonly List<float> Runs = [], Offsets = [], Back = [], BackOffsets = [];
    }

    private sealed class Candidate
    {
        public int Count;
        public required DecodedData Data;
        public Vector2 FirstStart, FirstEnd, LastStart, LastEnd;
        public bool Reported;

        // The scan line of the first read.
        public Vector2 LineStart, LineStep;
        public int LineLength;
    }

    /// <summary>The scan lines for an angle: start point, unit step along the line, and length in samples.</summary>
    private static IEnumerable<(Vector2 Start, Vector2 Step, int Length)> Lines(int width, int height, int angle, int effort)
    {
        // Lines from the middle outwards; more of them at higher effort.
        int count = effort <= 2 ? 9 : effort <= 5 ? 25 : effort <= 8 ? 61 : 151;
        if (angle is 0 or 90)
        {
            int across = angle == 0 ? height : width, along = angle == 0 ? width : height;
            int lines = Math.Min(count, across);
            float spacing = (float)across / (lines + 1);
            foreach (int k in Outward(lines))
            {
                float position = spacing * (k + 1);
                yield return angle == 0
                    ? (new Vector2(0, MathF.Floor(position)), new Vector2(1, 0), along)
                    : (new Vector2(MathF.Floor(position), 0), new Vector2(0, 1), along);
            }

            yield break;
        }

        // Diagonals at 45 and 135 degrees, spaced across the image.
        float diagonal = MathF.Sqrt((width * width) + (height * height));
        var direction = angle == 45 ? Vector2.Normalize(new Vector2(1, 1)) : Vector2.Normalize(new Vector2(1, -1));
        var normal = new Vector2(-direction.Y, direction.X);
        var center = new Vector2(width / 2f, height / 2f);
        int diagonals = Math.Min(count, (int)diagonal);
        float gap = diagonal / (diagonals + 1);
        foreach (int k in Outward(diagonals))
        {
            Vector2 middle = center + (normal * ((k + 1) * gap - (diagonal / 2)));
            Vector2 start = middle - (direction * (diagonal / 2));
            yield return (start, direction, (int)diagonal);
        }
    }

    /// <summary>Indices 0 to n - 1 from the middle outwards.</summary>
    private static IEnumerable<int> Outward(int n)
    {
        int middle = n / 2;
        yield return middle;
        for (int d = 1; d <= n; d++)
        {
            if (middle + d < n)
            {
                yield return middle + d;
            }

            if (middle - d >= 0)
            {
                yield return middle - d;
            }
        }
    }

    /// <summary>Luminance along a line, nearest sample; outside the image counts as white.</summary>
    private static ReadOnlySpan<byte> Sample(LuminanceImage image, Vector2 start, Vector2 step, int length, LineBuffers line)
    {
        if (step == new Vector2(1, 0))
        {
            return image.Row((int)start.Y)[..length];
        }

        if (line.Samples.Length < length)
        {
            line.Samples = new byte[length];
        }

        Span<byte> samples = line.Samples.AsSpan(0, length);
        for (int i = 0; i < length; i++)
        {
            int x = (int)(start.X + (step.X * i)), y = (int)(start.Y + (step.Y * i));
            samples[i] = (uint)x < (uint)image.Width && (uint)y < (uint)image.Height ? image[x, y] : (byte)255;
        }

        return samples;
    }

    private static void ReadLine(ReadOnlySpan<byte> samples, Vector2 start, Vector2 step, BarcodeFormat formats, LineBuffers line, Reads? normal, Reads? swapped)
    {
        Binarizer.RowRuns(samples, line.Runs, line.Offsets, ref line.Work);
        ReadRuns(line, samples.Length, start, step, formats, normal, swapped);
        Binarizer.AdaptiveRowRuns(samples, line.Runs, line.Offsets, ref line.Work, ref line.Prefix);
        ReadRuns(line, samples.Length, start, step, formats, normal, swapped);
    }

    /// <summary>Reads a line's runs as seen and, when asked, with light and dark swapped: the runs behind an empty light run.</summary>
    private static void ReadRuns(LineBuffers line, int length, Vector2 start, Vector2 step, BarcodeFormat formats, Reads? normal, Reads? swapped)
    {
        if (normal is not null)
        {
            ReadRuns(line, line.Runs, line.Offsets, length, start, step, formats, normal);
        }

        if (swapped is not null)
        {
            line.Runs.Insert(0, 0);
            line.Offsets.Insert(0, 0);
            ReadRuns(line, line.Runs, line.Offsets, length, start, step, formats, swapped);
            line.Runs.RemoveAt(0);
            line.Offsets.RemoveAt(0);
        }
    }

    /// <summary>Reads the runs left to right, then right to left (the symbol upside down).</summary>
    private static void ReadRuns(LineBuffers line, List<float> runs, List<float> offsets, int length, Vector2 start, Vector2 step, BarcodeFormat formats, Reads reads)
    {
        if (LinearDecoder.Read(CollectionsMarshal.AsSpan(runs), CollectionsMarshal.AsSpan(offsets), formats) is LinearRead read)
        {
            Vote(read, start + (step * read.Start), start + (step * read.End), (start, step, length), reads);
        }

        // Reversed: the runs backwards, light first again; positions measured from the far end.
        List<float> back = line.Back, backPositions = line.BackOffsets;
        back.Clear();
        backPositions.Clear();
        if (runs.Count % 2 == 0)
        {
            back.Add(0);
        }

        for (int i = runs.Count - 1; i >= 0; i--)
        {
            back.Add(runs[i]);
        }

        float at = 0;
        foreach (float r in back)
        {
            backPositions.Add(at);
            at += r;
        }

        backPositions.Add(length);
        if (LinearDecoder.Read(CollectionsMarshal.AsSpan(back), CollectionsMarshal.AsSpan(backPositions), formats) is LinearRead reversed)
        {
            Vote(reversed, start + (step * (length - reversed.Start)), start + (step * (length - reversed.End)), (start, step, length), reads);
        }
    }

    private static void Vote(LinearRead read, Vector2 from, Vector2 to, (Vector2 Start, Vector2 Step, int Length) line, Reads reads)
    {
        Dictionary<(BarcodeFormat, string), Candidate> votes = reads.Votes;
        List<Found> results = reads.Results;
        var key = (read.Data.Format, read.Data.Text);
        if (!votes.TryGetValue(key, out Candidate? candidate))
        {
            votes[key] = new Candidate { Count = 1, Data = read.Data, FirstStart = from, FirstEnd = to, LastStart = from, LastEnd = to, LineStart = line.Start, LineStep = line.Step, LineLength = line.Length };
            return;
        }

        // A line that also read the add-on wins over one that did not.
        if (candidate.Data.AddOn is null && read.Data.AddOn is not null)
        {
            candidate.Data = read.Data;
        }

        candidate.Count++;
        candidate.LastStart = from;
        candidate.LastEnd = to;
        if (!candidate.Reported && candidate.Count >= 2)
        {
            candidate.Reported = true;
            results.Add(new Found(candidate.Data, [candidate.FirstStart, candidate.FirstEnd, candidate.LastEnd, candidate.LastStart]));
        }
    }
}
