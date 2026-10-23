// dotnet run --project samples/Ocr [tesseract|azure] [output folder]
//   tesseract (the default) needs the tesseract program on the PATH, or its path in TESSERACT.
//   azure needs AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT and AZURE_DOCUMENT_INTELLIGENCE_KEY.
string engine = args.Length > 0 ? args[0] : "tesseract";
Egami.Samples.SampleRunner.Run(args.Length > 1 ? args[1..] : [], output => Egami.Samples.Ocr.Sample.RunAsync(engine, output).GetAwaiter().GetResult());
