using System.Globalization;
using System.Text;
using System.Xml;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Writes the playlists of <see cref="Mp4SegmentWriter"/> (V4.3 Design Q7): HLS (RFC 8216, version 7 for fragmented MP4 segments)
/// with each audio track as an alternative rendition of each video variant, and a static DASH MPD in the ISO live profile with
/// an adaptation set a track.
/// </summary>
internal static class SegmentPlaylistWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Hls(Func<string, Stream> open, IReadOnlyList<FragmentTrack> tracks)
    {
        foreach (FragmentTrack track in tracks)
        {
            StringBuilder media = new();
            double target = track.Fragments.Count == 0 ? 1 : track.Fragments.Max(f => Math.Round(Seconds(track, f.Duration)));
            media.Append("#EXTM3U\n#EXT-X-VERSION:7\n");
            media.Append(Invariant($"#EXT-X-TARGETDURATION:{Math.Max(1, target)}\n"));
            media.Append("#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
            media.Append(Invariant($"#EXT-X-MAP:URI=\"{Mp4SegmentWriter.NameOf(track)}-init.mp4\"\n"));
            for (int i = 0; i < track.Fragments.Count; i++)
            {
                media.Append(Invariant($"#EXTINF:{Seconds(track, track.Fragments[i].Duration):0.000000},\n{Mp4SegmentWriter.NameOf(track)}-{i + 1:D5}.m4s\n"));
            }

            media.Append("#EXT-X-ENDLIST\n");
            Write(open, $"{Mp4SegmentWriter.NameOf(track)}.m3u8", media.ToString());
        }

        List<FragmentTrack> video = [.. tracks.Where(t => t.Input.Kind == TrackKind.Video)];
        List<FragmentTrack> audio = [.. tracks.Where(t => t.Input.Kind == TrackKind.Audio)];
        StringBuilder master = new("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (video.Count > 0)
        {
            for (int i = 0; i < audio.Count; i++)
            {
                FragmentTrack a = audio[i];
                string language = a.Input.Language is { } l ? Invariant($",LANGUAGE=\"{l}\"") : "";
                string channels = a.Input is AudioTrackInfo info ? Invariant($",CHANNELS=\"{info.Channels}\"") : "";
                master.Append(Invariant($"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"{Quoted(a.Input.Name ?? a.Input.Language ?? Mp4SegmentWriter.NameOf(a))}\"{language},DEFAULT={(i == 0 ? "YES" : "NO")},AUTOSELECT=YES{channels},URI=\"{Mp4SegmentWriter.NameOf(a)}.m3u8\"\n"));
            }

            long audioPeak = audio.Count == 0 ? 0 : audio.Max(Peak), audioAverage = audio.Count == 0 ? 0 : audio.Max(Average);
            foreach (FragmentTrack v in video)
            {
                VideoTrackInfo info = (VideoTrackInfo)v.Input;
                string codecs = string.Join(',', new[] { Mp4CodecStrings.Of(v.Input) }.Concat(audio.Select(a => Mp4CodecStrings.Of(a.Input))).Distinct());
                string rate = info.FrameRate is { Numerator: > 0, Denominator: > 0 } r ? Invariant($",FRAME-RATE={(double)r.Numerator / r.Denominator:0.000}") : "";
                master.Append(Invariant($"#EXT-X-STREAM-INF:BANDWIDTH={Peak(v) + audioPeak},AVERAGE-BANDWIDTH={Average(v) + audioAverage},CODECS=\"{codecs}\",RESOLUTION={info.Width}x{info.Height}{rate}{(audio.Count > 0 ? ",AUDIO=\"audio\"" : "")}\n"));
                master.Append(Invariant($"{Mp4SegmentWriter.NameOf(v)}.m3u8\n"));
            }
        }
        else
        {
            foreach (FragmentTrack a in audio)
            {
                master.Append(Invariant($"#EXT-X-STREAM-INF:BANDWIDTH={Peak(a)},AVERAGE-BANDWIDTH={Average(a)},CODECS=\"{Mp4CodecStrings.Of(a.Input)}\"\n{Mp4SegmentWriter.NameOf(a)}.m3u8\n"));
            }
        }

        Write(open, "master.m3u8", master.ToString());
    }

    public static void Dash(Func<string, Stream> open, IReadOnlyList<FragmentTrack> tracks)
    {
        // The media's own end, before the edit lists shift it: players cut what is appended past the presentation's end, and an
        // edit that ends exactly at the last frame's end can lose that frame to rounding.
        double duration = tracks.Max(t => t.Fragments.Count == 0 ? 0 : Seconds(t, t.Fragments[^1].Start + t.Fragments[^1].Duration));
        double longest = tracks.Max(t => t.Fragments.Count == 0 ? 0 : t.Fragments.Max(f => Seconds(t, f.Duration)));
        using Stream stream = open("manifest.mpd");
        using XmlWriter x = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8, Indent = true, NewLineChars = "\n" });
        x.WriteStartDocument();
        x.WriteStartElement("MPD", "urn:mpeg:dash:schema:mpd:2011");
        x.WriteAttributeString("profiles", "urn:mpeg:dash:profile:isoff-live:2011");
        x.WriteAttributeString("type", "static");
        x.WriteAttributeString("mediaPresentationDuration", Invariant($"PT{duration:0.000}S"));
        x.WriteAttributeString("minBufferTime", Invariant($"PT{Math.Ceiling(Math.Max(1, longest))}S"));
        x.WriteStartElement("Period");
        x.WriteAttributeString("id", "0");
        x.WriteAttributeString("start", "PT0S");
        int id = 0;
        foreach (FragmentTrack track in tracks)
        {
            bool video = track.Input.Kind == TrackKind.Video;
            x.WriteStartElement("AdaptationSet");
            x.WriteAttributeString("id", Invariant($"{id++}"));
            x.WriteAttributeString("contentType", video ? "video" : "audio");
            x.WriteAttributeString("mimeType", video ? "video/mp4" : "audio/mp4");
            x.WriteAttributeString("segmentAlignment", "true");
            x.WriteAttributeString("startWithSAP", "1");
            if (track.Input.Language is { } language)
            {
                x.WriteAttributeString("lang", language);
            }

            x.WriteStartElement("Representation");
            x.WriteAttributeString("id", Mp4SegmentWriter.NameOf(track));
            x.WriteAttributeString("codecs", Mp4CodecStrings.Of(track.Input));
            x.WriteAttributeString("bandwidth", Invariant($"{Peak(track)}"));
            if (track.Input is VideoTrackInfo v)
            {
                x.WriteAttributeString("width", Invariant($"{v.Width}"));
                x.WriteAttributeString("height", Invariant($"{v.Height}"));
                if (v.FrameRate is { Numerator: > 0, Denominator: > 0 } rate)
                {
                    x.WriteAttributeString("frameRate", rate.Denominator == 1 ? Invariant($"{rate.Numerator}") : Invariant($"{rate.Numerator}/{rate.Denominator}"));
                }
            }
            else if (track.Input is AudioTrackInfo a)
            {
                x.WriteAttributeString("audioSamplingRate", Invariant($"{(a.CodecId == CodecIds.Opus ? 48000 : a.SampleRate)}"));
                x.WriteStartElement("AudioChannelConfiguration");
                x.WriteAttributeString("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011");
                x.WriteAttributeString("value", Invariant($"{a.Channels}"));
                x.WriteEndElement();
            }

            x.WriteStartElement("SegmentTemplate");
            x.WriteAttributeString("timescale", Invariant($"{track.Timescale}"));
            x.WriteAttributeString("initialization", $"{Mp4SegmentWriter.NameOf(track)}-init.mp4");
            x.WriteAttributeString("media", $"{Mp4SegmentWriter.NameOf(track)}-$Number%05d$.m4s");
            x.WriteAttributeString("startNumber", "1");
            // The times are the segments' decode times (tfdt), each lasting until the next starts, with no presentationTimeOffset:
            // the init segment's edit list shifts the media to the presentation, and players that read MP4 apply it.
            x.WriteStartElement("SegmentTimeline");
            long Length(int i) => i + 1 < track.Fragments.Count ? track.Fragments[i + 1].Start - track.Fragments[i].Start : track.Fragments[i].Duration;
            for (int i = 0; i < track.Fragments.Count;)
            {
                long start = track.Fragments[i].Start, length = Length(i);
                int repeat = 0;
                while (i + repeat + 1 < track.Fragments.Count && Length(i + repeat + 1) == length)
                {
                    repeat++;
                }

                x.WriteStartElement("S");
                x.WriteAttributeString("t", Invariant($"{start}"));
                x.WriteAttributeString("d", Invariant($"{length}"));
                if (repeat > 0)
                {
                    x.WriteAttributeString("r", Invariant($"{repeat}"));
                }

                x.WriteEndElement();
                i += repeat + 1;
            }

            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
        }

        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndDocument();
    }

    private static double Seconds(FragmentTrack track, long ticks) => (double)ticks / track.Timescale;

    /// <summary>Gets the highest bitrate of any segment, in bits per second.</summary>
    private static long Peak(FragmentTrack track) => track.Fragments.Count == 0 ? 0
        : (long)Math.Ceiling(track.Fragments.Max(f => f.Bytes * 8 / Math.Max(Seconds(track, f.Duration), 0.001)));

    /// <summary>Gets the bitrate over the whole track, in bits per second.</summary>
    private static long Average(FragmentTrack track)
    {
        double seconds = Seconds(track, track.Fragments.Sum(f => f.Duration));
        return (long)Math.Ceiling(track.Fragments.Sum(f => f.Bytes) * 8 / Math.Max(seconds, 0.001));
    }

    private static string Quoted(string text) => text.Replace("\"", "'", StringComparison.Ordinal);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void Write(Func<string, Stream> open, string name, string text)
    {
        using Stream stream = open(name);
        stream.Write(Utf8.GetBytes(text));
    }
}
