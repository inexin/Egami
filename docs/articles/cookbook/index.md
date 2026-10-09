# Cookbook

Task-sized recipes. Each one compiles and runs in Egami's test run.

## Thumbnails for the web

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#thumbnail)]

The JPEG decodes at 1/2, 1/4 or 1/8 scale, `AutoOrient` turns only the small result, and the metadata is left out of the file.

## Safe handling of untrusted uploads

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#untrusted)]

See the [security guide](../guides/security.md) for every limit.

## Use from ASP.NET Core

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#upload)]

Egami has no ASP.NET Core dependency: it reads and writes any `Stream`. The async calls read the upload into a pooled buffer,
counted against `MaxTotalBytes`, so a slow client never holds a thread.

## Batch convert a folder

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#batch-convert)]

## Make an animated GIF from frames

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#gif-from-frames)]

## Strip metadata

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#strip)]

## Read the size without decoding

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#read-size)]

`Probe` reads only the header, and reports the EXIF orientation, so you know how the image will be shown.

## Interop with raw buffers

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#raw-buffers)]

## Native AOT

Egami is trimming- and AOT-clean: no reflection, no runtime code generation. Publish as usual:

```sh
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

Egami's own build publishes a small console app this way on every change and checks that it runs with no warnings.
