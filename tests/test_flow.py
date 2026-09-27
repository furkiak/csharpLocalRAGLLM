import json
import shutil
from pathlib import Path

from fastapi.testclient import TestClient
from reportlab.pdfgen import canvas

from app import app, service
from eval.run import evaluate
from rag_service import RagService, RagSettings


def test_http_ingest_query_pdf_replacement_and_boundaries(tmp_path, monkeypatch):
    docs = tmp_path / "docs"
    docs.mkdir()
    monkeypatch.setenv("RAG_DOCUMENT_ROOT", str(docs))
    monkeypatch.setenv("RAG_DB_PATH", str(tmp_path / "db"))
    service.cache_clear()
    client = TestClient(app)

    text = docs / "faq.txt"
    text.write_text('İade süresi 30 gündür. "Özel" ürünler destek kapsamındadır.', encoding="utf-8")
    response = client.post("/documents", json={"file_path": "faq.txt", "category": "test", "unique_id": "faq"})
    assert response.status_code == 200, response.text
    assert response.json()["chunks"] == 1
    result = client.post("/query", json={"query": "İade süresi kaç gündür?"}).json()
    assert result["status"] == "grounded"
    assert "30 gündür" in result["answer"]
    assert result["sources"][0]["source_path"] == "faq.txt"
    quoted = client.post("/query", json={"query": '"Özel" ürünler destek kapsamındadır?'}).json()
    assert quoted["status"] == "grounded"
    assert '"Özel" ürünler' in quoted["answer"]
    assert client.post("/query", json={"query": "Kargo fiyatı nedir?"}).json()["status"] == "no_answer"

    pdf = docs / "manual.pdf"
    paper = canvas.Canvas(str(pdf))
    paper.drawString(40, 750, "PDF warranty lasts 18 months.")
    paper.save()
    response = client.post("/documents", json={"file_path": "manual.pdf", "category": "test", "unique_id": "pdf"})
    assert response.status_code == 200, response.text
    pdf_answer = client.post("/query", json={"query": "What is the PDF warranty?"}).json()
    assert "18 months" in pdf_answer["answer"]
    assert pdf_answer["sources"][0]["page"] == 1

    (docs / "bad.pdf").write_bytes(b"not a pdf")
    assert client.post("/documents", json={"file_path": "bad.pdf", "category": "test", "unique_id": "bad"}).status_code == 422
    outside = tmp_path / "private.txt"
    outside.write_text("private fixture", encoding="utf-8")
    assert client.post("/documents", json={"file_path": str(outside), "category": "test", "unique_id": "outside"}).status_code == 403
    assert client.post("/documents", json={"file_path": "faq.txt", "category": "test", "unique_id": "../bad"}).status_code == 422
    assert client.post("/documents", json={"file_path": "faq.txt", "category": " ", "unique_id": "blank"}).status_code == 422
    assert client.post("/query", json={"query": "x"}).status_code == 422

    text.write_text("Updated answer: The return period is 45 days.", encoding="utf-8")
    assert client.post("/documents", json={"file_path": "faq.txt", "category": "test", "unique_id": "faq"}).status_code == 200
    updated = client.post("/query", json={"query": "What is the updated return period?"}).json()
    assert "45 days" in updated["answer"]
    assert client.delete("/documents/faq").json()["deleted_chunks"] == 1
    assert client.post("/query", json={"query": "What is the updated return period?"}).json()["status"] == "no_answer"
    service.cache_clear()


def test_20_question_report_detects_changed_source(tmp_path):
    root = Path(__file__).resolve().parents[1]
    cases = json.loads((root / "eval" / "questions.json").read_text(encoding="utf-8"))
    docs = tmp_path / "docs"
    docs.mkdir()
    shutil.copyfile(root / "samples" / "handbook.md", docs / "handbook.md")
    rag = RagService(RagSettings(docs, tmp_path / "ephemeral"))
    rag.ingest("handbook.md", "example", "handbook")
    original = evaluate(rag, cases)
    assert len(original) == 20
    assert all(row["pass"] for row in original)

    path = docs / "handbook.md"
    path.write_text(path.read_text(encoding="utf-8").replace("30 days", "45 days"), encoding="utf-8")
    rag.ingest("handbook.md", "example", "handbook")
    changed = evaluate(rag, cases)
    assert not changed[0]["pass"]
    assert sum(row["pass"] for row in changed) < 20
