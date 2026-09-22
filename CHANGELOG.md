# Changelog

## 0.2.0-preview.1

- Added one local FastAPI endpoint set, a .NET 10 console project, Chroma document upsert with source metadata, TXT/Markdown/text-PDF extraction and a source-or-no-answer result.
- Added document-root and size limits, HTTP error responses, an offline 20-question evaluation with a mutation check, a synthetic sample and CI build/tests.
- Removed the untestable startup dependency on absent local model weights. This preview is extractive; no generative LLM quality claim is made.

The previous code used separate ports 8000 and 8001 and stored embeddings without document text. This preview uses port 8000 only and a new `documents_v1` Chroma collection. Old data is not migrated; retain a backup if it matters and re-ingest from source files. To revert, switch to the earlier Git commit and restore its own Python environment; the older service still requires absent weights for the query path.
