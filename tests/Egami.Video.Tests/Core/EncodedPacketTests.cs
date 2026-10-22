using System.Buffers;

namespace Egami.Video.Tests.Core;

public class EncodedPacketTests
{
    private sealed class TrackingOwner(int length) : IMemoryOwner<byte>
    {
        public int Disposals { get; private set; }

        public Memory<byte> Memory { get; } = new byte[length];

        public void Dispose() => Disposals++;
    }

    [Fact]
    public void DisposeReleasesTheBufferOnce()
    {
        TrackingOwner owner = new(16);
        EncodedPacket packet = new(1, owner, 10, MediaTime.Zero);
        Assert.True(packet.OwnsData);
        Assert.Equal(10, packet.Data.Length);
        packet.Dispose();
        packet.Dispose();
        Assert.Equal(1, owner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => packet.Data);
    }

    [Fact]
    public void WrappedMemoryIsNotReleasedOrWritable()
    {
        byte[] bytes = [1, 2, 3];
        using EncodedPacket packet = new(0, bytes, MediaTime.Zero) { IsKeyframe = true };
        Assert.False(packet.OwnsData);
        Assert.Throws<InvalidOperationException>(() => packet.GetWritableData());
        Assert.Equal(bytes, packet.Data.ToArray());
    }

    [Fact]
    public void RentAndCloneCopyTheBytesAndFields()
    {
        using EncodedPacket packet = EncodedPacket.Rent(2, 5, new MediaTime(7, new Rational(1, 25)));
        packet.GetWritableData().Span.Fill(9);
        packet.IsKeyframe = true;
        packet.Duration = new MediaTime(1, new Rational(1, 25));
        using EncodedPacket copy = packet.Clone();
        packet.GetWritableData().Span.Clear();
        Assert.Equal(new byte[] { 9, 9, 9, 9, 9 }, copy.Data.ToArray());
        Assert.Equal(packet.Timestamp, copy.Timestamp);
        Assert.Equal(packet.Duration, copy.Duration);
        Assert.True(copy.IsKeyframe);
        Assert.Equal(2, copy.TrackNumber);
    }
}
