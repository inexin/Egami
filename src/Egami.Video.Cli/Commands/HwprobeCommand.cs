using System.CommandLine;

using Egami.Video.Cli.Host;
using Egami.Video.Hardware;

namespace Egami.Video.Cli.Commands;

/// <summary><c>egami-video hwprobe</c> (Vulkan Video plan, PR E): the GPUs and what their video engines decode and encode.</summary>
internal static class HwprobeCommand
{
    public static Command Create(VideoSession session)
    {
        Option<bool> json = new("--json") { Description = "Write JSON instead of text." };
        Command command = new("hwprobe", "List the GPUs whose video engines Egami.Video.Hardware can use, with the codecs, profiles and limits of each.");
        command.Options.Add(json);
        command.SetAction(result =>
        {
            ReportObject report = Report();
            if (result.GetValue(json))
            {
                ReportWriter.Json(session.Console.Text, report);
                return ExitCodes.Success;
            }

            TextWriter text = session.Console.Text;
            IReadOnlyList<HardwareVideoDevice> devices = HardwareVideoCodecs.GetDevices();
            if (devices.Count == 0)
            {
                text.WriteLine($"No hardware video: {HardwareVideoCodecs.UnavailableReason}");
                return ExitCodes.Success;
            }

            foreach (HardwareVideoDevice device in devices)
            {
                text.WriteLine(device);
                foreach (HardwareVideoProfile profile in device.Profiles)
                {
                    text.WriteLine($"  {profile}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>The devices and their profiles as a report.</summary>
    internal static ReportObject Report()
    {
        ReportObject report = new();
        IReadOnlyList<HardwareVideoDevice> devices = HardwareVideoCodecs.GetDevices();
        report.AddIf("unavailable", devices.Count == 0 ? HardwareVideoCodecs.UnavailableReason : null);
        List<ReportObject> list = report.List("devices");
        foreach (HardwareVideoDevice device in devices)
        {
            ReportObject entry = new ReportObject()
                .Add("name", device.Name)
                .Add("kind", device.Kind.ToString())
                .Add("vendorId", device.VendorId)
                .Add("deviceId", device.DeviceId)
                .Add("api", $"{device.Api} {device.ApiVersion}")
                .Add("driver", $"{device.DriverName} {device.DriverInfo}");
            List<ReportObject> profiles = entry.List("profiles");
            foreach (HardwareVideoProfile profile in device.Profiles)
            {
                ReportObject p = new ReportObject()
                    .Add("codec", CodecNames.Name(profile.CodecId))
                    .Add("codecId", profile.CodecId)
                    .Add("direction", profile.Direction == HardwareDirection.Encode ? "encode" : "decode")
                    .Add("profile", profile.Profile)
                    .Add("bitDepth", profile.BitDepth)
                    .Add("minSize", $"{profile.MinWidth}x{profile.MinHeight}")
                    .Add("maxSize", $"{profile.MaxWidth}x{profile.MaxHeight}")
                    .Add("maxLevel", profile.MaxLevel)
                    .Add("maxReferences", profile.MaxReferences);
                if (profile.Direction == HardwareDirection.Encode)
                {
                    p.Add("rateControl", profile.RateControl.ToString())
                        .Add("qualityLevels", profile.QualityLevels)
                        .Add("maxBitrate", profile.MaxBitrate)
                        .Add("maxForwardReferences", profile.MaxForwardReferences)
                        .Add("maxBackwardReferences", profile.MaxBackwardReferences);
                }

                profiles.Add(p);
            }

            list.Add(entry);
        }

        return report;
    }
}
