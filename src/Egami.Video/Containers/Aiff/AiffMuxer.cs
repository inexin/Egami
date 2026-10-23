using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using Egami.Video.Containers.Mp3;

namespace Egami.Video.Containers.Aiff;

/// <summary>
/// Writes AIFF (integer PCM) or AIFF-C (float) from one PCM track: FORM, FVER for AIFF-C, COMM, CHAN for more than two channels, the text chunks, an ID3 chunk,
/// MARK, then SSND. Samples are turned big-endian on the way in. The sizes and frame count are filled in at the end, so a
/// non-seekable output is written to a temporary file first and copied out when the muxer finishes.
/// </summary>
internal sealed class AiffMuxer : IMuxer
{
    /// <summary>The bytes before the samples in an SSND chunk: its offset and block size fields.</summary>
    private const int SsndFields = 8;

    private readonly Stream _stream;
    private readonly Stream? _temp;
    private readonly OutputStream _out;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly int _bytesPerSample;
    private readonly int _blockAlign;
    private readonly bool _swap;
    private readonly bool _flipSign;
    private readonly long _start;
    private readonly long _framesAt;
    private readonly long _ssndAt;
    private long _dataBytes;
    private bool _finished;

    public AiffMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track] || track.CodecId is not (CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat))
        {
            throw new NotSupportedException("AIFF holds one PCM audio track.");
        }

        bool isFloat = track.CodecId == CodecIds.PcmFloat;
        int bits = track.BitsPerSample > 0 ? track.BitsPerSample : isFloat ? 32 : 16;
        if (isFloat ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"AIFF cannot hold {bits}-bit {(isFloat ? "float" : "integer")} PCM.");
        }

        if (track.Channels is < 1 or > short.MaxValue || track.SampleRate < 1)
        {
            throw new NotSupportedException($"AIFF cannot hold {track.Channels} channels at {track.SampleRate} Hz.");
        }

        if (!stream.CanWrite)
        {
            throw new NotSupportedException("The output is not writable.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        if (!stream.CanSeek)
        {
            _temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        }

        _out = new OutputStream(_temp ?? stream);
        _inputNumber = track.Number;
        _bytesPerSample = bits / 8;
        _blockAlign = _bytesPerSample * track.Channels;
        _swap = track.CodecId != CodecIds.PcmBigEndian && bits > 8;
        _flipSign = track.CodecId == CodecIds.PcmLittleEndian && bits == 8;
        _start = _out.Position;
        Tracks = [track with { Number = 1, CodecId = isFloat ? CodecIds.PcmFloat : CodecIds.PcmBigEndian, BitsPerSample = bits }];

        Span<byte> form = stackalloc byte[12];
        "FORM"u8.CopyTo(form);
        (isFloat ? "AIFC"u8 : "AIFF"u8).CopyTo(form[8..]);
        _out.Write(form);
        if (isFloat)
        {
            byte[] version = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(version, AiffChunks.AifcVersion1);
            WriteChunk("FVER", version);
        }

        using MemoryStream common = new();
        Span<byte> comm = stackalloc byte[18];
        BinaryPrimitives.WriteInt16BigEndian(comm, (short)track.Channels);
        BinaryPrimitives.WriteInt16BigEndian(comm[6..], (short)bits);
        AiffChunks.WriteExtended(track.SampleRate, comm[8..]);
        common.Write(comm);
        if (isFloat)
        {
            common.Write(bits == 64 ? "fl64"u8 : "fl32"u8);
            AiffChunks.WritePascal(common, bits == 64 ? "64-bit floating point" : "32-bit floating point");
        }

        _framesAt = _out.Position + 8 + 2;
        WriteChunk("COMM", common.ToArray());
        if (track.Channels > 2 && CoreAudioLayout.Write(track.EffectiveLayout) is { } layout)
        {
            WriteChunk("CHAN", layout);
        }

        // ASCII text that fits a text chunk goes there; every other tag, and cover art, goes into an ID3 chunk.
        Dictionary<string, string> extra = [];
        foreach ((string name, string value) in metadata.Tags)
        {
            string? chunk = Array.Find(AiffChunks.TextChunks, t => t.Tag == name).Chunk;
            if (value.Length == 0)
            {
                continue;
            }

            if (chunk is not null && Ascii.IsValid(value))
            {
                WriteChunk(chunk, Encoding.ASCII.GetBytes(value));
            }
            else
            {
                extra[name] = value;
            }
        }

        List<AttachmentInfo> images = [.. metadata.Attachments.Where(a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))];
        if (images.Count < metadata.Attachments.Count)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "AIFF holds cover art but no other attachments; they are left out."));
        }

        if (Id3Tag.Write(new MediaMetadata { Tags = extra, Attachments = images }, null) is { } id3)
        {
            WriteChunk("ID3 ", id3);
        }

        if (metadata.Chapters.Count > 0)
        {
            WriteChunk("MARK", AiffChunks.WriteMarkers(metadata.Chapters, track.SampleRate));
        }

        _ssndAt = _out.Position;
        Span<byte> ssnd = stackalloc byte[8 + SsndFields];
        "SSND"u8.CopyTo(ssnd);
        _out.Write(ssnd);
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The muxer was finished.");
        }

        if (packet.TrackNumber != _inputNumber)
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        ReadOnlySpan<byte> data = packet.Data.Span;
        if (data.Length % _blockAlign != 0)
        {
            throw new ArgumentException($"The packet holds {data.Length} bytes, not whole {_blockAlign}-byte samples.", nameof(packet));
        }

        if (_out.Position - _start + data.Length + 1 - 8 > uint.MaxValue)
        {
            throw new NotSupportedException("AIFF cannot hold more than 4 GB of samples; write CAF or Wave64 instead.");
        }

        if (_swap || _flipSign)
        {
            byte[] converted = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                Span<byte> c = converted.AsSpan(0, data.Length);
                data.CopyTo(c);
                if (_flipSign)
                {
                    // WAV's 8-bit samples are unsigned; AIFF's are signed.
                    for (int i = 0; i < c.Length; i++)
                    {
                        c[i] ^= 0x80;
                    }
                }
                else
                {
                    for (int i = 0; i < c.Length; i += _bytesPerSample)
                    {
                        c.Slice(i, _bytesPerSample).Reverse();
                    }
                }

                _out.Write(c);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(converted);
            }
        }
        else
        {
            _out.Write(data);
        }

        _dataBytes += data.Length;
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if ((_dataBytes & 1) != 0)
        {
            _out.WriteByte(0);
        }

        long end = _out.Position;
        Span<byte> value = stackalloc byte[4];
        Patch(_start + 4, (uint)(end - _start - 8), value);
        Patch(_framesAt, (uint)(_dataBytes / _blockAlign), value);
        Patch(_ssndAt + 4, (uint)(_dataBytes + SsndFields), value);
        _out.Position = end;
        _out.Flush();
        if (_temp is not null)
        {
            _temp.Position = 0;
            _temp.CopyTo(_stream);
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _out.Dispose();
            _temp?.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void Patch(long position, uint value, Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _out.Position = position;
        _out.Write(buffer);
    }

    private void WriteChunk(string id, ReadOnlySpan<byte> body)
    {
        Span<byte> h = stackalloc byte[8];
        Encoding.ASCII.GetBytes(id, h);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], (uint)body.Length);
        _out.Write(h);
        _out.Write(body);
        if ((body.Length & 1) != 0)
        {
            _out.WriteByte(0);
        }
    }
}
