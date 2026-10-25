using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

using AudioTrim = Egami.Video.Audio.Filters.Trim;

namespace Egami.Video.Editing;

/// <summary>
/// The edit's sound: every clip's and layer's audio, cut to its place, faded and crossfaded, and mixed with V9.1's
/// <see cref="AudioMixer"/>. It reads each source only while its part of the timeline plays, so it holds little.
/// </summary>
internal sealed class AudioTimeline : IDisposable
{
    private readonly List<Segment> _segments = [];
    private readonly AudioMixer _mixer;
    private readonly EditCanvas _canvas;
    private readonly MediaContext _context;
    private readonly long _total;
    private long _written;

    public AudioTimeline(EditPlan plan, MediaContext context)
    {
        _canvas = plan.Canvas;
        _context = context;
        _total = (long)Math.Round(plan.Duration.Seconds * _canvas.SampleRate);
        for (int i = 0; i < plan.Clips.Count; i++)
        {
            PlannedClip clip = plan.Clips[i];
            if (!clip.HasAudio)
            {
                continue;
            }

            // A transition crossfades the audio over the same frames as the picture.
            Transition? into = clip.TransitionFrames > 0 ? plan.Edit.Track[i].Transition : null;
            Transition? outOf = i + 1 < plan.Clips.Count && plan.Clips[i + 1].TransitionFrames > 0 ? plan.Edit.Track[i + 1].Transition : null;
            MediaTime fadeIn = into is null ? clip.Clip.FadeInDuration : plan.TimeOf(clip.TransitionFrames);
            MediaTime fadeOut = outOf is null ? clip.Clip.FadeOutDuration : plan.TimeOf(plan.Clips[i + 1].TransitionFrames);
            AddSegments(plan, clip.Clip, plan.TimeOf(clip.StartFrame), plan.TimeOf(clip.FrameCount), fadeIn, into?.AudioCurve ?? FadeCurve.Linear,
                fadeOut, outOf?.AudioCurve ?? FadeCurve.Linear, clip.Clip.VolumeDecibels);
        }

        foreach (PlannedLayer layer in plan.Layers)
        {
            if (layer.HasAudio)
            {
                AddSegments(plan, layer.Layer.Clip, plan.TimeOf(layer.StartFrame), plan.TimeOf(layer.ClipFrames), layer.Layer.FadeInDuration, FadeCurve.Linear,
                    layer.Layer.FadeOutDuration, FadeCurve.Linear, layer.Layer.Clip.VolumeDecibels);
            }
        }

        _mixer = new AudioMixer(_canvas.SampleRate, _canvas.AudioLayout, Math.Max(1, _segments.Count), context);
        _mixer.StartAt(MediaTime.Zero);
        for (int i = 0; i < _segments.Count; i++)
        {
            _mixer.Hold(i, _segments[i].Start);
        }

        if (_segments.Count == 0)
        {
            _mixer.Complete(0);
        }
    }

    /// <summary>Mixes the sound up to a time and hands it to the writer.</summary>
    public void WriteUntil(MediaTime time, VideoWriter writer, bool final)
    {
        long until = final ? _total : Math.Min(_total, (long)Math.Round(time.Seconds * _canvas.SampleRate));
        for (int i = 0; i < _segments.Count; i++)
        {
            _segments[i].Feed(_mixer, i, until, final);
        }

        if (final)
        {
            _mixer.Flush();
        }

        while (_mixer.TryReceive(out AudioFrame? frame))
        {
            using (frame)
            {
                Write(frame, writer);
            }
        }

        if (final)
        {
            // Silence to the edit's end when the sound stops sooner.
            while (_written < _total)
            {
                int n = (int)Math.Min(4096, _total - _written);
                using AudioFrame silence = new(SampleFormat.F32Planar, _canvas.AudioLayout, _canvas.SampleRate, n, new MediaTime(_written, new Rational(1, _canvas.SampleRate)), _context);
                Write(silence, writer);
            }
        }
    }

    public void Dispose()
    {
        foreach (Segment segment in _segments)
        {
            segment.Dispose();
        }

        _mixer.Dispose();
    }

    private void Write(AudioFrame frame, VideoWriter writer)
    {
        long first = frame.Timestamp.Rescale(new Rational(1, _canvas.SampleRate)).Ticks;
        long end = Math.Min(first + frame.SampleCount, _total);
        long from = Math.Max(first, _written);
        if (end <= from)
        {
            return;
        }

        if (from == first && end == first + frame.SampleCount)
        {
            writer.WriteAudio(frame);
        }
        else
        {
            using AudioFrame part = frame.Slice((int)(from - first), (int)(end - from), _context);
            writer.WriteAudio(part);
        }

        _written = end;
    }

    private void AddSegments(EditPlan plan, Clip clip, MediaTime start, MediaTime length, MediaTime fadeIn, FadeCurve inCurve, MediaTime fadeOut, FadeCurve outCurve, double gain)
    {
        SourceInfo info = plan.Sources[clip];
        ClipTiming timing = new(clip, info);
        Fades fades = new(start, length, fadeIn, inCurve, fadeOut, outCurve);
        if (clip.Source is StackClipSource stack)
        {
            // A stack's sound is its first clip's, or all of them mixed, on the stack's own timing.
            IEnumerable<Clip> children = stack.Options.MixAudio ? stack.Clips : stack.Clips.Take(1);
            foreach (Clip child in children.Where(c => plan.Sources[c].HasAudio && !c.IsMuted))
            {
                SourceInfo childInfo = plan.Sources[child];
                double childLength = new ClipTiming(child, childInfo).Length;
                AddPieces(child, Times.Add(childInfo.In, info.In).Seconds, childLength - info.In.Seconds, clip, timing, start, length, fades, gain + child.VolumeDecibels);
            }

            return;
        }

        AddPieces(clip, info.In.Seconds, double.PositiveInfinity, clip, timing, start, length, fades, gain);
    }

    /// <summary>Adds a segment for each piece of a clip's timing: loops, the parts between freezes, read backwards when reversed.</summary>
    private void AddPieces(Clip source, double sourceIn, double sourceLimit, Clip clip, ClipTiming timing, MediaTime start, MediaTime length, Fades fades, double gain)
    {
        double total = length.Seconds;
        foreach ((double a, double b, double at) in timing.Pieces())
        {
            double from = Math.Max(0, a), to = Math.Min(b, sourceLimit);
            double play = (to - from) / clip.SpeedFactor;
            play = Math.Min(play, total - at);
            if (play <= 1e-6)
            {
                continue;
            }

            to = from + (play * clip.SpeedFactor);
            _segments.Add(new Segment(source, sourceIn + from, to - from, Times.Add(start, MediaTime.FromSeconds(at)), clip.SpeedFactor, clip.KeepsPitch, clip.IsReversed, fades, gain, _canvas, _context));
        }
    }

    /// <summary>A clip's fades on the edit's time line: in from its start, out to its end.</summary>
    private sealed record Fades(MediaTime Start, MediaTime Length, MediaTime In, FadeCurve InCurve, MediaTime Out, FadeCurve OutCurve);

    /// <summary>
    /// One piece of a source's audio in its place on the timeline: read (backwards when reversed), cut, sped up or slowed down,
    /// moved to its edit time, and faded with its clip.
    /// </summary>
    private sealed class Segment : IDisposable
    {
        private const double Window = 1;

        private readonly Clip _clip;
        private readonly double _in;
        private readonly double _length;
        private readonly bool _reversed;
        private readonly EditCanvas _canvas;
        private readonly MediaContext _context;
        private readonly AudioFilterChain _chain;
        private readonly long _endSample;
        private AudioReader? _reader;
        private double _readEnd;
        private long _sent;
        private bool _done;

        public Segment(Clip clip, double sourceIn, double sourceLength, MediaTime start, double speed, bool keepPitch, bool reversed, Fades fades, double gain,
            EditCanvas canvas, MediaContext context)
        {
            _clip = clip;
            _in = sourceIn;
            _length = sourceLength;
            _reversed = reversed;
            _readEnd = sourceIn + sourceLength;
            Start = start;
            _canvas = canvas;
            _context = context;
            _endSample = (long)Math.Round((start.Seconds + (sourceLength / speed)) * canvas.SampleRate);
            _sent = (long)Math.Round(start.Seconds * canvas.SampleRate);
            _chain = new AudioFilterChain(canvas.SampleRate, canvas.AudioLayout, context);
            if (!reversed)
            {
                _chain.Add(new AudioTrim(MediaTime.FromSeconds(sourceIn), MediaTime.FromSeconds(sourceIn + sourceLength)));
            }

            foreach (double step in Steps(speed))
            {
                _chain.Add(keepPitch ? new Tempo(step) : new Egami.Video.Audio.Filters.Speed(step));
            }

            _chain.Add(new Retime(start));
            if (gain != 0)
            {
                _chain.Add(new Gain(gain));
            }

            if (fades.In > MediaTime.Zero)
            {
                _chain.Add(new AudioFade(FadeDirection.In, fades.Start, fades.In, fades.InCurve));
            }

            if (fades.Out > MediaTime.Zero)
            {
                _chain.Add(new AudioFade(FadeDirection.Out, Times.Sub(Times.Add(fades.Start, fades.Length), fades.Out), fades.Out, fades.OutCurve));
            }
        }

        public MediaTime Start { get; }

        /// <summary>Sends the mixer this segment's samples up to an output sample index.</summary>
        public void Feed(AudioMixer mixer, int input, long until, bool final)
        {
            if (_done || (!final && until <= _sent))
            {
                return;
            }

            if (_reader is null)
            {
                AudioReaderOptions options = new() { SampleRate = _canvas.SampleRate, Layout = _canvas.AudioLayout };
                _reader = _clip.Source switch
                {
                    StreamClipSource s => AudioReader.Open(s.Stream, options, _context, leaveOpen: true),
                    FileClipSource f => AudioReader.Open(f.Path, options, _context),
                    _ => throw new InvalidOperationException(),
                };
                if (_in > 0 && !_reversed)
                {
                    _reader.Seek(MediaTime.FromSeconds(_in));
                }
            }

            while (final || _sent < until)
            {
                AudioFrame? frame = _reversed ? ReadBackwards() : _reader.TryRead(out AudioFrame? next) ? next : null;
                if (frame is null)
                {
                    _chain.Flush();
                    Drain(mixer, input);
                    End(mixer, input);
                    return;
                }

                using (frame)
                {
                    _chain.Send(frame);
                }

                Drain(mixer, input);
                if (_sent >= _endSample)
                {
                    End(mixer, input);
                    return;
                }
            }
        }

        public void Dispose()
        {
            _chain.Dispose();
            _reader?.Dispose();
        }

        /// <summary>Splits a speed into steps the tempo and speed filters take (0.25 to 4 each).</summary>
        private static IEnumerable<double> Steps(double speed)
        {
            while (speed > 4 + 1e-9)
            {
                yield return 4;
                speed /= 4;
            }

            while (speed < 0.25 - 1e-9)
            {
                yield return 0.25;
                speed /= 0.25;
            }

            if (Math.Abs(speed - 1) > 1e-9)
            {
                yield return speed;
            }
        }

        /// <summary>The next window of the piece read from its end towards its start, its samples reversed, timed in playing order.</summary>
        private AudioFrame? ReadBackwards()
        {
            double end = _readEnd, begin = Math.Max(_in, end - Window);
            if (end - begin <= 1e-9)
            {
                return null;
            }

            _readEnd = begin;
            int rate = _canvas.SampleRate, channels = _canvas.AudioLayout.ChannelCount;
            long first = (long)Math.Round(begin * rate), last = (long)Math.Round(end * rate);
            float[][] samples = [.. Enumerable.Range(0, channels).Select(_ => new float[last - first])];
            _reader!.Seek(MediaTime.FromSeconds(begin));
            while (_reader.TryRead(out AudioFrame? frame))
            {
                using (frame)
                {
                    long at = frame.Timestamp.Rescale(new Rational(1, rate)).Ticks;
                    if (at >= last)
                    {
                        break;
                    }

                    int skip = (int)Math.Max(0, first - at), offset = (int)Math.Max(0, at - first);
                    int count = (int)Math.Min(frame.SampleCount - skip, last - first - offset);
                    for (int c = 0; c < channels && count > 0; c++)
                    {
                        frame.AsSpan<float>(c).Slice(skip, count).CopyTo(samples[c].AsSpan(offset));
                    }
                }
            }

            // Played order: this window comes after the windows nearer the end.
            double played = (_in + _length) - end;
            AudioFrame output = new(SampleFormat.F32Planar, _canvas.AudioLayout, rate, (int)(last - first), new MediaTime((long)Math.Round((_in + played) * rate), new Rational(1, rate)), _context);
            for (int c = 0; c < channels; c++)
            {
                Span<float> target = output.GetSpan<float>(c);
                samples[c].CopyTo(target);
                target.Reverse();
            }

            return output;
        }

        private void Drain(AudioMixer mixer, int input)
        {
            while (_chain.TryReceive(out AudioFrame? output))
            {
                using (output)
                {
                    long first = output.Timestamp.Rescale(new Rational(1, _canvas.SampleRate)).Ticks;
                    long keep = Math.Min(output.SampleCount, _endSample - first);
                    if (keep <= 0)
                    {
                        continue;
                    }

                    if (keep < output.SampleCount)
                    {
                        using AudioFrame part = output.Slice(0, (int)keep, _context);
                        mixer.Send(input, part);
                    }
                    else
                    {
                        mixer.Send(input, output);
                    }

                    _sent = first + keep;
                }
            }
        }

        private void End(AudioMixer mixer, int input)
        {
            _done = true;
            mixer.Complete(input);
            _reader?.Dispose();
            _reader = null;
        }
    }
}
