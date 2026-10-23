using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;

using Egami.Ocr;

namespace Egami.Samples.Ocr;

/// <summary>
/// Azure AI Document Intelligence's Read model through its REST API (no SDK): Egami posts the image as a PNG, polls the
/// operation and turns its words and lines into an <see cref="OcrResult"/>. Needs an endpoint and a key of your own.
/// </summary>
public sealed class AzureDocumentIntelligenceEngine : IOcrEngine, IDisposable
{
    private const string ApiVersion = "2024-11-30";

    private readonly HttpClient _http;
    private readonly Uri _analyze;

    /// <summary>Initializes a new instance of the <see cref="AzureDocumentIntelligenceEngine"/> class.</summary>
    /// <param name="endpoint">The resource's endpoint, such as https://my-resource.cognitiveservices.azure.com/.</param>
    /// <param name="key">The resource's key.</param>
    public AzureDocumentIntelligenceEngine(Uri endpoint, string key)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(key);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", key);
        _analyze = new Uri(endpoint, $"documentintelligence/documentModels/prebuilt-read:analyze?api-version={ApiVersion}");
    }

    /// <inheritdoc/>
    public OcrEngineInfo Info { get; } = new("azure-document-intelligence") { MaxImageDimension = 10_000, FindsOrientation = true };

    /// <summary>Creates the engine from AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT and AZURE_DOCUMENT_INTELLIGENCE_KEY.</summary>
    /// <returns>The engine.</returns>
    public static AzureDocumentIntelligenceEngine FromEnvironment()
    {
        string endpoint = Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT")
            ?? throw new InvalidOperationException("Set AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT and AZURE_DOCUMENT_INTELLIGENCE_KEY.");
        string key = Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_KEY")
            ?? throw new InvalidOperationException("Set AZURE_DOCUMENT_INTELLIGENCE_KEY.");
        return new AzureDocumentIntelligenceEngine(new Uri(endpoint), key);
    }

    /// <inheritdoc/>
    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using MemoryStream png = new();
        await request.Image.SaveAsync(png, "png", cancellationToken: cancellationToken);
        Uri analyze = request.Languages.Count > 0 ? new Uri($"{_analyze}&locale={Uri.EscapeDataString(request.Languages[0])}") : _analyze;
        using ByteArrayContent content = new(png.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage started = await _http.PostAsync(analyze, content, cancellationToken);
        if (!started.IsSuccessStatusCode || started.Headers.TryGetValues("Operation-Location", out IEnumerable<string>? locations) is false)
        {
            throw new OcrEngineException($"Document Intelligence refused the image: {(int)started.StatusCode} {await started.Content.ReadAsStringAsync(cancellationToken)}");
        }

        Uri operation = new(locations.First());
        while (true)
        {
            using HttpResponseMessage poll = await _http.GetAsync(operation, cancellationToken);
            using JsonDocument json = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(cancellationToken));
            string status = json.RootElement.GetProperty("status").GetString() ?? string.Empty;
            if (status == "succeeded")
            {
                return ToResult(json.RootElement.GetProperty("analyzeResult").GetProperty("pages")[0]);
            }

            if (status is "failed" or "canceled")
            {
                throw new OcrEngineException($"Document Intelligence {status}: {json.RootElement}");
            }

            TimeSpan wait = poll.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            await Task.Delay(wait, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>Turns one page of the Read model's result into lines of words: each word joins the line whose spans hold it.</summary>
    private static OcrResult ToResult(JsonElement page)
    {
        List<(int Offset, OcrWord Word)> words = [];
        foreach (JsonElement word in page.GetProperty("words").EnumerateArray())
        {
            JsonElement span = word.GetProperty("span");
            words.Add((span.GetProperty("offset").GetInt32(), new OcrWord(word.GetProperty("content").GetString()!, Quad(word.GetProperty("polygon")), word.GetProperty("confidence").GetDouble())));
        }

        List<OcrLine> lines = [];
        foreach (JsonElement line in page.GetProperty("lines").EnumerateArray())
        {
            List<OcrWord> inLine = [];
            foreach (JsonElement span in line.GetProperty("spans").EnumerateArray())
            {
                int start = span.GetProperty("offset").GetInt32(), end = start + span.GetProperty("length").GetInt32();
                inLine.AddRange(words.Where(w => w.Offset >= start && w.Offset < end).Select(w => w.Word));
            }

            if (inLine.Count > 0)
            {
                lines.Add(new OcrLine(inLine));
            }
        }

        // "angle" is the content's clockwise tilt; the turn that puts it upright, to a quarter turn, is the other way.
        double angle = page.TryGetProperty("angle", out JsonElement a) ? a.GetDouble() : 0;
        int rotation = ((360 - ((int)Math.Round(angle / 90) * 90)) % 360 + 360) % 360;
        Size size = new((int)Math.Round(page.GetProperty("width").GetDouble()), (int)Math.Round(page.GetProperty("height").GetDouble()));
        return new OcrResult(lines) { Rotation = rotation, ImageSize = size };
    }

    /// <summary>The Read model gives each word's polygon as x, y pairs from its top left, clockwise as the word reads.</summary>
    private static OcrQuad Quad(JsonElement polygon)
    {
        float[] p = [.. polygon.EnumerateArray().Select(v => (float)v.GetDouble())];
        return new OcrQuad(new Vector2(p[0], p[1]), new Vector2(p[2], p[3]), new Vector2(p[4], p[5]), new Vector2(p[6], p[7]));
    }
}
