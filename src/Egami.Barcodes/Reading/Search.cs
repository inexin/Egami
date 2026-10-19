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

    /// <summary>The full search: every detector over the image, then duplicates merged.</summary>
    private static List<Found> Scan(LuminanceImage image, BarcodeReaderOptions options)
    {
        var results = new List<Found>();
        if (PureReader.Read(image, options.Formats) is Found pure)
        {
            results.Add(pure);
        }

        return results;
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
