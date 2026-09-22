# Support and acceptance gates

Version `0.2.0-preview.1` targets Python 3.10, .NET 10 and local Chroma on Windows first. CI also runs the Python fixture and C# build on Ubuntu; CI results must be observed after pushing. The local tests use synthetic text and a generated one-page PDF.

| Gate | Local evidence | Still needed |
| --- | --- | --- |
| Ingest/retrieval | TXT and generated text-PDF, UTF-8 Turkish, source excerpt, re-ingest and delete via FastAPI TestClient | OCR/scanned PDFs, larger corpus and permission model |
| C# path | .NET 10 Release build and loopback HTTP add/answer/no-answer run | Packaged CLI on a clean user profile and other OSes |
| Evaluation | 20/20 synthetic fixture, changed source turns an expectation red | Real user questions, relevance labels and cross-language paraphrases |
| Generative LLM | No claim: weight files absent | Pick/licence/download a model, verify grounding and no-answer policy |
| Operations | Loopback defaults, 2 MiB/50-page limits, no remote C# target | Auth if sharing, backup/restore, dependency security review |

The new index uses a separate Chroma collection. Source files can be re-ingested with the same IDs. The API does not migrate a previous `chroma_db` folder, and no existing folder should be deleted automatically. Stop the server with Ctrl+C before changing environments. `.rag-data` is ignored by Git but stays on disk until its owner removes it.
