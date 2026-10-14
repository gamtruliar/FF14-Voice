"""Measure first PCM delivery separately from total inference; never plays audio."""
import base64
import json
from pathlib import Path
import time
import urllib.request
import wave

root = Path(__file__).resolve().parents[1]
folder = root / 'artifacts/omni-benchmark'
fixtures = json.loads((folder / 'input.json').read_text(encoding='utf-8'))
results = []
for kind, job in fixtures.items():
    for iteration in range(3):
        payload = dict(text=job['Text'], language='ja', control=job['Voice']['Control'],
                       reference_audio=None if job['Fallback'] else job['Voice']['ReferenceAudio'],
                       session_id='stream-benchmark-' + kind, revision=iteration, seed=42)
        req = urllib.request.Request('http://127.0.0.1:8808/api/tts/stream', json.dumps(payload).encode(),
                                     {'Content-Type': 'application/json'})
        start = time.perf_counter()
        chunks, first, done = [], None, False
        with urllib.request.urlopen(req, timeout=180) as response:
            for line in response:
                item = json.loads(line)
                if item['type'] == 'audio':
                    if first is None:
                        first = time.perf_counter() - start
                    chunks.append(base64.b64decode(item['pcm']))
                elif item['type'] == 'done':
                    done = True
                elif item['type'] in ('error', 'cancelled'):
                    raise RuntimeError(item)
        assert done and chunks
        elapsed = time.perf_counter() - start
        pcm = b''.join(chunks)
        with wave.open(str(folder / f'stream-{kind}-{iteration}.wav'), 'wb') as output:
            output.setparams((1, 2, 48000, 0, 'NONE', 'not compressed'))
            output.writeframes(pcm)
        record = dict(kind=kind, iteration=iteration, first_audio_seconds=first, total_seconds=elapsed,
                      chunks=len(chunks), audio_seconds=len(pcm)/96000)
        results.append(record)
        print(json.dumps(record), flush=True)
        (folder / 'stream-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
