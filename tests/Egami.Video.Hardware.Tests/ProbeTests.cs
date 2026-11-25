using Egami.Video.Hardware.Vulkan;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Tests;

/// <summary>The probe's contract, on any machine: with or without a GPU it answers and never throws.</summary>
public sealed unsafe class ProbeTests
{
    [Fact]
    public void AnswersWithoutThrowing()
    {
        IReadOnlyList<HardwareVideoDevice> devices = HardwareVideoCodecs.GetDevices();
        Assert.Same(devices, HardwareVideoCodecs.GetDevices());
        Assert.Equal(devices.Count == 0, HardwareVideoCodecs.UnavailableReason is not null);
        foreach (string codec in new[] { CodecIds.Avc, CodecIds.Hevc })
        {
            foreach (HardwareDirection direction in Enum.GetValues<HardwareDirection>())
            {
                bool expected = devices.Any(d => d.Profiles.Any(p => p.CodecId == codec && p.Direction == direction));
                Assert.Equal(expected, HardwareVideoCodecs.IsAvailable(codec, direction));
            }
        }
    }

    [Fact]
    public void UnknownCodecIsNotAvailable()
    {
        Assert.False(HardwareVideoCodecs.IsAvailable(CodecIds.Vp8, HardwareDirection.Decode));
        Assert.False(HardwareVideoCodecs.IsAvailable("", HardwareDirection.Encode));
        Assert.Throws<ArgumentNullException>(() => HardwareVideoCodecs.IsAvailable(null!, HardwareDirection.Decode));
    }

    [Fact]
    public void ProfilesHaveSaneLimits()
    {
        foreach (HardwareVideoDevice device in HardwareVideoCodecs.GetDevices())
        {
            Assert.False(string.IsNullOrEmpty(device.Name));
            Assert.Equal("Vulkan", device.Api);
            Assert.True(device.ApiVersion >= new Version(1, 3));
            Assert.NotEmpty(device.Profiles);
            foreach (HardwareVideoProfile profile in device.Profiles)
            {
                Assert.InRange(profile.MinWidth, 1, profile.MaxWidth);
                Assert.InRange(profile.MinHeight, 1, profile.MaxHeight);
                Assert.Matches(@"^\d\.\d$", profile.MaxLevel);
                Assert.True(profile.BitDepth is 8 or 10);
                if (profile.Direction == HardwareDirection.Encode)
                {
                    Assert.NotEqual(HardwareRateControl.None, profile.RateControl);
                    Assert.True(profile.QualityLevels >= 1);
                    Assert.True(profile.MaxForwardReferences >= 1);
                }
                else
                {
                    Assert.Equal(HardwareRateControl.None, profile.RateControl);
                    Assert.Equal(0, profile.QualityLevels);
                }

                Assert.Contains(profile.Profile, profile.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void LevelNamesFollowTheStdEnums()
    {
        // The probe turns the drivers' maximum level indices into level strings through tables in the Std enums' order.
        Assert.Equal("5.2", VulkanProbe.H264Levels[(int)StdVideoH264LevelIdc.Idc52]);
        Assert.Equal("6.2", VulkanProbe.H264Levels[(int)StdVideoH264LevelIdc.Idc62]);
        Assert.Equal((int)StdVideoH264LevelIdc.Idc62 + 1, VulkanProbe.H264Levels.Length);
        Assert.Equal("2.0", VulkanProbe.H265Levels[(int)StdVideoH265LevelIdc.Idc20]);
        Assert.Equal("6.2", VulkanProbe.H265Levels[(int)StdVideoH265LevelIdc.Idc62]);
        Assert.Equal((int)StdVideoH265LevelIdc.Idc62 + 1, VulkanProbe.H265Levels.Length);
    }

    [Fact]
    public void VersionsUnpack()
    {
        Assert.Equal(new Version(1, 4, 312), VulkanInstance.ToVersion((1u << 22) | (4u << 12) | 312));
        Assert.Equal(new Version(1, 3, 0), VulkanInstance.ToVersion(VulkanInstance.RequiredApiVersion));
    }

    [Fact]
    public void FailedResultsThrowWithTheCode()
    {
        HardwareVideoException e = Assert.Throws<HardwareVideoException>(() => VkCheck.ThrowIfFailed(VkResult.ErrorDeviceLost, "vkQueueSubmit"));
        Assert.Equal(-4, e.ResultCode);
        Assert.Contains("vkQueueSubmit", e.Message, StringComparison.Ordinal);
        Assert.Contains("ERROR_DEVICE_LOST", e.Message, StringComparison.Ordinal);
        VkCheck.ThrowIfFailed(VkResult.Success, "vkQueueSubmit");
        VkCheck.ThrowIfFailed(VkResult.Incomplete, "vkQueueSubmit");
    }

    [Fact]
    public void StructsSetTheirStructureType()
    {
        Assert.Equal(VkStructureType.VideoProfileInfoKHR, new VkVideoProfileInfoKHR().sType);
        Assert.Equal(1000023000, (int)new VkVideoProfileInfoKHR().sType);
        Assert.Equal(VkStructureType.VideoEncodeUsageInfoKHR, new VkVideoEncodeUsageInfoKHR().sType);
    }

    [Fact]
    public void BitfieldFlagsPackFromTheLowestBit()
    {
        StdVideoH264SpsFlags flags = default;
        flags.constraint_set0_flag = 1;
        flags.frame_mbs_only_flag = 1;
        Assert.Equal((1u << 0) | (1u << 8), Bits(flags));
        flags.constraint_set0_flag = 0;
        Assert.Equal(1u << 8, Bits(flags));
        Assert.Equal(1u, flags.frame_mbs_only_flag);
    }

    private static uint Bits(StdVideoH264SpsFlags flags) => *(uint*)&flags;
}
