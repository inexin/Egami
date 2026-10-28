namespace Egami.GdiPlus;

/// <summary>
/// A GDI+ string format, as [MS-EMFPLUS] 2.2.1.9 serializes it and System.Drawing's StringFormat exposes it: flags, alignment,
/// line alignment, hotkey prefix, margins, tracking, trimming and tab stops.
/// </summary>
internal sealed class GdiPlusStringFormat
{
    /// <summary>StringFormatFlagsDirectionRightToLeft.</summary>
    public const int DirectionRightToLeft = 0x0001;

    /// <summary>StringFormatFlagsDirectionVertical.</summary>
    public const int DirectionVertical = 0x0002;

    /// <summary>StringFormatFlagsNoFitBlackBox.</summary>
    public const int NoFitBlackBox = 0x0004;

    /// <summary>StringFormatFlagsDisplayFormatControl.</summary>
    public const int DisplayFormatControl = 0x0020;

    /// <summary>StringFormatFlagsNoFontFallback.</summary>
    public const int NoFontFallback = 0x0400;

    /// <summary>StringFormatFlagsMeasureTrailingSpaces.</summary>
    public const int MeasureTrailingSpaces = 0x0800;

    /// <summary>StringFormatFlagsNoWrap.</summary>
    public const int NoWrap = 0x1000;

    /// <summary>StringFormatFlagsLineLimit.</summary>
    public const int LineLimit = 0x2000;

    /// <summary>StringFormatFlagsNoClip.</summary>
    public const int NoClip = 0x4000;

    /// <summary>StringAlignmentNear (and line alignment near).</summary>
    public const int AlignNear = 0;

    /// <summary>StringAlignmentCenter.</summary>
    public const int AlignCenter = 1;

    /// <summary>StringAlignmentFar.</summary>
    public const int AlignFar = 2;

    /// <summary>HotkeyPrefixNone: an ampersand is drawn as it is.</summary>
    public const int HotkeyNone = 0;

    /// <summary>HotkeyPrefixShow: an ampersand underlines the next character and is not drawn.</summary>
    public const int HotkeyShow = 1;

    /// <summary>HotkeyPrefixHide: an ampersand is not drawn and underlines nothing.</summary>
    public const int HotkeyHide = 2;

    /// <summary>StringTrimmingNone.</summary>
    public const int TrimNone = 0;

    /// <summary>StringTrimmingCharacter.</summary>
    public const int TrimCharacter = 1;

    /// <summary>StringTrimmingWord.</summary>
    public const int TrimWord = 2;

    /// <summary>StringTrimmingEllipsisCharacter.</summary>
    public const int TrimEllipsisCharacter = 3;

    /// <summary>StringTrimmingEllipsisWord.</summary>
    public const int TrimEllipsisWord = 4;

    /// <summary>StringTrimmingEllipsisPath.</summary>
    public const int TrimEllipsisPath = 5;

    public int Flags { get; init; }

    public int Alignment { get; init; }

    public int LineAlignment { get; init; }

    public int HotkeyPrefix { get; init; }

    public float LeadingMargin { get; init; }

    public float TrailingMargin { get; init; }

    public float Tracking { get; init; } = 1;

    public int Trimming { get; init; }

    public float FirstTabOffset { get; init; }

    /// <summary>Gets the tab stops: intervals, each from the stop before it, the first from <see cref="FirstTabOffset"/>.</summary>
    public float[] TabStops { get; init; } = [];

    /// <summary>Gets a value indicating whether trimming ends a cut line with an ellipsis.</summary>
    public bool Ellipsis => Trimming is TrimEllipsisCharacter or TrimEllipsisWord or TrimEllipsisPath;

    /// <summary>
    /// The text to draw after the hotkey prefix: Show and Hide both drop the ampersand marks ("&amp;&amp;" stays one
    /// ampersand); None keeps them.
    /// </summary>
    public string DisplayText(string text) => HotkeyPrefix switch
    {
        HotkeyShow or HotkeyHide => text.Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&'),
        _ => text,
    };

    /// <summary>The tab stops as distances from the start of the line: the first offset, then each interval from the last stop.</summary>
    public float[] TabPositions()
    {
        float[] positions = new float[TabStops.Length];
        float at = FirstTabOffset;
        for (int i = 0; i < TabStops.Length; i++)
        {
            at += TabStops[i];
            positions[i] = at;
        }

        return positions;
    }
}
