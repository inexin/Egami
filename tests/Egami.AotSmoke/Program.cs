using Egami;
using Egami.Pixels;

// Exercises the public API under Native AOT. Grows as features land; any trim or AOT warning fails the publish.
using Raster<Rgba8> raster = Raster.Create(64, 48, Rgba8.Red);
raster.SetPixel(10, 10, Rgba8.FromHex("#00FF00"));

using Raster<Gray8> gray = raster.As<Gray8>();
using Raster<Rgba16> wide = raster.As<Rgba16>();
int sum = 0;
gray.ForEachRow((row, y) => Interlocked.Add(ref sum, row[0].L));

bool ok = gray.GetPixel(0, 0).L == 127
    && gray.GetPixel(10, 10).L == 220
    && wide.GetPixel(0, 0) == new Rgba16(65535, 0, 0, 65535)
    && sum == 127 * 48;

Console.WriteLine(ok ? "Egami AOT smoke test passed." : "Egami AOT smoke test FAILED.");
return ok ? 0 : 1;
