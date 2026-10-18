"""Generate a silent, local A/B audition: same text/language/seed, different refs.

No playback, GUI settings, active references or speech caches are changed.
"""
import argparse
import hashlib
import html
import io
import json
from pathlib import Path
import time
import urllib.request
import uuid
import wave


def audition(folder, server):
    report = json.loads((folder / "ranking.json").read_text(encoding="utf-8"))
    rows = report["rows"]
    baseline = next(r for r in rows if r.get("Baseline"))
    selected = [baseline] + [r for r in rows if not r.get("Baseline") and not r["rejection_reasons"]][:3]
    text = "これは声の比較テストです。同じ文章を、同じ設定で読み上げています。"
    seed = int.from_bytes(hashlib.sha256(("ff14sound-voice-seed-v1:voice:" + report["speaker"]).encode()).digest()[:4], "little")
    records = []
    session = "ref-audition-" + uuid.uuid4().hex
    for index, row in enumerate(selected):
        ref = (folder / row["File"]).resolve()
        if ref.parent != folder.resolve() or hashlib.sha256(ref.read_bytes()).hexdigest() != row["sha256"]:
            raise ValueError("Reference checksum/path mismatch")
        payload = dict(text=text, language="ja", reference_audio=str(ref), transcript="", control="", seed=seed,
                       session_id=session, revision=index, priority="background")
        start = time.perf_counter()
        request = urllib.request.Request(server.rstrip("/") + "/api/tts", json.dumps(payload).encode(), {"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=180) as response:
            audio = response.read()
        with wave.open(io.BytesIO(audio)) as wav:
            duration = wav.getnframes() / wav.getframerate()
            if duration <= .1: raise ValueError("Empty generated WAV")
        name = f"clone-comparison-{index}.wav"
        (folder / name).write_bytes(audio)
        records.append(dict(reference=row, file=name, seed=seed, text=text, seconds=duration,
                            generation_seconds=time.perf_counter() - start))
        print(index, name, f"{duration:.2f}s audio", flush=True)
    (folder / "clone-comparison.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    cards = []
    for index, record in enumerate(records):
        row = record["reference"]
        title = "原 REF" if index == 0 else f"候選 {index}"
        cards.append(f'<article><h2>{title} · 整體音質 {row["OVRL"]:.2f}/5</h2><p>{html.escape(row["Text"])}</p><p>遊戲原錄音 REF</p><audio controls preload="none" src="{row["File"]}"></audio><p>用呢段 REF clone 嘅測試句</p><audio controls preload="none" src="{record["file"]}"></audio><p class="path">{html.escape(row["GamePath"])}</p><details><summary>REF 檔案路徑（GUI 選擇音檔用）</summary><pre>{html.escape(str((folder / row["File"]).resolve()))}</pre></details></article>')
    page = '''<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>ミンフィリア Clone 比較</title><style>body{background:#111827;color:#e5e7eb;font:17px system-ui;max-width:960px;margin:40px auto;padding:0 24px}h1,h2{color:#f4d595}article{background:#1f2937;padding:20px;margin:18px 0;border-radius:12px}audio{width:100%}.path,pre{font-size:13px;overflow-wrap:anywhere;white-space:pre-wrap;color:#9ca3af}a{color:#93c5fd}</style><h1>ミンフィリア · Clone 比較</h1><p>四組用同一句日文、同一粒角色 seed；只改 REF。音質分數唔代表冇回音。未更改目前使用嘅 REF。</p><p>測試句：'''
    page += html.escape(text) + '</p><p><a href="index.html">全部候選及評分</a></p>' + "".join(cards)
    page += '<script>document.addEventListener("play",e=>{if(e.target.tagName==="AUDIO")document.querySelectorAll("audio").forEach(a=>{if(a!==e.target)a.pause()})},true)</script></html>'
    (folder / "clone-comparison.html").write_text(page, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--folder", type=Path, default=Path("artifacts/ref-quality/minfilia-ja"))
    parser.add_argument("--server", choices=["http://127.0.0.1:8808"], default="http://127.0.0.1:8808")
    args = parser.parse_args()
    audition(args.folder, args.server)
