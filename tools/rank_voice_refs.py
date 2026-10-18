"""Offline reference audition/ranking pilot; never overwrites the active voice catalog.

DNSMOS P.835 inference/calibration follows Microsoft's dnsmos_local.py.
Upstream source/license and the pinned revision are saved alongside the model.
Scores estimate speech quality, NOT speaker identity or freedom from reverb.
"""
import argparse
import hashlib
import html
import json
import math
from pathlib import Path
import shutil
import urllib.request

REVISION = "591184a9fcb2cbdec02520fed81a32bbbf9d73ff"
BASE = f"https://raw.githubusercontent.com/microsoft/DNS-Challenge/{REVISION}/"
MODEL_SHA256 = "269fbebdb513aa23cddfbb593542ecc540284a91849ac50516870e1ac78f6edd"


def download(folder):
    folder.mkdir(parents=True, exist_ok=True)
    for source, name in [("DNSMOS/DNSMOS/sig_bak_ovr.onnx", "sig_bak_ovr.onnx"),
                         ("LICENSE", "DNS-Challenge-LICENSE"),
                         ("DNSMOS/dnsmos_local.py", "upstream_dnsmos_local.py")]:
        target = folder / name
        if not target.exists():
            with urllib.request.urlopen(BASE + source, timeout=60) as response:
                data = response.read()
            staging = target.with_suffix(".download")
            staging.write_bytes(data)
            staging.replace(target)
        print(name, target.stat().st_size, hashlib.sha256(target.read_bytes()).hexdigest(), flush=True)
    (folder / "provenance.json").write_text(json.dumps({"repository": "https://github.com/microsoft/DNS-Challenge",
        "revision": REVISION, "files": {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in folder.iterdir() if p.is_file() and p.name != "provenance.json"}}, indent=2), encoding="utf-8")


def metrics(audio, rate):
    import numpy as np
    peak = float(np.max(np.abs(audio)))
    frame = max(1, int(rate * .02))
    frames = audio[:len(audio) // frame * frame].reshape(-1, frame)
    rms = np.sqrt(np.mean(frames ** 2, axis=1))
    # Energy occupancy is a cheap screening proxy, not a VAD or reverb estimator.
    active = rms > max(.003, float(np.max(rms)) * .08)
    active_seconds = float(active.sum() * .02)
    clipping = float(np.mean(np.abs(audio) >= .999))
    level = float(20 * np.log10(max(1e-10, np.sqrt(np.mean(audio ** 2)))))
    reasons = []
    if clipping > .001: reasons.append("clipping > 0.1%")
    if active_seconds < 2.5: reasons.append("less than 2.5s active audio")
    if level < -35: reasons.append("very quiet (< -35 dBFS RMS)")
    if len(audio) / rate < 3 or len(audio) / rate > 12.1: reasons.append("outside 3-12s")
    return {"peak": peak, "rms_dbfs": level, "clipping_fraction": clipping,
            "active_seconds_estimate": active_seconds, "rejection_reasons": reasons}


def mos(audio, rate, session):
    import numpy as np
    from scipy.signal import resample_poly
    divisor = math.gcd(rate, 16000)
    audio = resample_poly(audio, 16000 // divisor, rate // divisor).astype(np.float32)
    window = 144160  # upstream: 9.01 seconds, repeat short utterances
    while len(audio) < window:
        audio = np.concatenate((audio, audio))
    results = []
    for offset in range(0, len(audio) - window + 1, 16000):
        raw = session.run(None, {session.get_inputs()[0].name: audio[offset:offset + window][None, :]})[0][0]
        results.append([np.polyval(c, score) for c, score in zip(
            [(-.08397278, 1.22083953, .0052439), (-.13166888, 1.60915514, -.39604546),
             (-.06766283, 1.11546468, .04602535)], raw)])
    sig, bak, overall = np.mean(results, axis=0)
    return {"SIG": float(sig), "BAK": float(bak), "OVRL": float(overall)}


def rank(folder, models, catalog_path, speaker, language):
    import numpy as np
    import onnxruntime as ort
    import soundfile as sf
    provenance = json.loads((models / "provenance.json").read_text(encoding="utf-8"))
    model = models / "sig_bak_ovr.onnx"
    if hashlib.sha256(model.read_bytes()).hexdigest() != MODEL_SHA256 or provenance["files"][model.name] != MODEL_SHA256:
        raise ValueError("Model checksum mismatch")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    voice = next(v for v in catalog["Voices"] if v["Token"] == speaker)
    reference = next(r for r in voice["References"] if r["Language"] == language)
    current_path = (catalog_path.parent / reference["RelativePath"]).resolve()
    # A copied baseline makes the comparison self-contained, and preserves the original.
    shutil.copy2(current_path, folder / "current-reference.wav")
    entries = json.loads((folder / "candidates.json").read_text(encoding="utf-8"))
    if any(c["Token"] != speaker or c["Language"] != language for c in entries):
        raise ValueError("Candidate identity/language mismatch")
    entries.append({"File": "current-reference.wav", "Token": speaker, "Language": language,
                    "Text": reference["Transcript"], "GamePath": reference["GamePath"], "Sheet": reference["Sheet"], "Baseline": True})
    rows = []
    for index, item in enumerate(entries):
        path = (folder / item["File"]).resolve()
        if path.parent != folder.resolve(): raise ValueError("Candidate path outside folder")
        audio, rate = sf.read(path, dtype="float32", always_2d=True)
        audio = audio.mean(axis=1)
        if not len(audio) or not np.isfinite(audio).all(): raise ValueError("Invalid audio: " + item["File"])
        row = {**item, "Seconds": len(audio) / rate, **metrics(audio, rate), **mos(audio, rate, session)}
        row["rank_score"] = .5 * row["SIG"] + .2 * row["BAK"] + .3 * row["OVRL"]
        row["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        rows.append(row)
        print(f"{index+1}/{len(entries)} SIG={row['SIG']:.2f} BAK={row['BAK']:.2f} OVRL={row['OVRL']:.2f} {item['Sheet']}", flush=True)
    rows.sort(key=lambda r: (bool(r["rejection_reasons"]), -r["rank_score"], r["File"]))
    report = {"speaker": speaker, "language": language, "model": provenance,
              "note": "Quality ranking only; reverb and character identity require audition. No reference changed.",
              "formula": "0.5 * SIG + 0.2 * BAK + 0.3 * OVRL (heuristic, not a trained clone-quality score)", "rows": rows}
    (folder / "ranking.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    cards = []
    ordered = [r for r in rows if r.get("Baseline")] + [r for r in rows if not r.get("Baseline")]
    for index, row in enumerate(ordered):
        title = "目前使用嘅 REF（備份）" if row.get("Baseline") else f"候選 {index}"
        tags = "；".join(row["rejection_reasons"]) or "基本檢查通過"
        cards.append(f'<article><h2>{title} · {row["rank_score"]:.2f}</h2><p>人聲 {row["SIG"]:.2f} / 背景 {row["BAK"]:.2f} / 整體 {row["OVRL"]:.2f} · {row["Seconds"]:.1f} 秒 · {html.escape(tags)}</p><p>{html.escape(row["Text"])}</p><audio controls preload="none" src="{html.escape(row["File"], quote=True)}"></audio><p class="path">{html.escape(row["GamePath"])}</p></article>')
    page = '''<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Minfilia REF 試聽</title><style>body{background:#111827;color:#e5e7eb;font:17px system-ui;max-width:960px;margin:40px auto;padding:0 24px}h1,h2{color:#f4d595}article{background:#1f2937;padding:20px;margin:18px 0;border-radius:12px}audio{width:100%}.path{font-size:13px;overflow-wrap:anywhere;color:#9ca3af}</style><h1>ミンフィリア · 日文 REF 比較</h1><p>原 REF 喺最上面，候選按音質評分排列。分數唔代表冇回音／同一時期聲線；請試聽。未更改目前使用嘅 REF。</p>'''
    (folder / "index.html").write_text(page + "".join(cards) + "</html>", encoding="utf-8")
    print("REPORT", folder / "index.html")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--models", type=Path, default=Path("artifacts/ref-quality/models"))
    parser.add_argument("--folder", type=Path, default=Path("artifacts/ref-quality/minfilia-ja"))
    parser.add_argument("--catalog", type=Path, default=Path("data/game-voices/catalog.json"))
    parser.add_argument("--speaker", default="MINFILIA")
    parser.add_argument("--language", default="ja")
    args = parser.parse_args()
    if args.download: download(args.models)
    else: rank(args.folder, args.models, args.catalog, args.speaker, args.language)
