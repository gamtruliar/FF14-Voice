"""Adapter for the pinned, local llama.cpp-omni VoxCPM2 TTS server."""
import base64
import io
import struct
import threading
from pathlib import Path
from urllib.parse import urlparse

import httpx
import numpy as np
import soundfile as sf

from app import VoxCPMDemo
from speech_options import prepare_speech


class StreamingWav:
    """The pinned server emits a 44-byte PCM WAV header with unknown length."""
    def __init__(self):
        self.pending = b""
        self.header_read = False
        self.samples = 0

    def feed(self, data):
        self.pending += data
        if not self.header_read:
            if len(self.pending) < 44:
                return b""
            header, self.pending = self.pending[:44], self.pending[44:]
            if (header[:4] != b"RIFF" or header[8:16] != b"WAVEfmt "
                    or header[36:40] != b"data"
                    or struct.unpack_from("<IHHIIHH", header, 16) != (16, 1, 1, 48000, 96000, 2, 16)):
                raise RuntimeError("GGUF server returned an unsupported WAV stream; expected 48 kHz mono PCM16.")
            self.header_read = True
        size = len(self.pending) // 2 * 2
        result, self.pending = self.pending[:size], self.pending[size:]
        self.samples += size // 2
        return result

    def finish(self):
        if not self.header_read or not self.samples or self.pending:
            raise RuntimeError("GGUF server returned empty or incomplete audio.")


class GGUFDemo(VoxCPMDemo):
    backend_name = "llama-cpp-voxcpm2-gguf"
    fixed_generation_settings = True

    def __init__(self, url):
        parsed = urlparse(url)
        if (parsed.scheme != "http" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}
                or parsed.username or parsed.password or parsed.query or parsed.fragment):
            raise ValueError("GGUF endpoint must be a local HTTP address.")
        super().__init__(device="cpu")
        self.url = url.rstrip("/")
        self.lock = threading.RLock()

    def _request(self, path, body=None):
        try:
            with httpx.Client(timeout=600 if body is not None else 10, trust_env=False) as client:
                response = client.get(self.url + path) if body is None else client.post(self.url + path, json=body)
                response.raise_for_status()
                return response
        except httpx.HTTPError as exc:
            self.voxcpm_model = None
            raise RuntimeError(f"GGUF server request failed: {exc}") from exc

    def get_or_load_voxcpm(self):
        models = self._request("/v1/audio/speech/models").json().get("data", [])
        if not any(m.get("id") == "voxcpm" and m.get("sample_rate") == 48000 for m in models):
            self.voxcpm_model = None
            raise RuntimeError("GGUF server has not loaded the 48 kHz VoxCPM2 model.")
        self.voxcpm_model = self
        return self

    def _payload(self, text, control, reference, transcript, seed, language):
        prepared = prepare_speech(text, control, language, reference, transcript)
        # Upstream's parameter is int32; preserve all 32 seed bits.
        seed32 = int(42 if seed is None else seed) & 0xffffffff
        if seed32 == 0:
            raise ValueError("The GGUF engine reserves seed 0 for unseeded generation. Choose a nonzero seed.")
        payload = {"model": "voxcpm", "input": prepared, "voice": "default",
                   "seed": seed32 if seed32 < 2**31 else seed32 - 2**32,
                   "response_format": "wav", "cfg_value": 2.0, "inference_timesteps": 10}
        if reference:
            path = Path(reference)
            if path.suffix.lower() not in {".wav", ".flac", ".mp3", ".ogg"}:
                raise ValueError("Unsupported reference audio format.")
            samples, rate = sf.read(path, dtype="float32", always_2d=True)
            if not samples.size or not np.isfinite(samples).all():
                raise ValueError("Reference audio is empty or invalid.")
            buffer = io.BytesIO()
            # Upstream accepts PCM16 WAV only, including for originally float WAV/FLAC.
            sf.write(buffer, samples.mean(axis=1), rate, format="WAV", subtype="PCM_16")
            payload["reference_audio"] = base64.b64encode(buffer.getvalue()).decode("ascii")
            if transcript.strip():
                payload["prompt_text"] = transcript.strip()
        return payload

    def generate_tts_audio(self, text_input, control_instruction="", reference_wav_path_input=None,
                           prompt_text="", cfg_value_input=2.0, do_normalize=False, denoise=False,
                           inference_timesteps=10, seed=None, language="auto"):
        if do_normalize or denoise or float(cfg_value_input) != 2.0 or int(inference_timesteps) != 10:
            raise ValueError("GGUF mode uses 10 steps / CFG 2 without normalization or denoising.")
        payload = self._payload(text_input, control_instruction, reference_wav_path_input, prompt_text, seed, language)
        with self.lock:
            response = self._request("/v1/audio/speech", payload)
        samples, rate = sf.read(io.BytesIO(response.content), dtype="float32")
        if rate != 48000 or samples.ndim != 1 or not samples.size or not np.isfinite(samples).all():
            raise RuntimeError("GGUF server returned invalid audio; expected 48 kHz mono.")
        self.voxcpm_model = self
        return rate, samples, seed

    async def stream_tts_audio(self, text_input, control_instruction="", reference_wav_path_input=None,
                              prompt_text="", seed=42, language="auto"):
        payload = self._payload(text_input, control_instruction, reference_wav_path_input, prompt_text, seed, language)
        wav = StreamingWav()
        try:
            async with httpx.AsyncClient(timeout=600, trust_env=False) as client:
                async with client.stream("POST", self.url + "/v1/audio/speech/stream", json=payload) as response:
                    response.raise_for_status()
                    async for chunk in response.aiter_bytes():
                        pcm = wav.feed(chunk)
                        if pcm:
                            yield pcm
                    wav.finish()
            self.voxcpm_model = self
        except Exception:
            self.voxcpm_model = None
            raise
