namespace Egami.Fonts;

/// <summary>One decoded string of a font's naming table, such as the family name in one language.</summary>
/// <param name="NameId">What the string is: 1 family, 2 subfamily, 4 full name, 6 PostScript name, 16 typographic family and so on.</param>
/// <param name="PlatformId">The platform: 0 Unicode, 1 Macintosh, 3 Windows.</param>
/// <param name="EncodingId">The platform's encoding.</param>
/// <param name="LanguageId">The platform's language code, such as 0x409 for US English on Windows.</param>
/// <param name="Value">The string.</param>
public readonly record struct FontNameRecord(int NameId, int PlatformId, int EncodingId, int LanguageId, string Value);
