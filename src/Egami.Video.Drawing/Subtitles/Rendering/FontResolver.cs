using Egami.Fonts;
using Egami.Fonts.Shaping;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// Finds the families styles name: in the app's fonts, then the font folders, then the system's fonts; a family found nowhere
/// is reported once and replaced by the fallback fonts' family of that name, their first family, or the system's sans-serif.
/// </summary>
internal sealed class FontResolver
{
    private readonly List<FontCollection> _collections = [];
    private readonly FontCollection? _fallback;
    private readonly MediaContext _context;
    private readonly Dictionary<string, FontFamily?> _families = new(StringComparer.OrdinalIgnoreCase);
    private FontFamily? _default;
    private bool _defaultResolved;

    public FontResolver(SubtitleRenderOptions options, MediaContext context)
    {
        _context = context;
        if (options.Fonts is not null)
        {
            _collections.Add(options.Fonts);
        }

        if (options.FontFolders.Count > 0)
        {
            var folders = new FontCollection();
            foreach (string folder in options.FontFolders)
            {
                if (Directory.Exists(folder))
                {
                    folders.AddDirectory(folder);
                }
                else
                {
                    context.Warnings?.Invoke(new MediaWarning("subtitle-font-folder", $"The font folder \"{folder}\" does not exist."));
                }
            }

            _collections.Add(folders);
        }

        if (options.UseSystemFonts)
        {
            _collections.Add(FontCollection.System);
        }

        _fallback = options.Fallback;
        List<FontFamily> fallbackFamilies = [];
        foreach (FontCollection collection in _collections)
        {
            if (!ReferenceEquals(collection, FontCollection.System))
            {
                fallbackFamilies.AddRange(collection.Families);
            }
        }

        if (_fallback is not null)
        {
            fallbackFamilies.AddRange(_fallback.Families);
        }

        Fallback = fallbackFamilies.Count == 0 && !options.UseSystemFonts ? FontFallback.None
            : new FontFallback(fallbackFamilies, options.UseSystemFonts ? SystemFontFallback.Instance : null);
    }

    /// <summary>Gets the per-character fallback: the given fonts, the fallback fonts, then the system's choice for the script.</summary>
    public FontFallback Fallback { get; }

    /// <summary>Returns the family for a style's font name, or <see langword="null"/> when there are no fonts at all.</summary>
    public FontFamily? Resolve(string name)
    {
        if (_families.TryGetValue(name, out FontFamily? known))
        {
            return known;
        }

        FontFamily? family = Find(name);
        if (family is null)
        {
            family = _fallback is not null && _fallback.TryGetFamily(name, out FontFamily substitute) ? substitute : Default();
            _context.Warnings?.Invoke(new MediaWarning("subtitle-font-missing", family is null
                ? $"The font \"{name}\" was not found, and there are no fonts to draw with instead: its text is left out."
                : $"The font \"{name}\" was not found; \"{family.Name}\" is used instead."));
        }

        _families[name] = family;
        return family;
    }

    private FontFamily? Find(string name)
    {
        if (Generic(name) is GenericFamily generic)
        {
            foreach (FontCollection collection in _collections)
            {
                if (collection.Match(generic, FontStyle.Normal) is FontMatch match && collection.TryGetFamily(match.Typeface.FamilyName, out FontFamily family))
                {
                    return family;
                }
            }
        }

        foreach (FontCollection collection in _collections)
        {
            if (collection.TryGetFamily(name, out FontFamily family))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>The family a missing font is replaced by: the fallback fonts' first, the sans-serif, or any family at all.</summary>
    private FontFamily? Default()
    {
        if (_defaultResolved)
        {
            return _default;
        }

        _defaultResolved = true;
        if (_fallback is not null && _fallback.Families.Count > 0)
        {
            return _default = _fallback.TryGetFamily("Arial", out FontFamily arial) ? arial : _fallback.Families[0];
        }

        _default = Find("sans-serif");
        foreach (FontCollection collection in _collections)
        {
            if (_default is null && collection.Families.Count > 0)
            {
                _default = collection.Families[0];
            }
        }

        return _default;
    }

    private static GenericFamily? Generic(string name) => name.ToLowerInvariant() switch
    {
        "sans-serif" or "sans" => GenericFamily.SansSerif,
        "serif" => GenericFamily.Serif,
        "monospace" => GenericFamily.Monospace,
        _ => null,
    };
}
