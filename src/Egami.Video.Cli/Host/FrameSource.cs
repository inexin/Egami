using Egami.Pixels;
using Egami.Video.Filters;

namespace Egami.Video.Cli.Host;

/// <summary>
/// Decoded frames of one input's video track, for the commands that work on pictures (frames, run, thumbnail, scenes, sheet,
/// animate, hash, compare): the track <c>--track</c> picks (the first video track by default), read from a file or from standard
/// input, with the session's context, limits and diagnostics.
/// </summary>
internal sealed class FrameSource : IDisposable
{
    private readonly VideoReader _reader;

    private FrameSource(VideoReader reader)
    {
        _reader = reader;
    }

    /// <summary>Gets the decoder's reader.</summary>
    public VideoReader Reader => _reader;

    /// <summary>Gets the video track being decoded.</summary>
    public VideoTrackInfo Track => _reader.Track;

    /// <summary>Gets what the input holds.</summary>
    public MediaInfo Info => _reader.Info;

    /// <summary>
    /// Opens an input's video track; <paramref name="selection"/> picks the track. <paramref name="source"/> is the input's
    /// bytes when the caller has opened them already (standard input replayed after a first look), which the source then owns.
    /// </summary>
    public static FrameSource Open(VideoSession session, InputFile input, TrackSelection selection, bool autoOrient = false, Stream? source = null)
    {
        Stream stream = source ?? MediaIO.OpenStream(session, input);
        try
        {
            int? number = null;
            if (selection.IsExplicit)
            {
                if (!stream.CanSeek)
                {
                    throw new UsageException($"{input.Display}: --track needs a file; standard input is read once, so the first video track is used.");
                }

                using (IDemuxer probe = MediaFile.OpenDemuxer(stream, session.Context, leaveOpen: true))
                {
                    number = selection.SelectOne(probe.Tracks, TrackKind.Video, input.Display).Number;
                }

                stream.Position = 0;
            }

            VideoReaderOptions options = new() { TrackNumber = number, AutoOrient = autoOrient };
            return new FrameSource(VideoReader.Open(stream, options, session.Context, leaveOpen: false));
        }
        catch (UnsupportedFormatException error) when (error.Message.Contains("no video", StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            throw new UsageException($"{input.Display}: no video track to decode.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The frames in presentation order. The caller disposes each.</summary>
    public IEnumerable<YuvVideoFrame> Frames() => _reader.ReadFrames();

    /// <summary>The frames in presentation order, deinterlaced when <paramref name="deinterlace"/> is given. The caller disposes each.</summary>
    public IEnumerable<YuvVideoFrame> Frames(DeinterlacerOptions? deinterlace, MediaContext context) =>
        deinterlace is null ? _reader.ReadFrames() : Deinterlaced(deinterlace, context);

    /// <summary>
    /// The next frame, after a seek, deinterlaced when <paramref name="deinterlace"/> is given: the frame after it is read too,
    /// for the adaptive mode to look at. <see langword="null"/> at the end.
    /// </summary>
    public YuvVideoFrame? ReadFrame(DeinterlacerOptions? deinterlace, MediaContext context)
    {
        if (deinterlace is null)
        {
            return _reader.ReadFrame();
        }

        using Deinterlacer deinterlacer = new(Track, deinterlace with { Rate = DeinterlaceRate.Frame }, context);
        using (YuvVideoFrame? frame = _reader.ReadFrame())
        {
            if (frame is null)
            {
                return null;
            }

            deinterlacer.Send(frame);
        }

        using (YuvVideoFrame? next = _reader.ReadFrame())
        {
            if (next is not null)
            {
                deinterlacer.Send(next);
            }
        }

        deinterlacer.Flush();
        return deinterlacer.TryReceive(out YuvVideoFrame? output) ? output : null;
    }

    private IEnumerable<YuvVideoFrame> Deinterlaced(DeinterlacerOptions options, MediaContext context)
    {
        using Deinterlacer deinterlacer = new(Track, options, context);
        foreach (YuvVideoFrame frame in _reader.ReadFrames())
        {
            using (frame)
            {
                deinterlacer.Send(frame);
            }

            while (deinterlacer.TryReceive(out YuvVideoFrame? output))
            {
                yield return output;
            }
        }

        deinterlacer.Flush();
        while (deinterlacer.TryReceive(out YuvVideoFrame? output))
        {
            yield return output;
        }
    }

    /// <summary>A frame as RGBA pixels for Egami's operations, allocated from the session's Egami context.</summary>
    public static Raster<Rgba8> ToImage(VideoSession session, YuvVideoFrame frame) => frame.ToRaster<Rgba8>(session.Images.Context);

    public void Dispose() => _reader.Dispose();
}
