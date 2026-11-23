using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;
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

    [Fact]
    public void Encode()
    {
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Encode), "No GPU encodes H.264.");
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region encode
        // H.264 on the GPU, in MP4: the same options as Egami's software encoder. Effort picks the driver's quality level.
        VideoWriterOptions options = new() { Codec = HardwareVideoCodecs.H264, FrameRate = new Rational(30, 1), Quality = 60 };
        using (VideoWriter writer = VideoWriter.Create("duck-gpu.mp4", options))
        {
            for (int i = 0; i < 30; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // A stream at 2 Mbit/s with a keyframe every second.
        VideoWriterOptions streaming = options with { RateControl = RateControlMode.ConstantBitrate, Bitrate = 2_000_000, KeyframeInterval = 30 };
        #endregion

        Assert.Equal(2_000_000, streaming.Bitrate);
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Decode), "No GPU decodes H.264.");
        int count = 0;

        #region decode
        // Decode H.264 on the GPU where there is one: the hardware codec takes the software codec's place in the context.
        MediaContext context = MediaContext.Default.WithH264();
        if (HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Decode))
        {
            context = context.WithCodec(HardwareVideoCodecs.H264);
        }

        using VideoReader reader = VideoReader.Open("duck-gpu.mp4", context: context);
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                count++; // frame.Planes holds the 8-bit 4:2:0 picture, the same samples Egami's software decoder gives
            }
        }
        #endregion

        Assert.Equal(30, count);
    }

    [Fact]
    public void Hevc()
    {
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Encode), "No GPU encodes HEVC.");
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region encodehevc
        // HEVC on the GPU, in MP4: the software HEVC encoder's options: Main, or Main 10 for 10-bit frames.
        VideoWriterOptions options = new() { Codec = HardwareVideoCodecs.Hevc, FrameRate = new Rational(30, 1), Quality = 60 };
        using (VideoWriter writer = VideoWriter.Create("duck-gpu-hevc.mp4", options))
        {
            for (int i = 0; i < 30; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }
        #endregion

        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Decode), "No GPU decodes HEVC.");
        int count = 0;

        #region decodehevc
        // Decode HEVC (Main or Main 10) on the GPU where there is one, else with Egami's software decoder.
        MediaContext context = MediaContext.Default.WithHevc();
        if (HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Decode))
        {
            context = context.WithCodec(HardwareVideoCodecs.Hevc);
        }

        using VideoReader reader = VideoReader.Open("duck-gpu-hevc.mp4", context: context);
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                count++; // 8-bit streams give YuvRaster<byte>, Main 10 streams YuvRaster<ushort>
            }
        }
        #endregion

        Assert.Equal(30, count);
    }
}
