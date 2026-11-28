using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Editing;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// Draws the cues of a <see cref="SubtitleDocument"/> into video frames, as FFmpeg's <c>subtitles</c> filter does: into a
/// <see cref="YuvVideoFrame"/>'s planes in its own matrix, range and bit depth, or onto an RGBA raster. Each cue is laid out with
/// Egami.Fonts (shaping, bidi, UAX #14 line breaking, per-character font fallback), drawn once when it first shows and kept
/// while it stays on screen, so a frame whose cues have not changed only blends and allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// The renderer draws ASS and SSA styles (font, size, bold, italic, underline, strikeout, colors with alpha, outline and shadow,
/// the opaque box of <c>BorderStyle</c> 3, <c>ScaleX</c>, <c>ScaleY</c>, spacing, the nine alignments and the margins),
/// scaled from <c>PlayResX</c> and <c>PlayResY</c> to the frame; span styles inside a cue (bold, italic, underline, strikeout,
/// color, font and size); and hard line breaks. Lines wrap at the margins as libass wraps them for the script's
/// <c>WrapStyle</c>. SRT cues get the look FFmpeg gives them (Arial 16 at a 384×288 script, a thin black outline); WebVTT cues
/// get the W3C default look: white text, 5% of the frame's height, on a box of 80% black, centered at the bottom.
/// </para>
/// <para>
/// Not drawn yet: ASS override tags beyond the span styles (<c>\pos</c>, <c>\move</c>, <c>\fad</c>, <c>\t</c>, karaoke,
/// clips, blur, rotation, drawings), WebVTT cue settings, regions and <c>::cue</c> CSS, collision stacking of cues shown
/// together, and fonts attached to the file. They are reported once each through <see cref="MediaContext.Warnings"/>.
/// </para>
/// <para>The cues are read when the renderer is made; later changes to the document are not seen. A renderer is not thread safe.</para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-frames"/>
/// </example>
public sealed class SubtitleRenderer : IDisposable
{
    private readonly Entry[] _entries;
    private readonly long _longest;
    private readonly CueLayout _layout;
    private readonly MediaContext _context;
    private readonly Dictionary<string, SubtitleStyle> _styles = new(StringComparer.Ordinal);
    private readonly SubtitleStyle _defaultStyle;
    private readonly List<SubtitleStyle> _documentStyles;
    private List<Entry> _active = [];
    private List<Entry> _previous = [];
    private long _frame;
    private bool _disposed;

    /// <summary>Creates a renderer for a document's cues.</summary>
    /// <param name="document">The subtitles, read from SRT, WebVTT, ASS or SSA.</param>
    /// <param name="options">Where fonts come from and how styles change, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where warnings go (fonts not found, features not drawn), or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-options"/>
    /// </example>
    public SubtitleRenderer(SubtitleDocument document, SubtitleRenderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        Options = options ?? new SubtitleRenderOptions();
        _context = context ?? MediaContext.Default;
        var fonts = new FontResolver(Options, _context);
        _layout = new CueLayout(fonts, ScriptSettings.Of(document, Options.AuthoredSize));
        _documentStyles = [.. document.Styles];
        _defaultStyle = Force(DefaultStyle(document.Format));

        var entries = new List<Entry>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < document.Cues.Count; i++)
        {
            SubtitleCue cue = document.Cues[i];
            long start = Nanoseconds(cue.Start), end = Nanoseconds(cue.End);
            if (cue.IsComment || end <= start || cue.Spans.Count == 0)
            {
                continue;
            }

            Unsupported(cue, unsupported);
            entries.Add(new Entry(cue, i, start, end));
        }

        entries.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Order.CompareTo(b.Order));
        _entries = [.. entries];
        foreach (Entry entry in _entries)
        {
            _longest = Math.Max(_longest, entry.End - entry.Start);
        }

        foreach (string feature in unsupported)
        {
            _context.Warnings?.Invoke(new MediaWarning("subtitle-not-drawn", $"Subtitles use {feature}, which the renderer does not draw yet."));
        }
    }

    /// <summary>Gets the document the cues come from.</summary>
    public SubtitleDocument Document { get; }

    /// <summary>Gets the options the renderer was made with.</summary>
    public SubtitleRenderOptions Options { get; }

    /// <summary>Draws the cues showing at a frame's time into its planes.</summary>
    /// <param name="frame">The frame, changed in place: 8 to 16 bits, any chroma layout; HDR frames (PQ and HLG) get white at 203 cd/m².</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The renderer is disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-frames"/>
    /// </example>
    public void Draw(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        YuvRaster raster = frame.Raster;
        if (!Select(Nanoseconds(frame.Timestamp)))
        {
            return;
        }

        var key = new YuvFormatKey(raster.Color.Matrix, raster.Color.Range, raster.BitDepth, raster.ChromaFormat, Transfer(frame.Color), frame.Color?.Primaries == 9);
        foreach (Entry entry in _active)
        {
            Image(entry, raster.Width, raster.Height, _context.Egami)?.BlendInto(raster, key);
        }
    }

    /// <summary>Draws the cues showing at a time onto an RGBA raster, blended in its encoded values.</summary>
    /// <param name="raster">The raster, changed in place; the subtitles are laid out for its size.</param>
    /// <param name="time">The time on the subtitles' clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The renderer is disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-raster"/>
    /// </example>
    public void Draw(Raster<Rgba8> raster, MediaTime time)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Select(Nanoseconds(time)))
        {
            return;
        }

        foreach (Entry entry in _active)
        {
            Image(entry, raster.Width, raster.Height, raster.Context)?.BlendInto(raster);
        }
    }

    /// <summary>Returns an effect for <see cref="EditEffect.Of"/> that draws the subtitles on a <see cref="VideoEdit"/>'s frames.</summary>
    /// <returns>The effect. It draws at each frame's time from the start of what it is attached to, one frame at a time.</returns>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-edit"/>
    /// </example>
    public IFrameEffect AsEffect() => new Effect(this);

    /// <summary>Frees the drawn cues.</summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (Entry entry in _entries)
        {
            entry.Release();
        }

        _active.Clear();
        _previous.Clear();
    }

    /// <summary>Finds the cues showing at a time, drops the drawings of those that went, and orders them by layer.</summary>
    private bool Select(long time)
    {
        _frame++;
        (_previous, _active) = (_active, _previous);
        _active.Clear();

        // The cues starting at or before the time, back to the longest cue's length.
        int lo = Lower(time - _longest), hi = Lower(time + 1);
        for (int i = lo; i < hi; i++)
        {
            Entry entry = _entries[i];
            if (entry.End > time)
            {
                entry.Seen = _frame;
                Insert(entry);
            }
        }

        foreach (Entry entry in _previous)
        {
            if (entry.Seen != _frame)
            {
                entry.Release();
            }
        }

        return _active.Count > 0;
    }

    /// <summary>Adds a cue in drawing order: by layer, then file order.</summary>
    private void Insert(Entry entry)
    {
        int at = _active.Count;
        while (at > 0 && (_active[at - 1].Cue.Layer > entry.Cue.Layer || (_active[at - 1].Cue.Layer == entry.Cue.Layer && _active[at - 1].Order > entry.Order)))
        {
            at--;
        }

        _active.Insert(at, entry);
    }

    /// <summary>The first entry starting at or after a time.</summary>
    private int Lower(long time)
    {
        int lo = 0, hi = _entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Start < time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private CueImage? Image(Entry entry, int width, int height, EgamiContext egami)
    {
        if (!entry.Drawn || entry.Width != width || entry.Height != height)
        {
            entry.Image = _layout.Draw(entry.Cue, StyleOf(entry.Cue), width, height, egami);
            entry.Width = width;
            entry.Height = height;
            entry.Drawn = true;
        }

        return entry.Image;
    }

    /// <summary>The cue's style with <see cref="SubtitleRenderOptions.ForceStyle"/> applied: named, else Default, else the format's own.</summary>
    private SubtitleStyle StyleOf(SubtitleCue cue)
    {
        if (_documentStyles.Count == 0)
        {
            return _defaultStyle;
        }

        string name = (cue.Style ?? "Default").TrimStart('*');
        if (_styles.TryGetValue(name, out SubtitleStyle? known))
        {
            return known;
        }

        SubtitleStyle? style = _documentStyles.Find(s => s.Name == name) ?? _documentStyles.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? _documentStyles.Find(s => s.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        SubtitleStyle forced = style is null ? _defaultStyle : Force(style);
        _styles[name] = forced;
        return forced;
    }

    private SubtitleStyle Force(SubtitleStyle style) => Options.ForceStyle is { } force ? force(style) ?? style : style;

    /// <summary>The look of a format without styles: FFmpeg's for SRT, the W3C default for WebVTT, ASS's defaults otherwise.</summary>
    private static SubtitleStyle DefaultStyle(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => new SubtitleStyle("Default") { FontSize = 16, Outline = 1, Shadow = 0, BackColor = new Rgba8(0, 0, 0, 255) },
        SubtitleFormat.WebVtt => new SubtitleStyle("Default")
        {
            FontName = "sans-serif",
            FontSize = 288 * 0.05,
            BorderStyle = 3,
            Outline = 0,
            Shadow = 0,
            OutlineColor = new Rgba8(0, 0, 0, 204),
            MarginLeft = 0,
            MarginRight = 0,
            MarginVertical = 0,
        },
        _ => new SubtitleStyle("Default"),
    };

    private static HdrTransfer Transfer(ColorCodePoints? color) => color?.TransferCharacteristics switch
    {
        16 => HdrTransfer.Pq,
        18 => HdrTransfer.Hlg,
        _ => HdrTransfer.Sdr,
    };

    private static long Nanoseconds(MediaTime time) => time.Rescale(Rational.Nanosecond).Ticks;

    /// <summary>Notes the features a cue uses that the renderer does not draw.</summary>
    private static void Unsupported(SubtitleCue cue, HashSet<string> found)
    {
        if (cue.Position is not null)
        {
            found.Add("ASS positions (\\pos)");
        }

        if (cue.WebVtt is not null)
        {
            found.Add("WebVTT cue settings");
        }

        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.IsDrawing)
            {
                found.Add("ASS drawings (\\p)");
            }

            if (span.OtherTags is not null)
            {
                found.Add("ASS override tags beyond the span styles");
            }

            if (span.Ruby is not null)
            {
                found.Add("WebVTT ruby");
            }
        }
    }

    /// <summary>A cue, its place in the file and on the clock, and its drawing while it shows.</summary>
    private sealed class Entry(SubtitleCue cue, int order, long start, long end)
    {
        public SubtitleCue Cue { get; } = cue;

        public int Order { get; } = order;

        public long Start { get; } = start;

        public long End { get; } = end;

        public long Seen { get; set; }

        public bool Drawn { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public CueImage? Image { get; set; }

        public void Release()
        {
            Image = null;
            Drawn = false;
        }
    }

    /// <summary>The renderer as an edit effect, drawing one frame at a time.</summary>
    private sealed class Effect(SubtitleRenderer renderer) : IFrameEffect
    {
        public void Apply(Raster<Rgba8> frame, FrameEffectContext context)
        {
            lock (renderer)
            {
                renderer.Draw(frame, context.LocalTime);
            }
        }
    }
}
