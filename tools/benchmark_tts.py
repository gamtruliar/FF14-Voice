"""End-to-end HTTP benchmark, using identical Japanese text/reference across backends.

Writes WAVs and timing evidence; never plays audio or changes GUI settings.
"""
import argparse
import base64
import io
import json
from pathlib import Path
import sys
import time
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "VoxCPM"))
from speech_options import prepare_speech


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--backend", choices=["native", "omni", "gateway"], required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--calls", type=int, default=3)
    args = parser.parse_args()
    folder = ROOT / "artifacts" / "omni-benchmark"
    folder.mkdir(parents=True, exist_ok=True)
    fixture = folder / "input.json"
    if not fixture.exists():
        plan = json.loads((ROOT / "data/batch-ja/plan.json").read_text(encoding="utf-8-sig"))
        picked = {}
        for line in Path(plan["JobsFile"]).read_text(encoding="utf-8-sig").splitlines():
            job = json.loads(line)
            kind = "default" if job["Fallback"] else "clone"
            if kind in picked or not 30 <= len(job["Text"]) <= 75:
                continue
            if kind == "clone" and "YSHTOLA" not in job["Voice"]["ReferenceAudio"]:
                continue
            picked[kind] = job
            if len(picked) == 2:
                break
        if len(picked) != 2:
            raise RuntimeError("No suitable unvoiced benchmark fixtures")
        fixture.write_text(json.dumps(picked, ensure_ascii=False, indent=2), encoding="utf-8")
    fixtures = json.loads(fixture.read_text(encoding="utf-8"))
    results = []
    for kind, job in fixtures.items():
        voice = job["Voice"]
        reference = None if job["Fallback"] else voice["ReferenceAudio"]
        payload = dict(text=job["Text"], language="ja", reference_audio=reference,
                       control=voice["Control"], transcript="", seed=42)
        path = "/api/tts"
        if args.backend == "omni":
            path = "/v1/audio/speech"
            payload = dict(model="voxcpm2", voice="default", response_format="wav", seed=42,
                           input=prepare_speech(job["Text"], voice["Control"], "ja", reference))
            if reference:
                payload["ref_audio"] = "data:audio/wav;base64," + base64.b64encode(Path(reference).read_bytes()).decode()
        for call in range(1, args.calls + 1):
            request = urllib.request.Request(args.url.rstrip("/") + path,
                json.dumps(payload).encode(), {"Content-Type": "application/json"})
            start = time.perf_counter()
            with urllib.request.urlopen(request, timeout=900) as response:
                audio = response.read()
            elapsed = time.perf_counter() - start
            with wave.open(io.BytesIO(audio)) as wav:
                duration = wav.getnframes() / wav.getframerate()
                rate = wav.getframerate()
            if duration <= 0:
                raise RuntimeError("Empty audio")
            output = folder / f"{args.backend}-{kind}-{call}.wav"
            output.write_bytes(audio)
            result = dict(backend=args.backend, kind=kind, call=call, seconds=elapsed,
                          audio_seconds=duration, rtf=elapsed / duration, sample_rate=rate,
                          speaker=job["Speaker"], source=job["Source"], output=str(output))
            print(json.dumps(result, ensure_ascii=False), flush=True)
            results.append(result)
            (folder / f"{args.backend}-results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
