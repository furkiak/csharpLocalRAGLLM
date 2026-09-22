"""Local-only HTTP API for the grounded retrieval sample."""
import re
from functools import lru_cache

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from rag_service import DocumentError, RagService, RagSettings

app = FastAPI(title="C# Local RAG", version="0.2.0-preview.1")


class DocumentRequest(BaseModel):
    file_path: str = Field(min_length=1, max_length=1024)
    category: str = Field(min_length=1, max_length=40)
    unique_id: str = Field(min_length=1, max_length=64)


class QueryRequest(BaseModel):
    query: str = Field(min_length=2, max_length=500)
    category: str | None = Field(default=None, max_length=40)


@lru_cache(maxsize=1)
def service() -> RagService:
    return RagService(RagSettings.from_environment())


def check_id(unique_id: str) -> None:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", unique_id):
        raise HTTPException(422, "ID must contain only letters, numbers, _ or -.")


@app.post("/documents")
def add_document(request: DocumentRequest):
    check_id(request.unique_id)
    if not request.category.strip():
        raise HTTPException(422, "Category cannot be blank.")
    try:
        return service().ingest(request.file_path, request.category.strip(), request.unique_id)
    except DocumentError as error:
        raise HTTPException(error.status_code, str(error)) from error


@app.delete("/documents/{unique_id}")
def delete_document(unique_id: str):
    check_id(unique_id)
    return service().delete(unique_id)


@app.post("/query")
def query_document(request: QueryRequest):
    return service().query(request.query.strip(), request.category)


@app.get("/health")
def health():
    return {"status": "ready", "mode": "offline-extractive"}
