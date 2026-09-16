using local_rag_model.DTOs.Ocr;

namespace local_rag_model.Services.Vision;

public interface IMiniCpmVService
{
    Task<OcrResponse> ProcessOcrAsync(string base64Image, string? prompt = null, string? model = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamOcrAsync(string base64Image, string? prompt = null, string? model = null, CancellationToken cancellationToken = default);
}

