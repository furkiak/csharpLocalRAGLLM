# Optional local generation: separate from extractive retrieval

The default is still **extractive**: lexical retrieval returns a source sentence or `no_answer`, without model weights or inference. **Generative** is an explicit request mode that sends at most three retrieved excerpts to an already running, manually configured loopback llama.cpp-compatible server. It does not download/start a model, select a cloud provider, send an API key or silently fall back to an extractive answer.

The adapter uses [`/v1/chat/completions` and schema-constrained `response_format`](https://github.com/ggml-org/llama.cpp/blob/b11208/tools/server/README.md#post-v1chatcompletions-openai-compatible-chat-completions-api). The llama.cpp dialect is `{"type":"json_object","schema":...}`: required `answer` and `citations`, no additional fields, a nullable string answer, and citation strings limited to supplied IDs. A supported model chat template is needed. Temperature is zero, `stream` is false and output is limited to 384 tokens. The application still validates JSON, sizes, completed response, model alias and citations; a server may ignore or reject the schema. There is no plain-JSON retry or silent extractive fallback. Correct output shape is not factual grounding.

## Actual model observations, 28 September 2026

The user manually started verified llama.cpp b11208 CPU files and Qwen2.5-1.5B-Instruct Q4_K_M on 127.0.0.1:12403 with two CPU threads. Official archive and model SHA-256 values, extracted runtime bytes, health, model listing, startup/inference logs and actual responses were recorded outside the source package. Process executable/command-line access was unavailable at the agent privilege level, so this is corroborated run evidence, not cryptographic server identity. An API alias alone is never treated as proof.

The unchanged earlier source matched **14/20** tailored source/literal cases: six eligible model answers failed strict response validation. A captured real envelope omitted `citations`; the client correctly returned `generation_error` without fallback. The schema correction's working-source run matched **19/20**, with all 12 eligible answers structurally valid and eight absent-source queries rejected before inference. These small invented cases are not a general accuracy score. The first return-period answer was a generic definition instead of the supplied 30-day fact; a contradictory policy question produced an unrelated definition with a valid S1 ID. Both remain quality failures. A single injected instruction was ignored, but that one result is not general prompt-injection resistance. An unsupported payment-channel answer restated the source rather than abstaining, so abstention quality also needs review.

Three actual packaged C# → separate Uvicorn → genuine model flows for baseline `5f695df` had EN/TR generation errors and an absent-source no-answer. The schema-corrected preview.2 package at `897140a` subsequently matched 19/20 tailored cases without format errors; all three packaged CLI flows exited 0, including EN/TR answers and an absent-source no-answer. The exact preview.3 package at `9f5ee58` was then checked separately in a fresh Python 3.12 environment: 52 Python and 13 C# tests passed without warnings or skips. Its genuine-model run matched 19/20 tailored cases, completed 12 model requests, rejected eight absent-source questions before inference, and produced no format errors. Three packaged C# → API → genuine-model flows exited 0, including English and Turkish answers and an absent-source no-answer. The generic return-period answer, conflicting-source answer and unsupported-payment-channel answer still failed content review. Owned API processes exited and the user-owned model stayed running. Real documents, held-out paraphrases, conflicting evidence, physical Ctrl+C, other environments, remote CI and public release remain open. Always inspect the cited excerpt before using a generated answer.

## Runtime and model ownership

Choose/download a runtime and model separately, verify their official release/model metadata and licence, and keep the binaries/weights outside this repository/package. For the separate local validation, the selected reference is [Qwen/Qwen2.5-1.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF), Q4_K_M, Apache-2.0, revision `91cad51170dc346986eccefdc2dd33a9da36ead9`.

Recorded expected GGUF size: **1,117,320,736 bytes**; SHA-256: `6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e`. Check the selected official file/revision before using it; matching a weight hash does not prove runtime behaviour, source grounding or model quality. The repository remains GPL-3.0; runtime/model licences remain separate. The old `LLMModel/` tokenizer/config directory is not used by this adapter and still contains no weights.

For an already verified local runtime/model, a manual server command can use an explicit alias:

```powershell
# Paths are examples; use the verified local runtime/model files.
& 'C:\LocalModels\llama-server.exe' -m 'C:\LocalModels\qwen2.5-1.5b-instruct-q4_k_m.gguf' --alias qwen2.5-1.5b-local --host 127.0.0.1 --port 8080 -c 4096
```

This guide does not automatically run that command. Verify the selected runtime's flags/help and keep it bound to loopback. CPU latency depends on the machine and context length. No real-model performance result is claimed by the protocol fixtures.

## Configure the API explicitly

In the terminal that will start the Python API:

```powershell
$env:RAG_GENERATION_URL = 'http://127.0.0.1:8080/v1/chat/completions'
$env:RAG_GENERATION_MODEL = 'qwen2.5-1.5b-local'
$env:RAG_GENERATION_TIMEOUT_SECONDS = '60'
$env:RAG_GENERATION_KIND = 'local_model'
python -m uvicorn app:app --host 127.0.0.1 --port 8000
```

Use a **literal loopback IP** (`127.0.0.1` or `::1`), HTTP and that exact endpoint path. Hostnames, remote addresses, HTTPS, URL credentials/query/fragment and redirects are rejected. The client does not use HTTP proxy settings or authorization headers. A loopback server can itself forward data; local address validation cannot certify its forwarding policy. These variables are process-local in the shown shell; the API reads them when its retrieval service is constructed. Restart the API to change its server configuration, not indexed data.

`RAG_GENERATION_KIND` is **operator-declared**, one of `unverified` (default), `synthetic_fixture`, `local_model`. It changes the disclosure label; it is **not proof that a genuine model ran**. Synthetic HTTP tests always declare `synthetic_fixture`. The response model alias must exactly match the requested alias; this is protocol consistency, not verification of the weights or server identity. The `/health` endpoint reports configuration presence and always says runtime verification is false; it does not probe/load a model.

The generation deadline defaults to 30 seconds, configurable from 1 to 120. It covers connect, headers and body with an overall abort timer; a slowly trickling response cannot extend the deadline indefinitely. Keep the CLI deadline longer than the API generation deadline:

```powershell
dotnet run --project CSharpLocalRag.csproj -- ask --mode generative --timeout 90 'What is the return period?'
```

Ingest the sample handbook first using the normal `add` command. `ask <question>` remains extractive. CLI JSON goes to stdout; mode/provenance labels go to stderr. `Ctrl+C` cancels the client's HTTP wait and returns exit 130; timeout returns exit 1. The API/model server may continue until its own deadline. No guarantee that client cancellation stops inference is claimed.

## Result and acceptance boundaries

Retrieval is the same lexical hash-vector/overlap gate in both modes. No eligible source means `no_answer` / `reason: retrieval_gate` and no generation request, even if model configuration is absent or wrong. This gate is a literal-overlap heuristic, not semantic relevance or a factual evidence validator.

| Result | Meaning |
| --- | --- |
| `grounded`, `mode: extractive` | Existing source sentence and reference; no inference |
| `generated`, `mode: generative` | Completed model JSON with nonempty answer and valid supplied citation IDs; **facts are not independently checked** |
| `no_answer`, `reason: model_abstained` | The model explicitly returned `answer: null, citations: []` |
| `generation_error` | Configuration/connection/HTTP/timeout/size/JSON/finish/model-alias/citation failure; answer/sources/citations are empty; **no silent fallback** |

The response includes raw source references/excerpts for cited `S1`..`S3` IDs, the requested/reported alias, declared server kind and `citation_validation: provided_ids_only; factual correctness is not verified`. CLI returns exit 1 for `generation_error`, exit 0 for valid output or explicit `no_answer`.

Limits: at most three excerpts, each 2 KiB UTF-8, all context metadata/excerpts at most 8 KiB, question at most 500 characters, 64 KiB HTTP response, 4 KiB answer and 384 requested output tokens. Duplicate JSON fields, unknown/empty/duplicate citations, malformed JSON, non-stop/truncated completion, tool calls and alias mismatch are rejected. Model errors/bodies and partial answers are not echoed as output.

Sources are serialized as untrusted JSON data under a fixed system instruction; no tools are provided or executed. Source-instruction tests prove structural separation and rejection of invented IDs. They do **not** prove a real model ignores every injected instruction, supports every assertion with the right source, or avoids hallucinations with an existing citation ID. A valid ID alone cannot establish that a claim is true or that an excerpt supports it. Review the cited text.

## Validation

Build the C# CLI before Python tests so full CLI tests can execute:

```powershell
dotnet build CSharpLocalRag.csproj -c Release
dotnet test CSharpLocalRag.Tests/CSharpLocalRag.Tests.csproj -c Release
python -m pytest -q
python eval/run.py
```

Local protocol/source evidence: 52 Python cases (including the previous ingest/PDF/20-question mutation tests), 13 C# cases, and four full C# CLI → Uvicorn API → synthetic HTTP flow tests. Those four are included in the Python count. If the CLI has not been built, they explicitly skip; skipped cases are not passed evidence. The actual loopback HTTP fixtures test citations, malformed/truncated output, redirects/401/500, header/body deadline, context/response limits, model-alias mismatch, cancellation of client body reads, error handling and explicit synthetic labels. They do not load a model.

With a separately started/verified server and the above process settings, a manual real-model evaluation is:

```powershell
python eval/run.py --mode generative
```

The evaluator checks expected source plus a literal answer substring, or explicit `no_answer`. A generation failure does not count as a correct no-answer. It reports each response's declared kind/alias and errors, but **does not certify runtime provenance**. Its 20 small tailored questions are not a general quality score. The dated local run above includes model/runtime hash checks, actual requests, English/Turkish answers and absent-source behaviour. Conflicting sources, unsupported claims, held-out paraphrases, wrong-but-existing citations and adversarial source instructions need broader review. Generative quality acceptance and public release remain open despite the recorded run.

## Türkçe

Varsayılan mod belge cümlesini aynen döndürür; LLM çalıştırmaz. `--mode generative` yalnız açıkça tanımlanan yerel HTTP sunucusunu çağırır. Kaynak yoksa model çağrılmaz; model hatasında sahte veya sessiz extractive cevap üretilmez. `S1`..`S3` kontrolü kaynağın gerçekten gönderilmiş olduğunu sınar, cevabın doğru olduğunu kanıtlamaz. `RAG_GENERATION_KIND=local_model` operatör etiketidir; gerçek model çalıştığına dair kanıt değildir. Sahte HTTP testleri ile gerçek model değerlendirmesi ayrıdır. Ctrl+C istemcinin bekleyişini iptal eder; sunucunun inference işlemini her koşulda durdurduğu söylenmez. Model/ağırlık/runtime bu paketle gelmez; lisansları ve hash'leri ayrıca doğrulanır.
