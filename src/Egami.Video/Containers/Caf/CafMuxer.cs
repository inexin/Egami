using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Containers.Caf;

/// <summary>
/// Writes CAF from one audio track: the caff header, desc, kuki, chan for more than two channels, info, strg and mark, then
/// data. Constant-size samples (PCM, G.711) are streamed: on a non-seekable output the data chunk's size is -1 ("to the end of
/// the file"), on a seekable one it is filled in at the end. Variable-size packets need a packet table (pakt): on a seekable
/// output it follows the data chunk; on a non-seekable one the packets go to a temporary file and the table is written before
/// them when the muxer finishes, so the file streams.
/// </summary>
internal sealed class CafMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly OutputStream _out;
    private readonly Stream? _temp;
    private readonly MemoryStream? _header;
    private readonly int _inputNumber;
    private readonly CafDescription _desc;
    private readonly bool _variable;
    private readonly bool _opus;
    private readonly bool _flipSign;
    private readonly int _blockAlign;
    private readonly Rational _rate;
    private readonly long _descAt;
    private readonly long _dataAt;
    private readonly List<int> _sizes = [];
    private readonly List<long> _starts = [];
    private readonly List<long> _ends = [];
    private readonly List<int> _coded = [];
    private long _dataBytes;
    private bool _finished;

    public CafMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track])
        {
            throw new NotSupportedException("CAF holds one audio track.");
        }

        if (track.Channels is < 1 or > 255 || track.SampleRate < 1)
        {
            throw new NotSupportedException($"CAF cannot hold {track.Channels} channels at {track.SampleRate} Hz.");
        }

        if (CafCodecs.Write(track) is not { } written)
        {
            throw new NotSupportedException($"CAF cannot hold '{track.CodecId}'.");
        }

        if (!stream.CanWrite)
        {
            throw new NotSupportedException("The output is not writable.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _inputNumber = track.Number;
        _desc = written.Desc;
        _variable = _desc.BytesPerPacket == 0;
        _opus = track.CodecId == CodecIds.Opus;
        _flipSign = track.CodecId == CodecIds.PcmLittleEndian && _desc.Bits == 8;
        _blockAlign = (int)_desc.BytesPerPacket;
        _rate = new Rational(1, (int)_desc.SampleRate);
        if (_variable && !stream.CanSeek)
        {
            _temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            _header = new MemoryStream();
        }

        _out = new OutputStream(_temp ?? stream);
        Stream head = _header ?? (Stream)_out;
        int bits = _variable ? track.BitsPerSample : (int)_desc.Bits;
        Tracks = [track with { Number = 1, BitsPerSample = bits, CodecId = Normalized(track.CodecId, bits) }];

        head.Write("caff"u8);
        head.Write((ReadOnlySpan<byte>)[0, 1, 0, 0]);
        _descAt = head.Position + 12;
        byte[] desc = new byte[CafDescription.Size];
        _desc.Write(desc);
        WriteChunk(head, "desc", desc);
        if (written.Cookie is { } cookie)
        {
            WriteChunk(head, "kuki", cookie);
        }

        if (track.Channels > 2 && CoreAudioLayout.Write(track.EffectiveLayout) is { } layout)
        {
            WriteChunk(head, "chan", layout);
        }

        if (CafChunks.WriteInfo(metadata.Tags) is { } info)
        {
            WriteChunk(head, "info", info);
        }

        if (metadata.Chapters.Count > 0)
        {
            (byte[] strings, byte[] markers) = CafChunks.WriteMarkers(metadata.Chapters, (int)_desc.SampleRate);
            WriteChunk(head, "strg", strings);
            WriteChunk(head, "mark", markers);
        }

        if (metadata.Attachments.Count > 0)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "CAF holds no attachments or cover art; they are left out."));
        }

        if (_header is null)
        {
            // The data chunk: -1 ("to the end of the file") until Finish fills in the size, which it cannot on a non-seekable output.
            _dataAt = _out.Position;
            Span<byte> data = stackalloc byte[16];
            "data"u8.CopyTo(data);
            BinaryPrimitives.WriteInt64BigEndian(data[4..], -1);
            _out.Write(data);
        }
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
        if (_variable)
        {
            long start = packet.Timestamp.Rescale(_rate).Ticks;
            _sizes.Add(data.Length);
            _starts.Add(start);
            _ends.Add(packet.Duration is { } duration ? start + duration.Rescale(_rate).Ticks : -1);
            _coded.Add(_opus && !data.IsEmpty ? Math.Max(0, OpusPacket.SampleCount(data, 48000)) : -1);
            _out.Write(data);
            _dataBytes += data.Length;
            return;
        }

        if (data.Length % _blockAlign != 0)
        {
            throw new ArgumentException($"The packet holds {data.Length} bytes, not whole {_blockAlign}-byte samples.", nameof(packet));
        }

        if (_flipSign)
        {
            // WAV's 8-bit samples are unsigned; CAF's are signed.
            byte[] converted = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                Span<byte> c = converted.AsSpan(0, data.Length);
                for (int i = 0; i < c.Length; i++)
                {
                    c[i] = (byte)(data[i] ^ 0x80);
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
        Span<byte> value = stackalloc byte[8];
        if (!_variable)
        {
            if (_out.CanSeek)
            {
                long end = _out.Position;
                BinaryPrimitives.WriteInt64BigEndian(value, _dataBytes + 4);
                _out.Position = _dataAt + 4;
                _out.Write(value);
                _out.Position = end;
            }

            _out.Flush();
            return;
        }

        (uint framesPerPacket, byte[] table) = PacketTable();
        if (_header is null)
        {
            // Seekable: the data chunk's size, the frames per packet in desc, then the packet table after the data.
            long end = _out.Position;
            BinaryPrimitives.WriteInt64BigEndian(value, _dataBytes + 4);
            _out.Position = _dataAt + 4;
            _out.Write(value);
            BinaryPrimitives.WriteUInt32BigEndian(value, framesPerPacket);
            _out.Position = _descAt + 20;
            _out.Write(value[..4]);
            _out.Position = end;
            WriteChunk(_out, "pakt", table);
            _out.Flush();
            return;
        }

        // Non-seekable: the header, the packet table, then the data copied from the temporary file.
        _out.Flush();
        byte[] header = _header.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan((int)_descAt + 20), framesPerPacket);
        _stream.Write(header);
        WriteChunk(_stream, "pakt", table);
        Span<byte> data = stackalloc byte[16];
        "data"u8.CopyTo(data);
        BinaryPrimitives.WriteInt64BigEndian(data[4..], _dataBytes + 4);
        _stream.Write(data);
        _temp!.Position = 0;
        _temp.CopyTo(_stream);
        _stream.Flush();
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

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> h = stackalloc byte[12];
        Encoding.ASCII.GetBytes(type, h);
        BinaryPrimitives.WriteInt64BigEndian(h[4..], body.Length);
        stream.Write(h);
        stream.Write(body);
    }

    /// <summary>The codec id the file holds: PCM as CAF stores it.</summary>
    private static string Normalized(string codecId, int bits) => codecId switch
    {
        CodecIds.PcmLittleEndian when bits == 8 => CodecIds.PcmBigEndian,
        _ => codecId,
    };

    /// <summary>
    /// Builds the packet table: the priming frames before the first timestamp, the frames each packet codes (fixed for the codec,
    /// Opus's from each packet, else from the timestamps), and the remainder past the last packet's end.
    /// </summary>
    private (uint FramesPerPacket, byte[] Table) PacketTable()
    {
        int count = _sizes.Count;
        long[] coded = new long[count];
        long fixedFrames = _desc.FramesPerPacket;
        for (int i = 0; i < count; i++)
        {
            coded[i] = fixedFrames > 0 ? fixedFrames
                : _coded[i] >= 0 ? _coded[i]
                : i + 1 < count ? Math.Max(0, _starts[i + 1] - _starts[i])
                : _ends[i] >= 0 ? Math.Max(0, _ends[i] - _starts[i])
                : i > 0 ? coded[i - 1] : 0;
        }

        bool same = count > 0 && coded.All(c => c == coded[0]) && coded[0] > 0;
        uint framesPerPacket = same ? (uint)coded[0] : 0;
        long priming = count > 0 ? Math.Max(0, -_starts[0]) : 0;
        long total = coded.Sum();
        long validEnd = 0;
        for (int i = 0; i < count; i++)
        {
            validEnd = Math.Max(validEnd, (_ends[i] >= 0 ? _ends[i] : _starts[i] + coded[i]) + priming);
        }

        validEnd = Math.Min(validEnd, total);
        using MemoryStream table = new();
        Span<byte> h = stackalloc byte[CafChunks.PacketTableHeader];
        BinaryPrimitives.WriteInt64BigEndian(h, count);
        BinaryPrimitives.WriteInt64BigEndian(h[8..], Math.Max(0, validEnd - priming));
        BinaryPrimitives.WriteInt32BigEndian(h[16..], (int)Math.Min(priming, int.MaxValue));
        BinaryPrimitives.WriteInt32BigEndian(h[20..], (int)Math.Clamp(total - validEnd, 0, int.MaxValue));
        table.Write(h);
        for (int i = 0; i < count; i++)
        {
            CafChunks.WriteVarint(table, _sizes[i]);
            if (!same)
            {
                CafChunks.WriteVarint(table, coded[i]);
            }
        }

        return (framesPerPacket, table.ToArray());
    }
}
