using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using local_rag_model.DTOs.Ocr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace local_rag_model.Services.Vision;

public class MiniCpmVService : IMiniCpmVService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MiniCpmVService> _logger;

    // Max image dimensions before downscaling — keeps inference fast & avoids timeouts
    private const int MaxImageWidth = 1024;
    private const int MaxImageHeight = 1024;

    public MiniCpmVService(HttpClient httpClient, IConfiguration configuration, ILogger<MiniCpmVService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OcrResponse> ProcessOcrAsync(
        string base64Image,
        string? prompt = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var selectedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : _configuration["Ollama:Model"] ?? "minicpm-v";

        var selectedPrompt = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : _configuration["Ollama:DefaultPrompt"] ?? "Perform strict OCR. Transcribe all text visible in this image line by line. Output ONLY raw text.";

        var cleanedImage = PrepareAndOptimizeBase64Image(base64Image);
        if (string.IsNullOrEmpty(cleanedImage))
        {
            return new OcrResponse
            {
                Success = false,
                ErrorMessage = "Image payload is empty or invalid Base64.",
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                ModelUsed = selectedModel
            };
        }

        var numCtx = int.TryParse(_configuration["Ollama:NumCtx"], out var parsedCtx) ? parsedCtx : 1024;

        var requestPayload = new OllamaGenerateRequest
        {
            Model = selectedModel,
            Prompt = selectedPrompt,
            Images = new[] { cleanedImage },
            Stream = false,
            Options = new Dictionary<string, object>
            {
                { "num_ctx", numCtx }
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/generate", requestPayload, cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Ollama API call failed with status {StatusCode}: {Error}", response.StatusCode, errorContent);
                return new OcrResponse
                {
                    Success = false,
                    ErrorMessage = $"Ollama API request failed ({response.StatusCode}): {errorContent}",
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                    ModelUsed = selectedModel
                };
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
            return new OcrResponse
            {
                Success = true,
                ExtractedText = result?.Response ?? string.Empty,
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                ModelUsed = selectedModel
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error occurred during MiniCPM-V OCR process.");
            return new OcrResponse
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                ModelUsed = selectedModel
            };
        }
    }

    public async IAsyncEnumerable<string> StreamOcrAsync(
        string base64Image,
        string? prompt = null,
        string? model = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var selectedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : _configuration["Ollama:Model"] ?? "minicpm-v";

        var selectedPrompt = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : _configuration["Ollama:DefaultPrompt"] ?? "Perform strict OCR. Transcribe all text visible in this image line by line. Output ONLY raw text.";

        var cleanedImage = PrepareAndOptimizeBase64Image(base64Image);
        if (string.IsNullOrEmpty(cleanedImage))
        {
            yield return "Error: Image payload is empty or invalid Base64.";
            yield break;
        }

        var numCtx = int.TryParse(_configuration["Ollama:NumCtx"], out var parsedCtx) ? parsedCtx : 1024;

        var requestPayload = new OllamaGenerateRequest
        {
            Model = selectedModel,
            Prompt = selectedPrompt,
            Images = new[] { cleanedImage },
            Stream = true,
            Options = new Dictionary<string, object>
            {
                { "num_ctx", numCtx }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(requestPayload)
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Error ({response.StatusCode}): {error}";
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaStreamChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
            }
            catch
            {
                // Skip invalid JSON lines
            }

            if (chunk != null && !string.IsNullOrEmpty(chunk.Response))
            {
                yield return chunk.Response;
            }

            if (chunk?.Done == true)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Strips Data URI prefix, decodes the image, resizes it to max 1024x1024 if needed,
    /// re-encodes as JPEG (quality 85) to minimise token payload, then returns clean Base64.
    /// Smaller payloads = faster inference = no 120-second timeout.
    /// </summary>
    private string PrepareAndOptimizeBase64Image(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return string.Empty;

        // Strip "data:image/xxx;base64," prefix if present
        var commaIndex = base64.IndexOf(',');
        var rawBase64 = commaIndex >= 0 ? base64[(commaIndex + 1)..].Trim() : base64.Trim();

        if (string.IsNullOrEmpty(rawBase64)) return string.Empty;

        try
        {
            var imageBytes = Convert.FromBase64String(rawBase64);

            using var image = Image.Load(imageBytes);

            // Only resize if image is larger than our max dimensions
            if (image.Width > MaxImageWidth || image.Height > MaxImageHeight)
            {
                _logger.LogInformation(
                    "Resizing image from {W}x{H} to max {MaxW}x{MaxH} for faster inference.",
                    image.Width, image.Height, MaxImageWidth, MaxImageHeight);

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxImageWidth, MaxImageHeight),
                    Mode = ResizeMode.Max   // Preserves aspect ratio
                }));
            }

            using var outputStream = new MemoryStream();
            image.Save(outputStream, new JpegEncoder { Quality = 85 });
            return Convert.ToBase64String(outputStream.ToArray());
        }
        catch (Exception ex)
        {
            // If image processing fails, fall back to raw cleaned base64
            _logger.LogWarning(ex, "Image optimization failed, using raw Base64 instead.");
            return rawBase64;
        }
    }

    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("images")]
        public string[] Images { get; set; } = Array.Empty<string>();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public Dictionary<string, object>? Options { get; set; }
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private class OllamaStreamChunk
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}
