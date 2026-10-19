using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>Runs the readers over an image for one call of ReadBarcodes or ReadBarcode.</summary>
internal static class Search
{
    public static IReadOnlyList<BarcodeResult> Run(LuminanceImage image, BarcodeReaderOptions options, bool firstOnly)
    {
        var found = new List<(Found Found, bool Inverted)>();
        bool tryInverted = options.TryInverted ?? options.Effort >= 5;
        foreach (bool inverted in tryInverted ? [false, true] : (ReadOnlySpan<bool>)[false])
        {
            LuminanceImage source = inverted ? image.Inverted() : image;
            if (options.PureBarcode)
            {
                if (PureReader.Read(source, options.Formats) is Found pure)
                {
                    found.Add((pure, inverted));
                }
            }
            else
            {
                foreach (Found f in Scan(source, options))
                {
                    found.Add((f, inverted));
                }
            }

            if (found.Count > 0 && (firstOnly || options.PureBarcode))
            {
                break;
            }
        }

        return [.. found.Select(f => ToResult(f.Found, f.Inverted))];
    }

    /// <summary>
    /// The full search: every detector over the image, then duplicates merged. On large images the binarization runs in
    /// stripes and the detectors side by side, up to the context's parallelism; each detector's results are kept in a fixed
    /// order, so the results do not depend on the number of threads.
    /// </summary>
    private static List<Found> Scan(LuminanceImage image, BarcodeReaderOptions options)
    {
        BarcodeFormats formats = options.Formats;
        int effort = options.Effort;
        var offset = new Vector2(image.Left, image.Top);
        int threads = (long)image.Width * image.Height < 256 * 1024 ? 1 : Math.Max(1, (options.Context ?? EgamiContext.Default).MaxParallelism);
        BitImage local = Binarizer.Local(image, threads);
        var jobs = new List<Func<List<Found>>>
        {
            () => PureReader.Read(image, formats) is Found pure ? [pure] : [],
        };
        if ((formats & BarcodeFormats.Qr) != 0)
        {
            jobs.Add(() => QrDetector.Detect(local, effort, offset));
        }

        if ((formats & BarcodeFormats.DataMatrix) != 0)
        {
            jobs.Add(() => DataMatrixDetector.Detect(local, Shapes.Components(local, 10), offset));
        }

        if ((formats & BarcodeFormats.Aztec) != 0)
        {
            jobs.Add(() => AztecDetector.Detect(local, effort, offset));
        }

        if ((formats & BarcodeFormats.Pdf417) != 0)
        {
            jobs.Add(() => Pdf417Detector.Detect(local, effort, offset));
        }

        if ((formats & BarcodeFormats.Linear) != 0)
        {
            jobs.Add(() => LinearScanner.Scan(image, effort >= 6 ? local : null, formats, effort));
        }

        var found = new List<Found>[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(threads, jobs.Count) }, i => found[i] = jobs[i]());
        return Merge([.. found.SelectMany(f => f)]);
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
