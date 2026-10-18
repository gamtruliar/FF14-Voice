"""Local WAV API and the VoxCPM test UI, sharing one serialized model instance."""
import argparse
import asyncio
import base64
import json
import io
import threading
from pathlib import Path
from typing import Literal

import gradio as gr
import soundfile as sf
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import Response, StreamingResponse
from contextlib import aclosing
from pydantic import BaseModel, Field
import uvicorn

from app import VoxCPMDemo, create_demo_interface, I18N, _APP_THEME, _CUSTOM_CSS
from speech_options import LANGUAGES, prepare_speech
from request_versions import RequestVersions
from priority_gate import PriorityGate
from gemini_translation import list_models as gemini_models, translate as gemini_translate, GeminiFailure


async def while_connected(request, operation):
    """Close the SDK HTTP request when the GUI cancels an obsolete dialogue."""
    task = asyncio.create_task(operation)
    try:
        while not task.done():
            await asyncio.wait({task}, timeout=0.1)
            if not task.done() and await request.is_disconnected():
                raise HTTPException(499, '翻譯已取消。')
        return await task
    finally:
        if not task.done():
            task.cancel()
        await asyncio.gather(task, return_exceptions=True)


class SpeechRequest(BaseModel):
    text: str = Field(min_length=1, max_length=2000)
    language: str = "auto"
    reference_audio: str | None = None
    transcript: str = ""
    control: str = ""
    seed: int = Field(default=42, ge=0, le=2**32 - 1)
    session_id: str = Field(default="", max_length=128)
    revision: int = Field(default=0, ge=0)
    priority: Literal["interactive", "background"] = "interactive"


class CancelRequest(BaseModel):
    session_id: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0)


class GeminiModelsRequest(BaseModel):
    key_file: str = Field(min_length=1, max_length=1024)


class PreviousDialogueLine(BaseModel):
    speaker: str = Field(default='', max_length=200)
    text: str = Field(min_length=1, max_length=1600)
    language: str = Field(min_length=1, max_length=20)


class GeminiTranslationRequest(GeminiModelsRequest):
    model: str = Field(min_length=1, max_length=128)
    text: str = Field(min_length=1, max_length=4000)
    speaker: str = Field(default='', max_length=200)
    system_prompt: str = Field(min_length=1, max_length=8000)
    previous_dialogue: list[PreviousDialogueLine] = Field(default_factory=list, max_length=4)


class SerializedDemo(VoxCPMDemo):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.lock = threading.RLock()

    def generate_tts_audio(self, *args, **kwargs):
        with self.lock:
            return super().generate_tts_audio(*args, **kwargs)


def create_app(demo):
    api = FastAPI(title="FF14 Voice VoxCPM")
    versions = RequestVersions()
    gate = getattr(demo, "lock", threading.RLock())
    admission = PriorityGate()

    @api.post('/api/gemini/models')
    async def list_gemini_models(request: GeminiModelsRequest, http_request: Request):
        try:
            return {'models': await while_connected(http_request, gemini_models(request.key_file))}
        except GeminiFailure as exc:
            raise HTTPException(exc.status, str(exc)) from None

    @api.post('/api/gemini/translate')
    async def translate_gemini(request: GeminiTranslationRequest, http_request: Request):
        try:
            text = await while_connected(http_request, gemini_translate(request.key_file, request.model, request.text, request.speaker, request.system_prompt,
                [line.model_dump() for line in request.previous_dialogue]))
            return {'text': text, 'model': request.model, 'sdk': 'google-genai', 'dialogue_context_version': 1}
        except GeminiFailure as exc:
            raise HTTPException(exc.status, str(exc)) from None

    @api.post("/api/cancel")
    def cancel(request: CancelRequest):
        versions.advance(request.session_id, request.revision)
        return {"status": "superseded"}

    @api.post("/api/warmup")
    def warmup():
        with admission.hold(), gate:
            demo.get_or_load_voxcpm()
        return {"status": "ready", "model_loaded": True}

    @api.get("/api/health")
    def health():
        import os
        return {"status": "ready", "model_loaded": demo.voxcpm_model is not None,
                "process_id": os.getpid(),
                "languages": LANGUAGES, "text_only": True, "background_priority": True,
                "backend": getattr(demo, "backend_name", "native-pytorch"),
                "streaming": hasattr(demo, "stream_tts_audio")}

    @api.post("/api/tts/stream")
    async def speech_stream(request: SpeechRequest):
        if not hasattr(demo, "stream_tts_audio"):
            raise HTTPException(501, "Streaming requires an Omni or GGUF backend.")
        reference = Path(request.reference_audio) if request.reference_audio else None
        try:
            if reference is not None and (not reference.is_file() or reference.suffix.lower() not in {".wav", ".flac", ".mp3", ".ogg"}):
                raise ValueError("Select an existing voice sample.")
            prepare_speech(request.text, request.control, request.language, str(reference) if reference else None, request.transcript)
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc
        versions.advance(request.session_id, request.revision)

        def event(kind, **values):
            return (json.dumps({"type": kind, **values}) + "\n").encode()

        async def events():
            async with admission.ahold(background=request.priority == "background"):
                if not versions.current(request.session_id, request.revision):
                    yield event("cancelled")
                    return
                yield event("format", sample_rate=48000, channels=1, bits=16, encoding="pcm_s16le")
                try:
                    stream = demo.stream_tts_audio(text_input=request.text, language=request.language,
                        reference_wav_path_input=str(reference) if reference else None, prompt_text=request.transcript,
                        control_instruction=request.control, seed=request.seed)
                    async with aclosing(stream):
                        async for chunk in stream:
                            if not versions.current(request.session_id, request.revision):
                                yield event("cancelled")
                                return
                            yield event("audio", pcm=base64.b64encode(chunk).decode())
                    yield event("done" if versions.current(request.session_id, request.revision) else "cancelled")
                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    yield event("error", message=str(exc))
        return StreamingResponse(events(), media_type="application/x-ndjson", headers={"Cache-Control": "no-store", "X-Accel-Buffering": "no"})

    @api.post("/api/tts")
    def speech(request: SpeechRequest):
        reference = Path(request.reference_audio) if request.reference_audio else None
        try:
            if reference is not None and (not reference.is_file() or reference.suffix.lower() not in {".wav", ".flac", ".mp3", ".ogg"}):
                raise ValueError("Select an existing WAV, FLAC, MP3 or OGG voice sample.")
            prepare_speech(request.text, request.control, request.language, str(reference) if reference else None, request.transcript)
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc
        versions.advance(request.session_id, request.revision)
        with admission.hold(background=request.priority == "background"), gate:
            if not versions.current(request.session_id, request.revision):
                raise HTTPException(409, "Dialogue superseded before generation.")
            sr, wav, seed = demo.generate_tts_audio(
                text_input=request.text, language=request.language,
                reference_wav_path_input=str(reference) if reference else None, prompt_text=request.transcript,
                control_instruction=request.control, do_normalize=False, denoise=False, seed=request.seed,
            )
        if not versions.current(request.session_id, request.revision):
            raise HTTPException(409, "Dialogue superseded during generation.")
        output = io.BytesIO()
        sf.write(output, wav, sr, format="WAV", subtype="PCM_16")
        return Response(output.getvalue(), media_type="audio/wav", headers={"X-Seed": str(seed)})

    ui = create_demo_interface(demo)
    ui.queue(max_size=10, default_concurrency_limit=1)
    return gr.mount_gradio_app(api, ui, path="/", i18n=I18N, theme=_APP_THEME, css=_CUSTOM_CSS)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8808)
    parser.add_argument("--model-id", default="openbmb/VoxCPM2")
    parser.add_argument("--device", default="auto")
    backends = parser.add_mutually_exclusive_group()
    backends.add_argument("--omni-url", default="", help="Local WSL2 vLLM-Omni endpoint, e.g. http://127.0.0.1:8809")
    backends.add_argument("--gguf-url", default="", help="Local llama.cpp-omni VoxCPM2 TTS endpoint")
    args = parser.parse_args()
    if args.gguf_url:
        from gguf_backend import GGUFDemo
        demo = GGUFDemo(args.gguf_url)
    elif args.omni_url:
        from omni_backend import OmniDemo
        demo = OmniDemo(args.omni_url)
    else:
        demo = SerializedDemo(args.model_id, args.device)
    uvicorn.run(create_app(demo), host="127.0.0.1", port=args.port)
