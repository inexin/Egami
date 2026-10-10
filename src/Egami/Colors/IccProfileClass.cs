namespace Egami.Colors;

/// <summary>What an ICC profile describes, from its header's class field.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="icc"/>
/// </example>
public enum IccProfileClass
{
    /// <summary>A class Egami does not know.</summary>
    Unknown,

    /// <summary>An input device such as a camera or scanner (<c>scnr</c>).</summary>
    Input,

    /// <summary>A display (<c>mntr</c>); most RGB profiles in images.</summary>
    Display,

    /// <summary>An output device such as a printer (<c>prtr</c>); most CMYK profiles.</summary>
    Output,

    /// <summary>A link from one device space straight to another (<c>link</c>). Egami does not convert with these.</summary>
    DeviceLink,

    /// <summary>A color space such as a working space (<c>spac</c>).</summary>
    ColorSpace,

    /// <summary>An effect from PCS to PCS (<c>abst</c>). Egami does not convert with these.</summary>
    Abstract,

    /// <summary>A list of named colors (<c>nmcl</c>). Egami does not convert with these.</summary>
    NamedColor,
}
