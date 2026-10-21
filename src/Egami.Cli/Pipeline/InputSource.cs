using Egami.Cli.Host;
using Egami.Formats;

namespace Egami.Cli.Pipeline;

/// <summary>Reads one input: a file, or standard input read once into memory so it can be probed and then decoded.</summary>
internal sealed class InputSource
{
    private byte[]? _stdin;
    private RasterInfo? _info;

    public InputSource(InputFile file)
    {
        File = file;
    }

    /// <summary>Gets the input.</summary>
    public InputFile File { get; }

    /// <summary>Reads the input's size, format, frames and orientation without decoding pixels.</summary>
    public RasterInfo Probe(CliSession session)
    {
        if (_info is null)
        {
            _info = File.Path is { } path ? Raster.Probe(path, session.Context) : Raster.Probe(Stdin(session), session.Context);
            session.Info($"{File.Display}: {_info.Format.Name} {_info.Width}x{_info.Height}");
        }

        return _info;
    }

    /// <summary>Opens the input's bytes from the start.</summary>
    public Stream Open(CliSession session)
    {
        return File.Path is { } path
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)
            : new MemoryStream(Stdin(session), writable: false);
    }

    /// <summary>Decodes the input.</summary>
    public Raster Load(CliSession session, ReaderOptions? options = null)
    {
        return File.Path is { } path ? Raster.Load(path, session.Context, options) : Raster.Load(Stdin(session), session.Context, options);
    }

    /// <summary>Decodes every image of the input as a list (frames of an animation, pages, icon entries).</summary>
    public RasterList LoadAll(CliSession session, ReaderOptions? options = null)
    {
        return File.Path is { } path ? Raster.LoadAll(path, session.Context, options) : Raster.LoadAll(Stdin(session), session.Context, options);
    }

    /// <summary>The input's bytes.</summary>
    public ReadOnlyMemory<byte> Bytes(CliSession session)
    {
        return File.Path is { } path ? System.IO.File.ReadAllBytes(path) : Stdin(session);
    }

    private byte[] Stdin(CliSession session)
    {
        if (_stdin is null)
        {
            using Stream input = session.Console.In();
            using MemoryStream buffer = new();
            byte[] chunk = new byte[1 << 16];
            int empty = 0;

            // A pipe between two native programs in some shells (MSYS bash) carries zero-length writes, which read as an end
            // of data. A closed pipe answers 0 again at once, so a few retries cost nothing and keep the rest of the data.
            while (empty < 16)
            {
                int read = input.Read(chunk);
                if (read == 0)
                {
                    empty++;
                    continue;
                }

                empty = 0;
                buffer.Write(chunk, 0, read);
            }

            _stdin = buffer.ToArray();
        }

        return _stdin;
    }
}

/// <summary>Writes outputs: to standard output, or to a file through a temporary name that replaces the target only once complete.</summary>
internal static class Output
{
    private static readonly Lock StdoutLock = new();

    /// <summary>Writes an output; <paramref name="path"/> is a full path or <c>-</c>.</summary>
    public static void Write(CliSession session, string path, Action<Stream> write)
    {
        if (path == "-")
        {
            lock (StdoutLock)
            {
                using MemoryStream buffer = new();
                write(buffer);
                Stream stdout = session.Console.Out();
                buffer.Position = 0;
                buffer.CopyTo(stdout);
                stdout.Flush();
            }

            return;
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = Path.Combine(folder ?? string.Empty, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.egami-tmp");
        try
        {
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                write(file);
            }

            File.Move(temporary, path, overwrite: true);
            session.Info($"wrote {path}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
