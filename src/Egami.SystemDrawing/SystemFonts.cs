namespace Egami.SystemDrawing;

/// <summary>
/// The fonts Windows uses for its own interface, with Windows 10 and 11's defaults: Segoe UI 9 for captions, menus, message boxes,
/// icon titles and the status bar, Microsoft Sans Serif 8.25 for <see cref="DefaultFont"/> and Tahoma 8 for dialogs.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public static class SystemFonts
{
    /// <summary>Gets the font of window title bars.</summary>
    public static Font CaptionFont => Ui(nameof(CaptionFont));

    /// <summary>Gets the font of small window title bars.</summary>
    public static Font SmallCaptionFont => Ui(nameof(SmallCaptionFont));

    /// <summary>Gets the font of menus.</summary>
    public static Font MenuFont => Ui(nameof(MenuFont));

    /// <summary>Gets the font of status bars.</summary>
    public static Font StatusFont => Ui(nameof(StatusFont));

    /// <summary>Gets the font of message boxes.</summary>
    public static Font MessageBoxFont => Ui(nameof(MessageBoxFont));

    /// <summary>Gets the font of icon titles.</summary>
    public static Font IconTitleFont => Ui(nameof(IconTitleFont));

    /// <summary>Gets the default font of dialogs and controls: Microsoft Sans Serif 8.25 points, character set 0.</summary>
    public static Font DefaultFont => new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 0).AsSystemFont(nameof(DefaultFont));

    /// <summary>Gets the font of dialogs: Tahoma 8 points, or <see cref="DefaultFont"/> where Tahoma is not installed.</summary>
    public static Font DialogFont
    {
        get
        {
            Font tahoma = new("Tahoma", 8, FontStyle.Regular, GraphicsUnit.Point, 0);
            return tahoma.Name == "Tahoma" ? tahoma.AsSystemFont(nameof(DialogFont)) : DefaultFont;
        }
    }

    /// <summary>Gets a system font by its property name, such as "MenuFont".</summary>
    /// <param name="systemFontName">The name.</param>
    /// <returns>The font, or <see langword="null"/> for a name that is not one of the properties.</returns>
    public static Font? GetFontByName(string systemFontName) => systemFontName switch
    {
        nameof(CaptionFont) => CaptionFont,
        nameof(SmallCaptionFont) => SmallCaptionFont,
        nameof(MenuFont) => MenuFont,
        nameof(StatusFont) => StatusFont,
        nameof(MessageBoxFont) => MessageBoxFont,
        nameof(IconTitleFont) => IconTitleFont,
        nameof(DefaultFont) => DefaultFont,
        nameof(DialogFont) => DialogFont,
        _ => null,
    };

    private static Font Ui(string name) => new Font("Segoe UI", 9).AsSystemFont(name);
}
