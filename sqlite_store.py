"""Small-corpus lexical vectors in SQLite; no executable index deserialization."""
from __future__ import annotations

from contextlib import contextmanager
import json
from pathlib import Path
import sqlite3

MAX_INDEX_CHUNKS = 10000


class IndexLimitError(ValueError):
    pass


class SqliteStore:
    def __init__(self, folder: Path):
        self.path = folder / "rag-v2.sqlite3"
        with self.connection() as connection:
            connection.execute("BEGIN IMMEDIATE")
            version = connection.execute("PRAGMA user_version").fetchone()[0]
            if version not in (0, 2):
                raise ValueError("Unsupported index version; preserve it and use a new RAG_DB_PATH.")
            tables = {row[0] for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")}
            if version == 0 and tables:
                raise ValueError("Existing unversioned database is not this index; preserve it and use a new RAG_DB_PATH.")
            if version == 2:
                columns = [(row[1], row[2], row[3], row[5]) for row in connection.execute("PRAGMA table_info(chunks)")]
                expected = [("source_id", "TEXT", 1, 1), ("chunk", "INTEGER", 1, 2),
                            ("source_path", "TEXT", 1, 0), ("category", "TEXT", 1, 0),
                            ("page", "INTEGER", 1, 0), ("document", "TEXT", 1, 0), ("vector", "TEXT", 1, 0)]
                if tables != {"chunks"} or columns != expected:
                    raise ValueError("Unsupported index schema; preserve it and use a new RAG_DB_PATH.")
                return
            connection.execute("""CREATE TABLE IF NOT EXISTS chunks (
                source_id TEXT NOT NULL, chunk INTEGER NOT NULL,
                source_path TEXT NOT NULL, category TEXT NOT NULL,
                page INTEGER NOT NULL, document TEXT NOT NULL, vector TEXT NOT NULL,
                PRIMARY KEY (source_id, chunk))""")
            connection.execute("CREATE INDEX IF NOT EXISTS chunks_category ON chunks(category)")
            connection.execute("PRAGMA user_version = 2")

    @contextmanager
    def connection(self):
        connection = sqlite3.connect(self.path, timeout=5)
        try:
            with connection:
                yield connection
        finally:
            connection.close()

    def replace_document(self, source_id: str, rows: list[tuple]) -> None:
        with self.connection() as connection:
            connection.execute("BEGIN IMMEDIATE")
            total = connection.execute("SELECT COUNT(*) FROM chunks").fetchone()[0]
            old = connection.execute("SELECT COUNT(*) FROM chunks WHERE source_id = ?", (source_id,)).fetchone()[0]
            if total - old + len(rows) > MAX_INDEX_CHUNKS:
                raise IndexLimitError("Index exceeds the 10,000-chunk demonstration limit.")
            connection.execute("DELETE FROM chunks WHERE source_id = ?", (source_id,))
            connection.executemany("INSERT INTO chunks(source_id, chunk, source_path, category, page, document, vector) VALUES (?, ?, ?, ?, ?, ?, ?)", rows)

    def delete_document(self, source_id: str) -> int:
        with self.connection() as connection:
            return connection.execute("DELETE FROM chunks WHERE source_id = ?", (source_id,)).rowcount

    def nearest(self, vector: list[float], category: str | None, limit: int = 12) -> list[tuple]:
        sql = "SELECT source_id, chunk, source_path, category, page, document, vector FROM chunks"
        parameters = ()
        if category is not None:
            sql += " WHERE category = ?"
            parameters = (category,)
        sql += " ORDER BY source_id, chunk"
        with self.connection() as connection:
            rows = connection.execute(sql, parameters).fetchall()
        ranked = []
        for row in rows:
            stored = json.loads(row[6])
            if len(stored) != len(vector):
                raise ValueError("Corrupt vector dimensions; preserve the index and re-ingest in a new folder.")
            similarity = sum(a * b for a, b in zip(vector, stored))
            ranked.append((similarity, row))
        ranked.sort(key=lambda item: item[0], reverse=True)
        return ranked[:limit]
