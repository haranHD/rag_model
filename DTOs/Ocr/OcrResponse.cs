namespace local_rag_model.DTOs.Ocr;

public class OcrResponse
{
    public bool Success { get; set; }
    public string ExtractedText { get; set; } = string.Empty;
    public long ExecutionTimeMs { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

