"""Run the 20-question source/no-answer fixture without an LLM download."""
import json
import sys
from pathlib import Path

import chromadb
from chromadb.config import Settings as ChromaSettings

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from rag_service import RagService, RagSettings  # noqa: E402


def evaluate(service: RagService, cases: list[dict]) -> list[dict]:
    results = []
    for case in cases:
        actual = service.query(case["question"])
        source = actual["sources"][0]["source_id"] if actual["sources"] else None
        expected = case["source_id"]
        passed = source == expected and (
            expected is None or case["contains"].casefold() in actual["answer"].casefold()
        )
        results.append({"question": case["question"], "expected_source": expected,
                        "actual_source": source, "status": actual["status"], "pass": passed,
                        "answer": actual["answer"]})
    return results


def main() -> int:
    cases = json.loads((ROOT / "eval" / "questions.json").read_text(encoding="utf-8"))
    service = RagService(RagSettings(ROOT / "samples", ROOT / ".rag-data"),
                         client=chromadb.EphemeralClient(
                             settings=ChromaSettings(anonymized_telemetry=False)),
                         collection_name="evaluation_documents")
    service.ingest("handbook.md", "example", "handbook")
    results = evaluate(service, cases)
    report = {"passed": sum(row["pass"] for row in results), "total": len(results),
              "mode": "offline-extractive; lexical hash retrieval; no LLM inference", "results": results}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["passed"] == report["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
