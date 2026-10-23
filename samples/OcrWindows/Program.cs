// dotnet run --project samples/OcrWindows [image] [language]   (Windows 10 or 11, with the language's OCR pack installed)
using Egami;
using Egami.Ocr;
using Egami.Pixels;
using Egami.Samples.OcrWindows;

string path = args.Length > 0 ? args[0] : Egami.Samples.SampleRunner.Image("duck.png");
WindowsOcrEngine engine = args.Length > 1 ? new WindowsOcrEngine(args[1]) : new WindowsOcrEngine();
using Raster<Rgba8> image = Raster.Load<Rgba8>(path);
OcrResult result = await engine.RecognizeAsync(new OcrRequest(image));
Console.WriteLine($"{engine.Info.Name} found {result.Words.Count()} words:");
Console.WriteLine(result.Text);
