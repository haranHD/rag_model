using Microsoft.AspNetCore.Mvc;
using local_rag_model.DTOs.Ocr;
using local_rag_model.Services.Vision;

namespace local_rag_model.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OcrController : ControllerBase
{
    private readonly IMiniCpmVService _miniCpmVService;
    private readonly ILogger<OcrController> _logger;

    public OcrController(IMiniCpmVService miniCpmVService, ILogger<OcrController> logger)
    {
        _miniCpmVService = miniCpmVService;
        _logger = logger;
    }

    /// <summary>
    /// Synchronous high-speed OCR processing from Base64 image string.
    /// </summary>
    [HttpPost("process")]
    [ProducesResponseType(typeof(OcrResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessOcr([FromBody] OcrRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return BadRequest(new OcrResponse
            {
                Success = false,
                ErrorMessage = "Base64 image is required."
            });
        }

        var result = await _miniCpmVService.ProcessOcrAsync(
            request.ImageBase64,
            request.Prompt,
            request.Model,
            cancellationToken);

        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    /// <summary>
    /// Synchronous OCR processing from uploaded image file (multipart/form-data).
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(OcrResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadImage(
        [FromForm] IFormFile file,
        [FromForm] string? prompt,
        [FromForm] string? model,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new OcrResponse
            {
                Success = false,
                ErrorMessage = "A valid non-empty image file must be uploaded."
            });
        }

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken);
        var imageBytes = memoryStream.ToArray();
        var base64 = Convert.ToBase64String(imageBytes);

        var result = await _miniCpmVService.ProcessOcrAsync(base64, prompt, model, cancellationToken);
        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    /// <summary>
    /// Streaming OCR processing for lowest perceptual latency.
    /// </summary>
    [HttpPost("stream")]
    public async Task StreamOcr([FromBody] OcrRequest request, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/plain";

        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            await Response.WriteAsync("Error: Base64 image payload is required.", cancellationToken);
            return;
        }

        await foreach (var token in _miniCpmVService.StreamOcrAsync(
                           request.ImageBase64,
                           request.Prompt,
                           request.Model,
                           cancellationToken))
        {
            await Response.WriteAsync(token, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}

