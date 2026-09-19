namespace local_rag_model.DTOs.Ocr;

public class OcrResponse
{
    public bool Success { get; set; }

    /// <summary>Full raw text extracted from the image.</summary>
    public string ExtractedText { get; set; } = string.Empty;

    /// <summary>Each text line as a separate array item.</summary>
    public List<string> Lines { get; set; } = new();

    /// <summary>Key-value pairs parsed from the image (e.g. "Hank": "58.72").</summary>
    public Dictionary<string, string> KeyValues { get; set; } = new();

    public long ExecutionTimeMs { get; set; }
    public string ModelUsed { get; set; } = string.Empty;

    /// <summary>Original image dimensions before any resizing.</summary>
    public string? ImageSize { get; set; }

    public string? ErrorMessage { get; set; }
}
