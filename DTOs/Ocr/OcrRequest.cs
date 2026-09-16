namespace local_rag_model.DTOs.Ocr;

public class OcrRequest
{
    /// <summary>
    /// Base64 encoded image string (e.g. data:image/png;base64,... or raw base64).
    /// </summary>
    public string ImageBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Optional prompt override for MiniCPM-V (e.g., "Extract all text", "Read receipt items", etc.).
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Model name override (defaults to configured model, e.g. minicpm-v).
    /// </summary>
    public string? Model { get; set; }
}

