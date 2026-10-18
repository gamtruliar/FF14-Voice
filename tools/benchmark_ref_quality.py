import json
from pathlib import Path
import time

from refresh_voice_refs import create_session, normalize, mos

model = Path("artifacts/ref-quality/models/sig_bak_ovr.onnx")
cpu = create_session(model)
gpu = create_session(model, "cuda", Path("VoxCPM/.venv/Lib/site-packages/torch/lib"))
folder = Path("artifacts/ref-quality/minfilia-ja")
report = json.loads((folder / "ranking.json").read_text(encoding="utf-8"))
samples = [r for r in report["rows"] if r.get("Baseline")] + report["rows"][:3]
rows = []
for sample in samples:
    audio, rate, _ = normalize(folder / sample["File"])
    times = {}
    scores = {}
    for name, session in [("cpu", cpu), ("cuda", gpu)]:
        start = time.perf_counter()
        scores[name] = mos(audio, rate, session)
        times[name] = time.perf_counter() - start
    difference = max(abs(scores["cpu"][k] - scores["cuda"][k]) for k in ["SIG", "BAK", "OVRL"])
    if difference > .001: raise AssertionError("CPU/GPU score mismatch")
    rows.append({"file": sample["File"], "seconds": times, "scores": scores, "max_difference": difference})
    print(json.dumps(rows[-1]), flush=True)
Path("artifacts/ref-quality/gpu-parity.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
