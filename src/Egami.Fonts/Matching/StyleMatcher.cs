namespace Egami.Fonts.Matching;

/// <summary>The CSS Fonts Level 4 font matching steps (section 5.2): width, then slant, then weight.</summary>
internal static class StyleMatcher
{
    /// <summary>Returns the index of the best face for <paramref name="wanted"/>, or -1 when there are none.</summary>
    public static int Match(ReadOnlySpan<FontStyle> styles, FontStyle wanted)
    {
        int n = styles.Length;
        if (n == 0)
        {
            return -1;
        }

        Span<bool> alive = n <= 256 ? stackalloc bool[n] : new bool[n];
        alive.Fill(true);

        // Width: the nearest at or below normal-or-narrower requests, else above; the other side after.
        int width = wanted.ClampedWidth, best = -1;
        bool preferLower = width <= 5;
        for (int i = 0; i < n; i++)
        {
            int w = styles[i].ClampedWidth;
            if (best < 0 || Better(w, best, width, preferLower))
            {
                best = w;
            }
        }

        for (int i = 0; i < n; i++)
        {
            alive[i] = styles[i].ClampedWidth == best;
        }

        // Slant: italic falls back to oblique then upright; oblique to italic then upright; upright to oblique then italic.
        ReadOnlySpan<FontSlant> order = wanted.Slant switch
        {
            FontSlant.Italic => [FontSlant.Italic, FontSlant.Oblique, FontSlant.Upright],
            FontSlant.Oblique => [FontSlant.Oblique, FontSlant.Italic, FontSlant.Upright],
            _ => [FontSlant.Upright, FontSlant.Oblique, FontSlant.Italic],
        };
        foreach (FontSlant slant in order)
        {
            bool any = false;
            for (int i = 0; i < n && !any; i++)
            {
                any = alive[i] && styles[i].Slant == slant;
            }

            if (any)
            {
                for (int i = 0; i < n; i++)
                {
                    alive[i] &= styles[i].Slant == slant;
                }

                break;
            }
        }

        // Weight.
        Span<int> weights = n <= 256 ? stackalloc int[n] : new int[n];
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (alive[i])
            {
                weights[count++] = styles[i].ClampedWeight;
            }
        }

        int bestWeight = BestWeight(weights[..count], wanted.ClampedWeight);
        for (int i = 0; i < n; i++)
        {
            if (alive[i] && styles[i].ClampedWeight == bestWeight)
            {
                return i;
            }
        }

        return -1;
    }

    // Whether width a beats width b for the wanted width: exact first, then the preferred side nearest first, then the other side.
    private static bool Better(int a, int b, int wanted, bool preferLower)
    {
        int Rank(int v) => v == wanted ? 0 : (v < wanted) == preferLower ? 1 : 2;
        int ra = Rank(a), rb = Rank(b);
        return ra != rb ? ra < rb : Math.Abs(a - wanted) < Math.Abs(b - wanted);
    }

    // CSS font-stretch percentages of the width classes 1 to 9.
    private static readonly float[] s_widthPercent = [50, 62.5f, 75, 87.5f, 100, 112.5f, 125, 150, 200];

    /// <summary>Gets the CSS width percentage of a width class 1 to 9.</summary>
    public static float WidthPercentOf(int widthClass) => s_widthPercent[Math.Clamp(widthClass, 1, 9) - 1];

    /// <summary>Gets the width class nearest a wdth percentage.</summary>
    public static int WidthClassOf(float percent)
    {
        int best = 5;
        for (int c = 1; c <= 9; c++)
        {
            if (MathF.Abs(s_widthPercent[c - 1] - percent) < MathF.Abs(s_widthPercent[best - 1] - percent))
            {
                best = c;
            }
        }

        return best;
    }

    /// <summary>Says which synthesis a matched style needs to look like the wanted one.</summary>
    public static FontSynthesis SynthesisFor(FontStyle wanted, FontStyle matched)
    {
        FontSynthesis synthesis = FontSynthesis.None;
        if (wanted.ClampedWeight >= 600 && matched.ClampedWeight <= 500)
        {
            synthesis |= FontSynthesis.Bold;
        }

        if (wanted.Slant != FontSlant.Upright && matched.Slant == FontSlant.Upright)
        {
            synthesis |= FontSynthesis.Italic;
        }

        return synthesis;
    }

    internal static int BestWeight(ReadOnlySpan<int> weights, int wanted)
    {
        if (weights.Contains(wanted))
        {
            return wanted;
        }

        if (wanted is >= 400 and <= 500)
        {
            // Heavier up to 500, then lighter, then heavier than 500.
            int? up = Nearest(weights, w => w > wanted && w <= 500, ascending: true);
            if (up is int u)
            {
                return u;
            }

            int? down = Nearest(weights, w => w < wanted, ascending: false);
            if (down is int d)
            {
                return d;
            }

            return Nearest(weights, w => w > 500, ascending: true) ?? weights[0];
        }

        if (wanted < 400)
        {
            return Nearest(weights, w => w < wanted, ascending: false) ?? Nearest(weights, w => w > wanted, ascending: true) ?? weights[0];
        }

        return Nearest(weights, w => w > wanted, ascending: true) ?? Nearest(weights, w => w < wanted, ascending: false) ?? weights[0];
    }

    // The value nearest to the wanted one among those passing the filter: the smallest when ascending, else the largest.
    private static int? Nearest(ReadOnlySpan<int> values, Func<int, bool> filter, bool ascending)
    {
        int? best = null;
        foreach (int v in values)
        {
            if (filter(v) && (best is null || (ascending ? v < best : v > best)))
            {
                best = v;
            }
        }

        return best;
    }
}
