using Egami.Video;
using Egami.Video.Hardware;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoHardware
{
    [Fact]
    public void Available()
    {
        #region available
        // Ask once whether the GPU encodes H.264 in hardware. The answer comes from the GPU driver and is kept for the life of
        // the process; on a machine without a GPU or driver it is false, never an exception.
        bool canEncode = HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Encode);
        bool canDecodeHevc = HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Decode);
        Console.WriteLine($"H.264 encode: {canEncode}, HEVC decode: {canDecodeHevc}");
        #endregion

        Assert.Equal(
            HardwareVideoCodecs.GetDevices().Any(d => d.Profiles.Any(p => p.CodecId == CodecIds.Avc && p.Direction == HardwareDirection.Encode)),
            canEncode);
    }

    [Fact]
    public void Devices()
    {
        #region devices
        // List the GPUs and what their video hardware does: codec, profile, bit depth, size limits and, for encoders, the rate
        // control modes and quality levels. UnavailableReason says why the list is empty.
        IReadOnlyList<HardwareVideoDevice> devices = HardwareVideoCodecs.GetDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine($"No hardware video: {HardwareVideoCodecs.UnavailableReason}");
        }

        foreach (HardwareVideoDevice device in devices)
        {
            Console.WriteLine(device);
            foreach (HardwareVideoProfile profile in device.Profiles)
            {
                Console.WriteLine($"  {profile}");
            }
        }
        #endregion

        Assert.Equal(devices.Count == 0, HardwareVideoCodecs.UnavailableReason is not null);
    }
}
