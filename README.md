# C# Local RAG — grounded retrieval preview

**A reproducible C# client + Python API that returns a source excerpt or an explicit no-answer.** The demo indexes local TXT, Markdown and text-based PDF files in Chroma. It uses deterministic lexical hash vectors and an extractive answer, so no model weights, cloud API or fabricated LLM answer are needed for the first run. This is a teaching prototype, not a production semantic search system.

![Architecture of the current extractive preview, with the optional LLM step marked as future work](docs/flow.svg)

> **0.2.0-preview.1 · local source preview.** The earlier `LLMModel/` directory contains tokenizer/config files but **no model weights**. The former API could not load a usable LLM from a clean checkout. The current `/query` path is honestly labelled `offline-extractive`; generative inference is not part of this tested release.

## Quick start

Use Python 3.10 and .NET SDK 10. On Windows PowerShell, from a clean checkout:

```powershell
py -3.10 -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements-dev.txt
.\.venv\Scripts\python -m pytest -q
.\.venv\Scripts\python eval/run.py
.\.venv\Scripts\python -m uvicorn app:app --host 127.0.0.1 --port 8000
```

Keep that terminal open. In another terminal in the same checkout:

```powershell
dotnet run --project CSharpLocalRag.csproj -- add samples/handbook.md example handbook
dotnet run --project CSharpLocalRag.csproj -- ask "What is the return period?"
dotnet run --project CSharpLocalRag.csproj -- ask "What is the shipping fee?"
```

The first question returns the exact `30 days` source sentence with `source_id`, relative path and chunk; the second returns `status: no_answer`. The sample handbook is synthetic. On Linux/macOS replace the first command with `python3.10 -m venv .venv` and use `.venv/bin/python` for Python commands. The C# project needs the .NET 10 SDK on the chosen platform.

`requirements.txt` pins tested **direct** dependencies, including NumPy 1.26 for Chroma 0.4.24; it is not a full transitive lock. The sample loads neither `torch` nor `transformers`. Python's package installer still fetches the pinned packages from the configured package index. Run `python -m pytest -q` and `python eval/run.py` after dependency upgrades.

## How the sample works

1. Put a UTF-8 TXT/MD or text-based PDF under `samples/`, or set `RAG_DOCUMENT_ROOT` to a dedicated local directory. Only files resolving inside that root are accepted. The API rejects other paths, unsupported formats, files over 2 MiB and PDFs over 50 pages. Scanned image-only PDFs have no searchable text and are rejected.
2. `POST /documents` extracts text, makes overlapping chunks, stores the **document text**, source metadata and deterministic vectors in local Chroma. Reusing an ID updates its chunks and removes stale ones. `DELETE /documents/{id}` removes them.
3. `POST /query` uses the same vector function for the question, then a lexical overlap gate. It returns a sentence copied from the best eligible chunk and its source reference, or `no_answer` when no chunk passes. A matching word is not proof of a correct factual answer; review excerpts before using them.
4. The C# CLI uses structured JSON and checks HTTP status. It allows only a loopback API URL, so adding a file cannot silently send its path to a remote endpoint.

The API defaults to `127.0.0.1:8000`; don't bind it to a public interface. `RAG_DB_PATH` changes the local Chroma folder (default `.rag-data/`), and `RAG_API_URL` changes the C# client's **loopback** target. Local files and document text remain on this machine in the tested path, but every program with access to this unauthenticated loopback API can query indexed content. Use a dedicated sample directory, not private documents, for a demo.

## Evaluation and limits

`python eval/run.py` prints a 20-question report: 12 expected source excerpts and eight explicit no-answer cases, in English and Turkish. The test suite changes one fixture fact to ensure the evaluation turns red. The fixture is small and tailored to literal overlap; its score does **not** predict retrieval quality on real documents. Long PDF extraction, OCR, prompt injection, cross-language paraphrases, ranking quality, model licence/weights, and generative groundedness remain open work. The previous DeepSeek naming and alternative vector database comments were unverified promises; this preview supports Chroma only.

Source changes are in [CHANGELOG.md](CHANGELOG.md); current release gates are in [docs/SUPPORT.md](docs/SUPPORT.md). The repository code remains [GPL-3.0](LICENSE). Existing `LLMModel/` metadata has its own Apache-2.0 license file; no weight file is bundled, downloaded or implied by this project.

## Türkçe

Bu örnek, C# istemcisi ve Python API ile yerel belgelerden **doğrudan kaynak pasajı** ya da açık `no_answer` sonucu verir. Şu sürüm üretken LLM çalıştırmaz; depodaki model ağırlıkları eksiktir. Temiz kurulum komutları yukarıdadır. Sadece izin verilen yerel klasördeki TXT/MD ve metin içeren PDF dosyalarını kullan; gerçek özel belgeleri deneme koleksiyonuna koyma. `eval/run.py` içindeki 20 soru eğitim amaçlı küçük bir ölçümdür, gerçek kullanım başarısı kanıtı değildir.
