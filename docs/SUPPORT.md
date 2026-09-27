# Support and acceptance gates

Version `0.3.0-preview.2` targets Python 3.10, .NET 10 and local Chroma on Windows first. Default extraction remains model-free; generation is opt-in. CI declares Python/C# fixture checks on Ubuntu and Windows; remote CI results must be observed after pushing. Synthetic protocol tests and dated actual Qwen2.5-1.5B / llama.cpp CPU results are distinguished in [LOCAL_MODEL](LOCAL_MODEL.md). Schema-constrained format consistency does not establish factual or conflict-handling quality.

| Gate | Local evidence | Still needed |
| --- | --- | --- |
| Ingest/retrieval | TXT and generated text-PDF, UTF-8 Turkish, source excerpt, re-ingest and delete via FastAPI TestClient | OCR/scanned PDFs, larger corpus and permission model |
| C# path | .NET 10 Release build; 13 tests including real TCP mode/Unicode, body cancel/deadline, redirects/errors and response limits; four CLI → Uvicorn → synthetic generation flows | Packaged CLI on a clean user profile and other OSes; native Ctrl+C console test |
| Evaluation | 20/20 synthetic fixture, changed source turns an expectation red | Real user questions, relevance labels and cross-language paraphrases |
| Generative protocol | 42 Python cases including prior ingest/evaluation checks, real synthetic loopback HTTP, source/citation/model-alias/deadline/budget rejection and no silent fallback; actual Qwen2.5-1.5B requests exposed and corrected missing-field output | Genuine-model response quality and citation support still fail selected cases; real corpus, held-out paraphrases and conflicting evidence need acceptance |
| Model provenance | Explicit requested/reported alias match; operator-declared server kind disclosed | Alias/kind labels do not certify weights/runtime. Verify separately selected official model/runtime hashes/licences and record actual runs |
| Operations | Loopback defaults, 2 MiB/50-page limits, no remote C# target | Auth if sharing, backup/restore, dependency security review |

The new index uses a separate Chroma collection. Source files can be re-ingested with the same IDs. The API does not migrate a previous `chroma_db` folder, and no existing folder should be deleted automatically. Stop the server with Ctrl+C before changing environments. `.rag-data` is ignored by Git but stays on disk until its owner removes it.

[Local generation setup and EN/TR limits](LOCAL_MODEL.md) documents opt-in configuration, official reference sources and dated genuine-model observations. No model/runtime is included or automatically downloaded/started. Client cancellation stops its wait; cancellation of backend inference is not claimed. Real-model format results do not close the quality, fresh-machine, CI or public release gates.
