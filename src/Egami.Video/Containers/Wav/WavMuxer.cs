using System.Buffers;
using System.Buffers.Binary;

namespace Egami.Video.Containers.Wav;

/// <summary>
/// Writes WAV (switching to RF64 past 4 GB) or Wave64 from one PCM track: header, a JUNK chunk reserved for ds64, fmt, fact for
/// float, LIST/INFO, data, then cue points and their labels. Big-endian PCM is turned around on the way in.
/// </summary>
internal sealed class WavMuxer : IMuxer
{
    /// <summary>The ds64 chunk body: RIFF size, data size and sample count (8 bytes each) and an empty table (4 bytes).</summary>
    private const int Ds64Size = 28;

    private readonly OutputStream _out;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _wave64;
    private readonly int _inputNumber;
    private readonly int _bytesPerSample;
    private readonly int _blockAlign;
    private readonly bool _swap;
    private readonly IReadOnlyList<ChapterInfo> _chapters;
    private readonly int _sampleRate;
    private readonly long _start;
    private readonly long _junkAt;
    private readonly long _factAt;
    private readonly long _dataHeaderAt;
    private readonly long _dataStart;
    private long _dataBytes;
    private bool _finished;

    public WavMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen, bool wave64)
    {
        string name = wave64 ? "Wave64" : "WAV";
        if (tracks is not [AudioTrackInfo track] || track.CodecId is not (CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat))
        {
            throw new NotSupportedException($"{name} holds one PCM audio track.");
        }

        bool isFloat = track.CodecId == CodecIds.PcmFloat;
        int bits = track.BitsPerSample > 0 ? track.BitsPerSample : isFloat ? 32 : 16;
        if (isFloat ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"{name} cannot hold {bits}-bit {(isFloat ? "float" : "integer")} PCM.");
        }

        if (track.Channels is < 1 or > ushort.MaxValue || track.SampleRate < 1)
        {
            throw new NotSupportedException($"{name} cannot hold {track.Channels} channels at {track.SampleRate} Hz.");
        }

        _stream = stream;
        _out = new OutputStream(stream);
        _leaveOpen = leaveOpen;
        _wave64 = wave64;
        _inputNumber = track.Number;
        _bytesPerSample = bits / 8;
        _blockAlign = _bytesPerSample * track.Channels;
        _swap = track.CodecId == CodecIds.PcmBigEndian && bits > 8;
        _sampleRate = track.SampleRate;
        _start = _out.Position;
        Tracks = [track with { Number = 1, CodecId = isFloat ? CodecIds.PcmFloat : CodecIds.PcmLittleEndian, BitsPerSample = bits }];

        if (metadata.Attachments.Count > 0 || (metadata.Chapters.Count > 0 && !stream.CanSeek))
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", $"{name} holds no attachments{(stream.CanSeek ? "" : ", and chapters need a seekable stream")}; they are left out."));
        }

        _chapters = stream.CanSeek ? metadata.Chapters : [];
        if (wave64)
        {
            Span<byte> h = stackalloc byte[40];
            WavChunks.RiffGuid.CopyTo(h);
            WavChunks.WaveGuid.CopyTo(h[24..]);
            _out.Write(h);
        }
        else
        {
            Span<byte> h = stackalloc byte[12];
            "RIFF"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], uint.MaxValue);
            "WAVE"u8.CopyTo(h[8..]);
            _out.Write(h);
            if (stream.CanSeek)
            {
                _junkAt = _out.Position;
                WriteChunk("JUNK", new byte[Ds64Size]);
            }
        }

        WriteChunk("fmt ", WavChunks.WriteFormat((AudioTrackInfo)Tracks[0], bits, isFloat));
        if (isFloat)
        {
            _factAt = _out.Position;
            WriteChunk("fact", new byte[4]);
        }

        if (WavChunks.WriteInfo(metadata.Tags) is { } info)
        {
            WriteChunk("LIST", info);
        }

        _dataHeaderAt = _out.Position;
        WriteChunkHeader("data", -1);
        _dataStart = _out.Position;
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    /// <summary>Gets or sets the size past which a WAV file becomes RF64; lowered only by tests.</summary>
    internal static long Rf64Threshold { get; set; } = uint.MaxValue;

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

        if (_swap)
        {
            byte[] swapped = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                for (int i = 0; i < data.Length; i += _bytesPerSample)
                {
                    for (int k = 0; k < _bytesPerSample; k++)
                    {
                        swapped[i + k] = data[i + _bytesPerSample - 1 - k];
                    }
                }

                _out.Write(swapped.AsSpan(0, data.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(swapped);
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
        long samples = _dataBytes / _blockAlign;
        if (_wave64)
        {
            _out.Write(new byte[Pad8(_dataBytes)]);
        }
        else if ((_dataBytes & 1) != 0)
        {
            _out.WriteByte(0);
        }

        if (_chapters.Count > 0)
        {
            (byte[] cue, byte[]? labels) = WavChunks.WriteCue(_chapters, _sampleRate);
            WriteChunk("cue ", cue);
            if (labels is not null)
            {
                WriteChunk("LIST", labels);
            }
        }

        if (!_out.CanSeek)
        {
            _out.Flush();
            return;
        }

        long end = _out.Position;
        if (_wave64)
        {
            Patch(_start + 16, BitConverter.GetBytes((ulong)(end - _start)));
            Patch(_dataHeaderAt + 16, BitConverter.GetBytes((ulong)(_dataBytes + 24)));
        }
        else
        {
            long riffSize = end - _start - 8;
            bool rf64 = riffSize > Rf64Threshold || _dataBytes > Rf64Threshold;
            if (rf64)
            {
                // EBU Tech 3306: the reserved JUNK chunk becomes ds64, and the 32-bit sizes say "look in ds64".
                Patch(_start, "RF64"u8);
                Patch(_start + 4, BitConverter.GetBytes(uint.MaxValue));
                byte[] ds64 = new byte[8 + Ds64Size];
                "ds64"u8.CopyTo(ds64);
                BinaryPrimitives.WriteUInt32LittleEndian(ds64.AsSpan(4), Ds64Size);
                BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(8), (ulong)riffSize);
                BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(16), (ulong)_dataBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(24), (ulong)samples);
                Patch(_junkAt, ds64);
                Patch(_dataHeaderAt + 4, BitConverter.GetBytes(uint.MaxValue));
            }
            else
            {
                Patch(_start + 4, BitConverter.GetBytes((uint)riffSize));
                Patch(_dataHeaderAt + 4, BitConverter.GetBytes((uint)_dataBytes));
            }

            if (_factAt > 0)
            {
                Patch(_factAt + 8, BitConverter.GetBytes((uint)Math.Min(samples, uint.MaxValue)));
            }
        }

        _out.Position = end;
        _out.Flush();
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
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private static long Pad8(long length) => (8 - (length & 7)) & 7;

    private void Patch(long position, ReadOnlySpan<byte> bytes)
    {
        _out.Position = position;
        _out.Write(bytes);
    }

    private void WriteChunkHeader(string id, long size)
    {
        if (_wave64)
        {
            Span<byte> h = stackalloc byte[24];
            WavChunks.Wave64Guid(id, h);
            BinaryPrimitives.WriteUInt64LittleEndian(h[16..], size < 0 ? 0 : (ulong)(size + 24));
            _out.Write(h);
        }
        else
        {
            Span<byte> h = stackalloc byte[8];
            System.Text.Encoding.ASCII.GetBytes(id, h);
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], size < 0 ? uint.MaxValue : (uint)size);
            _out.Write(h);
        }
    }

    private void WriteChunk(string id, ReadOnlySpan<byte> body)
    {
        WriteChunkHeader(id, body.Length);
        _out.Write(body);
        long pad = _wave64 ? Pad8(body.Length) : body.Length & 1;
        if (pad > 0)
        {
            _out.Write(new byte[pad]);
        }
    }
}
