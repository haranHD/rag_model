# High-Speed Local MiniCPM-V OCR API Engine

This project provides a fast, lightweight **Vision & OCR Web API** powered by **MiniCPM-V** running locally on Ollama.

> [!NOTE]
> The previous RAG (Retrieval-Augmented Generation) & database vector search pipeline has been completely removed to optimize for speed, low latency, and direct image-to-text Vision processing.

---

## Tech Stack

* **Framework**: .NET 8 Web API
* **AI Model**: MiniCPM-V (Local Vision-Language / OCR model)
* **Inference Engine**: Ollama (`http://localhost:11434`)
* **API Specs**: Swagger / OpenAPI

---

## Architecture

```text
User / Client
     ↓
OcrController (POST /api/ocr/process or /api/ocr/stream or /api/ocr/upload)
     ↓
MiniCpmVService (HttpClient Connection Pool)
     ↓
Local Ollama API (/api/generate)
     ↓
MiniCPM-V Model
     ↓
Extracted OCR Text Output
```

---

## API Endpoints

### 1. Process Base64 Image (`POST /api/ocr/process`)
**Request Body:**
```json
{
  "imageBase64": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAA...",
  "prompt": "Extract all text from this image accurately.",
  "model": "minicpm-v"
}
```

**Response:**
```json
{
  "success": true,
  "extractedText": "INVOICE #10293\nDate: 2026-09-16\nTotal: $45.00",
  "executionTimeMs": 342,
  "modelUsed": "minicpm-v",
  "errorMessage": null
}
```

### 2. Upload Image File (`POST /api/ocr/upload`)
Multipart form upload supporting `.png`, `.jpg`, `.jpeg`, `.pdf`, etc.

### 3. Real-Time Streaming OCR (`POST /api/ocr/stream`)
Streams extracted text tokens directly as they are generated for minimal perceptual latency.

---

## Getting Started

### Prerequisites
1. Installed **.NET 8 SDK**
2. **Ollama** running locally:
   ```bash
   ollama serve
   ```
3. Pull the MiniCPM-V model:
   ```bash
   ollama pull minicpm-v
   ```

### Running the API
```bash
dotnet run
```
Access Swagger UI at `http://localhost:5000/swagger` (or configured port).
