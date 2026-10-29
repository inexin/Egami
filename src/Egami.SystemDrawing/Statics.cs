namespace Egami.SystemDrawing;

/// <summary>Solid brushes of the named web colors, one per color and thread, as System.Drawing keeps them; their color can be changed, as in System.Drawing.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public static class Brushes
{
    [ThreadStatic]
    private static Dictionary<KnownColor, Brush>? s_cache;

    /// <summary>Gets the alice blue brush.</summary>
    public static Brush AliceBlue => Get(KnownColor.AliceBlue);

    /// <summary>Gets the antique white brush.</summary>
    public static Brush AntiqueWhite => Get(KnownColor.AntiqueWhite);

    /// <summary>Gets the aqua brush.</summary>
    public static Brush Aqua => Get(KnownColor.Aqua);

    /// <summary>Gets the aquamarine brush.</summary>
    public static Brush Aquamarine => Get(KnownColor.Aquamarine);

    /// <summary>Gets the azure brush.</summary>
    public static Brush Azure => Get(KnownColor.Azure);

    /// <summary>Gets the beige brush.</summary>
    public static Brush Beige => Get(KnownColor.Beige);

    /// <summary>Gets the bisque brush.</summary>
    public static Brush Bisque => Get(KnownColor.Bisque);

    /// <summary>Gets the black brush.</summary>
    public static Brush Black => Get(KnownColor.Black);

    /// <summary>Gets the blanched almond brush.</summary>
    public static Brush BlanchedAlmond => Get(KnownColor.BlanchedAlmond);

    /// <summary>Gets the blue brush.</summary>
    public static Brush Blue => Get(KnownColor.Blue);

    /// <summary>Gets the blue violet brush.</summary>
    public static Brush BlueViolet => Get(KnownColor.BlueViolet);

    /// <summary>Gets the brown brush.</summary>
    public static Brush Brown => Get(KnownColor.Brown);

    /// <summary>Gets the burly wood brush.</summary>
    public static Brush BurlyWood => Get(KnownColor.BurlyWood);

    /// <summary>Gets the cadet blue brush.</summary>
    public static Brush CadetBlue => Get(KnownColor.CadetBlue);

    /// <summary>Gets the chartreuse brush.</summary>
    public static Brush Chartreuse => Get(KnownColor.Chartreuse);

    /// <summary>Gets the chocolate brush.</summary>
    public static Brush Chocolate => Get(KnownColor.Chocolate);

    /// <summary>Gets the coral brush.</summary>
    public static Brush Coral => Get(KnownColor.Coral);

    /// <summary>Gets the cornflower blue brush.</summary>
    public static Brush CornflowerBlue => Get(KnownColor.CornflowerBlue);

    /// <summary>Gets the cornsilk brush.</summary>
    public static Brush Cornsilk => Get(KnownColor.Cornsilk);

    /// <summary>Gets the crimson brush.</summary>
    public static Brush Crimson => Get(KnownColor.Crimson);

    /// <summary>Gets the cyan brush.</summary>
    public static Brush Cyan => Get(KnownColor.Cyan);

    /// <summary>Gets the dark blue brush.</summary>
    public static Brush DarkBlue => Get(KnownColor.DarkBlue);

    /// <summary>Gets the dark cyan brush.</summary>
    public static Brush DarkCyan => Get(KnownColor.DarkCyan);

    /// <summary>Gets the dark goldenrod brush.</summary>
    public static Brush DarkGoldenrod => Get(KnownColor.DarkGoldenrod);

    /// <summary>Gets the dark gray brush.</summary>
    public static Brush DarkGray => Get(KnownColor.DarkGray);

    /// <summary>Gets the dark green brush.</summary>
    public static Brush DarkGreen => Get(KnownColor.DarkGreen);

    /// <summary>Gets the dark khaki brush.</summary>
    public static Brush DarkKhaki => Get(KnownColor.DarkKhaki);

    /// <summary>Gets the dark magenta brush.</summary>
    public static Brush DarkMagenta => Get(KnownColor.DarkMagenta);

    /// <summary>Gets the dark olive green brush.</summary>
    public static Brush DarkOliveGreen => Get(KnownColor.DarkOliveGreen);

    /// <summary>Gets the dark orange brush.</summary>
    public static Brush DarkOrange => Get(KnownColor.DarkOrange);

    /// <summary>Gets the dark orchid brush.</summary>
    public static Brush DarkOrchid => Get(KnownColor.DarkOrchid);

    /// <summary>Gets the dark red brush.</summary>
    public static Brush DarkRed => Get(KnownColor.DarkRed);

    /// <summary>Gets the dark salmon brush.</summary>
    public static Brush DarkSalmon => Get(KnownColor.DarkSalmon);

    /// <summary>Gets the dark sea green brush.</summary>
    public static Brush DarkSeaGreen => Get(KnownColor.DarkSeaGreen);

    /// <summary>Gets the dark slate blue brush.</summary>
    public static Brush DarkSlateBlue => Get(KnownColor.DarkSlateBlue);

    /// <summary>Gets the dark slate gray brush.</summary>
    public static Brush DarkSlateGray => Get(KnownColor.DarkSlateGray);

    /// <summary>Gets the dark turquoise brush.</summary>
    public static Brush DarkTurquoise => Get(KnownColor.DarkTurquoise);

    /// <summary>Gets the dark violet brush.</summary>
    public static Brush DarkViolet => Get(KnownColor.DarkViolet);

    /// <summary>Gets the deep pink brush.</summary>
    public static Brush DeepPink => Get(KnownColor.DeepPink);

    /// <summary>Gets the deep sky blue brush.</summary>
    public static Brush DeepSkyBlue => Get(KnownColor.DeepSkyBlue);

    /// <summary>Gets the dim gray brush.</summary>
    public static Brush DimGray => Get(KnownColor.DimGray);

    /// <summary>Gets the dodger blue brush.</summary>
    public static Brush DodgerBlue => Get(KnownColor.DodgerBlue);

    /// <summary>Gets the firebrick brush.</summary>
    public static Brush Firebrick => Get(KnownColor.Firebrick);

    /// <summary>Gets the floral white brush.</summary>
    public static Brush FloralWhite => Get(KnownColor.FloralWhite);

    /// <summary>Gets the forest green brush.</summary>
    public static Brush ForestGreen => Get(KnownColor.ForestGreen);

    /// <summary>Gets the fuchsia brush.</summary>
    public static Brush Fuchsia => Get(KnownColor.Fuchsia);

    /// <summary>Gets the gainsboro brush.</summary>
    public static Brush Gainsboro => Get(KnownColor.Gainsboro);

    /// <summary>Gets the ghost white brush.</summary>
    public static Brush GhostWhite => Get(KnownColor.GhostWhite);

    /// <summary>Gets the gold brush.</summary>
    public static Brush Gold => Get(KnownColor.Gold);

    /// <summary>Gets the goldenrod brush.</summary>
    public static Brush Goldenrod => Get(KnownColor.Goldenrod);

    /// <summary>Gets the gray brush.</summary>
    public static Brush Gray => Get(KnownColor.Gray);

    /// <summary>Gets the green brush.</summary>
    public static Brush Green => Get(KnownColor.Green);

    /// <summary>Gets the green yellow brush.</summary>
    public static Brush GreenYellow => Get(KnownColor.GreenYellow);

    /// <summary>Gets the honeydew brush.</summary>
    public static Brush Honeydew => Get(KnownColor.Honeydew);

    /// <summary>Gets the hot pink brush.</summary>
    public static Brush HotPink => Get(KnownColor.HotPink);

    /// <summary>Gets the indian red brush.</summary>
    public static Brush IndianRed => Get(KnownColor.IndianRed);

    /// <summary>Gets the indigo brush.</summary>
    public static Brush Indigo => Get(KnownColor.Indigo);

    /// <summary>Gets the ivory brush.</summary>
    public static Brush Ivory => Get(KnownColor.Ivory);

    /// <summary>Gets the khaki brush.</summary>
    public static Brush Khaki => Get(KnownColor.Khaki);

    /// <summary>Gets the lavender brush.</summary>
    public static Brush Lavender => Get(KnownColor.Lavender);

    /// <summary>Gets the lavender blush brush.</summary>
    public static Brush LavenderBlush => Get(KnownColor.LavenderBlush);

    /// <summary>Gets the lawn green brush.</summary>
    public static Brush LawnGreen => Get(KnownColor.LawnGreen);

    /// <summary>Gets the lemon chiffon brush.</summary>
    public static Brush LemonChiffon => Get(KnownColor.LemonChiffon);

    /// <summary>Gets the light blue brush.</summary>
    public static Brush LightBlue => Get(KnownColor.LightBlue);

    /// <summary>Gets the light coral brush.</summary>
    public static Brush LightCoral => Get(KnownColor.LightCoral);

    /// <summary>Gets the light cyan brush.</summary>
    public static Brush LightCyan => Get(KnownColor.LightCyan);

    /// <summary>Gets the light goldenrod yellow brush.</summary>
    public static Brush LightGoldenrodYellow => Get(KnownColor.LightGoldenrodYellow);

    /// <summary>Gets the light gray brush.</summary>
    public static Brush LightGray => Get(KnownColor.LightGray);

    /// <summary>Gets the light green brush.</summary>
    public static Brush LightGreen => Get(KnownColor.LightGreen);

    /// <summary>Gets the light pink brush.</summary>
    public static Brush LightPink => Get(KnownColor.LightPink);

    /// <summary>Gets the light salmon brush.</summary>
    public static Brush LightSalmon => Get(KnownColor.LightSalmon);

    /// <summary>Gets the light sea green brush.</summary>
    public static Brush LightSeaGreen => Get(KnownColor.LightSeaGreen);

    /// <summary>Gets the light sky blue brush.</summary>
    public static Brush LightSkyBlue => Get(KnownColor.LightSkyBlue);

    /// <summary>Gets the light slate gray brush.</summary>
    public static Brush LightSlateGray => Get(KnownColor.LightSlateGray);

    /// <summary>Gets the light steel blue brush.</summary>
    public static Brush LightSteelBlue => Get(KnownColor.LightSteelBlue);

    /// <summary>Gets the light yellow brush.</summary>
    public static Brush LightYellow => Get(KnownColor.LightYellow);

    /// <summary>Gets the lime brush.</summary>
    public static Brush Lime => Get(KnownColor.Lime);

    /// <summary>Gets the lime green brush.</summary>
    public static Brush LimeGreen => Get(KnownColor.LimeGreen);

    /// <summary>Gets the linen brush.</summary>
    public static Brush Linen => Get(KnownColor.Linen);

    /// <summary>Gets the magenta brush.</summary>
    public static Brush Magenta => Get(KnownColor.Magenta);

    /// <summary>Gets the maroon brush.</summary>
    public static Brush Maroon => Get(KnownColor.Maroon);

    /// <summary>Gets the medium aquamarine brush.</summary>
    public static Brush MediumAquamarine => Get(KnownColor.MediumAquamarine);

    /// <summary>Gets the medium blue brush.</summary>
    public static Brush MediumBlue => Get(KnownColor.MediumBlue);

    /// <summary>Gets the medium orchid brush.</summary>
    public static Brush MediumOrchid => Get(KnownColor.MediumOrchid);

    /// <summary>Gets the medium purple brush.</summary>
    public static Brush MediumPurple => Get(KnownColor.MediumPurple);

    /// <summary>Gets the medium sea green brush.</summary>
    public static Brush MediumSeaGreen => Get(KnownColor.MediumSeaGreen);

    /// <summary>Gets the medium slate blue brush.</summary>
    public static Brush MediumSlateBlue => Get(KnownColor.MediumSlateBlue);

    /// <summary>Gets the medium spring green brush.</summary>
    public static Brush MediumSpringGreen => Get(KnownColor.MediumSpringGreen);

    /// <summary>Gets the medium turquoise brush.</summary>
    public static Brush MediumTurquoise => Get(KnownColor.MediumTurquoise);

    /// <summary>Gets the medium violet red brush.</summary>
    public static Brush MediumVioletRed => Get(KnownColor.MediumVioletRed);

    /// <summary>Gets the midnight blue brush.</summary>
    public static Brush MidnightBlue => Get(KnownColor.MidnightBlue);

    /// <summary>Gets the mint cream brush.</summary>
    public static Brush MintCream => Get(KnownColor.MintCream);

    /// <summary>Gets the misty rose brush.</summary>
    public static Brush MistyRose => Get(KnownColor.MistyRose);

    /// <summary>Gets the moccasin brush.</summary>
    public static Brush Moccasin => Get(KnownColor.Moccasin);

    /// <summary>Gets the navajo white brush.</summary>
    public static Brush NavajoWhite => Get(KnownColor.NavajoWhite);

    /// <summary>Gets the navy brush.</summary>
    public static Brush Navy => Get(KnownColor.Navy);

    /// <summary>Gets the old lace brush.</summary>
    public static Brush OldLace => Get(KnownColor.OldLace);

    /// <summary>Gets the olive brush.</summary>
    public static Brush Olive => Get(KnownColor.Olive);

    /// <summary>Gets the olive drab brush.</summary>
    public static Brush OliveDrab => Get(KnownColor.OliveDrab);

    /// <summary>Gets the orange brush.</summary>
    public static Brush Orange => Get(KnownColor.Orange);

    /// <summary>Gets the orange red brush.</summary>
    public static Brush OrangeRed => Get(KnownColor.OrangeRed);

    /// <summary>Gets the orchid brush.</summary>
    public static Brush Orchid => Get(KnownColor.Orchid);

    /// <summary>Gets the pale goldenrod brush.</summary>
    public static Brush PaleGoldenrod => Get(KnownColor.PaleGoldenrod);

    /// <summary>Gets the pale green brush.</summary>
    public static Brush PaleGreen => Get(KnownColor.PaleGreen);

    /// <summary>Gets the pale turquoise brush.</summary>
    public static Brush PaleTurquoise => Get(KnownColor.PaleTurquoise);

    /// <summary>Gets the pale violet red brush.</summary>
    public static Brush PaleVioletRed => Get(KnownColor.PaleVioletRed);

    /// <summary>Gets the papaya whip brush.</summary>
    public static Brush PapayaWhip => Get(KnownColor.PapayaWhip);

    /// <summary>Gets the peach puff brush.</summary>
    public static Brush PeachPuff => Get(KnownColor.PeachPuff);

    /// <summary>Gets the peru brush.</summary>
    public static Brush Peru => Get(KnownColor.Peru);

    /// <summary>Gets the pink brush.</summary>
    public static Brush Pink => Get(KnownColor.Pink);

    /// <summary>Gets the plum brush.</summary>
    public static Brush Plum => Get(KnownColor.Plum);

    /// <summary>Gets the powder blue brush.</summary>
    public static Brush PowderBlue => Get(KnownColor.PowderBlue);

    /// <summary>Gets the purple brush.</summary>
    public static Brush Purple => Get(KnownColor.Purple);

    /// <summary>Gets the red brush.</summary>
    public static Brush Red => Get(KnownColor.Red);

    /// <summary>Gets the rosy brown brush.</summary>
    public static Brush RosyBrown => Get(KnownColor.RosyBrown);

    /// <summary>Gets the royal blue brush.</summary>
    public static Brush RoyalBlue => Get(KnownColor.RoyalBlue);

    /// <summary>Gets the saddle brown brush.</summary>
    public static Brush SaddleBrown => Get(KnownColor.SaddleBrown);

    /// <summary>Gets the salmon brush.</summary>
    public static Brush Salmon => Get(KnownColor.Salmon);

    /// <summary>Gets the sandy brown brush.</summary>
    public static Brush SandyBrown => Get(KnownColor.SandyBrown);

    /// <summary>Gets the sea green brush.</summary>
    public static Brush SeaGreen => Get(KnownColor.SeaGreen);

    /// <summary>Gets the sea shell brush.</summary>
    public static Brush SeaShell => Get(KnownColor.SeaShell);

    /// <summary>Gets the sienna brush.</summary>
    public static Brush Sienna => Get(KnownColor.Sienna);

    /// <summary>Gets the silver brush.</summary>
    public static Brush Silver => Get(KnownColor.Silver);

    /// <summary>Gets the sky blue brush.</summary>
    public static Brush SkyBlue => Get(KnownColor.SkyBlue);

    /// <summary>Gets the slate blue brush.</summary>
    public static Brush SlateBlue => Get(KnownColor.SlateBlue);

    /// <summary>Gets the slate gray brush.</summary>
    public static Brush SlateGray => Get(KnownColor.SlateGray);

    /// <summary>Gets the snow brush.</summary>
    public static Brush Snow => Get(KnownColor.Snow);

    /// <summary>Gets the spring green brush.</summary>
    public static Brush SpringGreen => Get(KnownColor.SpringGreen);

    /// <summary>Gets the steel blue brush.</summary>
    public static Brush SteelBlue => Get(KnownColor.SteelBlue);

    /// <summary>Gets the tan brush.</summary>
    public static Brush Tan => Get(KnownColor.Tan);

    /// <summary>Gets the teal brush.</summary>
    public static Brush Teal => Get(KnownColor.Teal);

    /// <summary>Gets the thistle brush.</summary>
    public static Brush Thistle => Get(KnownColor.Thistle);

    /// <summary>Gets the tomato brush.</summary>
    public static Brush Tomato => Get(KnownColor.Tomato);

    /// <summary>Gets the transparent brush.</summary>
    public static Brush Transparent => Get(KnownColor.Transparent);

    /// <summary>Gets the turquoise brush.</summary>
    public static Brush Turquoise => Get(KnownColor.Turquoise);

    /// <summary>Gets the violet brush.</summary>
    public static Brush Violet => Get(KnownColor.Violet);

    /// <summary>Gets the wheat brush.</summary>
    public static Brush Wheat => Get(KnownColor.Wheat);

    /// <summary>Gets the white brush.</summary>
    public static Brush White => Get(KnownColor.White);

    /// <summary>Gets the white smoke brush.</summary>
    public static Brush WhiteSmoke => Get(KnownColor.WhiteSmoke);

    /// <summary>Gets the yellow brush.</summary>
    public static Brush Yellow => Get(KnownColor.Yellow);

    /// <summary>Gets the yellow green brush.</summary>
    public static Brush YellowGreen => Get(KnownColor.YellowGreen);

    private static Brush Get(KnownColor color)
    {
        s_cache ??= [];
        if (!s_cache.TryGetValue(color, out var value))
        {
            value = new SolidBrush(Color.FromKnownColor(color), immutable: false);
            s_cache[color] = value;
        }

        return value;
    }
}

/// <summary>Solid brushes of the system colors, which cannot be changed or disposed.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public static class SystemBrushes
{
    [ThreadStatic]
    private static Dictionary<KnownColor, Brush>? s_cache;

    /// <summary>Gets the active border brush.</summary>
    public static Brush ActiveBorder => Get(KnownColor.ActiveBorder);

    /// <summary>Gets the active caption brush.</summary>
    public static Brush ActiveCaption => Get(KnownColor.ActiveCaption);

    /// <summary>Gets the active caption text brush.</summary>
    public static Brush ActiveCaptionText => Get(KnownColor.ActiveCaptionText);

    /// <summary>Gets the app workspace brush.</summary>
    public static Brush AppWorkspace => Get(KnownColor.AppWorkspace);

    /// <summary>Gets the button face brush.</summary>
    public static Brush ButtonFace => Get(KnownColor.ButtonFace);

    /// <summary>Gets the button highlight brush.</summary>
    public static Brush ButtonHighlight => Get(KnownColor.ButtonHighlight);

    /// <summary>Gets the button shadow brush.</summary>
    public static Brush ButtonShadow => Get(KnownColor.ButtonShadow);

    /// <summary>Gets the control brush.</summary>
    public static Brush Control => Get(KnownColor.Control);

    /// <summary>Gets the control dark brush.</summary>
    public static Brush ControlDark => Get(KnownColor.ControlDark);

    /// <summary>Gets the control dark dark brush.</summary>
    public static Brush ControlDarkDark => Get(KnownColor.ControlDarkDark);

    /// <summary>Gets the control light brush.</summary>
    public static Brush ControlLight => Get(KnownColor.ControlLight);

    /// <summary>Gets the control light light brush.</summary>
    public static Brush ControlLightLight => Get(KnownColor.ControlLightLight);

    /// <summary>Gets the control text brush.</summary>
    public static Brush ControlText => Get(KnownColor.ControlText);

    /// <summary>Gets the desktop brush.</summary>
    public static Brush Desktop => Get(KnownColor.Desktop);

    /// <summary>Gets the gradient active caption brush.</summary>
    public static Brush GradientActiveCaption => Get(KnownColor.GradientActiveCaption);

    /// <summary>Gets the gradient inactive caption brush.</summary>
    public static Brush GradientInactiveCaption => Get(KnownColor.GradientInactiveCaption);

    /// <summary>Gets the gray text brush.</summary>
    public static Brush GrayText => Get(KnownColor.GrayText);

    /// <summary>Gets the highlight brush.</summary>
    public static Brush Highlight => Get(KnownColor.Highlight);

    /// <summary>Gets the highlight text brush.</summary>
    public static Brush HighlightText => Get(KnownColor.HighlightText);

    /// <summary>Gets the hot track brush.</summary>
    public static Brush HotTrack => Get(KnownColor.HotTrack);

    /// <summary>Gets the inactive border brush.</summary>
    public static Brush InactiveBorder => Get(KnownColor.InactiveBorder);

    /// <summary>Gets the inactive caption brush.</summary>
    public static Brush InactiveCaption => Get(KnownColor.InactiveCaption);

    /// <summary>Gets the inactive caption text brush.</summary>
    public static Brush InactiveCaptionText => Get(KnownColor.InactiveCaptionText);

    /// <summary>Gets the info brush.</summary>
    public static Brush Info => Get(KnownColor.Info);

    /// <summary>Gets the info text brush.</summary>
    public static Brush InfoText => Get(KnownColor.InfoText);

    /// <summary>Gets the menu brush.</summary>
    public static Brush Menu => Get(KnownColor.Menu);

    /// <summary>Gets the menu bar brush.</summary>
    public static Brush MenuBar => Get(KnownColor.MenuBar);

    /// <summary>Gets the menu highlight brush.</summary>
    public static Brush MenuHighlight => Get(KnownColor.MenuHighlight);

    /// <summary>Gets the menu text brush.</summary>
    public static Brush MenuText => Get(KnownColor.MenuText);

    /// <summary>Gets the scroll bar brush.</summary>
    public static Brush ScrollBar => Get(KnownColor.ScrollBar);

    /// <summary>Gets the window brush.</summary>
    public static Brush Window => Get(KnownColor.Window);

    /// <summary>Gets the window frame brush.</summary>
    public static Brush WindowFrame => Get(KnownColor.WindowFrame);

    /// <summary>Gets the window text brush.</summary>
    public static Brush WindowText => Get(KnownColor.WindowText);

    /// <summary>The brush of a system color.</summary>
    /// <param name="c">A system color.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">The color is not a system color.</exception>
    public static Brush FromSystemColor(Color c) => c.IsSystemColor ? Get(c.ToKnownColor()) : throw new ArgumentException($"The color {c} is not a system color.", nameof(c));

    private static Brush Get(KnownColor color)
    {
        s_cache ??= [];
        if (!s_cache.TryGetValue(color, out var value))
        {
            value = new SolidBrush(Color.FromKnownColor(color), immutable: true);
            s_cache[color] = value;
        }

        return value;
    }
}

/// <summary>One-unit pens of the named web colors, which cannot be changed or disposed.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public static class Pens
{
    [ThreadStatic]
    private static Dictionary<KnownColor, Pen>? s_cache;

    /// <summary>Gets the alice blue pen.</summary>
    public static Pen AliceBlue => Get(KnownColor.AliceBlue);

    /// <summary>Gets the antique white pen.</summary>
    public static Pen AntiqueWhite => Get(KnownColor.AntiqueWhite);

    /// <summary>Gets the aqua pen.</summary>
    public static Pen Aqua => Get(KnownColor.Aqua);

    /// <summary>Gets the aquamarine pen.</summary>
    public static Pen Aquamarine => Get(KnownColor.Aquamarine);

    /// <summary>Gets the azure pen.</summary>
    public static Pen Azure => Get(KnownColor.Azure);

    /// <summary>Gets the beige pen.</summary>
    public static Pen Beige => Get(KnownColor.Beige);

    /// <summary>Gets the bisque pen.</summary>
    public static Pen Bisque => Get(KnownColor.Bisque);

    /// <summary>Gets the black pen.</summary>
    public static Pen Black => Get(KnownColor.Black);

    /// <summary>Gets the blanched almond pen.</summary>
    public static Pen BlanchedAlmond => Get(KnownColor.BlanchedAlmond);

    /// <summary>Gets the blue pen.</summary>
    public static Pen Blue => Get(KnownColor.Blue);

    /// <summary>Gets the blue violet pen.</summary>
    public static Pen BlueViolet => Get(KnownColor.BlueViolet);

    /// <summary>Gets the brown pen.</summary>
    public static Pen Brown => Get(KnownColor.Brown);

    /// <summary>Gets the burly wood pen.</summary>
    public static Pen BurlyWood => Get(KnownColor.BurlyWood);

    /// <summary>Gets the cadet blue pen.</summary>
    public static Pen CadetBlue => Get(KnownColor.CadetBlue);

    /// <summary>Gets the chartreuse pen.</summary>
    public static Pen Chartreuse => Get(KnownColor.Chartreuse);

    /// <summary>Gets the chocolate pen.</summary>
    public static Pen Chocolate => Get(KnownColor.Chocolate);

    /// <summary>Gets the coral pen.</summary>
    public static Pen Coral => Get(KnownColor.Coral);

    /// <summary>Gets the cornflower blue pen.</summary>
    public static Pen CornflowerBlue => Get(KnownColor.CornflowerBlue);

    /// <summary>Gets the cornsilk pen.</summary>
    public static Pen Cornsilk => Get(KnownColor.Cornsilk);

    /// <summary>Gets the crimson pen.</summary>
    public static Pen Crimson => Get(KnownColor.Crimson);

    /// <summary>Gets the cyan pen.</summary>
    public static Pen Cyan => Get(KnownColor.Cyan);

    /// <summary>Gets the dark blue pen.</summary>
    public static Pen DarkBlue => Get(KnownColor.DarkBlue);

    /// <summary>Gets the dark cyan pen.</summary>
    public static Pen DarkCyan => Get(KnownColor.DarkCyan);

    /// <summary>Gets the dark goldenrod pen.</summary>
    public static Pen DarkGoldenrod => Get(KnownColor.DarkGoldenrod);

    /// <summary>Gets the dark gray pen.</summary>
    public static Pen DarkGray => Get(KnownColor.DarkGray);

    /// <summary>Gets the dark green pen.</summary>
    public static Pen DarkGreen => Get(KnownColor.DarkGreen);

    /// <summary>Gets the dark khaki pen.</summary>
    public static Pen DarkKhaki => Get(KnownColor.DarkKhaki);

    /// <summary>Gets the dark magenta pen.</summary>
    public static Pen DarkMagenta => Get(KnownColor.DarkMagenta);

    /// <summary>Gets the dark olive green pen.</summary>
    public static Pen DarkOliveGreen => Get(KnownColor.DarkOliveGreen);

    /// <summary>Gets the dark orange pen.</summary>
    public static Pen DarkOrange => Get(KnownColor.DarkOrange);

    /// <summary>Gets the dark orchid pen.</summary>
    public static Pen DarkOrchid => Get(KnownColor.DarkOrchid);

    /// <summary>Gets the dark red pen.</summary>
    public static Pen DarkRed => Get(KnownColor.DarkRed);

    /// <summary>Gets the dark salmon pen.</summary>
    public static Pen DarkSalmon => Get(KnownColor.DarkSalmon);

    /// <summary>Gets the dark sea green pen.</summary>
    public static Pen DarkSeaGreen => Get(KnownColor.DarkSeaGreen);

    /// <summary>Gets the dark slate blue pen.</summary>
    public static Pen DarkSlateBlue => Get(KnownColor.DarkSlateBlue);

    /// <summary>Gets the dark slate gray pen.</summary>
    public static Pen DarkSlateGray => Get(KnownColor.DarkSlateGray);

    /// <summary>Gets the dark turquoise pen.</summary>
    public static Pen DarkTurquoise => Get(KnownColor.DarkTurquoise);

    /// <summary>Gets the dark violet pen.</summary>
    public static Pen DarkViolet => Get(KnownColor.DarkViolet);

    /// <summary>Gets the deep pink pen.</summary>
    public static Pen DeepPink => Get(KnownColor.DeepPink);

    /// <summary>Gets the deep sky blue pen.</summary>
    public static Pen DeepSkyBlue => Get(KnownColor.DeepSkyBlue);

    /// <summary>Gets the dim gray pen.</summary>
    public static Pen DimGray => Get(KnownColor.DimGray);

    /// <summary>Gets the dodger blue pen.</summary>
    public static Pen DodgerBlue => Get(KnownColor.DodgerBlue);

    /// <summary>Gets the firebrick pen.</summary>
    public static Pen Firebrick => Get(KnownColor.Firebrick);

    /// <summary>Gets the floral white pen.</summary>
    public static Pen FloralWhite => Get(KnownColor.FloralWhite);

    /// <summary>Gets the forest green pen.</summary>
    public static Pen ForestGreen => Get(KnownColor.ForestGreen);

    /// <summary>Gets the fuchsia pen.</summary>
    public static Pen Fuchsia => Get(KnownColor.Fuchsia);

    /// <summary>Gets the gainsboro pen.</summary>
    public static Pen Gainsboro => Get(KnownColor.Gainsboro);

    /// <summary>Gets the ghost white pen.</summary>
    public static Pen GhostWhite => Get(KnownColor.GhostWhite);

    /// <summary>Gets the gold pen.</summary>
    public static Pen Gold => Get(KnownColor.Gold);

    /// <summary>Gets the goldenrod pen.</summary>
    public static Pen Goldenrod => Get(KnownColor.Goldenrod);

    /// <summary>Gets the gray pen.</summary>
    public static Pen Gray => Get(KnownColor.Gray);

    /// <summary>Gets the green pen.</summary>
    public static Pen Green => Get(KnownColor.Green);

    /// <summary>Gets the green yellow pen.</summary>
    public static Pen GreenYellow => Get(KnownColor.GreenYellow);

    /// <summary>Gets the honeydew pen.</summary>
    public static Pen Honeydew => Get(KnownColor.Honeydew);

    /// <summary>Gets the hot pink pen.</summary>
    public static Pen HotPink => Get(KnownColor.HotPink);

    /// <summary>Gets the indian red pen.</summary>
    public static Pen IndianRed => Get(KnownColor.IndianRed);

    /// <summary>Gets the indigo pen.</summary>
    public static Pen Indigo => Get(KnownColor.Indigo);

    /// <summary>Gets the ivory pen.</summary>
    public static Pen Ivory => Get(KnownColor.Ivory);

    /// <summary>Gets the khaki pen.</summary>
    public static Pen Khaki => Get(KnownColor.Khaki);

    /// <summary>Gets the lavender pen.</summary>
    public static Pen Lavender => Get(KnownColor.Lavender);

    /// <summary>Gets the lavender blush pen.</summary>
    public static Pen LavenderBlush => Get(KnownColor.LavenderBlush);

    /// <summary>Gets the lawn green pen.</summary>
    public static Pen LawnGreen => Get(KnownColor.LawnGreen);

    /// <summary>Gets the lemon chiffon pen.</summary>
    public static Pen LemonChiffon => Get(KnownColor.LemonChiffon);

    /// <summary>Gets the light blue pen.</summary>
    public static Pen LightBlue => Get(KnownColor.LightBlue);

    /// <summary>Gets the light coral pen.</summary>
    public static Pen LightCoral => Get(KnownColor.LightCoral);

    /// <summary>Gets the light cyan pen.</summary>
    public static Pen LightCyan => Get(KnownColor.LightCyan);

    /// <summary>Gets the light goldenrod yellow pen.</summary>
    public static Pen LightGoldenrodYellow => Get(KnownColor.LightGoldenrodYellow);

    /// <summary>Gets the light gray pen.</summary>
    public static Pen LightGray => Get(KnownColor.LightGray);

    /// <summary>Gets the light green pen.</summary>
    public static Pen LightGreen => Get(KnownColor.LightGreen);

    /// <summary>Gets the light pink pen.</summary>
    public static Pen LightPink => Get(KnownColor.LightPink);

    /// <summary>Gets the light salmon pen.</summary>
    public static Pen LightSalmon => Get(KnownColor.LightSalmon);

    /// <summary>Gets the light sea green pen.</summary>
    public static Pen LightSeaGreen => Get(KnownColor.LightSeaGreen);

    /// <summary>Gets the light sky blue pen.</summary>
    public static Pen LightSkyBlue => Get(KnownColor.LightSkyBlue);

    /// <summary>Gets the light slate gray pen.</summary>
    public static Pen LightSlateGray => Get(KnownColor.LightSlateGray);

    /// <summary>Gets the light steel blue pen.</summary>
    public static Pen LightSteelBlue => Get(KnownColor.LightSteelBlue);

    /// <summary>Gets the light yellow pen.</summary>
    public static Pen LightYellow => Get(KnownColor.LightYellow);

    /// <summary>Gets the lime pen.</summary>
    public static Pen Lime => Get(KnownColor.Lime);

    /// <summary>Gets the lime green pen.</summary>
    public static Pen LimeGreen => Get(KnownColor.LimeGreen);

    /// <summary>Gets the linen pen.</summary>
    public static Pen Linen => Get(KnownColor.Linen);

    /// <summary>Gets the magenta pen.</summary>
    public static Pen Magenta => Get(KnownColor.Magenta);

    /// <summary>Gets the maroon pen.</summary>
    public static Pen Maroon => Get(KnownColor.Maroon);

    /// <summary>Gets the medium aquamarine pen.</summary>
    public static Pen MediumAquamarine => Get(KnownColor.MediumAquamarine);

    /// <summary>Gets the medium blue pen.</summary>
    public static Pen MediumBlue => Get(KnownColor.MediumBlue);

    /// <summary>Gets the medium orchid pen.</summary>
    public static Pen MediumOrchid => Get(KnownColor.MediumOrchid);

    /// <summary>Gets the medium purple pen.</summary>
    public static Pen MediumPurple => Get(KnownColor.MediumPurple);

    /// <summary>Gets the medium sea green pen.</summary>
    public static Pen MediumSeaGreen => Get(KnownColor.MediumSeaGreen);

    /// <summary>Gets the medium slate blue pen.</summary>
    public static Pen MediumSlateBlue => Get(KnownColor.MediumSlateBlue);

    /// <summary>Gets the medium spring green pen.</summary>
    public static Pen MediumSpringGreen => Get(KnownColor.MediumSpringGreen);

    /// <summary>Gets the medium turquoise pen.</summary>
    public static Pen MediumTurquoise => Get(KnownColor.MediumTurquoise);

    /// <summary>Gets the medium violet red pen.</summary>
    public static Pen MediumVioletRed => Get(KnownColor.MediumVioletRed);

    /// <summary>Gets the midnight blue pen.</summary>
    public static Pen MidnightBlue => Get(KnownColor.MidnightBlue);

    /// <summary>Gets the mint cream pen.</summary>
    public static Pen MintCream => Get(KnownColor.MintCream);

    /// <summary>Gets the misty rose pen.</summary>
    public static Pen MistyRose => Get(KnownColor.MistyRose);

    /// <summary>Gets the moccasin pen.</summary>
    public static Pen Moccasin => Get(KnownColor.Moccasin);

    /// <summary>Gets the navajo white pen.</summary>
    public static Pen NavajoWhite => Get(KnownColor.NavajoWhite);

    /// <summary>Gets the navy pen.</summary>
    public static Pen Navy => Get(KnownColor.Navy);

    /// <summary>Gets the old lace pen.</summary>
    public static Pen OldLace => Get(KnownColor.OldLace);

    /// <summary>Gets the olive pen.</summary>
    public static Pen Olive => Get(KnownColor.Olive);

    /// <summary>Gets the olive drab pen.</summary>
    public static Pen OliveDrab => Get(KnownColor.OliveDrab);

    /// <summary>Gets the orange pen.</summary>
    public static Pen Orange => Get(KnownColor.Orange);

    /// <summary>Gets the orange red pen.</summary>
    public static Pen OrangeRed => Get(KnownColor.OrangeRed);

    /// <summary>Gets the orchid pen.</summary>
    public static Pen Orchid => Get(KnownColor.Orchid);

    /// <summary>Gets the pale goldenrod pen.</summary>
    public static Pen PaleGoldenrod => Get(KnownColor.PaleGoldenrod);

    /// <summary>Gets the pale green pen.</summary>
    public static Pen PaleGreen => Get(KnownColor.PaleGreen);

    /// <summary>Gets the pale turquoise pen.</summary>
    public static Pen PaleTurquoise => Get(KnownColor.PaleTurquoise);

    /// <summary>Gets the pale violet red pen.</summary>
    public static Pen PaleVioletRed => Get(KnownColor.PaleVioletRed);

    /// <summary>Gets the papaya whip pen.</summary>
    public static Pen PapayaWhip => Get(KnownColor.PapayaWhip);

    /// <summary>Gets the peach puff pen.</summary>
    public static Pen PeachPuff => Get(KnownColor.PeachPuff);

    /// <summary>Gets the peru pen.</summary>
    public static Pen Peru => Get(KnownColor.Peru);

    /// <summary>Gets the pink pen.</summary>
    public static Pen Pink => Get(KnownColor.Pink);

    /// <summary>Gets the plum pen.</summary>
    public static Pen Plum => Get(KnownColor.Plum);

    /// <summary>Gets the powder blue pen.</summary>
    public static Pen PowderBlue => Get(KnownColor.PowderBlue);

    /// <summary>Gets the purple pen.</summary>
    public static Pen Purple => Get(KnownColor.Purple);

    /// <summary>Gets the red pen.</summary>
    public static Pen Red => Get(KnownColor.Red);

    /// <summary>Gets the rosy brown pen.</summary>
    public static Pen RosyBrown => Get(KnownColor.RosyBrown);

    /// <summary>Gets the royal blue pen.</summary>
    public static Pen RoyalBlue => Get(KnownColor.RoyalBlue);

    /// <summary>Gets the saddle brown pen.</summary>
    public static Pen SaddleBrown => Get(KnownColor.SaddleBrown);

    /// <summary>Gets the salmon pen.</summary>
    public static Pen Salmon => Get(KnownColor.Salmon);

    /// <summary>Gets the sandy brown pen.</summary>
    public static Pen SandyBrown => Get(KnownColor.SandyBrown);

    /// <summary>Gets the sea green pen.</summary>
    public static Pen SeaGreen => Get(KnownColor.SeaGreen);

    /// <summary>Gets the sea shell pen.</summary>
    public static Pen SeaShell => Get(KnownColor.SeaShell);

    /// <summary>Gets the sienna pen.</summary>
    public static Pen Sienna => Get(KnownColor.Sienna);

    /// <summary>Gets the silver pen.</summary>
    public static Pen Silver => Get(KnownColor.Silver);

    /// <summary>Gets the sky blue pen.</summary>
    public static Pen SkyBlue => Get(KnownColor.SkyBlue);

    /// <summary>Gets the slate blue pen.</summary>
    public static Pen SlateBlue => Get(KnownColor.SlateBlue);

    /// <summary>Gets the slate gray pen.</summary>
    public static Pen SlateGray => Get(KnownColor.SlateGray);

    /// <summary>Gets the snow pen.</summary>
    public static Pen Snow => Get(KnownColor.Snow);

    /// <summary>Gets the spring green pen.</summary>
    public static Pen SpringGreen => Get(KnownColor.SpringGreen);

    /// <summary>Gets the steel blue pen.</summary>
    public static Pen SteelBlue => Get(KnownColor.SteelBlue);

    /// <summary>Gets the tan pen.</summary>
    public static Pen Tan => Get(KnownColor.Tan);

    /// <summary>Gets the teal pen.</summary>
    public static Pen Teal => Get(KnownColor.Teal);

    /// <summary>Gets the thistle pen.</summary>
    public static Pen Thistle => Get(KnownColor.Thistle);

    /// <summary>Gets the tomato pen.</summary>
    public static Pen Tomato => Get(KnownColor.Tomato);

    /// <summary>Gets the transparent pen.</summary>
    public static Pen Transparent => Get(KnownColor.Transparent);

    /// <summary>Gets the turquoise pen.</summary>
    public static Pen Turquoise => Get(KnownColor.Turquoise);

    /// <summary>Gets the violet pen.</summary>
    public static Pen Violet => Get(KnownColor.Violet);

    /// <summary>Gets the wheat pen.</summary>
    public static Pen Wheat => Get(KnownColor.Wheat);

    /// <summary>Gets the white pen.</summary>
    public static Pen White => Get(KnownColor.White);

    /// <summary>Gets the white smoke pen.</summary>
    public static Pen WhiteSmoke => Get(KnownColor.WhiteSmoke);

    /// <summary>Gets the yellow pen.</summary>
    public static Pen Yellow => Get(KnownColor.Yellow);

    /// <summary>Gets the yellow green pen.</summary>
    public static Pen YellowGreen => Get(KnownColor.YellowGreen);

    private static Pen Get(KnownColor color)
    {
        s_cache ??= [];
        if (!s_cache.TryGetValue(color, out var value))
        {
            value = new Pen(Color.FromKnownColor(color)) { Immutable = true };
            s_cache[color] = value;
        }

        return value;
    }
}

/// <summary>One-unit pens of the system colors, which cannot be changed or disposed.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public static class SystemPens
{
    [ThreadStatic]
    private static Dictionary<KnownColor, Pen>? s_cache;

    /// <summary>Gets the active border pen.</summary>
    public static Pen ActiveBorder => Get(KnownColor.ActiveBorder);

    /// <summary>Gets the active caption pen.</summary>
    public static Pen ActiveCaption => Get(KnownColor.ActiveCaption);

    /// <summary>Gets the active caption text pen.</summary>
    public static Pen ActiveCaptionText => Get(KnownColor.ActiveCaptionText);

    /// <summary>Gets the app workspace pen.</summary>
    public static Pen AppWorkspace => Get(KnownColor.AppWorkspace);

    /// <summary>Gets the button face pen.</summary>
    public static Pen ButtonFace => Get(KnownColor.ButtonFace);

    /// <summary>Gets the button highlight pen.</summary>
    public static Pen ButtonHighlight => Get(KnownColor.ButtonHighlight);

    /// <summary>Gets the button shadow pen.</summary>
    public static Pen ButtonShadow => Get(KnownColor.ButtonShadow);

    /// <summary>Gets the control pen.</summary>
    public static Pen Control => Get(KnownColor.Control);

    /// <summary>Gets the control dark pen.</summary>
    public static Pen ControlDark => Get(KnownColor.ControlDark);

    /// <summary>Gets the control dark dark pen.</summary>
    public static Pen ControlDarkDark => Get(KnownColor.ControlDarkDark);

    /// <summary>Gets the control light pen.</summary>
    public static Pen ControlLight => Get(KnownColor.ControlLight);

    /// <summary>Gets the control light light pen.</summary>
    public static Pen ControlLightLight => Get(KnownColor.ControlLightLight);

    /// <summary>Gets the control text pen.</summary>
    public static Pen ControlText => Get(KnownColor.ControlText);

    /// <summary>Gets the desktop pen.</summary>
    public static Pen Desktop => Get(KnownColor.Desktop);

    /// <summary>Gets the gradient active caption pen.</summary>
    public static Pen GradientActiveCaption => Get(KnownColor.GradientActiveCaption);

    /// <summary>Gets the gradient inactive caption pen.</summary>
    public static Pen GradientInactiveCaption => Get(KnownColor.GradientInactiveCaption);

    /// <summary>Gets the gray text pen.</summary>
    public static Pen GrayText => Get(KnownColor.GrayText);

    /// <summary>Gets the highlight pen.</summary>
    public static Pen Highlight => Get(KnownColor.Highlight);

    /// <summary>Gets the highlight text pen.</summary>
    public static Pen HighlightText => Get(KnownColor.HighlightText);

    /// <summary>Gets the hot track pen.</summary>
    public static Pen HotTrack => Get(KnownColor.HotTrack);

    /// <summary>Gets the inactive border pen.</summary>
    public static Pen InactiveBorder => Get(KnownColor.InactiveBorder);

    /// <summary>Gets the inactive caption pen.</summary>
    public static Pen InactiveCaption => Get(KnownColor.InactiveCaption);

    /// <summary>Gets the inactive caption text pen.</summary>
    public static Pen InactiveCaptionText => Get(KnownColor.InactiveCaptionText);

    /// <summary>Gets the info pen.</summary>
    public static Pen Info => Get(KnownColor.Info);

    /// <summary>Gets the info text pen.</summary>
    public static Pen InfoText => Get(KnownColor.InfoText);

    /// <summary>Gets the menu pen.</summary>
    public static Pen Menu => Get(KnownColor.Menu);

    /// <summary>Gets the menu bar pen.</summary>
    public static Pen MenuBar => Get(KnownColor.MenuBar);

    /// <summary>Gets the menu highlight pen.</summary>
    public static Pen MenuHighlight => Get(KnownColor.MenuHighlight);

    /// <summary>Gets the menu text pen.</summary>
    public static Pen MenuText => Get(KnownColor.MenuText);

    /// <summary>Gets the scroll bar pen.</summary>
    public static Pen ScrollBar => Get(KnownColor.ScrollBar);

    /// <summary>Gets the window pen.</summary>
    public static Pen Window => Get(KnownColor.Window);

    /// <summary>Gets the window frame pen.</summary>
    public static Pen WindowFrame => Get(KnownColor.WindowFrame);

    /// <summary>Gets the window text pen.</summary>
    public static Pen WindowText => Get(KnownColor.WindowText);

    /// <summary>The pen of a system color.</summary>
    /// <param name="c">A system color.</param>
    /// <returns>The pen.</returns>
    /// <exception cref="ArgumentException">The color is not a system color.</exception>
    public static Pen FromSystemColor(Color c) => c.IsSystemColor ? Get(c.ToKnownColor()) : throw new ArgumentException($"The color {c} is not a system color.", nameof(c));

    private static Pen Get(KnownColor color)
    {
        s_cache ??= [];
        if (!s_cache.TryGetValue(color, out var value))
        {
            value = new Pen(Color.FromKnownColor(color)) { Immutable = true };
            s_cache[color] = value;
        }

        return value;
    }
}
