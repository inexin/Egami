using Egami.Video.Containers;

namespace Egami.Video.Analysis;

/// <summary>
/// How the analysis methods that take a <see cref="Stream"/> read it: a seekable stream through views that each keep their own
/// position, as two handles on a file would; an unseekable one once, its packets shared out between a video and an audio
/// reader. The caller owns the stream: nothing here disposes it.
/// </summary>
internal static class StreamSources
{
    /// <summary>A view of a seekable stream with its own position, so two demuxers can read one stream side by side.</summary>
    internal sealed class View(Stream inner, long start) : Stream
    {
        private long _position = start;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            inner.Position = _position;
            int read = inner.Read(buffer);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => inner.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that keeps a copy of the bytes read through it until told to stop, so an unseekable stream's header can be
    /// read again from a temporary copy.
    /// </summary>
    internal sealed class Recording(Stream inner) : Stream
    {
        private MemoryStream? _recorded = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Copies what was read and the rest of the stream into <paramref name="target"/>.</summary>
        public void CopyAllTo(Stream target, CancellationToken cancellationToken)
        {
            _recorded?.WriteTo(target);
            _recorded = null;
            byte[] buffer = new byte[1 << 16];
            int read;
            while ((read = inner.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            _recorded?.Write(buffer[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A temporary file that is deleted when it is closed.</summary>
    internal static FileStream TemporaryFile() =>
        new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);

    /// <summary>
    /// One demuxer read once, its packets shared between two readers, each of which sees its own track's packets in order.
    /// Packets of the other reader's track wait in its queue until it reads; packets of other tracks are dropped.
    /// </summary>
    internal sealed class Shared(IDemuxer inner)
    {
        private readonly IDemuxer _inner = inner;
        private readonly Part[] _parts = [null!, null!];
        private int _open;

        /// <summary>Gets a demuxer for one of the two readers; give each its track with <see cref="Part.Track"/> once it is known.</summary>
        public IDemuxer Open(int index)
        {
            _open++;
            return _parts[index] = new Part(this);
        }

        private EncodedPacket? Next(Part reader)
        {
            if (reader.Queue.TryDequeue(out EncodedPacket? waiting))
            {
                return waiting;
            }

            while (_inner.ReadPacket() is { } packet)
            {
                if (packet.TrackNumber == reader.Track)
                {
                    return packet;
                }

                Part? other = Array.Find(_parts, p => p is not null && p != reader && p.Track == packet.TrackNumber);
                if (other is null)
                {
                    packet.Dispose();
                    continue;
                }

                other.Queue.Enqueue(packet);
            }

            return null;
        }

        private void Close()
        {
            if (--_open == 0)
            {
                _inner.Dispose();
            }
        }

        /// <summary>One reader's view of the shared demuxer.</summary>
        internal sealed class Part(Shared owner) : IDemuxer
        {
            public Queue<EncodedPacket> Queue { get; } = new();

            /// <summary>Gets or sets the track this view hands out.</summary>
            public int? Track { get; set; }

            public MediaInfo Info => owner._inner.Info;

            public IReadOnlyList<TrackInfo> Tracks => owner._inner.Tracks;

            public bool CanSeek => false;

            public EncodedPacket? ReadPacket() => owner.Next(this);

            public void Seek(MediaTime time, int? trackNumber = null) => throw new NotSupportedException("The stream cannot seek.");

            public void Dispose()
            {
                while (Queue.TryDequeue(out EncodedPacket? packet))
                {
                    packet.Dispose();
                }

                owner.Close();
            }
        }
    }
}
