import base64
import io
import sys
from pathlib import Path

import httpx
import numpy as np
import pytest
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from gguf_backend import GGUFDemo, StreamingWav


def wav_bytes(rate=48000, channels=1, subtype="PCM_16"):
    out = io.BytesIO()
    sf.write(out, np.ones((48, channels), dtype=np.float32) * 0.1, rate, format="WAV", subtype=subtype)
    return out.getvalue()


@pytest.mark.parametrize("chunk_size", [1, 3, 43, 44, 47, 4096])
def test_stream_strips_fragmented_header_and_preserves_every_sample(chunk_size):
    data = wav_bytes()
    decoder = StreamingWav()
    output = b"".join(decoder.feed(data[i:i + chunk_size]) for i in range(0, len(data), chunk_size))
    decoder.finish()
    assert output == data[44:]


@pytest.mark.parametrize("data", [b"", wav_bytes()[:20], wav_bytes()[:44], wav_bytes()[:-1]])
def test_incomplete_stream_is_an_error(data):
    decoder = StreamingWav()
    decoder.feed(data)
    with pytest.raises(RuntimeError, match="incomplete"):
        decoder.finish()


@pytest.mark.parametrize("data", [wav_bytes(24000), wav_bytes(channels=2), wav_bytes(subtype="FLOAT"), b"x" * 44])
def test_wrong_stream_format_is_rejected(data):
    with pytest.raises(RuntimeError, match="unsupported"):
        StreamingWav().feed(data)


def test_payload_preserves_language_voice_and_converts_reference(tmp_path, monkeypatch):
    demo = GGUFDemo("http://127.0.0.1:8810")
    sent = []
    def request(path, body=None):
        sent.append((path, body))
        return httpx.Response(200, content=wav_bytes())
    monkeypatch.setattr(demo, "_request", request)
    rate, audio, seed = demo.generate_tts_audio("你好呀", "成年男聲", language="yue", seed=4294967295)
    body = sent[-1][1]
    assert body["input"] == "(用廣東話講; 成年男聲)你好呀"
    assert body["seed"] == -1 and seed == 4294967295
    assert rate == 48000 and len(audio) == 48 and "reference_audio" not in body
    with pytest.raises(ValueError, match="nonzero seed"):
        demo.generate_tts_audio("Hello", seed=0)
    reference = tmp_path / "stereo float reference.wav"
    reference.write_bytes(wav_bytes(rate=24000, channels=2, subtype="FLOAT"))
    demo.generate_tts_audio("Hello", reference_wav_path_input=reference, prompt_text="My reference")
    body = sent[-1][1]
    info = sf.info(io.BytesIO(base64.b64decode(body["reference_audio"])))
    assert (info.channels, info.subtype, info.samplerate) == (1, "PCM_16", 24000)
    assert body["prompt_text"] == "My reference"
    with pytest.raises(ValueError, match="Transcript cloning"):
        demo.generate_tts_audio("你好", reference_wav_path_input=reference, prompt_text="My reference", language="yue")


def test_health_requires_a_loaded_voxcpm2_model(monkeypatch):
    demo = GGUFDemo("http://127.0.0.1:8810")
    for data in [[], [{"id": "voxcpm", "sample_rate": 16000}]]:
        monkeypatch.setattr(demo, "_request", lambda path: httpx.Response(200, json={"data": data}))
        with pytest.raises(RuntimeError, match="not loaded"):
            demo.get_or_load_voxcpm()
        assert demo.voxcpm_model is None
    monkeypatch.setattr(demo, "_request", lambda path: httpx.Response(200, json={"data": [{"id": "voxcpm", "sample_rate": 48000}]}))
    assert demo.get_or_load_voxcpm() is demo


@pytest.mark.parametrize("url", ["https://example.com", "http://example.com", "http://user:pass@localhost", "http://127.0.0.1?x=1"])
def test_only_local_servers(url):
    with pytest.raises(ValueError):
        GGUFDemo(url)
