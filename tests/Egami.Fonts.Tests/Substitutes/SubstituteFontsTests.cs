using Egami.Fonts.Substitutes;

namespace Egami.Fonts.Tests.Substitutes;

/// <summary>Egami.Fonts.Substitutes: the bundled metric-compatible substitutes, found by their names and aliases.</summary>
public sealed class SubstituteFontsTests
{
    [Theory]
    [InlineData("Helvetica", 400, "LiberationSans")]
    [InlineData("Arial", 700, "LiberationSans-Bold")]
    [InlineData("Times New Roman", 400, "LiberationSerif")]
    [InlineData("Courier-New", 700, "LiberationMono-Bold")]
    [InlineData("Calibri", 400, "Carlito-Regular")]
    [InlineData("Cambria", 700, "Caladea-Bold")]
    public void AliasesFindTheirMetricCompatibleFaces(string name, int weight, string postScript)
    {
        Typeface face = SubstituteFonts.Find(name, new FontStyle(weight, 5, FontSlant.Upright))!;

        Assert.Equal(postScript, face.PostScriptName);
    }

    [Fact]
    public void ItalicsAndUnknownNames()
    {
        Assert.Equal("LiberationSerif-BoldItalic", SubstituteFonts.Find("Times", new FontStyle(700, 5, FontSlant.Italic))!.PostScriptName);
        Assert.Null(SubstituteFonts.Find("Verdana", FontStyle.Normal));
    }

    [Fact]
    public void TheCollectionHoldsEveryFaceAndTheLicencesShip()
    {
        Assert.Equal(5, SubstituteFonts.Collection.Count);
        Assert.All(SubstituteFonts.Collection.Families, f => Assert.Equal(4, f.Styles.Count));
        string[] resources = typeof(SubstituteFonts).Assembly.GetManifestResourceNames();
        Assert.Contains("Egami.Fonts.Substitutes.Liberation-OFL.txt", resources);
        Assert.Contains("Egami.Fonts.Substitutes.Carlito-OFL.txt", resources);
        Assert.Contains("Egami.Fonts.Substitutes.Caladea-OFL.txt", resources);
    }
}
