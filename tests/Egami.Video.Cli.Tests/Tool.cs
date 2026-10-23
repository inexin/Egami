using System.Text;

namespace Egami.Video.Cli.Tests;

/// <summary>What one run of the tool did.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Text">Standard output as text.</param>
/// <param name="Bytes">Standard output as bytes (files written to -o -).</param>
/// <param name="Error">Standard error.</param>
internal sealed record ToolRun(int Code, string Text, byte[] Bytes, string Error);

/// <summary>Runs egami-video in this process, through the same entry point the executable uses, in a scratch folder.</summary>
internal sealed class Tool : IDisposable
{
    public Tool()
    {
        Folder = Path.Combine(Path.GetTempPath(), "egami-video-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    /// <summary>Gets the scratch folder the tool runs in.</summary>
    public string Folder { get; }

    /// <summary>Gets the environment variables the tool sees.</summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the token a run is stopped with, as Ctrl+C does.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>The video corpus folder (tests/video/corpus, copied next to the tests).</summary>
    public static string Corpus { get; } = Path.Combine(AppContext.BaseDirectory, "corpus");

    /// <summary>The repository's test images folder.</summary>
    public static string Images { get; } = FindImages();

    /// <summary>Copies a corpus file into the scratch folder and returns its name there.</summary>
    public string Copy(string corpusFile, string? name = null)
    {
        name ??= Path.GetFileName(corpusFile);
        string target = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(Corpus, corpusFile), target, overwrite: true);
        return name;
    }

    /// <summary>Copies a test image into the scratch folder and returns its name there.</summary>
    public string CopyImage(string image, string? name = null)
    {
        name ??= Path.GetFileName(image);
        string target = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(Images, image), target, overwrite: true);
        return name;
    }

    /// <summary>A path in the scratch folder.</summary>
    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>Runs the tool with arguments.</summary>
    public ToolRun Run(params string[] args) => Run(args, null);

    /// <summary>Runs the tool with arguments and standard input.</summary>
    public ToolRun Run(string[] args, byte[]? stdin)
    {
        MemoryStream output = new();
        StringWriter text = new() { NewLine = "\n" };
        StringWriter error = new() { NewLine = "\n" };
        CliConsole console = new(() => new NonSeekable(new MemoryStream(stdin ?? [])), () => new KeepOpen(output), text, TextWriter.Synchronized(error));
        int code = VideoCli.Run(args, console, Folder, name => Environment.TryGetValue(name, out string? value) ? value : null, session => session.Cancellation = Cancellation);
        return new ToolRun(code, text.ToString(), output.ToArray(), error.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindImages()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "tests", "images")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return Path.Combine(folder ?? throw new DirectoryNotFoundException("tests/images"), "tests", "images");
    }

    /// <summary>A stream that ignores Dispose, so the tool's writes stay readable; it cannot seek, as a pipe cannot.</summary>
    private sealed class KeepOpen(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    /// <summary>Standard input as a pipe: readable once, not seekable.</summary>
    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Text helpers for assertions.</summary>
internal static class TextExtensions
{
    public static string Utf8(this byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
