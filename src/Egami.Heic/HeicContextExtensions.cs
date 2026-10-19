namespace Egami.Heic;

/// <summary>Registers Egami.Heic with an <see cref="EgamiContext"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="register"/>
/// </example>
public static class HeicContextExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="context"/> that reads HEIC with Egami.Heic. Nothing registers it on its own: an app calls
    /// this once, for example <c>EgamiContext.SetDefault(new EgamiContext().WithHeic())</c> at startup, and so opts in to
    /// decoding HEVC.
    /// </summary>
    /// <remarks>
    /// HEVC is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
    /// depends on your country and on how you use and distribute this package; commercial products may need one from the HEVC
    /// patent pools or holders. The core Egami package contains no HEVC code and does not depend on this package.
    /// </remarks>
    /// <param name="context">The context to add HEIC to.</param>
    /// <returns>The new context, with core Egami's HEIC entry replaced by <see cref="HeicFormat.Info"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithHeic(this EgamiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WithFormat(HeicFormat.Info);
    }
}
