using System.Buffers;

namespace Egami.Video;

/// <summary>
/// One compressed unit of a track, such as a Motion JPEG frame or a block of audio: what demuxers return, decoders take,
/// encoders return and muxers write. The bytes may sit in a pooled buffer, which <see cref="Dispose"/> returns.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var demuxer = MediaFile.OpenDemuxer("in.avi");
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///         Console.WriteLine($"track {packet.TrackNumber} at {packet.Timestamp}: {packet.Data.Length} bytes");
/// }
/// </code>
/// </example>
public sealed class EncodedPacket : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private ReadOnlyMemory<byte> _data;
    private bool _disposed;

    /// <summary>Creates a packet over bytes the caller owns. <see cref="Dispose"/> releases nothing.</summary>
    /// <param name="trackNumber">The track the packet belongs to.</param>
    /// <param name="data">The compressed bytes. They must stay unchanged while the packet is used.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    public EncodedPacket(int trackNumber, ReadOnlyMemory<byte> data, MediaTime timestamp)
    {
        TrackNumber = trackNumber;
        _data = data;
        Timestamp = timestamp;
    }

    /// <summary>Creates a packet over the first <paramref name="length"/> bytes of a buffer it takes ownership of.</summary>
    /// <param name="trackNumber">The track the packet belongs to.</param>
    /// <param name="owner">The buffer, disposed by <see cref="Dispose"/>.</param>
    /// <param name="length">The number of bytes used.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or larger than the buffer.</exception>
    public EncodedPacket(int trackNumber, IMemoryOwner<byte> owner, int length, MediaTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, owner.Memory.Length);
        TrackNumber = trackNumber;
        _owner = owner;
        _data = owner.Memory[..length];
        Timestamp = timestamp;
    }

    /// <summary>
    /// Rents a packet of <paramref name="length"/> bytes from the shared memory pool, for a demuxer or encoder to fill through
    /// <see cref="GetWritableData"/>.
    /// </summary>
    /// <param name="trackNumber">The track the packet belongs to.</param>
    /// <param name="length">The number of bytes.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    /// <returns>The packet. The caller must dispose it.</returns>
    public static EncodedPacket Rent(int trackNumber, int length, MediaTime timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new EncodedPacket(trackNumber, MemoryPool<byte>.Shared.Rent(Math.Max(length, 1)), length, timestamp);
    }

    /// <summary>Gets the track the packet belongs to, as numbered by the container (AVI streams count from 0, Matroska from 1).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Gets the compressed bytes.</summary>
    /// <exception cref="ObjectDisposedException">The packet has been disposed.</exception>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _data;
        }
    }

    /// <summary>Gets the presentation timestamp: when the decoded frame or audio starts.</summary>
    public MediaTime Timestamp { get; set; }

    /// <summary>
    /// Gets the decode timestamp, or <see langword="null"/> when it equals <see cref="Timestamp"/>, which holds for every
    /// codec without frame reordering (Motion JPEG, audio).
    /// </summary>
    public MediaTime? DecodeTimestamp { get; set; }

    /// <summary>Gets the duration, or <see langword="null"/> when the container does not say.</summary>
    public MediaTime? Duration { get; set; }

    /// <summary>Gets whether a decoder can start at this packet. Every Motion JPEG and audio packet is a keyframe.</summary>
    public bool IsKeyframe { get; set; }

    /// <summary>
    /// Gets the extra data stored with the packet (Matroska BlockAdditions), such as the alpha frame of a WebM VP8 or VP9 track
    /// with alpha (id 1). Empty for most packets; muxers that cannot store additions drop them.
    /// </summary>
    public IReadOnlyList<PacketAddition> Additions { get; set; } = [];

    /// <summary>Gets whether the packet's bytes are in a buffer it owns and returns on <see cref="Dispose"/>.</summary>
    public bool OwnsData => _owner is not null;

    /// <summary>Gets the bytes for writing, when the packet owns its buffer (it was rented or given an owner).</summary>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">The packet wraps memory it does not own.</exception>
    /// <exception cref="ObjectDisposedException">The packet has been disposed.</exception>
    public Memory<byte> GetWritableData()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _owner is null
            ? throw new InvalidOperationException("The packet wraps memory it does not own.")
            : _owner.Memory[.._data.Length];
    }

    /// <summary>Returns a packet with the same bytes and fields that owns its own copy of the bytes.</summary>
    /// <returns>The copy. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The packet has been disposed.</exception>
    public EncodedPacket Clone()
    {
        ReadOnlyMemory<byte> data = Data;
        EncodedPacket copy = Rent(TrackNumber, data.Length, Timestamp);
        data.CopyTo(copy.GetWritableData());
        copy.DecodeTimestamp = DecodeTimestamp;
        copy.Duration = Duration;
        copy.IsKeyframe = IsKeyframe;
        copy.Additions = [.. Additions.Select(a => a with { Data = a.Data.ToArray() })];
        return copy;
    }

    /// <summary>Returns the pooled buffer, if any. The bytes must not be used afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _data = default;
        Additions = [];
        IMemoryOwner<byte>? owner = _owner;
        _owner = null;
        owner?.Dispose();
    }
}

/// <summary>
/// Extra data stored with a packet (a Matroska BlockMore): an id saying what it is, and its bytes. Id 1 holds a WebM VP8 or VP9
/// track's alpha frame when the track has <see cref="VideoTrackInfo.HasAlpha"/>.
/// </summary>
/// <param name="Id">What the data is (Matroska BlockAddID); 1 unless the track maps other ids.</param>
/// <param name="Data">The bytes (Matroska BlockAdditional).</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-remux"/>
/// </example>
public sealed record PacketAddition(long Id, ReadOnlyMemory<byte> Data);
