using System.CommandLine;
using System.Globalization;
using System.Numerics;

using Egami.Cli.Pipeline;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Substitutes;
using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// The <c>waveform</c> and <c>spectrum</c> commands (V1.4 Build Plan task 18, Design Q9 and Q11): the audio pictures of
/// <see cref="AudioPictures"/> written as images, with an optional time, level and frequency legend drawn in the bundled
/// Liberation Sans.
/// </summary>
internal static class AudioPictureCommands
{
    private const int LegendLeft = 58, LegendBottom = 26, LegendTop = 10, LegendRight = 14;
    private const float LabelSize = 11;

    private static readonly Rgba8 LegendBackground = new(22, 22, 26, 255);
    private static readonly Rgba8 LegendInk = new(200, 200, 205, 255);
    private static readonly Rgba8 GridInk = new(255, 255, 255, 40);
    private static readonly double[] LogMarks = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
    private static readonly double[] StepMultiples = [1, 2, 5, 10];

    public static Command Waveform(VideoSession session)
    {
        PictureOptions options = new("waves/{name}.png");
        Option<string> size = new("--size", "-s") { Description = "The picture's size, width x height. Default 1800x280.", HelpName = "WxH", DefaultValueFactory = _ => "1800x280" };
        Option<string> channels = new("--channels") { Description = "mixed (the default) draws every channel in one strip; stacked gives each its own.", HelpName = "mixed|stacked", DefaultValueFactory = _ => "mixed" };
        Option<string> scale = new("--scale") { Description = "linear (the default) or db, which shows quiet passages; --floor sets the lowest level.", HelpName = "linear|db", DefaultValueFactory = _ => "linear" };
        Option<double> floor = new("--floor") { Description = "The lowest level --scale db shows, in dBFS. Default -60.", HelpName = "dB", DefaultValueFactory = _ => -60 };
        Option<string?> color = new("--color") { Description = "The outline color as #rrggbb or #rrggbbaa. Default a mid blue.", HelpName = "color" };
        Option<string?> rms = new("--rms-color") { Description = "The color of the RMS band inside the outline, or none. Default a light blue.", HelpName = "color|none" };
        Option<string?> background = new("--background") { Description = "The background color. Default transparent, or dark gray with --legend.", HelpName = "color" };
        Option<bool> legend = new("--legend") { Description = "Add a time axis and level labels around the picture." };
        options.Track.Description = "Which audio track: a:1 for the second, or a track number as info shows it; the first audio track by default.";
        Command command = new("waveform", "Draw the waveform of each file's audio as an image: the min to max outline of each column with the RMS level inside, as editors and podcast players show it.");
        options.AddTo(command);
        command.Options.Add(size);
        command.Options.Add(channels);
        command.Options.Add(scale);
        command.Options.Add(floor);
        command.Options.Add(color);
        command.Options.Add(rms);
        command.Options.Add(background);
        command.Options.Add(legend);
        command.SetAction(result =>
        {
            (int width, int height) = Size(result.GetValue(size)!);
            bool withLegend = result.GetValue(legend);
            WaveformOptions picture = new()
            {
                Width = withLegend ? Math.Max(1, width - LegendLeft - LegendRight) : width,
                Height = withLegend ? Math.Max(1, height - LegendTop - LegendBottom) : height,
                Channels = Channels(result.GetValue(channels)!),
                Scale = result.GetValue(scale)!.ToLowerInvariant() switch
                {
                    "linear" => WaveformScale.Linear,
                    "db" or "decibel" => WaveformScale.Decibel,
                    string other => throw new UsageException($"--scale '{other}' is not linear or db."),
                },
                Floor = result.GetValue(floor) is < 0 and > -1000 and var f ? f : throw new UsageException("--floor must be below 0 dBFS."),
            };
            picture = result.GetValue(color) is { } outline ? picture with { Foreground = Color(outline, "--color") } : picture;
            picture = result.GetValue(rms) is { } rmsText ? picture with { Rms = rmsText.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : Color(rmsText, "--rms-color") } : picture;
            picture = result.GetValue(background) is { } back ? picture with { Background = Color(back, "--background") } : picture;
            Rgba8 frame = result.GetValue(background) is { } legendBack ? Color(legendBack, "--background") : LegendBackground;
            return Run(session, options, result, "waves/{name}.png", (input, track) =>
            {
                Raster<Rgba8> image = AudioPictures.Waveform(input.Path!, picture with { Track = track.Number }, session.Context);
                return withLegend ? WaveformLegend(image, track, picture, frame, session, input) : image;
            });
        });
        return command;
    }

    public static Command Spectrum(VideoSession session)
    {
        PictureOptions options = new("spectra/{name}.png");
        Option<string> size = new("--size", "-s") { Description = "The picture's size, width x height. Default 1800x512.", HelpName = "WxH", DefaultValueFactory = _ => "1800x512" };
        Option<int> window = new("--window") { Description = "The FFT window in samples, a power of two from 256 to 32768; longer resolves frequency finer and time coarser. Default 2048.", HelpName = "samples", DefaultValueFactory = _ => 2048 };
        Option<string> scale = new("--scale") { Description = "The frequency axis: linear (the default) or log, which gives each octave the same height.", HelpName = "linear|log", DefaultValueFactory = _ => "linear" };
        Option<double> minFrequency = new("--min-frequency") { Description = "The lowest frequency on the log axis, in hertz. Default 20.", HelpName = "Hz", DefaultValueFactory = _ => 20 };
        Option<double> floor = new("--floor") { Description = "The level drawn darkest, in dBFS. Default -120.", HelpName = "dB", DefaultValueFactory = _ => -120 };
        Option<double> ceiling = new("--ceiling") { Description = "The level drawn brightest, in dBFS, where a full-scale sine reads 0. Default 0.", HelpName = "dB", DefaultValueFactory = _ => 0 };
        Option<string> channels = new("--channels") { Description = "mixed (the default) shows the mix of every channel; stacked gives each its own strip.", HelpName = "mixed|stacked", DefaultValueFactory = _ => "mixed" };
        Option<string> colors = new("--colors") { Description = "The color map: magma (the default) or gray.", HelpName = "magma|gray", DefaultValueFactory = _ => "magma" };
        Option<bool> legend = new("--legend") { Description = "Add a time axis and frequency labels around the picture." };
        options.Track.Description = "Which audio track: a:1 for the second, or a track number as info shows it; the first audio track by default.";
        Command command = new("spectrum", "Draw the spectrogram of each file's audio as an image: frequency up, time across, level as color.");
        options.AddTo(command);
        command.Options.Add(size);
        command.Options.Add(window);
        command.Options.Add(scale);
        command.Options.Add(minFrequency);
        command.Options.Add(floor);
        command.Options.Add(ceiling);
        command.Options.Add(channels);
        command.Options.Add(colors);
        command.Options.Add(legend);
        command.SetAction(result =>
        {
            (int width, int height) = Size(result.GetValue(size)!);
            bool withLegend = result.GetValue(legend);
            SpectrogramOptions picture = new()
            {
                Width = withLegend ? Math.Max(1, width - LegendLeft - LegendRight) : width,
                Height = withLegend ? Math.Max(1, height - LegendTop - LegendBottom) : height,
                WindowSize = result.GetValue(window),
                Scale = result.GetValue(scale)!.ToLowerInvariant() switch
                {
                    "linear" => FrequencyScale.Linear,
                    "log" or "logarithmic" => FrequencyScale.Logarithmic,
                    string other => throw new UsageException($"--scale '{other}' is not linear or log."),
                },
                MinFrequency = result.GetValue(minFrequency),
                Floor = result.GetValue(floor),
                Ceiling = result.GetValue(ceiling),
                Channels = Channels(result.GetValue(channels)!),
                Colors = result.GetValue(colors)!.ToLowerInvariant() switch
                {
                    "magma" => SpectrogramColors.Magma,
                    "gray" or "grey" or "grayscale" => SpectrogramColors.Grayscale,
                    string other => throw new UsageException($"--colors '{other}' is not magma or gray."),
                },
            };
            if (picture.WindowSize is < 256 or > 32768 || !BitOperations.IsPow2(picture.WindowSize))
            {
                throw new UsageException("--window must be a power of two from 256 to 32768.");
            }

            if (!(picture.MinFrequency > 0))
            {
                throw new UsageException("--min-frequency must be above 0 Hz.");
            }

            if (!(picture.Floor < picture.Ceiling))
            {
                throw new UsageException("--floor must be below --ceiling.");
            }

            return Run(session, options, result, "spectra/{name}.png", (input, track) =>
            {
                Raster<Rgba8> image = AudioPictures.Spectrogram(input.Path!, picture with { Track = track.Number }, session.Context);
                return withLegend ? SpectrumLegend(image, track, picture, session, input) : image;
            });
        });
        return command;
    }

    /// <summary>Draws each input's picture and writes it where <c>-o</c> says.</summary>
    private static int Run(VideoSession session, PictureOptions options, ParseResult result, string example, Func<InputFile, AudioTrackInfo, Raster<Rgba8>> draw)
    {
        BatchOptions batch = options.Media.Batch(session, result);
        List<InputFile> inputs = options.Media.ExpandInputs(session, result);
        if (result.GetValue(options.Output) is null)
        {
            throw new UsageException($"No output: give -o with a file, a folder or a template such as {example}.");
        }

        OutputTemplate output = options.OutputTemplate(session, result, inputs);
        TrackSelection selection = options.Tracks(result);
        SaveSpec save = options.Save(result);
        string? format = result.GetValue(options.Format);
        return Batch.Run(session, inputs, batch, input =>
        {
            if (input.Path is null)
            {
                throw new UsageException("The audio pictures read the file to find its length first, so they need a file, not standard input.");
            }

            MediaInfo info = MediaIO.Probe(session, input);
            AudioTrackInfo track = (AudioTrackInfo)selection.SelectOne(info.Tracks, TrackKind.Audio, input.Display);
            PictureTarget target = PictureTarget.Resolve(session, input, output, format, "png");
            using Raster<Rgba8> image = draw(input, track);
            target.Write(session, image, save);
            return [target.Path];
        });
    }

    private static (int Width, int Height) Size(string text)
    {
        string[] parts = text.Trim().ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            && width is >= 16 and <= 32768 && height is >= 16 and <= 32768
                ? (width, height)
                : throw new UsageException($"--size '{text}' is not a size: give width x height, each from 16 to 32768, such as 1800x280.");
    }

    private static AudioPictureChannels Channels(string text) => text.ToLowerInvariant() switch
    {
        "mixed" or "mix" => AudioPictureChannels.Mixed,
        "stacked" or "stack" => AudioPictureChannels.Stacked,
        string other => throw new UsageException($"--channels '{other}' is not mixed or stacked."),
    };

    private static Rgba8 Color(string text, string option)
    {
        string hex = text.Trim().TrimStart('#');
        if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            uint rgba = hex.Length == 6 ? (value << 8) | 0xFF : value;
            return new Rgba8((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        }

        throw new UsageException($"{option} '{text}' is not a color: give #rrggbb or #rrggbbaa, such as #2e6ec4.");
    }

    /// <summary>The waveform inside a frame with a time axis below and level labels for each strip on the left.</summary>
    private static Raster<Rgba8> WaveformLegend(Raster<Rgba8> image, AudioTrackInfo track, WaveformOptions options, Rgba8 background, VideoSession session, InputFile input)
    {
        using (image)
        {
            int strips = options.Channels == AudioPictureChannels.Stacked ? Math.Max(1, track.Channels) : 1;
            List<(float Y, string Label)> levels = [];
            for (int s = 0; s < strips; s++)
            {
                float top = s * options.Height / (float)strips;
                float bottom = (s + 1) * options.Height / (float)strips;
                float middle = (top + bottom) / 2, half = (bottom - top) / 2;
                if (options.Scale == WaveformScale.Linear)
                {
                    levels.Add((middle - half, "1"));
                    levels.Add((middle - (half / 2), "0.5"));
                    levels.Add((middle, "0"));
                    levels.Add((middle + (half / 2), "-0.5"));
                    levels.Add((middle + half, "-1"));
                }
                else
                {
                    levels.Add((middle - half, "0 dB"));
                    levels.Add((middle - (half / 2), $"{options.Floor / 2:0}"));
                    levels.Add((middle, $"{options.Floor:0}"));
                    levels.Add((middle + (half / 2), $"{options.Floor / 2:0}"));
                    levels.Add((middle + half, "0 dB"));
                }
            }

            return Frame(image, background, Duration(session, input, track), levels, session);
        }
    }

    /// <summary>The spectrogram inside a frame with a time axis below and frequency labels on the left.</summary>
    private static Raster<Rgba8> SpectrumLegend(Raster<Rgba8> image, AudioTrackInfo track, SpectrogramOptions options, VideoSession session, InputFile input)
    {
        using (image)
        {
            int strips = options.Channels == AudioPictureChannels.Stacked ? Math.Max(1, track.Channels) : 1;
            double nyquist = track.SampleRate / 2.0;
            double low = Math.Min(options.MinFrequency, nyquist / 2);
            List<(float Y, string Label)> labels = [];
            for (int s = 0; s < strips; s++)
            {
                float top = s * options.Height / (float)strips;
                float height = ((s + 1) * options.Height / (float)strips) - top;
                IEnumerable<double> marks = options.Scale == FrequencyScale.Linear
                    ? Steps(nyquist, Math.Max(2, (int)(height / 40)))
                    : LogMarks.Where(f => f >= low && f <= nyquist);
                foreach (double frequency in marks)
                {
                    double t = options.Scale == FrequencyScale.Linear ? frequency / nyquist : Math.Log(frequency / low) / Math.Log(nyquist / low);
                    labels.Add(((float)(top + ((1 - t) * height)), Hertz(frequency)));
                }
            }

            return Frame(image, LegendBackground, Duration(session, input, track), labels, session);
        }
    }

    /// <summary>Puts the picture on a larger background and draws the time axis and the left-hand labels with their grid lines.</summary>
    private static Raster<Rgba8> Frame(Raster<Rgba8> image, Rgba8 background, double? duration, List<(float Y, string Label)> labels, VideoSession session)
    {
        int width = image.Width + LegendLeft + LegendRight, height = image.Height + LegendTop + LegendBottom;
        Raster<Rgba8> framed = Raster.Create(width, height, background, session.Images.Context);
        try
        {
            framed.Apply(ops => ops.DrawImage(image, new Point(LegendLeft, LegendTop)));
            FontFamily family = SubstituteFonts.Collection.TryGetFamily(PictureCommands.SheetFamily, out FontFamily bundled) ? bundled : SubstituteFonts.Collection.Families[0];
            using RasterCanvas<Rgba8> canvas = framed.CreateCanvas();
            SolidPaint ink = new(LegendInk), grid = new(GridInk);
            ParagraphStyle right = Style(family, LegendInk, TextAlign.Right), center = Style(family, LegendInk, TextAlign.Center);
            foreach ((float y, string label) in labels)
            {
                float at = LegendTop + y;
                canvas.FillRect(new RectF(LegendLeft - 4, at - 0.5f, 4, 1), ink);
                canvas.FillRect(new RectF(LegendLeft, at - 0.5f, image.Width, 1), grid);
                canvas.DrawText(label, new RectF(0, at - 8, LegendLeft - 7, 16), right, VerticalAlign.Center);
            }

            canvas.FillRect(new RectF(LegendLeft, LegendTop + image.Height, image.Width, 1), ink);
            if (duration is { } seconds && seconds > 0)
            {
                foreach (double time in Steps(seconds, Math.Max(2, image.Width / 110)))
                {
                    float x = LegendLeft + (float)(time / seconds * image.Width);
                    canvas.FillRect(new RectF(x - 0.5f, LegendTop + image.Height, 1, 4), ink);
                    canvas.DrawText(Clock(time, seconds), new RectF(x - 40, LegendTop + image.Height + 5, 80, 16), center, VerticalAlign.Center);
                }
            }

            return framed;
        }
        catch
        {
            framed.Dispose();
            throw;
        }
    }

    private static double? Duration(VideoSession session, InputFile input, AudioTrackInfo track)
    {
        if (track.Duration is { Ticks: > 0 } own)
        {
            return own.Seconds;
        }

        MediaInfo info = MediaIO.Probe(session, input);
        return info.Duration is { Ticks: > 0 } total ? total.Seconds : null;
    }

    /// <summary>Round marks from 0 up to <paramref name="end"/>, about <paramref name="count"/> of them, on 1, 2 and 5 steps.</summary>
    private static IEnumerable<double> Steps(double end, int count)
    {
        double raw = end / count;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = StepMultiples.Select(m => m * magnitude).First(s => s >= raw);
        for (double mark = 0; mark <= end + (step * 1e-9); mark += step)
        {
            yield return Math.Round(mark, 9);
        }
    }

    private static string Hertz(double frequency) =>
        frequency >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{frequency / 1000:0.#} kHz") : string.Create(CultureInfo.InvariantCulture, $"{frequency:0} Hz");

    private static string Clock(double seconds, double total)
    {
        if (total < 10)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s");
        }

        long whole = (long)Math.Round(seconds);
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    private static ParagraphStyle Style(FontFamily family, Rgba8 color, TextAlign align) => new()
    {
        DefaultStyle = new TextStyle { FontFamilies = [family], Size = LabelSize, Color = color },
        Align = align,
        MaxLines = 1,
    };
}
