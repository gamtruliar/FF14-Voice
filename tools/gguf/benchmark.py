"""Measure the FF14 gateway's actual streaming audio without playing it."""
import argparse
import base64
import json
import time
import wave
from pathlib import Path
from uuid import uuid4

import httpx
import numpy as np


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:8808")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--reference", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    jobs = [
        ("design-cold", "你好呀，歡迎嚟到艾奧傑亞。", "yue", "年輕女聲，溫柔自然。", None),
        ("design-warm", "你好呀，歡迎嚟到艾奧傑亞。", "yue", "年輕女聲，溫柔自然。", None),
    ]
    if args.reference:
        jobs.append(("clone-cantonese", "多謝你幫手，我哋一齊出發啦。", "yue", "", str(args.reference.resolve())))
    records = []
    session = "gguf-benchmark-" + uuid4().hex
    with httpx.Client(timeout=600, trust_env=False) as client:
        health = client.get(args.url + "/api/health")
        health.raise_for_status()
        if health.json().get("backend") != "llama-cpp-voxcpm2-gguf":
            raise RuntimeError("This benchmark requires the GGUF gateway.")
        for name, text, language, control, reference in jobs:
            payload = dict(text=text, language=language, control=control, reference_audio=reference,
                           seed=42, session_id=session, revision=len(records) + 1)
            start = time.perf_counter()
            first, chunks, completed = None, [], False
            with client.stream("POST", args.url + "/api/tts/stream", json=payload) as response:
                response.raise_for_status()
                for line in response.iter_lines():
                    item = json.loads(line)
                    if item["type"] == "format":
                        assert (item["sample_rate"], item["channels"], item["bits"]) == (48000, 1, 16)
                    elif item["type"] == "audio":
                        if first is None:
                            first = time.perf_counter() - start
                        chunks.append(base64.b64decode(item["pcm"]))
                    elif item["type"] == "done":
                        completed = True
                    else:
                        raise RuntimeError(item)
            elapsed = time.perf_counter() - start
            pcm = b"".join(chunks)
            if not completed or not pcm:
                raise RuntimeError("Incomplete stream")
            samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768
            duration = samples.size / 48000
            rms = float(np.sqrt(np.mean(samples ** 2)))
            if rms < 1e-5:
                raise RuntimeError("Generated audio is silent.")
            with wave.open(str(args.output / (name + ".wav")), "wb") as out:
                out.setparams((1, 2, 48000, 0, "NONE", "not compressed"))
                out.writeframes(pcm)
            record = dict(name=name, first_audio_seconds=first, elapsed_seconds=elapsed,
                          audio_seconds=duration, realtime_factor=elapsed / duration, chunks=len(chunks),
                          rms=rms, peak=float(np.max(np.abs(samples))),
                          clipped_fraction=float(np.mean(np.abs(samples) >= 0.999)))
            records.append(record)
            print(json.dumps(record), flush=True)
            (args.output / "results.json").write_text(json.dumps(records, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
