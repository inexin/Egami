using System.Buffers.Binary;

using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs;

/// <summary>
/// What the first pass of a two-pass encode learned about each frame: how hard it is to code. Pass it as
/// <see cref="VideoEncoderOptions.FirstPassStats"/> with <see cref="RateControlMode.Bitrate"/>, and the second pass spreads the
/// bits over the whole clip by it instead of guessing from the frames so far. <see cref="Save(Stream)"/> keeps it in a file,
/// so the two passes can run apart, as FFmpeg's pass log does.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-two-pass"/>
/// </example>
public sealed class FirstPassStats
{
    private const uint Magic = 0x50464745; // "EGFP"
    private const int Version = 1;

    private readonly FrameStats[] _frames;

    internal FirstPassStats(int width, int height, FrameStats[] frames)
    {
        Width = width;
        Height = height;
        _frames = frames;
    }

    /// <summary>Gets the picture width the first pass saw.</summary>
    public int Width { get; }

    /// <summary>Gets the picture height the first pass saw.</summary>
    public int Height { get; }

    /// <summary>Gets the number of frames.</summary>
    public int FrameCount => _frames.Length;

    internal ReadOnlySpan<FrameStats> Frames => _frames;

    /// <summary>Analyzes frames as a first pass. <see cref="VideoWriter.AnalyzeFirstPass(IEnumerable{VideoFrame}, IProgress{MediaProgress}?, CancellationToken)"/> calls this.</summary>
    /// <param name="frames">The frames in display order, all one size. They are not disposed.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <param name="progress">Called after each frame with the frame's timestamp, or <see langword="null"/>.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentException">The frames change size, or there are none.</exception>
    internal static FirstPassStats Analyze(IEnumerable<VideoFrame> frames, CancellationToken cancellationToken, Action<VideoFrame>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        List<FrameStats> stats = [];
        EncoderPictures? pictures = null;
        FrameAnalyzer? analyzer = null;
        int width = 0, height = 0;
        try
        {
            foreach (VideoFrame frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pictures is null)
                {
                    (width, height) = (frame.Width, frame.Height);
                    pictures = new EncoderPictures(width, height, EncoderPictures.Vp8Color, EgamiContext.Default);
                    analyzer = new FrameAnalyzer(width, height);
                }
                else if (frame.Width != width || frame.Height != height)
                {
                    throw new ArgumentException($"Frame {stats.Count} is {frame.Width}x{frame.Height}; the first was {width}x{height}.", nameof(frames));
                }

                YuvRaster<byte> picture = pictures.Import(frame, alpha: false);
                stats.Add(analyzer!.Analyze(picture));
                pictures.Return(picture);
                progress?.Invoke(frame);
            }
        }
        finally
        {
            pictures?.Dispose();
        }

        return stats.Count == 0 ? throw new ArgumentException("There are no frames.", nameof(frames)) : new FirstPassStats(width, height, [.. stats]);
    }

    /// <summary>Writes the statistics to a stream, 32 bytes a frame.</summary>
    /// <param name="stream">Where to write.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _frames.Length);
        stream.Write(header);
        Span<byte> record = stackalloc byte[32];
        foreach (FrameStats frame in _frames)
        {
            record.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(record, frame.Intra);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], frame.Inter);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], frame.Motion);
            BinaryPrimitives.WriteInt64LittleEndian(record[20..], frame.Detail);
            stream.Write(record);
        }
    }

    /// <summary>Writes the statistics to a file, replacing it.</summary>
    /// <param name="path">The file path.</param>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.Create(path);
        Save(stream);
    }

    /// <summary>Reads statistics that <see cref="Save(Stream)"/> wrote.</summary>
    /// <param name="stream">Where to read from.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="InvalidDataException">The data is not first-pass statistics, or is cut short.</exception>
    public static FirstPassStats Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version)
        {
            throw new InvalidDataException("This is not Egami.Video first-pass statistics.");
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (width < 1 || height < 1 || count < 1 || (stream.CanSeek && stream.Length - stream.Position < (long)count * 32))
        {
            throw new InvalidDataException("The first-pass statistics are damaged or cut short.");
        }

        FrameStats[] frames = new FrameStats[count];
        Span<byte> record = stackalloc byte[32];
        for (int i = 0; i < count; i++)
        {
            try
            {
                stream.ReadExactly(record);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("The first-pass statistics are cut short.", e);
            }

            frames[i] = new FrameStats(BinaryPrimitives.ReadInt64LittleEndian(record), BinaryPrimitives.ReadInt64LittleEndian(record[8..]), BinaryPrimitives.ReadInt32LittleEndian(record[16..]), BinaryPrimitives.ReadInt64LittleEndian(record[20..]));
        }

        return new FirstPassStats(width, height, frames);
    }

    /// <summary>Reads statistics that <see cref="Save(string)"/> wrote.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="InvalidDataException">The file is not first-pass statistics, or is cut short.</exception>
    public static FirstPassStats Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }
}
