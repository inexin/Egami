using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds linear symbols by scanning lines across the image: rows, and at higher efforts columns and diagonals. Each line is
/// thresholded by its own histogram, cut into runs and read both ways; a symbol counts once two lines agree on it.
/// </summary>
internal static class LinearScanner
{
    public static List<Found> Scan(LuminanceImage image, BitImage? local, BarcodeFormats formats, int effort)
    {
        formats &= BarcodeFormats.Linear;
        var results = new List<Found>();
        if (formats == 0)
        {
            return results;
        }

        // Angles in degrees: rows always, columns from effort 3, diagonals from effort 7.
        int[] angles = effort < 3 ? [0] : effort < 7 ? [0, 90] : [0, 90, 45, 135];
        var votes = new Dictionary<(BarcodeFormat, string), Candidate>();
        foreach (int angle in angles)
        {
            foreach ((Vector2 start, Vector2 step, int length) in Lines(image.Width, image.Height, angle, effort))
            {
                byte[] samples = Sample(image, start, step, length);
                ReadLine(samples, start, step, formats, votes, results);
                if (local is not null && angle == 0 && effort >= 6)
                {
                    // The local binarization of the same row, for uneven light.
                    byte[] dark = new byte[length];
                    int y = (int)start.Y;
                    local.Row(y).CopyTo(dark);
                    ReadDark(dark, start, step, formats, votes, results);
                }
            }
        }

        // A symbol read on one line only: the lines next to it, where small or glary symbols often read, get a look before it
        // is given up (the scan lines can be further apart than such a symbol is tall).
        foreach (Candidate candidate in votes.Values.Where(c => !c.Reported).ToList())
        {
            var normal = new Vector2(-candidate.LineStep.Y, candidate.LineStep.X);
            for (int d = 1; d <= 16 && !candidate.Reported; d++)
            {
                Vector2 start = candidate.LineStart + (normal * ((d + 1) / 2 * (d % 2 == 0 ? -1 : 1)));
                if (start.X < 0 || start.Y < 0 || start.X >= image.Width || start.Y >= image.Height)
                {
                    continue;
                }

                ReadLine(Sample(image, start, candidate.LineStep, candidate.LineLength), start, candidate.LineStep, formats, votes, results);
            }
        }

        // Offset to the source image.
        var offset = new Vector2(image.Left, image.Top);
        return [.. results.Select(f => f with { Corners = [.. f.Corners.Select(c => c + offset)] })];
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
    private static byte[] Sample(LuminanceImage image, Vector2 start, Vector2 step, int length)
    {
        byte[] samples = new byte[length];
        if (step == new Vector2(1, 0))
        {
            image.Row((int)start.Y).CopyTo(samples);
            return samples;
        }

        for (int i = 0; i < length; i++)
        {
            int x = (int)(start.X + (step.X * i)), y = (int)(start.Y + (step.Y * i));
            samples[i] = (uint)x < (uint)image.Width && (uint)y < (uint)image.Height ? image[x, y] : (byte)255;
        }

        return samples;
    }

    private static void ReadLine(byte[] samples, Vector2 start, Vector2 step, BarcodeFormats formats, Dictionary<(BarcodeFormat, string), Candidate> votes, List<Found> results)
    {
        var runs = new List<float>();
        var offsets = new List<float>();
        Binarizer.RowRuns(samples, runs, offsets);
        ReadRuns(runs, offsets, samples.Length, start, step, formats, votes, results);
        Binarizer.AdaptiveRowRuns(samples, runs, offsets);
        ReadRuns(runs, offsets, samples.Length, start, step, formats, votes, results);
    }

    private static void ReadDark(byte[] dark, Vector2 start, Vector2 step, BarcodeFormats formats, Dictionary<(BarcodeFormat, string), Candidate> votes, List<Found> results)
    {
        var runs = new List<float>();
        var offsets = new List<float>();
        Binarizer.BitRuns(dark, runs, offsets);
        ReadRuns(runs, offsets, dark.Length, start, step, formats, votes, results);
    }

    /// <summary>Reads the runs left to right, then right to left (the symbol upside down).</summary>
    private static void ReadRuns(List<float> runs, List<float> offsets, int length, Vector2 start, Vector2 step, BarcodeFormats formats, Dictionary<(BarcodeFormat, string), Candidate> votes, List<Found> results)
    {
        float[] forward = [.. runs], positions = [.. offsets];
        if (LinearDecoder.Read(forward, positions, formats) is LinearRead read)
        {
            Vote(read, start + (step * read.Start), start + (step * read.End), (start, step, length), votes, results);
        }

        // Reversed: the runs backwards, light first again; positions measured from the far end.
        var back = new List<float>(forward.Length + 1);
        var backPositions = new List<float>(forward.Length + 2);
        if (forward.Length % 2 == 0)
        {
            back.Add(0);
        }

        for (int i = forward.Length - 1; i >= 0; i--)
        {
            back.Add(forward[i]);
        }

        float at = 0;
        foreach (float r in back)
        {
            backPositions.Add(at);
            at += r;
        }

        backPositions.Add(length);
        if (LinearDecoder.Read([.. back], [.. backPositions], formats) is LinearRead reversed)
        {
            Vote(reversed, start + (step * (length - reversed.Start)), start + (step * (length - reversed.End)), (start, step, length), votes, results);
        }
    }

    private static void Vote(LinearRead read, Vector2 from, Vector2 to, (Vector2 Start, Vector2 Step, int Length) line, Dictionary<(BarcodeFormat, string), Candidate> votes, List<Found> results)
    {
        var key = (read.Data.Format, read.Data.Text);
        if (!votes.TryGetValue(key, out Candidate? candidate))
        {
            votes[key] = new Candidate { Count = 1, Data = read.Data, FirstStart = from, FirstEnd = to, LastStart = from, LastEnd = to, LineStart = line.Start, LineStep = line.Step, LineLength = line.Length };
            return;
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
