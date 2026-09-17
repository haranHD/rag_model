# High-Speed Local Vision OCR Engine — Technical R&D & Codebase Guide

## 1. Executive Summary & Purpose

This project is a lightweight, high-performance **C# .NET 8 Web API** that performs local image-to-text Optical Character Recognition (OCR) using local Vision-Language Models (VLMs) such as **MiniCPM-V**, **Llama 3.2 Vision**, or **Moondream** via **Ollama**.

### Key Architectural Evolution
- **Previous Design (RAG Architecture)**: Heavily reliant on embedding generation, PostgreSQL/pgvector database lookups, and document chunkers. This incurred high CPU/RAM overhead and slow end-to-end response times.
- **Current Design (Direct Vision OCR Engine)**: Stripped out all database/vector bottlenecks. Images are streamed directly to the local Vision-Language Model, resulting in high-speed, direct text extraction.

---

## 2. Architectural Overview & Data Flow

```text
+-----------------------------------------------------------------------------------+
|                                 CLIENT LAYER                                      |
|   Web UI (wwwroot/index.html)  OR  External Mobile/Web App  OR  cURL / Swagger UI |
+----------------------------------------+------------------------------------------+
                                         |
                                         | HTTP POST (Multipart Form or Base64 JSON)
                                         v
+-----------------------------------------------------------------------------------+
|                            CONTROLLER LAYER (API Gateway)                         |
|   Controllers/OcrController.cs                                                    |
|     - POST /api/ocr/upload (Multipart Image File Upload)                        |
|     - POST /api/ocr/process (Base64 Image JSON Payload)                           |
|     - POST /api/ocr/stream (Real-time Token-by-Token Streaming Output)            |
+----------------------------------------+------------------------------------------+
                                         |
                                         | Dependency Injected Call (IMiniCpmVService)
                                         v
+-----------------------------------------------------------------------------------+
|                             SERVICE LAYER (AI Engine)                             |
|   Services/Vision/MiniCpmVService.cs                                            |
|     - Clean & format Base64 image payload                                         |
|     - Inject Strict OCR Prompt Directive                                          |
|     - Apply RAM Memory Limits (num_ctx: 1024)                                     |
|     - Manage HttpClient Connection Pool to Ollama                                 |
+----------------------------------------+------------------------------------------+
                                         |
                                         | HTTP POST (/api/generate)
                                         v
+-----------------------------------------------------------------------------------+
|                        LOCAL AI INFERENCE SERVER (Ollama)                         |
|   Ollama API (http://localhost:11434)                                             |
|     - Model: openbmb/minicpm-v (or moondream / llama3.2-vision)                   |
|     - Vision Encoder + LLM Backbone                                               |
+----------------------------------------+------------------------------------------+
                                         |
                                         | Extracted Text Response / Stream Tokens
                                         v
+-----------------------------------------------------------------------------------+
|                                 OUTPUT DELIVERY                                   |
|   Extracted raw text returned to client with execution time in milliseconds       |
+-----------------------------------------------------------------------------------+
```

---

## 3. Core Concepts & Technical Deep Dive

### 3.1. Vision-Language Models (VLM) vs Traditional OCR (Tesseract)
- **Traditional OCR (e.g. Tesseract)**: Relies on rigid pattern matching. Struggles with rotated images, complex invoice layouts, noisy background colors, handwriting, or low-resolution text.
- **Vision-Language Models (MiniCPM-V)**: Combines a **Vision Transformer (ViT)** encoder with a **Large Language Model (LLM)**. It "understands" visual structures (tables, receipts, IDs, forms) semantically, allowing accurate text extraction even under poor lighting, rotation, or complex layouts.

### 3.2. Strict OCR Directive vs Conversational Image Description
VLMs are conversational by default. If given a generic prompt like *"Describe this image"*, the model will describe visual elements (*"This image shows a receipt from a coffee shop..."*).

To enforce strict OCR output:
- We pass a **Strict OCR Directive Prompt**:
  > `"Perform strict OCR. Transcribe all text visible in this image line by line. Do NOT describe the image or explain anything. Output ONLY raw text."`
- This forces the LLM decoder to suppress conversational commentary and output verbatim text lines.

### 3.3. Memory Allocation & Quantization (`num_ctx`)
- **Full Model Weights (`minicpm-v:latest`)**: 5.5 GB unquantized file. Requires ~3.9–4.1 GiB of system RAM.
- **Context Window (`num_ctx`)**: Defines how many tokens of context memory Ollama allocates in RAM.
- By setting `"num_ctx": 1024` in our HTTP request options, we restrict context memory usage, reducing the total RAM footprint so the model loads smoothly on 8 GB RAM laptops.

---

## 4. File-by-File Code Logic Analysis

### 4.1. `local_rag_model.csproj` (Project Configuration)
- **Target Framework**: `.NET 8.0` (`net8.0`).
- **Dependencies**: `Swashbuckle.AspNetCore` (provides Swagger UI documentation at `/swagger`).
- **SDK**: `Microsoft.NET.Sdk.Web` (configures ASP.NET Core Web API engine).

---

### 4.2. `appsettings.json` (Configuration Store)
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "Model": "openbmb/minicpm-v",
    "DefaultPrompt": "Perform strict OCR. Transcribe all text visible in this image line by line. Do NOT describe the image or explain anything. Output ONLY the raw text.",
    "NumCtx": 1024
  }
}
```
- **`BaseUrl`**: The network address of your local Ollama instance.
- **`Model`**: The default model tag (`openbmb/minicpm-v`).
- **`DefaultPrompt`**: System-level prompt enforcing raw text extraction.
- **`NumCtx`**: Memory control setting passed to Ollama.

---

### 4.3. Data Transfer Objects (`DTOs/Ocr/`)

#### `OcrRequest.cs`
```csharp
namespace local_rag_model.DTOs.Ocr;

public class OcrRequest
{
    public string ImageBase64 { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public string? Model { get; set; }
}
```
- Captures input payload from API callers: Base64 image string, optional custom prompt, and optional model override.

#### `OcrResponse.cs`
```csharp
namespace local_rag_model.DTOs.Ocr;

public class OcrResponse
{
    public bool Success { get; set; }
    public string ExtractedText { get; set; } = string.Empty;
    public long ExecutionTimeMs { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
```
- Standardized API response containing extracted text, execution duration in milliseconds (`ExecutionTimeMs`), model used, and error details if applicable.

---

### 4.4. `Services/Vision/IMiniCpmVService.cs` & `MiniCpmVService.cs`

#### Service Interface (`IMiniCpmVService.cs`)
```csharp
public interface IMiniCpmVService
{
    Task<OcrResponse> ProcessOcrAsync(string base64Image, string? prompt = null, string? model = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamOcrAsync(string base64Image, string? prompt = null, string? model = null, CancellationToken cancellationToken = default);
}
```
- Defines contract for both synchronous OCR (`ProcessOcrAsync`) and real-time streaming OCR (`StreamOcrAsync`).

#### Service Implementation (`MiniCpmVService.cs`)
Key internal mechanics:

1. **Base64 Sanitization (`CleanBase64`)**:
   ```csharp
   private static string CleanBase64(string base64)
   {
       if (string.IsNullOrWhiteSpace(base64)) return string.Empty;
       var commaIndex = base64.IndexOf(',');
       if (commaIndex >= 0)
       {
           return base64.Substring(commaIndex + 1).Trim();
       }
       return base64.Trim();
   }
   ```
   *Removes Data URI prefixes like `data:image/png;base64,` so Ollama receives raw Base64 strings.*

2. **Ollama JSON Request Construction**:
   ```csharp
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
   ```
   *Packs model name, prompt, image array, and memory options into Ollama's `/api/generate` structure.*

3. **High-Speed Execution Timing**:
   Uses `Stopwatch.StartNew()` and `stopwatch.Stop()` to measure exact inference time in milliseconds.

4. **Streaming Execution (`StreamOcrAsync`)**:
   Uses `SendAsync` with `HttpCompletionOption.ResponseHeadersRead` and reads line-by-line JSON streams from Ollama, yielding string tokens asynchronously as `IAsyncEnumerable<string>`.

---

### 4.5. `Controllers/OcrController.cs` (API Gateway)

1. **`POST /api/ocr/process`**: Accepts JSON containing Base64 image data and delegates to `ProcessOcrAsync`.
2. **`POST /api/ocr/upload`**: Accepts `IFormFile` from multipart form requests, converts file streams into Base64 in memory (`Convert.ToBase64String`), and returns JSON `OcrResponse`.
3. **`POST /api/ocr/stream`**: Writes text stream directly to `Response.Body` with `Response.ContentType = "text/plain"`, flushing each token immediately (`await Response.Body.FlushAsync()`).

---

### 4.6. `Program.cs` (Startup Pipeline)

```csharp
using local_rag_model.Services.Vision;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure HttpClient with connection pooling for Ollama
var ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";

builder.Services.AddHttpClient<IMiniCpmVService, MiniCpmVService>(client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles(); // Serves index.html from wwwroot
app.UseStaticFiles();  // Enables web UI assets

app.UseAuthorization();
app.MapControllers();

app.Run();
```
- **HttpClient Factory (`AddHttpClient`)**: Maintains long-lived TCP socket pools to `http://localhost:11434` to eliminate socket exhaustion and latency spikes.

---

### 4.7. `wwwroot/index.html` (Web UI Demo)
- **Drag-and-Drop Area (`dropZone`)**: Handles `dragover`, `dragleave`, and `drop` DOM events.
- **FileReader API**: Reads uploaded images locally using `reader.readAsDataURL(file)` and displays live thumbnail preview.
- **Model Selector Dropdown (`modelSelect`)**: Lets users switch between `openbmb/minicpm-v`, `llama3.2-vision:1b`, and `moondream`.
- **Streaming Consumer (`ReadableStreamDefaultReader`)**: Consumes the SSE text stream chunk-by-chunk using `fetch` + `res.body.getReader()` and updates the screen live.

---

## 5. R&D Testing & Benchmarking Commands

### 5.1. Test via cURL (File Upload)
```bash
curl -X POST "http://localhost:5000/api/ocr/upload" \
  -F "file=@/path/to/sample_invoice.png" \
  -F "prompt=Perform strict OCR. Transcribe all text line by line."
```

### 5.2. Test via cURL (Base64 JSON)
```bash
curl -X POST "http://localhost:5000/api/ocr/process" \
  -H "Content-Type: application/json" \
  -d '{
    "imageBase64": "iVBORw0KGgoAAAANSUhEUgAA...",
    "model": "openbmb/minicpm-v"
  }'
```

### 5.3. Real-Time Streaming Test
```bash
curl -N -X POST "http://localhost:5000/api/ocr/stream" \
  -H "Content-Type: application/json" \
  -d '{
    "imageBase64": "iVBORw0KGgoAAAANSUhEUgAA...",
    "prompt": "Perform strict OCR."
  }'
```

---

## 6. Summary of Key Achievements

1. **Latency Reduction**: Direct vision processing without database or embedding steps.
2. **Zero Memory Crashes**: Added `num_ctx` controls and support for lightweight vision models (`moondream`, `llama3.2-vision:1b`) for 8GB RAM laptops.
3. **Strict Text Extraction**: Solved conversational text issues using targeted system directives.
4. **Production Readiness**: Full .NET 8 Web API with Swagger UI, streaming capabilities, and a web demo interface.

