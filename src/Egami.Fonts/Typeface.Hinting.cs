using Egami.Fonts.Cff;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    private const int OpBlueValues = 6;
    private const int OpOtherBlues = 7;

    private HintZone[]? _hintZones;
    private float _stemWidth = -1;

    /// <summary>
    /// Gets the face's vertical stem width in font units, which stem darkening follows: a CFF font's StdVW, else the narrowest ink
    /// run across the middle of 'l' (or 'I'), else a twelfth of the em.
    /// </summary>
    internal float StemWidth
    {
        get
        {
            if (_stemWidth < 0)
            {
                _stemWidth = MeasureStem();
            }

            return _stemWidth;
        }
    }

    /// <summary>
    /// Gets the alignment zones light hinting snaps, sorted by flat edge: a CFF font's BlueValues and OtherBlues (Adobe Technical
    /// Note #5176), else zones measured once from the face's own glyphs: the baseline (x and H, with o and O below it), the x-height
    /// (x, with o above), the cap height (H, with O above) and the descender (p).
    /// </summary>
    internal HintZone[] HintZones => Volatile.Read(ref _hintZones) ?? Publish(ref _hintZones, ComputeHintZones());

    private HintZone[] ComputeHintZones()
    {
        var zones = new List<HintZone>();
        if (_cff is not null && !_cff.IsCff2 && _instance is null && _cff.Privates.Length > 0 && CffTransform(0) is null)
        {
            CffDict dict = _cff.Privates[0].Dict;
            AddBlues(dict.Get(OpBlueValues), zones, top: true);
            AddBlues(dict.Get(OpOtherBlues), zones, top: false);
        }

        if (zones.Count == 0)
        {
            MeasureZones(zones);
        }

        zones.Sort((a, b) => a.Flat.CompareTo(b.Flat));

        // Zones sharing a flat edge keep the first; the snap is defined by the flat edges alone.
        var distinct = new List<HintZone>(zones.Count);
        foreach (HintZone zone in zones)
        {
            if (distinct.Count == 0 || distinct[^1].Flat != zone.Flat)
            {
                distinct.Add(zone);
            }
        }

        return [.. distinct];
    }

    // BlueValues and OtherBlues are delta-encoded pairs. BlueValues' first pair is the baseline zone (flat at its top), its others
    // are top zones (flat at their bottom); OtherBlues are bottom zones (flat at their top).
    private static void AddBlues(double[]? values, List<HintZone> zones, bool top)
    {
        if (values is null || values.Length < 2)
        {
            return;
        }

        double running = 0;
        var absolute = new double[values.Length & ~1];
        for (int i = 0; i < absolute.Length; i++)
        {
            running += values[i];
            absolute[i] = running;
        }

        for (int i = 0; i + 1 < absolute.Length; i += 2)
        {
            float low = (float)absolute[i], high = (float)absolute[i + 1];
            if (high < low)
            {
                continue;
            }

            bool bottomZone = !top || i == 0;
            zones.Add(bottomZone ? new HintZone(high, low) : new HintZone(low, high));
        }
    }

    private const int OpStdVW = 11;

    private float MeasureStem()
    {
        if (_cff is not null && !_cff.IsCff2 && _instance is null && _cff.Privates.Length > 0 && _cff.Privates[0].Dict.Get(OpStdVW) is { Length: > 0 } std && std[0] > 0)
        {
            return (float)std[0];
        }

        foreach (char c in "lI")
        {
            ushort glyph = GetGlyph(c);
            GlyphExtents e = glyph == 0 ? GlyphExtents.Empty : GetGlyphExtents(glyph);
            if (e.IsEmpty)
            {
                continue;
            }

            var sink = new CrossingSink((e.YMin + e.YMax) / 2);
            GetGlyphOutline(glyph, sink);
            if (sink.Narrowest() is float width)
            {
                return width;
            }
        }

        return UnitsPerEm / 12f;
    }

    /// <summary>Where an outline crosses a horizontal line, curves taken by their control polygons (exact for straight stems).</summary>
    private sealed class CrossingSink(float y) : IGlyphOutlineSink
    {
        private readonly List<float> _crossings = [];
        private float _x;
        private float _y;
        private float _startX;
        private float _startY;

        public void MoveTo(float x, float y)
        {
            Close();
            (_x, _y, _startX, _startY) = (x, y, x, y);
        }

        public void LineTo(float x, float y) => Segment(x, y);

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Segment(cx, cy);
            Segment(x, y);
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Segment(c1x, c1y);
            Segment(c2x, c2y);
            Segment(x, y);
        }

        public void Close() => Segment(_startX, _startY);

        public float? Narrowest()
        {
            _crossings.Sort();
            float? narrowest = null;
            for (int i = 0; i + 1 < _crossings.Count; i += 2)
            {
                float w = _crossings[i + 1] - _crossings[i];
                if (w > 0 && (narrowest is null || w < narrowest))
                {
                    narrowest = w;
                }
            }

            return narrowest;
        }

        private void Segment(float x, float y1)
        {
            if ((_y <= y && y1 > y) || (_y > y && y1 <= y))
            {
                _crossings.Add(_x + ((y - _y) * (x - _x) / (y1 - _y)));
            }

            (_x, _y) = (x, y1);
        }
    }

    private void MeasureZones(List<HintZone> zones)
    {
        GlyphExtents x = InkOf('x'), h = InkOf('H'), o = InkOf('o'), capO = InkOf('O'), p = InkOf('p');
        float baselineOvershoot = Math.Min(o.IsEmpty ? 0 : o.YMin, capO.IsEmpty ? 0 : capO.YMin);
        zones.Add(new HintZone(0, Math.Min(0, baselineOvershoot)));
        if (!x.IsEmpty)
        {
            zones.Add(new HintZone(x.YMax, Math.Max(x.YMax, o.IsEmpty ? x.YMax : o.YMax)));
        }

        if (!h.IsEmpty)
        {
            zones.Add(new HintZone(h.YMax, Math.Max(h.YMax, capO.IsEmpty ? h.YMax : capO.YMax)));
        }

        if (!p.IsEmpty && p.YMin < 0)
        {
            zones.Add(new HintZone(p.YMin, p.YMin));
        }

        GlyphExtents InkOf(char c)
        {
            ushort glyph = GetGlyph(c);
            return glyph == 0 ? GlyphExtents.Empty : GetGlyphExtents(glyph);
        }
    }
}
