"""Resumable full-library DNSMOS ranking and transactional catalog replacement.

Original reference WAVs are never overwritten. A verified backup and a JSON report
are saved before an atomic catalog switch to versioned, scored PCM references.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED
from datetime import datetime, timezone
import hashlib
import html
import io
import json
import math
import os
from pathlib import Path
import shutil
import time
import threading
from functools import lru_cache

import numpy as np
import onnxruntime as ort
from scipy.signal import resample_poly, firwin
import soundfile as sf

from rank_voice_refs import MODEL_SHA256, REVISION, metrics, mos

ALGORITHM = "dnsmos-p835-v1-scipy-pcm24k-energy-3to12s"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    stage = path.with_name(path.name + ".tmp")
    stage.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    os.replace(stage, path)


def inside(root, relative):
    target = (root / relative).resolve()
    if not target.is_relative_to(root.resolve()) or target == root.resolve():
        raise ValueError("Path outside intended directory: " + str(relative))
    return target


@lru_cache(maxsize=128)
def resample_filter(up, down):
    # Identical to SciPy 1.15's default float32 Kaiser FIR. Game sample rates
    # such as 44099 Hz otherwise rebuild a ~480k-tap filter for every clip.
    maximum = max(up, down)
    window = firwin(20 * maximum + 1, 1. / maximum, window=("kaiser", 5.0)).astype(np.float32)
    window.flags.writeable = False
    return window


def normalize(path):
    audio, rate = sf.read(path, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if len(audio) < rate * .02 or not np.isfinite(audio).all():
        raise ValueError("Invalid/empty audio")
    if rate != 24000:
        common = math.gcd(rate, 24000)
        up, down = 24000 // common, rate // common
        audio = resample_poly(audio, up, down, window=resample_filter(up, down))
    buffer = io.BytesIO()
    sf.write(buffer, audio, 24000, format="WAV", subtype="PCM_16")
    pcm = buffer.getvalue()
    # Rank precisely the PCM that will be installed, including quantization.
    audio, rate = sf.read(io.BytesIO(pcm), dtype="float32")
    return audio, rate, pcm


def score_one(job, session):
    try:
        audio, rate, pcm = normalize(Path(job["Path"]))
        result = metrics(audio, rate)
        if not result["rejection_reasons"] or job["Baseline"]:
            result.update(mos(audio, rate, session))
            result["rank_score"] = .5 * result["SIG"] + .2 * result["BAK"] + .3 * result["OVRL"]
        return {"Id": job["Id"], "SourceHash": job["SourceHash"], "Seconds": len(audio) / rate,
                "PcmHash": hashlib.sha256(pcm).hexdigest(), **result}
    except Exception as error:
        return {"Id": job["Id"], "SourceHash": job["SourceHash"], "Error": str(error)}


def choose(baseline, candidates):
    eligible = [c for c in candidates if not c.get("Error") and not c["rejection_reasons"]]
    if not baseline.get("Error") and not baseline["rejection_reasons"]:
        eligible.append(baseline)
    if not eligible:
        return baseline, "no_eligible_candidate"
    # Preserve the original on ties. Do not replace a good reference with a worse one.
    best = max(eligible, key=lambda x: (x["rank_score"], x is baseline))
    return best, "already_best" if best is baseline else "replaced"


def prepare(catalog_path, folder, candidates_path):
    context_path = folder / "run.json"
    if context_path.exists():
        context = json.loads(context_path.read_text(encoding="utf-8"))
        if context["Algorithm"] != ALGORITHM or context["CandidatesHash"] != digest(candidates_path):
            raise ValueError("Inputs/algorithm changed; use a new run folder")
        return context
    original = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup = catalog_path.parent / "ref-quality-backups" / run_id
    backup.mkdir(parents=True, exist_ok=False)
    catalog_hash = digest(catalog_path)
    shutil.copy2(catalog_path, backup / "catalog.json")
    hashes = {}
    for voice in original["Voices"]:
        for ref in voice["References"]:
            source = inside(catalog_path.parent, ref["RelativePath"])
            destination = inside(backup, ref["RelativePath"])
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
            hashes[ref["RelativePath"]] = digest(source)
            if digest(destination) != hashes[ref["RelativePath"]]: raise ValueError("Backup hash mismatch")
    if digest(catalog_path) != catalog_hash: raise ValueError("Catalog changed during backup")
    context = {"RunId": run_id, "Algorithm": ALGORITHM, "ModelRevision": REVISION, "ModelHash": MODEL_SHA256,
               "Catalog": str(catalog_path.resolve()), "OriginalCatalogHash": catalog_hash, "Backup": str(backup.resolve()),
               "CandidatesHash": digest(candidates_path), "OriginalReferenceHashes": hashes}
    write_json(backup / "backup-manifest.json", context)
    write_json(context_path, context)
    return context


def build_jobs(original, catalog_root, candidates_root, candidates, folder=None):
    jobs = {}
    groups = {}
    for voice in original["Voices"]:
        for ref in voice["References"]:
            key = (voice["Token"], ref["Language"])
            name = "original:" + voice["Token"] + ":" + ref["Language"]
            jobs[name] = {"Id": name, "Root": catalog_root, "Relative": ref["RelativePath"], "Baseline": True}
            groups[key] = {"Original": ref, "BaselineId": name, "Candidates": []}
    for row in candidates:
        key = (row["Token"], row["Language"])
        if key not in groups: continue
        name = "candidate:" + row["File"]
        if name not in jobs:
            jobs[name] = {"Id": name, "Root": candidates_root, "Relative": row["File"], "Baseline": False}
        groups[key]["Candidates"].append({**row, "Id": name})
    def validate(job):
        path = inside(job["Root"], job["Relative"])
        return {"Id": job["Id"], "Path": str(path), "SourceHash": digest(path), "Baseline": job["Baseline"]}
    verified = {}
    # Small independent WAVs are latency-bound on the workspace drive. Parallel
    # reads retain full content-hash verification instead of trusting timestamps.
    with ThreadPoolExecutor(max_workers=12) as pool:
        for job in pool.map(validate, jobs.values()):
            verified[job["Id"]] = job
            if len(verified) % 2000 == 0 or len(verified) == len(jobs):
                status = {"State": "preparing", "VerifiedInputs": len(verified), "Total": len(jobs)}
                if folder: write_json(folder / "status.json", status)
                print(json.dumps(status), flush=True)
    return verified, groups


def create_session(model, device="cpu", dll_directory=None):
    if digest(model) != MODEL_SHA256: raise ValueError("Model checksum mismatch")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    providers = ["CPUExecutionProvider"]
    if device == "cuda":
        ort.preload_dlls(directory=str(dll_directory.resolve()) if dll_directory else None)
        providers.insert(0, ("CUDAExecutionProvider", {"gpu_mem_limit": 512 * 1024 * 1024,
            "arena_extend_strategy": "kSameAsRequested", "cudnn_conv_algo_search": "HEURISTIC",
            "cudnn_conv_use_max_workspace": "0", "use_tf32": "0"}))
    session = ort.InferenceSession(str(model), sess_options=options, providers=providers)
    if device == "cuda" and session.get_providers()[0] != "CUDAExecutionProvider":
        raise RuntimeError("CUDA unavailable; rerun explicitly with --device cpu")
    if device == "cuda":
        # Preprocessing may run concurrently, but GPU inference stays single-file
        # to respect the shared 512 MiB arena instead of multiplying allocations.
        class SerializedSession:
            def __init__(self): self.gate = threading.Lock()
            def get_inputs(self): return session.get_inputs()
            def run(self, *args, **kwargs):
                with self.gate: return session.run(*args, **kwargs)
        return SerializedSession()
    return session


def score_all(jobs, folder, model, workers, device="cpu", dll_directory=None):
    session = create_session(model, device, dll_directory)
    results = {}
    log = folder / "scores.jsonl"
    if log.exists():
        for line in log.read_text(encoding="utf-8").splitlines():
            try: row = json.loads(line)
            except json.JSONDecodeError: continue  # interrupted last write; retry it
            job = jobs.get(row["Id"])
            if job and row["SourceHash"] == job["SourceHash"] and not row.get("Error"):
                results[row["Id"]] = row
        # Remove any interrupted JSON line before appending resumable records.
        log.write_text("".join(json.dumps(r, allow_nan=False) + "\n" for r in results.values()), encoding="utf-8")
    pending = iter(j for name, j in jobs.items() if name not in results)
    start = time.perf_counter()
    previous = len(results)
    last_report = 0
    with ThreadPoolExecutor(max_workers=workers) as pool, log.open("a", encoding="utf-8") as out:
        running = {}
        buffered = []
        def flush_scores():
            if buffered:
                out.write("".join(buffered))
                buffered.clear()
            out.flush()
        def fill():
            while len(running) < workers * 2:
                job = next(pending, None)
                if job is None: break
                running[pool.submit(score_one, job, session)] = job["Id"]
        fill()
        while running:
            done, _ = wait(running, timeout=5, return_when=FIRST_COMPLETED)
            for future in done:
                name = running.pop(future)
                row = future.result()
                results[name] = row
                buffered.append(json.dumps(row, allow_nan=False) + "\n")
            if len(buffered) >= 64: flush_scores()
            fill()
            elapsed = time.perf_counter() - start
            if elapsed - last_report >= 20 or not running:
                flush_scores()
                rate = (len(results) - previous) / max(elapsed, .001)
                status = {"State": "scoring", "Completed": len(results), "Total": len(jobs),
                          "Errors": sum(bool(r.get("Error")) for r in results.values()), "ClipsPerSecond": rate,
                          "RemainingSeconds": (len(jobs) - len(results)) / max(rate, .001), "Workers": workers, "Device": device}
                write_json(folder / "status.json", status)
                print(json.dumps(status), flush=True)
                last_report = elapsed
    if any(r.get("Error") for r in results.values()):
        raise RuntimeError("Scoring errors; see scores.jsonl. Rerun to retry before applying.")
    return results


def make_report(groups, scores, context):
    rows = []
    for (speaker, language), group in groups.items():
        baseline = scores[group["BaselineId"]]
        candidates = [{**c, **scores[c["Id"]]} for c in group["Candidates"]]
        best, reason = choose(baseline, candidates)
        if best["PcmHash"] == baseline["PcmHash"]: best, reason = baseline, "already_best"
        rows.append({"Token": speaker, "Language": language, "Status": reason, "Candidates": len(candidates),
                     "Eligible": sum(not c["rejection_reasons"] for c in candidates),
                     "Original": group["Original"], "Before": baseline, "After": best})
    return {"Run": context, "Summary": {state: sum(r["Status"] == state for r in rows)
                                        for state in ["replaced", "already_best", "no_eligible_candidate"]}, "Rows": rows}


def apply_report(report, jobs, folder):
    context = report["Run"]
    catalog_path = Path(context["Catalog"])
    if digest(catalog_path) != context["OriginalCatalogHash"]:
        raise ValueError("Catalog changed since backup; refusing to overwrite concurrent changes")
    for relative, expected in context["OriginalReferenceHashes"].items():
        if digest(inside(catalog_path.parent, relative)) != expected:
            raise ValueError("Active reference changed since backup: " + relative)
    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    mapping = {(v["Token"], r["Language"]): r for v in catalog["Voices"] for r in v["References"]}
    for row in report["Rows"]:
        if row["Status"] != "replaced": continue
        best = row["After"]
        _, _, pcm = normalize(Path(jobs[best["Id"]]["Path"]))
        if hashlib.sha256(pcm).hexdigest() != best["PcmHash"]: raise ValueError("Candidate changed since scoring")
        relative = "references-quality/" + context["RunId"] + "/" + best["PcmHash"] + ".wav"
        target = inside(catalog_path.parent, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        stage = target.with_suffix(".tmp")
        stage.write_bytes(pcm)
        os.replace(stage, target)
        if digest(target) != best["PcmHash"]: raise ValueError("Installed reference hash mismatch")
        mapping[(row["Token"], row["Language"])].update(RelativePath=relative, Seconds=best["Seconds"],
                                                       Transcript=best["Text"], GamePath=best["GamePath"], Sheet=best["Sheet"])
    # All new files are complete and verified before the single catalog switch.
    if digest(catalog_path) != context["OriginalCatalogHash"]: raise ValueError("Concurrent catalog modification")
    write_json(folder / "proposed-catalog.json", catalog)
    report["InstalledCatalogHash"] = digest(folder / "proposed-catalog.json")
    write_json(folder / "report.json", report)
    backup = Path(context["Backup"])
    write_json(backup / "replacement-report.json", report)
    stage = catalog_path.with_name("catalog.quality.tmp")
    shutil.copyfile(folder / "proposed-catalog.json", stage)
    os.replace(stage, catalog_path)
    if digest(catalog_path) != report["InstalledCatalogHash"]: raise ValueError("Catalog verification failed")
    write_json(folder / "status.json", {"State": "complete", **report["Summary"], "References": len(report["Rows"]), "Backup": str(backup)})
    write_review(report, folder)


def write_review(report, folder):
    context = report["Run"]
    original = json.loads((Path(context["Backup"]) / "catalog.json").read_text(encoding="utf-8-sig"))
    names = {v["Token"]: " / ".join(v["Names"]) for v in original["Voices"]}
    cards = []
    labels = {"replaced": "已換最佳候選", "already_best": "原 REF 已最佳", "no_eligible_candidate": "冇合格候選，保留原 REF"}
    for row in report["Rows"]:
        before = inside(Path(context["Backup"]), row["Original"]["RelativePath"])
        best = row["After"]
        after = (Path(context["Catalog"]).parent / "references-quality" / context["RunId"] / (best["PcmHash"] + ".wav")) if row["Status"] == "replaced" else before
        title = row["Token"] + " · " + row["Language"] + " · " + names[row["Token"]]
        old_score = row["Before"].get("rank_score", 0)
        new_score = best.get("rank_score", 0)
        details = (f'<p>{row["Candidates"]} 候選／{row["Eligible"]} 合格 · 綜合排名分數 {old_score:.3f} → {new_score:.3f}</p>'
                   f'<p>原 REF</p><audio controls preload="none" src="{html.escape(before.as_uri(), quote=True)}"></audio>'
                   f'<p>更新後 REF</p><audio controls preload="none" src="{html.escape(after.as_uri(), quote=True)}"></audio>'
                   f'<p>{html.escape(best.get("Text", row["Original"]["Transcript"]))}</p>'
                   f'<p class="path">{html.escape(best.get("GamePath", row["Original"]["GamePath"]))}</p>')
        cards.append(f'<details data-state="{row["Status"]}"><summary>{html.escape(title)} — {labels[row["Status"]]}</summary>{details}</details>')
    summary = report["Summary"]
    page = '''<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>全聲庫 REF 音質更新</title><style>body{background:#111827;color:#e5e7eb;font:16px system-ui;max-width:1100px;margin:35px auto;padding:0 20px}h1{color:#f4d595}details{background:#1f2937;padding:16px;margin:10px 0;border-radius:8px}summary{cursor:pointer}audio{width:100%}input,select{padding:10px;font:inherit;margin:5px}input{width:50%}.path{overflow-wrap:anywhere;font-size:13px;color:#9ca3af}</style><h1>全聲庫 REF 音質更新</h1>'''
    page += f'<p>{len(report["Rows"])} 個 REF：換咗 {summary["replaced"]} 個；原本最佳 {summary["already_best"]} 個；冇合格候選保留 {summary["no_eligible_candidate"]} 個。</p>'
    page += '<p>同角色、同語言內比較完整 3–12 秒片段。排序用 0.5×人聲 + 0.2×背景 + 0.3×整體品質。高分唔保證冇回音，亦唔判斷角色唔同時期聲線。</p><input id="search" placeholder="搜尋角色／語言"><select id="state"><option value="">全部狀態</option><option value="replaced">已更換</option><option value="already_best">原本最佳</option><option value="no_eligible_candidate">冇合格候選</option></select>'
    page += "".join(cards) + '''<script>const q=document.getElementById('search'),s=document.getElementById('state');function filter(){document.querySelectorAll('details').forEach(d=>d.hidden=!(d.querySelector('summary').textContent.toLowerCase().includes(q.value.toLowerCase())&&(!s.value||s.value===d.dataset.state)))}q.addEventListener('input',filter);s.addEventListener('change',filter);document.addEventListener('play',e=>{if(e.target.tagName==='AUDIO')document.querySelectorAll('audio').forEach(a=>{if(a!==e.target)a.pause()})},true)</script></html>'''
    (folder / "index.html").write_text(page, encoding="utf-8")


def restore(folder):
    report = json.loads((folder / "report.json").read_text(encoding="utf-8"))
    context = report["Run"]
    catalog = Path(context["Catalog"])
    if digest(catalog) != report["InstalledCatalogHash"]: raise ValueError("Catalog changed since installation; refusing to overwrite newer edits")
    original = Path(context["Backup"]) / "catalog.json"
    if digest(original) != context["OriginalCatalogHash"]: raise ValueError("Backup checksum mismatch")
    for relative, expected in context["OriginalReferenceHashes"].items():
        if digest(inside(catalog.parent, relative)) != expected: raise ValueError("Original WAV changed; restore from verified backup first")
    stage = catalog.with_name("catalog.restore.tmp")
    shutil.copyfile(original, stage)
    os.replace(stage, catalog)
    write_json(folder / "status.json", {"State": "restored", "Backup": context["Backup"]})


def main(args):
    folder = args.folder.resolve()
    folder.mkdir(parents=True, exist_ok=True)
    # Prevent two runs from scoring/applying the same work directory concurrently.
    import msvcrt
    with (folder / ".run.lock").open("a+b") as lock:
        lock.seek(0)
        if not lock.read(1): lock.write(b"0"); lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        if args.restore: restore(folder); return
        context = prepare(args.catalog.resolve(), folder, args.candidates / "candidates.json")
        report_path = folder / "report.json"
        if report_path.exists():
            report = json.loads(report_path.read_text(encoding="utf-8"))
            if report.get("InstalledCatalogHash") == digest(args.catalog):
                write_review(report, folder)
                print("Already applied; no changes needed."); return
        if digest(args.catalog) != context["OriginalCatalogHash"]: raise ValueError("Active catalog changed since run started")
        errors_path = args.candidates / "extraction-errors.json"
        if not errors_path.exists() or json.loads(errors_path.read_text(encoding="utf-8")):
            raise ValueError("Missing extraction report or unresolved extraction errors")
        write_json(folder / "status.json", {"State": "preparing", "Message": "Validating candidate hashes before resumable scoring"})
        original = json.loads((Path(context["Backup"]) / "catalog.json").read_text(encoding="utf-8-sig"))
        candidates = json.loads((args.candidates / "candidates.json").read_text(encoding="utf-8"))
        jobs, groups = build_jobs(original, args.catalog.resolve().parent, args.candidates.resolve(), candidates, folder)
        scores = score_all(jobs, folder, args.model, args.workers, args.device, args.dll_directory)
        report = make_report(groups, scores, context)
        write_json(folder / "report.json", report)
        if args.apply: apply_report(report, jobs, folder)
        print(json.dumps(report["Summary"]), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path("data/game-voices/catalog.json"))
    parser.add_argument("--folder", type=Path, default=Path("artifacts/ref-quality/all"))
    parser.add_argument("--candidates", type=Path, default=Path("artifacts/ref-quality/all/candidates"))
    parser.add_argument("--model", type=Path, default=Path("artifacts/ref-quality/models/sig_bak_ovr.onnx"))
    parser.add_argument("--workers", type=int, choices=range(1, 25), default=12)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--dll-directory", type=Path)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--restore", action="store_true")
    main(parser.parse_args())
