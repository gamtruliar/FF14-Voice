import io
import sys
from pathlib import Path

import numpy as np
import pytest
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from speech_options import prepare_speech


def test_language_guides_voice_without_altering_text():
    assert prepare_speech("你好呀", language="yue") == "(用廣東話講)你好呀"
    assert prepare_speech("Hello", language="auto") == "Hello"


@pytest.mark.parametrize("kwargs", [
    {"text": " "}, {"text": "Hello", "language": "invalid"},
    {"text": "Hello", "transcript": "reference"},
    {"text": "Hello", "transcript": "reference", "reference": "voice.wav", "language": "ja"},
])
def test_invalid_clone_requests_are_rejected(kwargs):
    with pytest.raises(ValueError):
        prepare_speech(**kwargs)


def test_transcript_clone_and_control_parentheses():
    assert prepare_speech("Hello", reference="voice.wav", transcript="Hi") == "Hello"
    assert prepare_speech("Hello", control="(warm)（calm）") == "(warmcalm)Hello"


def test_api_returns_real_wav_and_passes_voice_settings(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from ff14_server import create_app

    class FakeDemo:
        voxcpm_model = None
        def generate_tts_audio(self, **kwargs):
            self.request = kwargs
            return 48000, np.zeros(480, dtype=np.float32), 42

    demo = FakeDemo()
    monkeypatch.chdir(tmp_path)
    client = TestClient(create_app(demo))
    reference = tmp_path / "voice.wav"
    sf.write(reference, np.zeros(480), 48000)
    assert client.get("/api/health").json()["model_loaded"] is False
    response = client.post("/api/tts", json={"text": "你好呀", "language": "yue", "reference_audio": str(reference)})
    assert response.status_code == 200
    samples, rate = sf.read(io.BytesIO(response.content))
    assert rate == 48000 and len(samples) == 480
    assert demo.request["language"] == "yue"
    assert demo.request["reference_wav_path_input"] == str(reference)
    for optional in [{}, {"reference_audio": None}, {"reference_audio": ""}]:
        response = client.post("/api/tts", json={"text": "你好呀", "language": "yue", "control": "成年男聲", **optional})
        assert response.status_code == 200
        assert demo.request["reference_wav_path_input"] is None
        assert demo.request["prompt_text"] == ""
        assert demo.request["control_instruction"] == "成年男聲"
    assert client.post("/api/tts", json={"text": "hi", "transcript": "orphan transcript"}).status_code == 422
    assert client.get("/api/health").json()["text_only"] is True
    assert client.post("/api/tts", json={"text": "hi", "reference_audio": "missing.wav"}).status_code == 422
    assert client.post("/api/tts", json={"text": "hi", "reference_audio": str(reference), "language": "bad"}).status_code == 422
    assert client.get("/").status_code == 200
    components = client.get("/config").json()["components"]
    languages = next(c for c in components if c["type"] == "dropdown")
    assert ["廣東話 / Cantonese", "yue"] in languages["props"]["choices"]
    reference_input = next(c for c in components if c["type"] == "audio")
    assert reference_input["props"]["sources"] == ["upload", "microphone"]
    logo = next(c for c in components if c["type"] == "html")["props"]["value"].split('src="')[1].split('"')[0]
    assert client.get(logo).status_code == 200


def test_superseded_requests_never_start_queued_inference(tmp_path, monkeypatch):
    import threading
    from concurrent.futures import ThreadPoolExecutor
    from fastapi.testclient import TestClient
    import ff14_server
    from request_versions import RequestVersions

    started, queued, release = threading.Event(), threading.Event(), threading.Event()
    original_advance = RequestVersions.advance
    def advance(self, session, revision):
        original_advance(self, session, revision)
        if revision == 2:
            queued.set()
    monkeypatch.setattr(RequestVersions, "advance", advance)

    class FakeDemo:
        voxcpm_model = None
        calls = 0
        def get_or_load_voxcpm(self):
            self.voxcpm_model = object()
        def generate_tts_audio(self, **kwargs):
            self.calls += 1
            if self.calls == 1:
                started.set()
                assert release.wait(10)
            return 24000, np.zeros(240), 42

    demo = FakeDemo()
    client = TestClient(ff14_server.create_app(demo))
    assert client.post("/api/warmup").json()["model_loaded"] is True
    reference = tmp_path / "voice.wav"
    sf.write(reference, np.zeros(240), 24000)
    body = {"text": "test", "reference_audio": str(reference), "session_id": "game"}
    with ThreadPoolExecutor(max_workers=2) as workers:
        first = workers.submit(client.post, "/api/tts", json={**body, "revision": 1})
        assert started.wait(10)
        second = workers.submit(client.post, "/api/tts", json={**body, "revision": 2})
        try:
            assert queued.wait(10)
            assert client.post("/api/cancel", json={"session_id": "game", "revision": 3}).status_code == 200
        finally:
            release.set()
        assert first.result().status_code == 409
        assert second.result().status_code == 409
    assert demo.calls == 1
    # A delayed older request cannot undo a stop/newer dialogue.
    assert client.post("/api/tts", json={**body, "revision": 1}).status_code == 409
    assert client.post("/api/tts", json={**body, "revision": 4}).status_code == 200
    assert demo.calls == 2
