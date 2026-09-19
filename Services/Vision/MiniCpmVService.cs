using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
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

    // Resize to max 800px to keep inference fast without losing OCR detail
    private const int MaxImageWidth = 800;
    private const int MaxImageHeight = 800;

    public MiniCpmVService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MiniCpmVService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Synchronous OCR
    // ─────────────────────────────────────────────────────────────────────────

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

        // Force JSON-structured output via prompt
        var selectedPrompt = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : _configuration["Ollama:DefaultPrompt"]
              ?? BuildDefaultOcrPrompt();

        string originalSize = string.Empty;

        var cleanedImage =
            PrepareAndOptimizeBase64Image(base64Image, ref originalSize);

        if (string.IsNullOrEmpty(cleanedImage))
        {
            return ErrorResponse(
                "Image payload is empty or invalid Base64.",
                selectedModel,
                stopwatch);
        }

        _logger.LogInformation(
            "Starting OCR with model={Model}, imageSize={Size}",
            selectedModel,
            originalSize);

        var numCtx =
            int.TryParse(
                _configuration["Ollama:NumCtx"],
                out var ctx)
                ? ctx
                : 2048;

        // Use /api/chat — recommended endpoint for multimodal Ollama models
        var chatRequest =
            BuildChatRequest(
                selectedModel,
                selectedPrompt,
                cleanedImage,
                false,
                numCtx);

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/api/chat",
                chatRequest,
                cancellationToken);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var err =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                _logger.LogError(
                    "Ollama /api/chat failed {Status}: {Error}",
                    response.StatusCode,
                    err);

                return ErrorResponse(
                    $"Ollama API request failed ({response.StatusCode}): {err}",
                    selectedModel,
                    stopwatch,
                    originalSize);
            }

            var result =
                await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                    cancellationToken: cancellationToken);

            var rawText =
                result?.Message?.Content ?? string.Empty;

            return BuildOcrResponse(
                rawText,
                selectedModel,
                stopwatch.ElapsedMilliseconds,
                originalSize);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();

            _logger.LogError(
                "Request timed out for model {Model}.",
                selectedModel);

            return ErrorResponse(
                $"Request timed out after {_httpClient.Timeout.TotalSeconds}s. " +
                "Try closing other applications to free RAM, or use a lighter model.",
                selectedModel,
                stopwatch,
                originalSize);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(
                ex,
                "OCR process error.");

            return ErrorResponse(
                ex.Message,
                selectedModel,
                stopwatch,
                originalSize);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Streaming OCR
    // ─────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> StreamOcrAsync(
        string base64Image,
        string? prompt = null,
        string? model = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var selectedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : _configuration["Ollama:Model"] ?? "minicpm-v";

        var selectedPrompt = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : _configuration["Ollama:DefaultPrompt"]
              ?? BuildDefaultOcrPrompt();

        string originalSize = string.Empty;

        var cleanedImage =
            PrepareAndOptimizeBase64Image(
                base64Image,
                ref originalSize);

        if (string.IsNullOrEmpty(cleanedImage))
        {
            yield return "Error: Image payload is empty or invalid Base64.";
            yield break;
        }

        var numCtx =
            int.TryParse(
                _configuration["Ollama:NumCtx"],
                out var ctx)
                ? ctx
                : 2048;

        var chatRequest =
            BuildChatRequest(
                selectedModel,
                selectedPrompt,
                cleanedImage,
                true,
                numCtx);

        using var httpRequest =
            new HttpRequestMessage(
                HttpMethod.Post,
                "/api/chat")
            {
                Content = JsonContent.Create(chatRequest)
            };

        using var response =
            await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            yield return $"Error ({response.StatusCode}): {error}";
            yield break;
        }

        using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var reader =
            new StreamReader(stream);

        while (!reader.EndOfStream &&
               !cancellationToken.IsCancellationRequested)
        {
            var line =
                await reader.ReadLineAsync(
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            OllamaChatStreamChunk? chunk = null;

            try
            {
                chunk =
                    JsonSerializer.Deserialize<OllamaChatStreamChunk>(
                        line);
            }
            catch
            {
                // Skip malformed lines
            }

            if (chunk?.Message?.Content is { Length: > 0 } content)
            {
                yield return content;
            }

            if (chunk?.Done == true)
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Image Preprocessing
    // ─────────────────────────────────────────────────────────────────────────
    // - Strip Data URI prefix
    // - Resize to max 800×800
    // - Boost brightness
    // - Boost contrast
    // - Gaussian sharpen
    // - Re-encode as JPEG 90%
    // ─────────────────────────────────────────────────────────────────────────

    private string PrepareAndOptimizeBase64Image(
        string base64,
        ref string originalSize)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return string.Empty;

        var comma = base64.IndexOf(',');

        var raw =
            comma >= 0
                ? base64[(comma + 1)..].Trim()
                : base64.Trim();

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        try
        {
            var bytes =
                Convert.FromBase64String(raw);

            using var image =
                Image.Load(bytes);

            originalSize =
                $"{image.Width}x{image.Height}";

            // ─────────────────────────────────────────────────────────────
            // Resize if too large
            // ─────────────────────────────────────────────────────────────

            if (image.Width > MaxImageWidth ||
                image.Height > MaxImageHeight)
            {
                _logger.LogInformation(
                    "Resizing image {W}x{H} → max {MW}x{MH}",
                    image.Width,
                    image.Height,
                    MaxImageWidth,
                    MaxImageHeight);

                image.Mutate(x =>
                    x.Resize(new ResizeOptions
                    {
                        Size =
                            new Size(
                                MaxImageWidth,
                                MaxImageHeight),

                        Mode = ResizeMode.Max
                    }));
            }

            // ─────────────────────────────────────────────────────────────
            // Enhance dark / low-contrast screens
            // ─────────────────────────────────────────────────────────────

            image.Mutate(x =>
                x
                .Brightness(1.15f)
                .Contrast(1.4f)
                .GaussianSharpen(1.0f)
            );

            // ─────────────────────────────────────────────────────────────
            // Convert to JPEG
            // ─────────────────────────────────────────────────────────────

            using var ms =
                new MemoryStream();

            image.Save(
                ms,
                new JpegEncoder
                {
                    Quality = 90
                });

            return Convert.ToBase64String(
                ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Image preprocessing failed, falling back to raw Base64.");

            return raw;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ollama Request
    // ─────────────────────────────────────────────────────────────────────────

    private static object BuildChatRequest(
        string model,
        string prompt,
        string base64Image,
        bool stream,
        int numCtx)
    {
        return new
        {
            model,

            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[]
                    {
                        base64Image
                    }
                }
            },

            stream,

            options = new Dictionary<string, object>
            {
                {
                    "num_ctx",
                    numCtx
                },
                {
                    "temperature",
                    0.0
                }
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OCR Prompt
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildDefaultOcrPrompt() =>
        "You are a precise OCR engine. " +
        "Read all text visible in the image exactly as it appears. " +
        "Include every label, number, date, and value. " +
        "Do NOT describe the image. " +
        "Do NOT add explanations. " +
        "Output ONLY the raw text, line by line.";

    // ─────────────────────────────────────────────────────────────────────────
    // OCR Response
    // ─────────────────────────────────────────────────────────────────────────

    private static OcrResponse BuildOcrResponse(
        string rawText,
        string model,
        long ms,
        string imageSize)
    {
        // Split into lines and remove empty ones
        var lines =
            rawText
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

        // Parse key-value pairs
        // Example: "Hank : 58.72"

        var kvPairs =
            new Dictionary<string, string>();

        foreach (var line in lines)
        {
            var separators =
                new[]
                {
                    " : ",
                    ": ",
                    " - ",
                    "\t"
                };

            foreach (var sep in separators)
            {
                var idx =
                    line.IndexOf(
                        sep,
                        StringComparison.OrdinalIgnoreCase);

                if (idx > 0)
                {
                    var key =
                        line[..idx].Trim();

                    var val =
                        line[(idx + sep.Length)..].Trim();

                    if (!string.IsNullOrEmpty(key) &&
                        !string.IsNullOrEmpty(val))
                    {
                        kvPairs.TryAdd(
                            key,
                            val);
                    }

                    break;
                }
            }
        }

        return new OcrResponse
        {
            Success = true,
            ExtractedText = rawText.Trim(),
            Lines = lines,
            KeyValues = kvPairs,
            ExecutionTimeMs = ms,
            ModelUsed = model,
            ImageSize = imageSize
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Error Response
    // ─────────────────────────────────────────────────────────────────────────

    private static OcrResponse ErrorResponse(
        string message,
        string model,
        Stopwatch sw,
        string imageSize = "")
    {
        sw.Stop();

        return new OcrResponse
        {
            Success = false,
            ErrorMessage = message,
            ModelUsed = model,
            ExecutionTimeMs = sw.ElapsedMilliseconds,
            ImageSize = imageSize
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ollama /api/chat DTO Models
    // ─────────────────────────────────────────────────────────────────────────

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private class OllamaChatStreamChunk
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private class OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}