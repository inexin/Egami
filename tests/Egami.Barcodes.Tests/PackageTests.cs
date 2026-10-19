using System.Reflection;

namespace Egami.Barcodes.Tests;

/// <summary>The package builds, loads and carries its version.</summary>
public class PackageTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assembly assembly = Assembly.Load("Egami.Barcodes");
        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
    }
}
