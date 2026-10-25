using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

using AudioFade = Egami.Video.Audio.Filters.Fade;
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
        if (clip.Source is StackClipSource stack)
        {
            // A stack's sound is its first clip's, or all of them mixed; each child keeps the stack's timing and fades.
            IEnumerable<Clip> children = stack.Options.MixAudio ? stack.Clips : stack.Clips.Take(1);
            foreach (Clip child in children.Where(c => plan.Sources[c].HasAudio && !c.IsMuted))
            {
                SourceInfo childInfo = plan.Sources[child];
                MediaTime childLength = Min(length, Times.Sub(Times.Sub(childInfo.Out, childInfo.In), info.In));
                if (childLength > MediaTime.Zero)
                {
                    _segments.Add(new Segment(child, Times.Add(childInfo.In, info.In), start, childLength, fadeIn, inCurve, Min(fadeOut, childLength), outCurve, gain + child.VolumeDecibels, _canvas, _context));
                }
            }

            return;
        }

        _segments.Add(new Segment(clip, info.In, start, Min(length, Times.Sub(info.Out, info.In)), fadeIn, inCurve, fadeOut, outCurve, gain, _canvas, _context));
    }

    private static MediaTime Min(MediaTime a, MediaTime b) => a < b ? a : b;

    /// <summary>One source's audio in its place on the timeline: read, trimmed, faded, then moved to its edit time.</summary>
    private sealed class Segment : IDisposable
    {
        private readonly Clip _clip;
        private readonly MediaTime _in;
        private readonly MediaTime _length;
        private readonly EditCanvas _canvas;
        private readonly MediaContext _context;
        private readonly AudioFilterChain _chain;
        private readonly long _endSample;
        private AudioReader? _reader;
        private long _sent;
        private bool _done;

        public Segment(Clip clip, MediaTime sourceIn, MediaTime start, MediaTime length, MediaTime fadeIn, FadeCurve inCurve, MediaTime fadeOut, FadeCurve outCurve,
            double gain, EditCanvas canvas, MediaContext context)
        {
            _clip = clip;
            _in = sourceIn;
            Start = start;
            _length = length;
            _canvas = canvas;
            _context = context;
            _endSample = (long)Math.Round(Times.Add(start, length).Seconds * canvas.SampleRate);
            _sent = (long)Math.Round(start.Seconds * canvas.SampleRate);
            _chain = new AudioFilterChain(canvas.SampleRate, canvas.AudioLayout, context)
                .Add(new AudioTrim(sourceIn, Times.Add(sourceIn, length)));
            if (gain != 0)
            {
                _chain.Add(new Gain(gain));
            }

            if (fadeIn > MediaTime.Zero)
            {
                _chain.Add(new AudioFade(FadeDirection.In, sourceIn, fadeIn, inCurve));
            }

            if (fadeOut > MediaTime.Zero)
            {
                _chain.Add(new AudioFade(FadeDirection.Out, Times.Sub(Times.Add(sourceIn, length), fadeOut), fadeOut, outCurve));
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
                if (_in > MediaTime.Zero)
                {
                    _reader.Seek(_in);
                }
            }

            MediaTime shift = Times.Sub(Start, _in);
            while (final || _sent < until)
            {
                if (!_reader.TryRead(out AudioFrame? frame))
                {
                    _chain.Flush();
                    Drain(mixer, input, shift);
                    End(mixer, input);
                    return;
                }

                using (frame)
                {
                    _chain.Send(frame);
                }

                Drain(mixer, input, shift);
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

        private void Drain(AudioMixer mixer, int input, MediaTime shift)
        {
            while (_chain.TryReceive(out AudioFrame? output))
            {
                using (output)
                {
                    output.Timestamp = Times.Add(output.Timestamp, shift);
                    mixer.Send(input, output);
                    _sent = output.Timestamp.Rescale(new Rational(1, _canvas.SampleRate)).Ticks + output.SampleCount;
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
