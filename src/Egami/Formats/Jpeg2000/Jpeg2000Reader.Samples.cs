using Egami.Metadata;

namespace Egami.Formats.Jpeg2000;

/// <content>Decoding to stored samples, for <see cref="SampleImage.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</content>
internal sealed partial class Jpeg2000Reader : ISampleFormatReader
{
    /// <summary>
    /// Decodes every component at full resolution and copies its samples as the decoder leaves them: after the inverse wavelet
    /// and component transforms and the DC level shift (T.800 G.1), so signed components keep their sign and unsigned ones run
    /// from 0. Palettes, channel definitions and color spaces are not applied; subsampled components are replicated.
    /// </summary>
    public SampleImage ReadSamples(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, headerOnly: false, (file, stream) =>
        {
            J2kComponent[] components = stream.Components;
            foreach (J2kComponent component in components)
            {
                if (component.Precision > 31)
                {
                    throw new NotSupportedException($"This JPEG 2000 image has {component.Precision}-bit samples; stored samples are given up to 31 bits.");
                }
            }

            J2kGrid grid = J2kGrid.Create(stream, 0);
            int count = components.Length;
            int[] samples = SampleImage.Allocate(input, grid.Width, grid.Height, count);
            J2kImage image = Decode(input, stream, options, needed: null);
            try
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    Span<int> row = samples.AsSpan(y * grid.Width * count, grid.Width * count);
                    for (int c = 0; c < count; c++)
                    {
                        ReadOnlySpan<int> plane = image.Planes[c];
                        int[] columns = grid.Columns[c];
                        int start = (grid.Rows[c][y] - image.RowOffsets[c]) * image.Widths[c];
                        for (int x = 0; x < grid.Width; x++)
                        {
                            row[(x * count) + c] = plane[start + columns[x]];
                        }
                    }
                }

                RasterMetadata metadata = new();
                Describe(metadata, file, stream, options);
                metadata.FormatName = Jpeg2000Format.Info.Name;
                foreach ((string id, string message) in file.Warnings)
                {
                    input.Warn(metadata, id, message);
                }

                if (image.Truncated)
                {
                    input.Warn(metadata, "Jpeg2000.Truncated", "The codestream ends before its last packet; what it holds was decoded.");
                }

                if (image.MissingTiles > 0)
                {
                    input.Warn(metadata, "Jpeg2000.MissingTiles", $"{image.MissingTiles} tiles have no data and were left 0.");
                }

                SampleComponent[] described = [.. components.Select(c => new SampleComponent(c.Precision, c.Signed))];
                return new SampleImage(grid.Width, grid.Height, described, Color(metadata, count), samples, metadata);
            }
            finally
            {
                image.Release();
            }
        });
    }

    /// <summary>Says what the components are from the file's color specification, or from their count in a bare codestream.</summary>
    private static SampleColor Color(RasterMetadata metadata, int count)
    {
        if (metadata.FormatDetails is not Jpeg2000Details details || details.HasPalette)
        {
            return SampleColor.Unknown;
        }

        return details.ColorSpace switch
        {
            Jpeg2000ColorSpace.Gray when count <= 2 => SampleColor.Gray,
            Jpeg2000ColorSpace.Srgb when count is 3 or 4 => SampleColor.Rgb,
            Jpeg2000ColorSpace.Sycc when count is 3 or 4 => SampleColor.YCbCr,
            Jpeg2000ColorSpace.Unspecified or Jpeg2000ColorSpace.Icc => count switch
            {
                1 or 2 => SampleColor.Gray,
                3 or 4 => SampleColor.Rgb,
                _ => SampleColor.Unknown,
            },
            _ => SampleColor.Unknown,
        };
    }
}
