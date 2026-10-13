# Changelog

## 2.0.0

A complete rewrite in C#. The Python prototype (two FastAPI servers, ChromaDB and a model folder) is replaced by a single .NET 10 application that talks to Ollama or any OpenAI-compatible server.

### New

- **`localrag` command line tool** — `index`, `ask`, `chat`, `search`, `list`, `remove`, `reindex`, `doctor` and `serve`. Install it with `dotnet tool install -g LocalRag` or download a single self-contained file for Windows, Linux or macOS.
- **Local web app** (`localrag serve`) in a Liquid Glass design: streaming answers with clickable citations, a source panel, drag-and-drop uploads, folder indexing with progress, and a document library. Light and dark mode, works on phones.
- **Documents:** PDF (page by page), Word `.docx`, HTML, Markdown, plain text, CSV/JSON/YAML and source code. Text files are decoded with BOM detection and a legacy-encoding fallback.
- **Chunking that follows the text:** paragraphs, then sentences, then words, with overlap. Chunks never cross a page, so every answer cites the right page.
- **Hybrid search:** vector similarity and SQLite FTS5 (BM25) keyword search, fused with reciprocal rank fusion. Exact terms such as names, codes and error numbers are found as reliably as paraphrases.
- **Answers with sources:** the model gets numbered passages and must cite them; follow-up questions keep the conversation.
- **Incremental indexing:** unchanged files are skipped by content hash, changed files are re-embedded, files deleted from an indexed folder are removed.
- **Providers:** Ollama (native API, `think: false` for reasoning models) and OpenAI-compatible servers such as LM Studio, llama.cpp, vLLM, LocalAI and Jan. Reasoning blocks (`<think>…</think>`) are removed from streamed answers.
- **Safety net:** the index remembers its embedding model and refuses to mix vectors from different models; `localrag doctor` explains missing models and unreachable servers.
- **LocalRag.Core library** on NuGet for embedding the same pipeline in your own application.
- Sample documents (a fictional employee handbook, one page in Turkish) to try everything in a minute.

### Fixed (compared with 1.x)

- Questions crashed the API server: documents were stored without their text, so every query failed while joining the results.
- PDF files could not be indexed (they were read as UTF-8 text), even though the example used one.
- Only the first ~256 tokens of each file were searchable, because a whole file was embedded as a single vector.
- The model weights were not in the repository, and a second `strict=False` load hid loading errors.
- The prompt placed the retrieved text where the answer belongs, and the reply repeated the prompt.
- The indexing server read any file path it was sent; the new web app accepts uploads into its own folder and only listens on 127.0.0.1.
- The console was cleared every second, hiding the server log.

### Quality

- 57 unit and integration tests (documents, chunking, both AI protocols, the store, the engine, settings and the web API) on Windows, Linux and macOS in CI.
- Release workflow: single-file apps for four platforms, checksums, NuGet packages.
