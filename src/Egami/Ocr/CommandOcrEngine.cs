using System.Diagnostics;
using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Ocr;

/// <summary>What an OCR program writes, for <see cref="CommandOcrOptions.Output"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="command"/>
/// </example>
public enum OcrOutputFormat
{
    /// <summary>Tell hOCR from ALTO by the content: an <c>alto</c> root element is ALTO, anything else hOCR.</summary>
    Auto,

    /// <summary>hOCR.</summary>
    Hocr,

    /// <summary>ALTO.</summary>
    Alto,
}

/// <summary>Options for <see cref="CommandOcrEngine"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="command"/>
/// </example>
public sealed record CommandOcrOptions
{
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

    /// <summary>Gets the default options.</summary>
    public static CommandOcrOptions Default { get; } = new();

    /// <summary>Gets what the program writes. Defaults to <see cref="OcrOutputFormat.Auto"/>.</summary>
    public OcrOutputFormat Output { get; init; }

    /// <summary>
    /// Gets the languages the program reads, reported in <see cref="OcrEngineInfo.Languages"/> and used for <c>{langs}</c> when a
    /// request names none. Empty by default.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Gets how long one run may take before the program is stopped. Defaults to 5 minutes. Must be positive.</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive.");
    }

    /// <summary>Gets the folder the program runs in, or <see langword="null"/> for the run's own temporary folder.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the engine's name in <see cref="OcrEngineInfo.Name"/>, or <see langword="null"/> for the program's file name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the limits for reading what the program writes. Defaults to <see cref="OcrReadOptions.Default"/>.</summary>
    public OcrReadOptions Read { get; init; } = OcrReadOptions.Default;
}

/// <summary>
/// An <see cref="IOcrEngine"/> that runs an OCR program for each image and reads the hOCR or ALTO it writes, such as
/// <c>tesseract {image} {output} -l {langs} --dpi {dpi} hocr</c>. Egami ships no engine and links none: the program is yours.
/// </summary>
/// <remarks>
/// <para>
/// The command is split into words at spaces, with double or single quotes around a word that holds spaces; no shell runs it, so
/// file names and languages can never become commands. Each word may hold these placeholders: <c>{image}</c>, the path of the
/// image as a PNG file; <c>{output}</c>, a path (without extension) for the program's output file; <c>{langs}</c>, the request's
/// languages joined by <c>+</c>; <c>{dpi}</c>, the resolution as a whole number; <c>{page}</c>, the page index. Write <c>{{</c> and
/// <c>}}</c> for literal braces.
/// </para>
/// <para>
/// With <c>{output}</c> in the command, the engine reads that file, or the one file the program wrote beside it with an extension
/// added (Tesseract adds <c>.hocr</c> or <c>.xml</c>); without it, the engine reads the program's standard output. Each run gets a
/// temporary folder of its own that is deleted afterwards, so runs may overlap.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="command"/>
/// </example>
public sealed class CommandOcrEngine : IOcrEngine
{
    private const int ErrorTail = 4096;

    private static readonly string[] Placeholders = ["image", "output", "langs", "dpi", "page"];

    private readonly string _program;
    private readonly string[] _arguments;
    private readonly bool _usesOutput;
    private readonly CommandOcrOptions _options;

    /// <summary>Initializes a new instance of the <see cref="CommandOcrEngine"/> class.</summary>
    /// <param name="command">The program and its arguments, with placeholders, such as <c>tesseract {image} - -l {langs} hocr</c>.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="CommandOcrOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The command is empty, has an unclosed quote, or names an unknown placeholder.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="command"/>
    /// </example>
    public CommandOcrEngine(string command, CommandOcrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        _options = options ?? CommandOcrOptions.Default;
        List<string> words = Split(command);
        if (words.Count == 0)
        {
            throw new ArgumentException("The command is empty.", nameof(command));
        }

        foreach (string word in words)
        {
            foreach (string name in Names(word))
            {
                if (Array.IndexOf(Placeholders, name) < 0)
                {
                    throw new ArgumentException($"The command names an unknown placeholder {{{name}}}; use {{image}}, {{output}}, {{langs}}, {{dpi}} or {{page}}.", nameof(command));
                }

                _usesOutput |= name == "output";
            }
        }

        _program = Substitute(words[0], _ => string.Empty);
        _arguments = [.. words.Skip(1)];
        Info = new OcrEngineInfo(_options.Name ?? Path.GetFileNameWithoutExtension(_program)) { Languages = _options.Languages };
    }

    /// <inheritdoc/>
    public OcrEngineInfo Info { get; }

    /// <summary>Runs the program on the request's image and reads what it writes.</summary>
    /// <param name="request">The image and how to read it.</param>
    /// <param name="cancellationToken">Cancels the run; the program is stopped.</param>
    /// <returns>The first page the program wrote, with <see cref="OcrResult.ImageSize"/> set to the image's size if the output gives none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The command uses <c>{langs}</c> and neither the request nor the options name a language.</exception>
    /// <exception cref="OcrEngineException">The program could not start, failed, timed out or wrote no hOCR or ALTO.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> languages = request.Languages.Count > 0 ? request.Languages : _options.Languages;
        DirectoryInfo folder = Directory.CreateTempSubdirectory("egami-ocr-");
        try
        {
            string image = Path.Combine(folder.FullName, "image.png");
            string output = Path.Combine(folder.FullName, "output");
            await SaveForEngineAsync(request.Image, image, cancellationToken).ConfigureAwait(false);
            string Value(string name) => name switch
            {
                "image" => image,
                "output" => output,
                "langs" => languages.Count > 0 ? string.Join('+', languages) : throw new ArgumentException("The command uses {langs}, but the request and the options name no language.", nameof(request)),
                "dpi" => ((int)Math.Round(request.Dpi)).ToString(CultureInfo.InvariantCulture),
                _ => request.PageIndex.ToString(CultureInfo.InvariantCulture),
            };

            ProcessStartInfo start = new(_program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _options.WorkingDirectory ?? folder.FullName,
                CreateNoWindow = true,
            };
            foreach (string argument in _arguments)
            {
                start.ArgumentList.Add(Substitute(argument, Value));
            }

            string standardOutput = await RunAsync(start, cancellationToken).ConfigureAwait(false);
            string text = _usesOutput ? ReadOutputFile(output) : standardOutput;
            IReadOnlyList<OcrResult> pages;
            try
            {
                pages = Parse(text);
            }
            catch (CorruptDataException e)
            {
                throw new OcrEngineException($"{Info.Name} wrote no hOCR or ALTO: {e.Message}", innerException: e);
            }

            OcrResult page = pages.Count > 0 ? pages[0] : OcrResult.Empty;
            return page.ImageSize.Width > 0 && page.ImageSize.Height > 0 ? page : page with { ImageSize = new Size(request.Image.Width, request.Image.Height) };
        }
        finally
        {
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Starts the program and waits for it; returns its standard output.</summary>
    private async Task<string> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new OcrEngineException($"{Info.Name} did not start.");
            }
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new OcrEngineException($"{Info.Name} could not start: {e.Message}", innerException: e);
        }

        process.StandardInput.Close();
        long maxBytes = _options.Read.MaxBytes;
        Task<string> output = ReadLimitedAsync(process.StandardOutput, maxBytes);
        Task<string> errors = ReadTailAsync(process.StandardError);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OcrEngineException($"{Info.Name} did not finish within {_options.Timeout}.", errorOutput: await TailOrNull(errors).ConfigureAwait(false));
        }

        string standardOutput;
        try
        {
            standardOutput = await output.ConfigureAwait(false);
        }
        catch (LimitExceededException)
        {
            Stop(process);
            throw;
        }

        string errorOutput = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new OcrEngineException($"{Info.Name} exited with code {process.ExitCode}.", process.ExitCode, errorOutput.Length > 0 ? errorOutput : null);
        }

        return standardOutput;
    }

    private static async Task<string?> TailOrNull(Task<string> errors)
    {
        try
        {
            string tail = await errors.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return tail.Length > 0 ? tail : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It has already exited.
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, long maxChars)
    {
        StringBuilder text = new();
        char[] buffer = new char[16384];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > maxChars)
            {
                throw new LimitExceededException(nameof(OcrReadOptions.MaxBytes), maxChars, text.Length + read);
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        StringBuilder tail = new();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > ErrorTail)
            {
                tail.Remove(0, tail.Length - ErrorTail);
            }
        }

        return tail.ToString().Trim();
    }

    private string ReadOutputFile(string output)
    {
        string? path = File.Exists(output) ? output : null;
        if (path is null)
        {
            string[] written = Directory.GetFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".*");
            path = written.Length == 1 ? written[0] : null;
        }

        if (path is null)
        {
            throw new OcrEngineException($"{Info.Name} wrote no output file at {{output}}.");
        }

        using FileStream stream = File.OpenRead(path);
        return OcrText.ReadAll(stream, _options.Read.MaxBytes);
    }

    private IReadOnlyList<OcrResult> Parse(string text)
    {
        OcrOutputFormat format = _options.Output;
        if (format == OcrOutputFormat.Auto)
        {
            format = text.Contains("<alto", StringComparison.OrdinalIgnoreCase) || text.Contains(":alto", StringComparison.OrdinalIgnoreCase) ? OcrOutputFormat.Alto : OcrOutputFormat.Hocr;
        }

        return format == OcrOutputFormat.Alto ? Alto.Parse(text, _options.Read) : Hocr.Parse(text, _options.Read);
    }

    /// <summary>Splits a command into words at spaces, keeping quoted runs together.</summary>
    internal static List<string> Split(string command)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        char quote = '\0';
        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (quote != '\0')
        {
            throw new ArgumentException("The command has an unclosed quote.", nameof(command));
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>Returns the placeholder names in a word, such as "image" for "--in={image}".</summary>
    private static List<string> Names(string word)
    {
        List<string> names = [];
        Substitute(word, name =>
        {
            names.Add(name);
            return string.Empty;
        });
        return names;
    }

    /// <summary>Replaces each {name} in a word; {{ and }} are literal braces.</summary>
    internal static string Substitute(string word, Func<string, string> value)
    {
        StringBuilder result = new(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if ((c == '{' || c == '}') && i + 1 < word.Length && word[i + 1] == c)
            {
                result.Append(c);
                i++;
            }
            else if (c == '{')
            {
                int close = word.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new ArgumentException($"The command word \"{word}\" has an unclosed {{.", nameof(word));
                }

                result.Append(value(word[(i + 1)..close]));
                i = close;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Writes the image as the program reads it fastest: gray PNG when every pixel is gray and opaque (scans, mostly), RGB
    /// when it is opaque, else RGBA. Tesseract reads a gray PNG of a page about a quarter faster than the same page in RGBA.
    /// </summary>
    private static async Task SaveForEngineAsync(Raster<Rgba8> image, string path, CancellationToken cancellationToken)
    {
        if (!image.IsOpaque())
        {
            await image.SaveAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        using Raster narrow = image.IsGray() ? image.As<Gray8>() : image.As<Rgb8>();
        await narrow.SaveAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
