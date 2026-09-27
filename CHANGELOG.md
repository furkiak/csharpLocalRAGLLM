# Changelog

## 0.3.0-preview.3 — isolated SQLite dependency correction, not published

- Replace the Chroma backend with Python standard-library SQLite for the small lexical demonstration. Earlier Chroma files are preserved and not auto-migrated; re-ingest source files into `rag-v2.sqlite3`. Replacement/removal of stale chunks is atomic, query categories are parameterized, and concurrent writes use separate connections.
- Bound the exact O(n) vector scan to 10,000 total chunks. This is a deliberate small-corpus limit, not an approximate vector-search or throughput claim.
- Upgrade FastAPI/Starlette, pypdf and pytest after a dated complete dependency audit found advisories in the old graph. Remove the unused Chroma/NumPy/PostHog graph; published Chroma versions had no fix for two advisories at review time. Keep actual runtime and dependency audit evidence separate.
- Move the default setup and declared CI target to Python 3.12. Use the supported HTTPX2 TestClient path instead of Starlette's deprecated HTTPX fallback; no deprecation filter is added. Bootstrap pip/setuptools is pinned and audited separately in the virtual environment.
- Retain all 42 ingest/protocol/CLI cases and add nine real SQLite regression cases for restart, failed-replacement rollback, parameterized categories/stale removal, index capacity, concurrent writers and preservation/rejection of incompatible existing databases. The earlier genuine-model result belongs to preview.2; each later exact package is tested and recorded separately.

## 0.3.0-preview.2 — local structured-output correction, not published

- Actual Qwen2.5-1.5B Q4_K_M / llama.cpp b11208 requests exposed missing citation fields despite plain JSON output. Request a llama.cpp-supported response schema with required answer/citations, no extra fields and only supplied source IDs. Keep local response, alias, finish, size, deadline and citation validation authoritative when a server ignores the schema.
- Clarify fact, language and conflict instructions. This improves format consistency; it does not prove factual grounding. A dated working-source evaluation scored 19/20 on small tailored literal/source checks with zero generation errors; a generic return-period answer and an unsupported conflict answer remained quality failures. Real responses are recorded separately from synthetic protocol tests.
- Default extraction still makes no model call. No model/runtime, credentials or index is bundled; the user starts the model independently. Real corpus, paraphrase quality, physical cancellation, fresh machine, remote CI and public release remain open.
- Scope pytest collection to the source `tests` directory so retained clean-checkout and package copies under ignored `TestResults` do not collide with source modules. No source tests are suppressed.

## 0.3.0-preview.1 — local source/protocol preview, not published

- Preserve extractive defaults and separate retrieval into a reusable method. Add explicit generative mode for a manually configured literal-loopback llama.cpp-compatible HTTP server; no model/runtime download/start, cloud recipient or API key.
- Bound source context, token/answer/HTTP size and total generation deadline. Separate untrusted source data from fixed instructions; require completed JSON, matching model alias and supplied citation IDs. Return explicit generation errors without silent fallback; valid IDs are not factual validation.
- Add C# mode/timeout/provenance disclosure and cancellation tokens through HTTP body reads. Client cancellation does not claim to stop backend inference.
- Keep earlier source/evaluation tests and add synthetic TCP/API/CLI protocol checks. Extend manual evaluation with explicit generative mode; generation failures no longer count as correct no-answer cases. Actual model quality and release acceptance remain pending.

## 0.2.0-preview.1

- Added one local FastAPI endpoint set, a .NET 10 console project, Chroma document upsert with source metadata, TXT/Markdown/text-PDF extraction and a source-or-no-answer result.
- Added document-root and size limits, HTTP error responses, an offline 20-question evaluation with a mutation check, a synthetic sample and CI build/tests.
- Removed the untestable startup dependency on absent local model weights. This preview is extractive; no generative LLM quality claim is made.

Historical 0.2.0 note: the previous code used separate ports 8000 and 8001 and stored embeddings without document text. This preview uses port 8000 only and a new `documents_v1` Chroma collection. Old data is not migrated; retain a backup if it matters and re-ingest from source files. To revert, switch to the earlier Git commit and restore its own Python environment; the older service still requires absent weights for the query path.
