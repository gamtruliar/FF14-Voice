"""Windows GUI/API adapter for the local WSL2 vLLM-Omni speech server."""
import base64
import io
import json
from pathlib import Path
import threading
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen

import soundfile as sf
import httpx

from app import VoxCPMDemo
from speech_options import prepare_speech


class OmniDemo(VoxCPMDemo):
    backend_name = "vllm-omni-wsl2"
    fixed_generation_settings = True

    def __init__(self, url):
        parsed = urlparse(url)
        if parsed.scheme != "http" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("Omni endpoint must be a local HTTP address.")
        # Keep optional transcript recognition on CPU. No Windows TTS model is loaded.
        super().__init__(device="cpu")
        self.url = url.rstrip("/")
        self.lock = threading.RLock()

    def _request(self, path, body=None):
        request = Request(self.url + path, None if body is None else json.dumps(body).encode(),
                          {"Content-Type": "application/json"})
        try:
            with urlopen(request, timeout=10 if body is None else 600) as response:
                return response.read()
        except HTTPError as exc:
            self.voxcpm_model = None
            raise RuntimeError(f"WSL Omni HTTP {exc.code}: {exc.read().decode(errors='replace')[:1000]}") from exc
        except URLError as exc:
            self.voxcpm_model = None
            raise RuntimeError("WSL Omni 未就緒，請先啟動 Start-VoxCPM-Omni.cmd。") from exc

    def get_or_load_voxcpm(self):
        self._request("/health")
        self.voxcpm_model = self  # readiness marker; never a Windows CUDA model
        return self

    async def stream_tts_audio(self, text_input, control_instruction="", reference_wav_path_input=None,
                              prompt_text="", seed=42, language="auto"):
        text = prepare_speech(text_input, control_instruction, language, reference_wav_path_input, prompt_text)
        payload = {"model": "voxcpm2", "input": text, "voice": "default", "seed": seed,
                   "response_format": "pcm", "stream": True, "stream_format": "audio"}
        if reference_wav_path_input:
            path = Path(reference_wav_path_input)
            mime = {".wav": "audio/wav", ".flac": "audio/flac", ".mp3": "audio/mpeg", ".ogg": "audio/ogg"}[path.suffix.lower()]
            payload["ref_audio"] = f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode()
            if prompt_text.strip():
                payload["ref_text"] = prompt_text.strip()
        # Async contexts close the upstream response on client disconnect/cancellation.
        async with httpx.AsyncClient(timeout=600, trust_env=False) as client:
            async with client.stream("POST", self.url + "/v1/audio/speech", json=payload) as response:
                if response.status_code != 200:
                    body = await response.aread()
                    raise RuntimeError(f"Omni stream HTTP {response.status_code}: {body.decode(errors='replace')[:1000]}")
                carry = b""
                async for chunk in response.aiter_bytes():
                    carry += chunk
                    size = len(carry) // 2 * 2
                    if size:
                        yield carry[:size]
                        carry = carry[size:]
                if carry:
                    raise RuntimeError("Incomplete PCM sample from Omni.")
        self.voxcpm_model = self

    def generate_tts_audio(self, text_input, control_instruction="", reference_wav_path_input=None,
                           prompt_text="", cfg_value_input=2.0, do_normalize=False, denoise=False,
                           inference_timesteps=10, seed=None, language="auto"):
        if do_normalize or denoise or float(cfg_value_input) != 2.0 or int(inference_timesteps) != 10:
            raise ValueError("Omni 測試模式固定 10 steps / CFG 2；不啟用文字正規化或 reference 降噪。")
        text = prepare_speech(text_input, control_instruction, language, reference_wav_path_input, prompt_text)
        payload = {"model": "voxcpm2", "input": text, "voice": "default", "response_format": "wav"}
        if seed is not None:
            payload["seed"] = seed
        if reference_wav_path_input:
            path = Path(reference_wav_path_input)
            mime = {".wav": "audio/wav", ".flac": "audio/flac", ".mp3": "audio/mpeg", ".ogg": "audio/ogg"}.get(path.suffix.lower())
            if mime is None:
                raise ValueError("Unsupported reference audio format.")
            payload["ref_audio"] = f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode()
            if prompt_text.strip():
                payload["ref_text"] = prompt_text.strip()
        with self.lock:
            data = self._request("/v1/audio/speech", payload)
        samples, rate = sf.read(io.BytesIO(data), dtype="float32")
        if rate != 48000 or samples.size == 0:
            raise RuntimeError("Omni returned empty audio or an unexpected sample rate.")
        self.voxcpm_model = self
        return rate, samples, seed
