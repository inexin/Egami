using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>Runs the readers over an image for one call of ReadBarcodes or ReadBarcode.</summary>
internal static class Search
{
    public static IReadOnlyList<BarcodeResult> Run(LuminanceImage image, BarcodeReaderOptions options, bool firstOnly)
    {
        bool tryInverted = options.TryInverted ?? options.Effort >= 5;
        if (options.PureBarcode)
        {
            BitImage bits = Binarizer.Global(image);
            if (PureReader.Read(image, bits, options.Formats, inverted: false) is Found pure)
            {
                return [ToResult(pure, false)];
            }

            return tryInverted && PureReader.Read(image, bits.Complement(), options.Formats, inverted: true) is Found inverted ? [ToResult(inverted, true)] : [];
        }

        return Scan(image, options, tryInverted, firstOnly);
    }

    /// <summary>
    /// The full search: every detector over the image, then duplicates merged; then, when asked, the same over the image with
    /// light and dark swapped. The inverted pass reuses the first pass's thresholds with the bits swapped, and the linear scan
    /// reads both ways from the same lines. On large images the binarization runs in stripes and the detectors side by side,
    /// up to the context's parallelism; each detector's results are kept in a fixed order, so the results do not depend on
    /// the number of threads.
    /// </summary>
    private static List<BarcodeResult> Scan(LuminanceImage image, BarcodeReaderOptions options, bool tryInverted, bool firstOnly)
    {
        BarcodeFormat formats = options.Formats;
        int effort = options.Effort;
        int threads = (long)image.Width * image.Height < 256 * 1024 ? 1 : Math.Max(1, (options.Context ?? EgamiContext.Default).MaxParallelism);
        // The local threshold serves the 2D detectors and, from effort 6, the linear scan; the global one the pure reader.
        bool matrix = (formats & (BarcodeFormat.Qr | BarcodeFormat.DataMatrix | BarcodeFormat.Aztec | BarcodeFormat.Pdf417)) != 0;
        BitImage? local = matrix || effort >= 6 ? Binarizer.Local(image, threads) : null;
        BitImage global = local is not null && (image.Width < 40 || image.Height < 40) ? local : Binarizer.Global(image);
        List<Found> linearInverted = [];
        var jobs = Jobs(image, false, local, global, formats, effort);
        if ((formats & BarcodeFormat.Linear) != 0)
        {
            jobs.Add(() =>
            {
                (List<Found> normal, List<Found> inverted) = LinearScanner.Scan(image, effort >= 6 ? local : null, formats, effort, tryInverted);
                linearInverted = inverted;
                return normal;
            });
        }

        List<Found> first = Merge(RunJobs(jobs, threads));
        var results = first.Select(f => ToResult(f, false)).ToList();
        if (tryInverted && !(firstOnly && results.Count > 0))
        {
            BitImage? swapped = local?.Complement();
            jobs = Jobs(image, true, swapped, ReferenceEquals(global, local) ? swapped! : global.Complement(), formats, effort, first);
            jobs.Add(() => linearInverted);
            results.AddRange(Merge(RunJobs(jobs, threads)).Select(f => ToResult(f, true)));
        }

        return results;
    }

    /// <summary>
    /// The detectors for the formats asked for, over one pass's bits; in the inverted pass the gray levels that PDF417 samples
    /// are the inverted image's, made when first needed, and the Data Matrix detector skips the light modules of the first
    /// pass's 2D symbols (<paramref name="found"/>).
    /// </summary>
    private static List<Func<List<Found>>> Jobs(LuminanceImage image, bool inverted, BitImage? local, BitImage global, BarcodeFormat formats, int effort, List<Found>? found = null)
    {
        var offset = new Vector2(image.Left, image.Top);
        var jobs = new List<Func<List<Found>>>
        {
            () => PureReader.Read(image, global, formats, inverted, searching: true) is Found pure ? [pure] : [],
        };
        if (local is null)
        {
            return jobs;
        }

        if ((formats & BarcodeFormat.Qr) != 0)
        {
            jobs.Add(() => QrDetector.Detect(local, effort, offset));
        }

        if ((formats & BarcodeFormat.DataMatrix) != 0)
        {
            jobs.Add(() => DataMatrixDetector.Detect(local, Shapes.Components(local, 10), offset, found?.Where(f => IsMatrix(f.Data.Format)).Select(f => f.Corners)));
        }

        if ((formats & BarcodeFormat.Aztec) != 0)
        {
            jobs.Add(() => AztecDetector.Detect(local, effort, offset));
        }

        if ((formats & BarcodeFormat.Pdf417) != 0)
        {
            jobs.Add(() => Pdf417Detector.Detect(local, inverted ? image.Inverted() : image, effort, offset));
        }

        return jobs;
    }

    private static List<Found> RunJobs(List<Func<List<Found>>> jobs, int threads)
    {
        var found = new List<Found>[jobs.Count];
        if (threads > 1 && jobs.Count > 1)
        {
            Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(threads, jobs.Count) }, i => found[i] = jobs[i]());
        }
        else
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                found[i] = jobs[i]();
            }
        }

        return [.. found.SelectMany(f => f)];
    }

    /// <summary>Keeps one result per symbol: the same format and text found again near the same place is a duplicate.</summary>
    private static List<Found> Merge(List<Found> results)
    {
        var kept = new List<Found>();
        Found[] matrices = [.. results.Where(f => IsMatrix(f.Data.Format))];
        foreach (Found found in results)
        {
            // A linear read inside a 2D symbol is part of that symbol.
            Vector2 center = Center(found.Corners);
            if (!IsMatrix(found.Data.Format) && matrices.Any(m => Inside(m.Corners, center)))
            {
                continue;
            }

            bool duplicate = kept.Any(k => k.Data.Format == found.Data.Format && k.Data.Text == found.Data.Text
                && Vector2.Distance(Center(k.Corners), center) < Size(k.Corners) + Size(found.Corners));
            if (!duplicate)
            {
                kept.Add(found);
            }
        }

        // Reading order: top to bottom, then left to right.
        return [.. kept.OrderBy(f => MathF.Round(Center(f.Corners).Y / 32)).ThenBy(f => Center(f.Corners).X)];
    }

    private static bool IsMatrix(BarcodeFormat format) => format is BarcodeFormat.Qr or BarcodeFormat.DataMatrix or BarcodeFormat.Pdf417 or BarcodeFormat.Aztec;

    /// <summary>Whether a point is inside a convex quadrilateral.</summary>
    internal static bool Inside(Vector2[] quad, Vector2 point)
    {
        int sign = 0;
        for (int i = 0; i < quad.Length; i++)
        {
            Vector2 a = quad[i], b = quad[(i + 1) % quad.Length];
            float cross = ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
            int s = Math.Sign(cross);
            if (s != 0)
            {
                if (sign != 0 && s != sign)
                {
                    return false;
                }

                sign = s;
            }
        }

        return true;
    }

    private static float Size(Vector2[] corners)
    {
        float size = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            size = MathF.Max(size, Vector2.Distance(corners[i], corners[(i + 1) % corners.Length]));
        }

        return size;
    }

    private static BarcodeResult ToResult(Found found, bool inverted)
    {
        DecodedData data = found.Data;
        return new BarcodeResult(data.Format, data.Text, data.Bytes, found.Corners)
        {
            Eci = data.Eci,
            Gs1 = data.Gs1,
            ErrorsCorrected = data.ErrorsCorrected,
            Inverted = inverted,
            Mirrored = data.Mirrored,
            AddOn = data.AddOn,
        };
    }

    internal static Vector2 Center(Vector2[] corners)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2 c in corners)
        {
            sum += c;
        }

        return sum / corners.Length;
    }
}
