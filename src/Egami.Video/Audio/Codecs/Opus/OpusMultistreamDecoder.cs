// Translated from libopus 1.5.2 src/opus_multistream_decoder.c (opus_multistream_decode_native) and src/opus_multistream.c
// (get_left_channel, get_right_channel, get_mono_channel).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// Several Opus streams in one packet (RFC 7845 section 5.1.1): coupled stereo streams first, then mono ones, every stream
/// but the last self-delimited, their channels placed by the mapping table (255 is a silent channel). Output is planar.
/// </summary>
internal sealed class OpusMultistreamDecoder
{
    private readonly OpusStreamDecoder[] _streams;
    private readonly byte[] _mapping;
    private readonly int _coupled;
    private float[] _buffer = [];

    public OpusMultistreamDecoder(OpusHead head)
    {
        _mapping = head.Mapping;
        _coupled = head.CoupledCount;
        Channels = head.Channels;
        ChannelOrder = [.. Enumerable.Range(0, head.Channels)];
        _streams = new OpusStreamDecoder[head.StreamCount];
        for (int s = 0; s < _streams.Length; s++)
        {
            _streams[s] = new OpusStreamDecoder(s < _coupled ? 2 : 1) { Gain = head.OutputGain };
        }
    }

    public int Channels { get; }

    /// <summary>Gets or sets the output channel each coded channel goes to; by default the coded order.</summary>
    public int[] ChannelOrder { get; set; }

    /// <summary>Gets the final ranges of the streams XORed, as libopus's multistream decoder reports them.</summary>
    public uint FinalRange
    {
        get
        {
            uint range = 0;
            foreach (OpusStreamDecoder s in _streams)
            {
                range ^= s.FinalRange;
            }

            return range;
        }
    }

    public void Reset()
    {
        foreach (OpusStreamDecoder s in _streams)
        {
            s.Reset();
        }
    }

    /// <summary>
    /// Decodes a packet, or conceals a lost one when <paramref name="data"/> is empty, into the planar float channels of
    /// <paramref name="output"/> (at least <paramref name="frameSize"/> samples, channels in the stream's own order). Returns
    /// samples per channel or a negative error.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> data, AudioFrame output, int frameSize, bool decodeFec)
    {
        frameSize = Math.Min(frameSize, OpusStreamDecoder.SampleRate / 25 * 3);
        bool plc = data.IsEmpty;
        if (!plc && data.Length < (2 * _streams.Length) - 1)
        {
            return OpusPacket.InvalidPacket;
        }

        if (_buffer.Length < 2 * frameSize)
        {
            _buffer = new float[2 * frameSize];
        }

        for (int s = 0; s < _streams.Length; s++)
        {
            if (!plc && data.IsEmpty)
            {
                return OpusPacket.InternalError;
            }

            int ret = _streams[s].Decode(plc ? [] : data, _buffer, frameSize, decodeFec, s != _streams.Length - 1, out int packetLength);
            if (!plc)
            {
                data = data[Math.Min(packetLength, data.Length)..];
            }

            if (ret <= 0)
            {
                return ret == 0 ? OpusPacket.InvalidPacket : ret;
            }

            frameSize = ret;
            if (s < _coupled)
            {
                for (int c = 0; c < Channels; c++)
                {
                    if (_mapping[c] == s * 2)
                    {
                        Copy(_buffer, 0, 2, output.GetSpan<float>(ChannelOrder[c]), frameSize);
                    }
                    else if (_mapping[c] == (s * 2) + 1)
                    {
                        Copy(_buffer, 1, 2, output.GetSpan<float>(ChannelOrder[c]), frameSize);
                    }
                }
            }
            else
            {
                for (int c = 0; c < Channels; c++)
                {
                    if (_mapping[c] == s + _coupled)
                    {
                        Copy(_buffer, 0, 1, output.GetSpan<float>(ChannelOrder[c]), frameSize);
                    }
                }
            }
        }

        for (int c = 0; c < Channels; c++)
        {
            if (_mapping[c] == 255)
            {
                output.GetSpan<float>(ChannelOrder[c])[..frameSize].Clear();
            }
        }

        return frameSize;
    }

    private static void Copy(ReadOnlySpan<float> source, int start, int stride, Span<float> destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            destination[i] = source[start + (i * stride)];
        }
    }
}
