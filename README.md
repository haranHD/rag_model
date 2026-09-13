# rag_model
# AI Chatbot – RAG Integration

This project adds an **AI chatbot with RAG (Retrieval-Augmented Generation)** to the existing application.

The chatbot can retrieve relevant project and report information from the database and generate responses using a local LLM.

## Tech Stack

* C# / .NET
* PostgreSQL
* pgvector
* Ollama
* MiniCPM5-2B
* RAG (Retrieval-Augmented Generation)

## Architecture

```text
User
  ↓
ChatController
  ↓
ChatService
  ↓
RagService
  ↓
Embedding + Vector Search
  ↓
PostgreSQL / pgvector
  ↓
Relevant Context
  ↓
Ollama
  ↓
MiniCPM5-2B
  ↓
AI Response
```

## Main Components

* **ChatController** – Handles chatbot API requests.
* **ChatService** – Manages the chatbot flow.
* **RagService** – Handles the RAG process.
* **EmbeddingService** – Converts text into embeddings.
* **VectorSearchService** – Finds relevant information using vector similarity.
* **RagRepository** – Handles RAG-related database operations.
* **OllamaService** – Communicates with the local AI model.
* **DocumentProcessing** – Handles document reading and text chunking.

## Purpose

The chatbot is designed to provide information related to:

* Project status
* Reports
* Tasks
* Modules
* Issues
* Other application-related information

## Current Status

🚧 RAG chatbot integration is currently under development.

## Future Improvements

* Improve document processing
* Add better semantic search
* Improve chatbot responses
* Add conversation history
* Add source references to responses
* Fine-tune the system based on project requirements
