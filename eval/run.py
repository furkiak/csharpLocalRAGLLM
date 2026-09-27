"""Run literal source/no-answer evaluation; generative mode is an explicit opt-in."""
import argparse
import json
import sys
from tempfile import TemporaryDirectory
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from rag_service import RagService, RagSettings  # noqa: E402
from generation_adapter import GenerationSettings  # noqa: E402


def evaluate(service: RagService, cases: list[dict], mode: str = "extractive") -> list[dict]:
    results = []
    for case in cases:
        actual = service.query(case["question"], mode=mode)
        source = actual["sources"][0]["source_id"] if actual["sources"] else None
        expected = case["source_id"]
        passed = (actual["status"] == "no_answer" and actual["answer"] is None) if expected is None else (
            actual["status"] in {"grounded", "generated"} and source == expected
            and isinstance(actual["answer"], str) and case["contains"].casefold() in actual["answer"].casefold())
        results.append({"question": case["question"], "expected_source": expected,
                        "actual_source": source, "status": actual["status"], "pass": passed,
                        "answer": actual["answer"], "mode": actual["mode"], "citations": actual.get("citations", []),
                        "generation": actual.get("generation"), "error": actual.get("error")})
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=["extractive", "generative"], default="extractive")
    options = parser.parse_args()
    cases = json.loads((ROOT / "eval" / "questions.json").read_text(encoding="utf-8"))
    with TemporaryDirectory(prefix="rag-evaluation-") as folder:
        service = RagService(RagSettings(ROOT / "samples", Path(folder), GenerationSettings.from_environment()))
        service.ingest("handbook.md", "example", "handbook")
        results = evaluate(service, cases, options.mode)
    report = {"passed": sum(row["pass"] for row in results), "total": len(results),
              "mode": options.mode, "criteria": "Expected source + literal answer substring, or explicit no_answer; not a general quality/grounding score",
              "runtime_provenance_verified_by_evaluator": False, "results": results}
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["passed"] == report["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
