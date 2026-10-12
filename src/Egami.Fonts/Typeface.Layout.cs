using Egami.Fonts.Shaping;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    /// <summary>Gets what the face's OpenType layout tables offer: scripts, language systems and features.</summary>
    public OpenTypeLayout Layout => new(OpenTypeLayoutTables.For(this));
}
