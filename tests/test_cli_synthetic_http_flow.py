"""Actual C# CLI -> Uvicorn API -> synthetic HTTP server, never real model evidence."""
import json
import os
import shutil
import socket
import subprocess
import threading
import time
from pathlib import Path

import pytest
import uvicorn

import app as api
from test_synthetic_generation_protocol import completion, rag, synthetic_server  # noqa: F401


@pytest.fixture
def cli_api(tmp_path, synthetic_server, monkeypatch):
    dll = Path(__file__).resolve().parents[1] / "bin" / "Release" / "net10.0" / "CSharpLocalRag.dll"
    dotnet = shutil.which("dotnet")
    if not dotnet or not dll.is_file():
        pytest.skip("Build CSharpLocalRag.csproj Release first; these are full CLI protocol tests.")
    service = rag(tmp_path, synthetic_server, timeout=3)
    monkeypatch.setattr(api, "service", lambda: service)
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("127.0.0.1", 0))
    port = listener.getsockname()[1]
    config = uvicorn.Config(api.app, log_level="error", access_log=False)
    server = uvicorn.Server(config)
    thread = threading.Thread(target=lambda: server.run(sockets=[listener]), daemon=True)
    thread.start()
    deadline = time.monotonic() + 5
    while not server.started:
        assert time.monotonic() < deadline and thread.is_alive(), "Owned synthetic API did not start."
        time.sleep(0.01)
    env = {**os.environ, "RAG_API_URL": f"http://127.0.0.1:{port}"}

    def invoke(*args):
        return subprocess.run([dotnet, str(dll), *args], env=env, capture_output=True,
                              encoding="utf-8", timeout=15, check=False)
    try:
        yield invoke
    finally:
        server.should_exit = True
        thread.join(timeout=6)
        listener.close()
        assert not thread.is_alive(), "Owned synthetic API did not stop."


def test_full_synthetic_cli_generated_output_is_labelled_and_cited(cli_api, synthetic_server):
    process = cli_api("ask", "--mode", "generative", "--timeout", "10", "What is the return period?")
    assert process.returncode == 0, process.stderr
    result = json.loads(process.stdout)
    assert result["status"] == "generated"
    assert result["citations"] == ["S1"]
    assert result["generation"]["server_kind"] == "synthetic_fixture"
    assert "SYNTHETIC HTTP FIXTURE: no model inference" in process.stderr
    assert "not fact-checked" in process.stderr
    assert len(synthetic_server.calls) == 1


def test_full_synthetic_cli_default_and_no_answer_do_not_call_generator(cli_api, synthetic_server):
    process = cli_api("ask", "What is the return period?")
    assert process.returncode == 0
    assert json.loads(process.stdout)["mode"] == "extractive"
    assert "no model invocation" in process.stderr
    unknown = cli_api("ask", "--mode", "generative", "What is the shipping cost?")
    assert unknown.returncode == 0
    assert json.loads(unknown.stdout)["reason"] == "retrieval_gate"
    assert synthetic_server.calls == []


def test_full_synthetic_cli_generation_error_returns_nonzero_without_fallback(cli_api, synthetic_server):
    synthetic_server.response = completion(citations=["S404"])
    process = cli_api("ask", "--mode", "generative", "What is the return period?")
    assert process.returncode == 1
    result = json.loads(process.stdout)
    assert result["status"] == "generation_error"
    assert result["error"]["code"] == "invalid_citation"
    assert result["answer"] is None
    assert result["sources"] == []


def test_full_synthetic_cli_deadline_returns_without_partial_output(cli_api, synthetic_server):
    synthetic_server.delay_body = 1.5
    process = cli_api("ask", "--mode", "generative", "--timeout", "1", "What is the return period?")
    assert process.returncode == 1
    assert process.stdout == ""
    assert "Client request timed out" in process.stderr
    assert "server may continue" in process.stderr
