using System.Diagnostics.CodeAnalysis;

using Egami.Video.Codecs;

namespace Egami.Video.Hardware.Integration;

/// <summary>
/// Decodes on the GPU and, when the hardware decoder turns the stream down before its first frame (a profile, bit depth or
/// chroma format it does not decode, found at the first picture), carries on with the software decoder: the packets sent so
/// far are kept until then and sent again.
/// </summary>
internal sealed class FallbackVideoDecoder : IVideoDecoder
{
    /// <summary>The most packets kept for a switch: a stream the GPU has not turned down by then is decoded on the GPU.</summary>
    internal const int MaxKept = 256;

    /// <summary>The most bytes kept for a switch.</summary>
    internal const long MaxKeptBytes = 64 << 20;

    private readonly Func<IVideoDecoder> _software;
    private readonly Queue<YuvVideoFrame> _frames = new();
    private List<EncodedPacket>? _kept = [];
    private long _keptBytes;
    private IVideoDecoder _decoder;
    private bool _disposed;

    public FallbackVideoDecoder(IVideoDecoder hardware, Func<IVideoDecoder> software)
    {
        _decoder = hardware;
        _software = software;
    }

    /// <summary>Gets whether the decoder has moved to the software decoder, for tests.</summary>
    internal bool UsesSoftware { get; private set; }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_kept is not null)
        {
            if (_kept.Count >= MaxKept || _keptBytes + packet.Data.Length > MaxKeptBytes)
            {
                Commit();
            }
            else
            {
                _kept.Add(packet.Clone());
                _keptBytes += packet.Data.Length;
            }
        }

        try
        {
            _decoder.Send(packet);
        }
        catch (UnsupportedFormatException) when (_kept is not null)
        {
            Switch();
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _decoder.Flush();
        }
        catch (UnsupportedFormatException) when (_kept is not null)
        {
            Switch();
            _decoder.Flush();
        }
    }

    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frames.TryDequeue(out frame))
        {
            return true;
        }

        if (!_decoder.TryReceive(out frame))
        {
            return false;
        }

        Commit();
        return true;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.Reset();
        while (_frames.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        if (_kept is not null)
        {
            _kept.ForEach(p => p.Dispose());
            _kept.Clear();
            _keptBytes = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decoder.Dispose();
        while (_frames.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        _kept?.ForEach(p => p.Dispose());
        _kept = null;
    }

    /// <summary>Stops keeping packets: the stream stays on the decoder it is on.</summary>
    private void Commit()
    {
        _kept?.ForEach(p => p.Dispose());
        _kept = null;
    }

    /// <summary>Moves to the software decoder and sends it the packets kept so far.</summary>
    private void Switch()
    {
        List<EncodedPacket> kept = _kept!;
        _kept = null;
        _decoder.Dispose();
        UsesSoftware = true;
        try
        {
            _decoder = _software();
            foreach (EncodedPacket packet in kept)
            {
                _decoder.Send(packet);
                while (_decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    _frames.Enqueue(frame);
                }
            }
        }
        finally
        {
            kept.ForEach(p => p.Dispose());
        }
    }
}
