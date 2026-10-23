using System.Globalization;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>info --format ffprobe</c> (Video CLI design Q6): JSON with the field names ffprobe's <c>-of json -show_streams
/// -show_format</c> writes (and <c>-show_packets</c>, <c>-show_frames</c>), so scripts written for ffprobe read it unchanged.
/// Built from ffprobe's documented output only; numbers that ffprobe writes as text are text here too.
/// </summary>
internal static class FfprobeReport
{
    /// <summary>One file's report, or <c>{ "files": [...] }</c> for several.</summary>
    public static ReportObject Build(IReadOnlyList<InfoCommand.InfoData> files, InfoCommand.InfoRequest request)
    {
        if (files.Count == 1)
        {
            return File(files[0], request);
        }

        ReportObject root = new();
        List<ReportObject> list = root.List("files");
        list.AddRange(files.Select(f => File(f, request)));
        return root;
    }

    private static ReportObject File(InfoCommand.InfoData data, InfoCommand.InfoRequest request)
    {
        ReportObject report = new();
        List<TrackInfo> all = [.. data.Info.Tracks];
        if (request.Packets)
        {
            List<ReportObject> packets = report.List("packets");
            foreach (InfoCommand.PacketRow row in data.PacketRows)
            {
                double tb = row.Track.TimeBase.ToDouble();
                long dts = row.Dts ?? row.Pts;
                ReportObject packet = new ReportObject()
                    .Add("codec_type", CodecType(row.Track.Kind))
                    .Add("stream_index", all.IndexOf(row.Track))
                    .Add("pts", row.Pts)
                    .Add("pts_time", Time(row.Pts * tb))
                    .Add("dts", dts)
                    .Add("dts_time", Time(dts * tb));
                if (row.Duration is { } duration)
                {
                    packet.Add("duration", duration).Add("duration_time", Time(duration * tb));
                }

                packet.Add("size", row.Size.ToString(CultureInfo.InvariantCulture));
                packet.Add("flags", (row.Keyframe ? "K" : "_") + (row.Discardable ? "D" : "_") + "_");
                packets.Add(packet);
            }
        }

        if (request.Frames)
        {
            List<ReportObject> frames = report.List("frames");
            foreach (InfoCommand.FrameRow row in data.FrameRows)
            {
                Rational timeBase = row.Track.TimeBase;
                ReportObject frame = new ReportObject()
                    .Add("media_type", CodecType(row.Track.Kind))
                    .Add("stream_index", all.IndexOf(row.Track))
                    .Add("key_frame", row.Keyframe is true ? 1 : 0)
                    .Add("pts", row.Timestamp.Rescale(timeBase).Ticks)
                    .Add("pts_time", Time(row.Timestamp.Seconds))
                    .Add("best_effort_timestamp", row.Timestamp.Rescale(timeBase).Ticks)
                    .Add("best_effort_timestamp_time", Time(row.Timestamp.Seconds));
                if (row.Duration is { } duration)
                {
                    frame.Add("duration", duration.Rescale(timeBase).Ticks).Add("duration_time", Time(duration.Seconds));
                }

                if (row.Track.Kind == TrackKind.Video)
                {
                    frame.Add("width", row.Width).Add("height", row.Height);
                }
                else
                {
                    frame.Add("sample_fmt", row.SampleFormat)
                        .Add("nb_samples", row.Samples)
                        .Add("channels", row.Channels)
                        .Add("sample_rate", row.SampleRate.ToString(CultureInfo.InvariantCulture));
                }

                frames.Add(frame);
            }
        }

        List<ReportObject> streams = report.List("streams");
        foreach (TrackInfo track in all.Where(data.Tracks.Contains))
        {
            streams.Add(Stream(track, all.IndexOf(track), data));
        }

        ReportObject format = report.Object("format");
        format.Add("filename", data.File.Path ?? "pipe:");
        format.Add("nb_streams", all.Count);
        format.Add("nb_programs", 0);
        format.Add("format_name", FormatName(data.Info.FormatName));
        format.Add("format_long_name", FormatLongName(data.Info.FormatName));
        if (Start(data) is { } start)
        {
            format.Add("start_time", Time(start));
        }

        if (data.Info.Duration is { } fileDuration)
        {
            format.Add("duration", Time(fileDuration.Seconds));
        }

        if (data.Size is { } size)
        {
            format.Add("size", size.ToString(CultureInfo.InvariantCulture));
            if (data.Info.Duration is { Seconds: > 0 } d)
            {
                format.Add("bit_rate", ((long)(size * 8 / d.Seconds)).ToString(CultureInfo.InvariantCulture));
            }
        }

        format.Add("probe_score", 100);
        if (data.Info.Tags.Count > 0)
        {
            format.Add("tags", data.Info.Tags);
        }

        return report;
    }

    private static ReportObject Stream(TrackInfo track, int index, InfoCommand.InfoData data)
    {
        ReportObject stream = new();
        stream.Add("index", index);
        stream.AddIf("codec_name", CodecNames.FfprobeName(track));
        stream.AddIf("codec_long_name", CodecNames.FfprobeLongName(track));
        stream.Add("codec_type", CodecType(track.Kind));
        if (track is VideoTrackInfo video)
        {
            stream.Add("width", video.Width);
            stream.Add("height", video.Height);
            stream.Add("coded_width", video.Width);
            stream.Add("coded_height", video.Height);
            Rational par = video.PixelAspectRatio;
            stream.Add("sample_aspect_ratio", $"{par.Numerator}:{par.Denominator}");
            long dw = video.Width * par.Numerator, dh = video.Height * par.Denominator;
            long g = Gcd(dw, dh);
            stream.Add("display_aspect_ratio", g > 0 ? $"{dw / g}:{dh / g}" : "0:1");
            stream.AddIf("pix_fmt", PixelFormat(video));
            if (video.Color is { } color)
            {
                stream.Add("color_range", color.FullRange ? "pc" : "tv");
                stream.Add("color_space", H273.Matrix(color.MatrixCoefficients));
                stream.Add("color_transfer", H273.Transfer(color.TransferCharacteristics));
                stream.Add("color_primaries", H273.Primaries(color.Primaries));
            }

            if (video.FieldOrder != FieldOrder.Progressive)
            {
                stream.Add("field_order", video.FieldOrder == FieldOrder.TopFieldFirst ? "tt" : "bb");
            }

            string rate = video.FrameRate is { } r ? $"{r.Numerator}/{r.Denominator}" : "0/0";
            stream.Add("r_frame_rate", rate);
            stream.Add("avg_frame_rate", AverageFrameRate(track, data) ?? rate);
        }
        else if (track is AudioTrackInfo audio)
        {
            stream.AddIf("sample_fmt", InfoCommand.TrackSampleFormat(audio));
            // Opus always decodes at 48 kHz; the rate in its header is only the rate the source had.
            int rate = track.CodecId == CodecIds.Opus ? 48000 : audio.SampleRate;
            stream.Add("sample_rate", rate.ToString(CultureInfo.InvariantCulture));
            stream.Add("channels", audio.Channels);
            stream.Add("channel_layout", InfoCommand.AudioLayout(audio));
            stream.Add("bits_per_sample", track.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat ? audio.BitsPerSample : 0);
            stream.Add("r_frame_rate", "0/0");
            stream.Add("avg_frame_rate", "0/0");
        }
        else
        {
            stream.Add("r_frame_rate", "0/0");
            stream.Add("avg_frame_rate", "0/0");
        }

        stream.Add("time_base", $"{track.TimeBase.Numerator}/{track.TimeBase.Denominator}");
        if (data.Stats.TryGetValue(track.Number, out InfoCommand.TrackStats? stats))
        {
            if (stats.Start is { } first)
            {
                stream.Add("start_pts", first.Rescale(track.TimeBase).Ticks);
                stream.Add("start_time", Time(first.Seconds));
            }
        }

        if (track.Duration is { } duration)
        {
            stream.Add("duration_ts", duration.Rescale(track.TimeBase).Ticks);
            stream.Add("duration", Time(duration.Seconds));
        }

        if (stats is { Packets: > 0 })
        {
            if (stats.End is { } end && stats.Start is { } start && (end - start).Seconds > 0)
            {
                stream.Add("bit_rate", ((long)(stats.Bytes * 8 / (end - start).Seconds)).ToString(CultureInfo.InvariantCulture));
            }

            stream.Add("nb_frames", stats.Packets.ToString(CultureInfo.InvariantCulture));
        }

        stream.Add("disposition", new ReportObject().Add("default", track.IsDefault ? 1 : 0));
        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        if (track.Language is { } language)
        {
            tags["language"] = language;
        }

        if (track.Name is { } name)
        {
            tags["title"] = name;
        }

        foreach ((string key, string value) in track.Tags)
        {
            tags.TryAdd(key, value);
        }

        if (tags.Count > 0)
        {
            stream.Add("tags", tags);
        }

        return stream;
    }

    private static string? AverageFrameRate(TrackInfo track, InfoCommand.InfoData data)
    {
        if (!data.Stats.TryGetValue(track.Number, out InfoCommand.TrackStats? stats) || stats.Packets < 2 || stats.Start is not { } start || stats.End is not { } end)
        {
            return null;
        }

        double seconds = (end - start).Seconds;
        if (seconds <= 0)
        {
            return null;
        }

        double fps = stats.Packets / seconds;
        foreach (long denominator in new long[] { 1, 1001, 1000 })
        {
            double numerator = fps * denominator;
            if (Math.Abs(numerator - Math.Round(numerator)) < 1e-3 * denominator)
            {
                long n = (long)Math.Round(numerator);
                long g = Gcd(n, denominator);
                return $"{n / g}/{denominator / g}";
            }
        }

        long scaled = (long)Math.Round(fps * 1_000_000);
        long gcd = Gcd(scaled, 1_000_000);
        return $"{scaled / gcd}/{1_000_000 / gcd}";
    }

    private static double? Start(InfoCommand.InfoData data)
    {
        MediaTime? start = null;
        foreach (InfoCommand.TrackStats stats in data.Stats.Values)
        {
            if (stats.Start is { } s && (start is null || s < start.Value))
            {
                start = s;
            }
        }

        return start?.Seconds;
    }

    private static string? PixelFormat(VideoTrackInfo video)
    {
        if (video.ChromaFormat is not { } chroma)
        {
            return null;
        }

        string planes = video.HasAlpha ? "yuva" : "yuv";
        string sampling = chroma switch
        {
            Pixels.ChromaFormat.Yuv420 => "420",
            Pixels.ChromaFormat.Yuv422 => "422",
            Pixels.ChromaFormat.Yuv444 => "444",
            Pixels.ChromaFormat.Yuv400 => "gray",
            _ => chroma.ToString()[3..],
        };
        if (sampling == "gray")
        {
            return video.BitDepth > 8 ? $"gray{video.BitDepth}le" : "gray";
        }

        bool fullRange = video.CodecId == CodecIds.MotionJpeg;
        string prefix = fullRange && video.BitDepth <= 8 ? "yuvj" : planes;
        return video.BitDepth > 8 ? $"{planes}{sampling}p{video.BitDepth}le" : $"{prefix}{sampling}p";
    }

    private static string CodecType(TrackKind kind) => kind switch
    {
        TrackKind.Video => "video",
        TrackKind.Audio => "audio",
        TrackKind.Subtitle => "subtitle",
        _ => "data",
    };

    /// <summary>ffprobe's format_name for a container name.</summary>
    internal static string FormatName(string name) => name switch
    {
        "Matroska" or "WebM" => "matroska,webm",
        "AVI" => "avi",
        "MP4" or "QuickTime" => "mov,mp4,m4a,3gp,3g2,mj2",
        "IVF" => "ivf",
        "YUV4MPEG2" => "yuv4mpegpipe",
        "WAV" => "wav",
        "Wave64" => "w64",
        "FLAC" => "flac",
        "Ogg" => "ogg",
        "AV1 OBU" => "obu",
        _ => name.ToLowerInvariant(),
    };

    private static string FormatLongName(string name) => name switch
    {
        "Matroska" or "WebM" => "Matroska / WebM",
        "AVI" => "AVI (Audio Video Interleaved)",
        "MP4" or "QuickTime" => "QuickTime / MOV",
        "IVF" => "On2 IVF",
        "YUV4MPEG2" => "YUV4MPEG pipe",
        "WAV" => "WAV / WAVE (Waveform Audio)",
        "Wave64" => "Sony Wave64",
        "FLAC" => "raw FLAC",
        "Ogg" => "Ogg",
        _ => name,
    };

    private static string Time(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
