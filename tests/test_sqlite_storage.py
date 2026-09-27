import hashlib
import sqlite3
from concurrent.futures import ThreadPoolExecutor

import pytest

from rag_service import DocumentError, RagService, RagSettings
import sqlite_store


def create_rag(tmp_path):
    docs, data = tmp_path / "docs", tmp_path / "db"
    docs.mkdir(exist_ok=True)
    return RagService(RagSettings(docs, data))


def test_persistent_restart_preserves_unicode_source_and_legacy_index(tmp_path):
    rag = create_rag(tmp_path)
    legacy = rag.settings.db_path / "chroma.sqlite3"
    legacy.write_bytes(b"preserved old Chroma fixture")
    before = hashlib.sha256(legacy.read_bytes()).hexdigest()
    (rag.settings.document_root / "policy.md").write_text("İade süresi 30 gündür.", encoding="utf-8")
    rag.ingest("policy.md", "iade", "policy")
    restarted = RagService(rag.settings)
    answer = restarted.query("İade süresi kaç gündür?", category="iade")
    assert "30 gündür" in answer["answer"]
    assert answer["sources"][0]["source_path"] == "policy.md"
    assert hashlib.sha256(legacy.read_bytes()).hexdigest() == before
    assert restarted.store.path.name == "rag-v2.sqlite3"


def test_failed_replacement_rolls_back_old_chunks(tmp_path):
    rag = create_rag(tmp_path)
    file = rag.settings.document_root / "policy.md"
    file.write_text("The return period is 30 days.", encoding="utf-8")
    rag.ingest("policy.md", "policy", "policy")
    with sqlite3.connect(rag.store.path) as connection:
        connection.execute("""CREATE TRIGGER simulate_write_failure BEFORE INSERT ON chunks
            WHEN NEW.document LIKE '%45 days%'
            BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END""")
    file.write_text("The return period is 45 days.", encoding="utf-8")
    with pytest.raises(sqlite3.IntegrityError, match="synthetic write failure"):
        rag.ingest("policy.md", "policy", "policy")
    original = RagService(rag.settings).query("What is the return period?")
    assert original["status"] == "grounded"
    assert "30 days" in original["answer"]


def test_category_is_parameterized_and_replacement_removes_stale_chunks(tmp_path):
    rag = create_rag(tmp_path)
    file = rag.settings.document_root / "policy.md"
    file.write_text("The return period is 30 days. " + "filler " * 300, encoding="utf-8")
    assert rag.ingest("policy.md", "policy", "policy")["chunks"] > 1
    assert rag.query("What is the return period?", category="x' OR 1=1 --")["status"] == "no_answer"
    file.write_text("The return period is 45 days.", encoding="utf-8")
    assert rag.ingest("policy.md", "replacement", "policy")["chunks"] == 1
    assert rag.query("What is the return period?", category="policy")["status"] == "no_answer"
    assert "45 days" in rag.query("What is the return period?", category="replacement")["answer"]
    assert rag.delete("policy")["deleted_chunks"] == 1
    assert rag.delete("policy")["deleted_chunks"] == 0


def test_index_limit_keeps_existing_document_and_allows_same_size_replacement(tmp_path, monkeypatch):
    rag = create_rag(tmp_path)
    monkeypatch.setattr(sqlite_store, "MAX_INDEX_CHUNKS", 1)
    file = rag.settings.document_root / "policy.md"
    file.write_text("The return period is 30 days.", encoding="utf-8")
    rag.ingest("policy.md", "policy", "policy")
    with pytest.raises(DocumentError) as error:
        rag.ingest("policy.md", "policy", "second")
    assert error.value.status_code == 413
    assert "30 days" in rag.query("What is the return period?")["answer"]
    file.write_text("The return period is 45 days.", encoding="utf-8")
    rag.ingest("policy.md", "policy", "policy")
    assert "45 days" in rag.query("What is the return period?")["answer"]


def test_concurrent_document_writers_keep_distinct_sources(tmp_path):
    rag = create_rag(tmp_path)
    for number in range(8):
        (rag.settings.document_root / f"policy{number}.md").write_text(f"Warranty lasts {number + 12} months.", encoding="utf-8")
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda number: rag.ingest(f"policy{number}.md", "policy", f"policy{number}"), range(8)))
    assert len(results) == 8
    with sqlite3.connect(rag.store.path) as connection:
        assert connection.execute("SELECT COUNT(DISTINCT source_id) FROM chunks").fetchone()[0] == 8
    assert all(rag.delete(f"policy{number}")["deleted_chunks"] == 1 for number in range(8))


@pytest.mark.parametrize("version", [0, 2, 99])
def test_unsupported_existing_database_is_rejected_without_byte_changes(tmp_path, version):
    docs, data = tmp_path / "docs", tmp_path / "db"
    docs.mkdir()
    data.mkdir()
    database = data / "rag-v2.sqlite3"
    with sqlite3.connect(database) as connection:
        connection.execute("CREATE TABLE private_records (value TEXT)")
        connection.execute("INSERT INTO private_records VALUES ('synthetic data to preserve')")
        connection.execute(f"PRAGMA user_version = {version}")
    before = hashlib.sha256(database.read_bytes()).hexdigest()
    with pytest.raises(ValueError, match="preserve it"):
        RagService(RagSettings(docs, data))
    assert hashlib.sha256(database.read_bytes()).hexdigest() == before


def test_reordered_version_two_schema_is_preserved_and_rejected(tmp_path):
    docs, data = tmp_path / "docs", tmp_path / "db"
    docs.mkdir()
    data.mkdir()
    database = data / "rag-v2.sqlite3"
    with sqlite3.connect(database) as connection:
        connection.execute("""CREATE TABLE chunks (chunk INTEGER NOT NULL, source_id TEXT NOT NULL,
            source_path TEXT NOT NULL, category TEXT NOT NULL, page INTEGER NOT NULL,
            document TEXT NOT NULL, vector TEXT NOT NULL, PRIMARY KEY(source_id, chunk))""")
        connection.execute("PRAGMA user_version = 2")
    before = hashlib.sha256(database.read_bytes()).hexdigest()
    with pytest.raises(ValueError, match="Unsupported index schema"):
        RagService(RagSettings(docs, data))
    assert hashlib.sha256(database.read_bytes()).hexdigest() == before


def test_sqlite_like_foreign_table_name_cannot_hide_unversioned_database(tmp_path):
    docs, data = tmp_path / "docs", tmp_path / "db"
    docs.mkdir()
    data.mkdir()
    database = data / "rag-v2.sqlite3"
    with sqlite3.connect(database) as connection:
        connection.execute("CREATE TABLE sqlitex_backup (value TEXT)")
        connection.execute("INSERT INTO sqlitex_backup VALUES ('synthetic data to preserve')")
    before = hashlib.sha256(database.read_bytes()).hexdigest()
    with pytest.raises(ValueError, match="Existing unversioned database"):
        RagService(RagSettings(docs, data))
    assert hashlib.sha256(database.read_bytes()).hexdigest() == before
