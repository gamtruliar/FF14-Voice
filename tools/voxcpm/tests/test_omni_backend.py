import base64
import io
import sys
from pathlib import Path

import numpy as np
import pytest
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from omni_backend import OmniDemo


def test_local_endpoint_only():
    with pytest.raises(ValueError):
        OmniDemo("https://example.com")


def test_omni_default_and_clone_preserve_direction_and_reference(tmp_path, monkeypatch):
    demo = OmniDemo("http://127.0.0.1:8809")
    output = io.BytesIO()
    sf.write(output, np.zeros(480, dtype=np.float32), 48000, format="WAV")
    sent = []
    def request(path, body=None):
        sent.append((path, body))
        return output.getvalue()
    monkeypatch.setattr(demo, "_request", request)
    sr, audio, seed = demo.generate_tts_audio("你好呀", "成年男聲", language="yue", seed=4294967295)
    body = sent[-1][1]
    assert body["input"] == "(用廣東話講; 成年男聲)你好呀"
    assert "ref_audio" not in body and "ref_text" not in body
    assert body["seed"] == seed == 4294967295 and sr == 48000 and len(audio) == 480
    reference = tmp_path / "voice.wav"
    reference.write_bytes(output.getvalue())
    demo.generate_tts_audio("こんにちは", reference_wav_path_input=str(reference), language="ja")
    body = sent[-1][1]
    assert body["input"] == "(Speak in Japanese)こんにちは"
    assert base64.b64decode(body["ref_audio"].split(",", 1)[1]) == reference.read_bytes()
    assert "ref_text" not in body
    demo.generate_tts_audio("Hello", reference_wav_path_input=str(reference), prompt_text="Reference transcript")
    assert sent[-1][1]["ref_text"] == "Reference transcript"
    with pytest.raises(ValueError):
        demo.generate_tts_audio("Hello", inference_timesteps=4)
    with pytest.raises(ValueError):
        demo.generate_tts_audio("Hello", denoise=True)
