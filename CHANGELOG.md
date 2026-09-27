# Changelog

## 0.3.0-preview.1 — local source/protocol preview, not published

- Preserve extractive defaults and separate retrieval into a reusable method. Add explicit generative mode for a manually configured literal-loopback llama.cpp-compatible HTTP server; no model/runtime download/start, cloud recipient or API key.
- Bound source context, token/answer/HTTP size and total generation deadline. Separate untrusted source data from fixed instructions; require completed JSON, matching model alias and supplied citation IDs. Return explicit generation errors without silent fallback; valid IDs are not factual validation.
- Add C# mode/timeout/provenance disclosure and cancellation tokens through HTTP body reads. Client cancellation does not claim to stop backend inference.
- Keep earlier source/evaluation tests and add synthetic TCP/API/CLI protocol checks. Extend manual evaluation with explicit generative mode; generation failures no longer count as correct no-answer cases. Actual model quality and release acceptance remain pending.

## 0.2.0-preview.1

- Added one local FastAPI endpoint set, a .NET 10 console project, Chroma document upsert with source metadata, TXT/Markdown/text-PDF extraction and a source-or-no-answer result.
- Added document-root and size limits, HTTP error responses, an offline 20-question evaluation with a mutation check, a synthetic sample and CI build/tests.
- Removed the untestable startup dependency on absent local model weights. This preview is extractive; no generative LLM quality claim is made.

The previous code used separate ports 8000 and 8001 and stored embeddings without document text. This preview uses port 8000 only and a new `documents_v1` Chroma collection. Old data is not migrated; retain a backup if it matters and re-ingest from source files. To revert, switch to the earlier Git commit and restore its own Python environment; the older service still requires absent weights for the query path.
