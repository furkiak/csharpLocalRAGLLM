"""Offline retrieval core for the C# / Python teaching sample."""
from __future__ import annotations

import hashlib
import math
import os
import re
from dataclasses import dataclass
from pathlib import Path

import chromadb
from chromadb.config import Settings as ChromaSettings
from pypdf import PdfReader

VECTOR_SIZE = 384
MAX_FILE_BYTES = 2 * 1024 * 1024
STOP_WORDS = set("a an and are for from how in is of on the to what when where which who with your bir bu da de için ile mi mı mu mü ne nedir nasıl kaç ve var olan hangi nerede".split())


class DocumentError(ValueError):
    def __init__(self, message: str, status_code: int = 422):
        super().__init__(message)
        self.status_code = status_code


def words(text: str) -> list[str]:
    normalized = text.casefold().translate(str.maketrans({"ı": "i", "ğ": "g", "ş": "s", "ç": "c", "ö": "o", "ü": "u"}))
    return [word for word in re.findall(r"\w+", normalized) if word not in STOP_WORDS and len(word) > 1]


def embedding(text: str) -> list[float]:
    vector = [0.0] * VECTOR_SIZE
    for token in words(text):
        index = int.from_bytes(hashlib.sha256(token.encode("utf-8")).digest()[:4], "big") % VECTOR_SIZE
        vector[index] += 1.0
    magnitude = math.sqrt(sum(value * value for value in vector))
    return [value / magnitude for value in vector] if magnitude else vector


def chunk_text(text: str, size: int = 110, overlap: int = 20) -> list[str]:
    spans = list(re.finditer(r"\S+", text))
    chunks = []
    for start in range(0, len(spans), size - overlap):
        end = min(start + size, len(spans))
        chunks.append(text[spans[start].start():spans[end - 1].end()])
        if end == len(spans):
            break
    return chunks


@dataclass(frozen=True)
class RagSettings:
    document_root: Path
    db_path: Path

    @classmethod
    def from_environment(cls) -> "RagSettings":
        project = Path(__file__).resolve().parent
        return cls(Path(os.environ.get("RAG_DOCUMENT_ROOT", project / "samples")).resolve(),
                   Path(os.environ.get("RAG_DB_PATH", project / ".rag-data")).resolve())


class RagService:
    def __init__(self, settings: RagSettings, client=None, collection_name: str = "documents_v1"):
        self.settings = RagSettings(settings.document_root.resolve(), settings.db_path.resolve())
        self.settings.document_root.mkdir(parents=True, exist_ok=True)
        self.settings.db_path.mkdir(parents=True, exist_ok=True)
        self.client = client or chromadb.PersistentClient(
            path=str(self.settings.db_path), settings=ChromaSettings(anonymized_telemetry=False))
        self.collection = self.client.get_or_create_collection(
            name=collection_name, metadata={"hnsw:space": "cosine"})

    def allowed_file(self, file_path: str) -> Path:
        root = self.settings.document_root.resolve()
        proposed = Path(file_path)
        resolved = (proposed if proposed.is_absolute() else root / proposed).resolve()
        if not resolved.is_relative_to(root):
            raise DocumentError("File must stay inside RAG_DOCUMENT_ROOT.", 403)
        if not resolved.is_file():
            raise DocumentError("File not found.", 404)
        if resolved.suffix.lower() not in {".txt", ".md", ".pdf"}:
            raise DocumentError("Only TXT, Markdown and PDF files are supported.")
        if resolved.stat().st_size > MAX_FILE_BYTES:
            raise DocumentError("File exceeds the 2 MiB limit.", 413)
        return resolved

    def read_blocks(self, path: Path) -> list[tuple[str, int]]:
        if path.suffix.lower() == ".pdf":
            try:
                reader = PdfReader(str(path))
                if len(reader.pages) > 50:
                    raise DocumentError("PDF exceeds the 50-page limit.", 413)
                blocks = [(page.extract_text() or "", number + 1)
                          for number, page in enumerate(reader.pages)]
            except DocumentError:
                raise
            except Exception as error:
                raise DocumentError("PDF could not be read.") from error
        else:
            try:
                blocks = [(path.read_text(encoding="utf-8"), 0)]
            except (UnicodeError, OSError) as error:
                raise DocumentError("File must contain readable UTF-8 text.") from error
        if not any(words(text) for text, _ in blocks):
            raise DocumentError("Document has no searchable text.")
        return blocks

    def ingest(self, file_path: str, category: str, unique_id: str) -> dict:
        path = self.allowed_file(file_path)
        sections = [(chunk, page) for text, page in self.read_blocks(path)
                    for chunk in chunk_text(text) if words(chunk)]
        if len(sections) > 200:
            raise DocumentError("Document exceeds the 200-chunk limit.", 413)
        source_path = path.relative_to(self.settings.document_root).as_posix()
        ids = [f"{unique_id}:{index:04d}" for index in range(len(sections))]
        existing = set(self.collection.get(where={"source_id": unique_id})["ids"])
        self.collection.upsert(
            ids=ids,
            documents=[chunk for chunk, _ in sections],
            embeddings=[embedding(chunk) for chunk, _ in sections],
            metadatas=[{"source_id": unique_id, "source_path": source_path,
                        "category": category, "page": page, "chunk": index}
                       for index, (_, page) in enumerate(sections)])
        stale = existing - set(ids)
        if stale:
            self.collection.delete(ids=list(stale))
        return {"source_id": unique_id, "source_path": source_path, "chunks": len(ids)}

    def delete(self, unique_id: str) -> dict:
        existing = self.collection.get(where={"source_id": unique_id})["ids"]
        if existing:
            self.collection.delete(ids=existing)
        return {"source_id": unique_id, "deleted_chunks": len(existing)}

    def query(self, question: str, category: str | None = None) -> dict:
        question_words = set(words(question))
        if not question_words or not self.collection.count():
            return {"status": "no_answer", "answer": None, "sources": []}
        kwargs = {"where": {"category": category}} if category else {}
        result = self.collection.query(
            query_embeddings=[embedding(question)], n_results=min(12, self.collection.count()),
            include=["documents", "metadatas", "distances"], **kwargs)
        candidates = []
        for document, meta, distance in zip(
            result["documents"][0], result["metadatas"][0], result["distances"][0]):
            overlap = len(question_words & set(words(document))) / len(question_words)
            if overlap >= 0.5:
                candidates.append((overlap, 1.0 - distance, document, meta))
        if not candidates:
            return {"status": "no_answer", "answer": None, "sources": []}
        candidates.sort(key=lambda item: (item[0], item[1]), reverse=True)
        _, similarity, document, meta = candidates[0]
        sentences = [part.strip() for part in re.split(r"(?<=[.!?])\s+|\n+", document) if part.strip()]
        sentence = max(sentences, key=lambda part: len(question_words & set(words(part))))
        return {"status": "grounded", "answer": sentence, "sources": [{
            "source_id": meta["source_id"], "source_path": meta["source_path"],
            "page": meta["page"] or None, "chunk": meta["chunk"],
            "similarity": round(similarity, 4), "excerpt": sentence}]}
