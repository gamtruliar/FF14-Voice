import json
from pathlib import Path
import tempfile
import unittest

import numpy as np
import soundfile as sf

from refresh_voice_refs import (apply_report, build_jobs, choose, digest, inside,
                               make_report, normalize, prepare, restore, score_all, write_json)


class RefreshChecks(unittest.TestCase):
    def test_resume_retries_failed_changed_and_partial_records(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            jobs = {name: {"Id": name, "SourceHash": "new"} for name in ["good", "changed", "failed", "partial"]}
            saved = [{"Id": "good", "SourceHash": "new"}, {"Id": "changed", "SourceHash": "old"},
                     {"Id": "failed", "SourceHash": "new", "Error": "transient"}]
            (folder / "scores.jsonl").write_text("".join(json.dumps(r) + "\n" for r in saved) + '{"Id":"partial"', encoding="utf-8")
            called = []
            def score(job, session):
                called.append(job["Id"])
                return dict(job)
            with patch("refresh_voice_refs.create_session", return_value=object()), patch("refresh_voice_refs.score_one", side_effect=score):
                result = score_all(jobs, folder, folder / "unused.onnx", 2)
            self.assertEqual(set(called), {"changed", "failed", "partial"})
            self.assertEqual(set(result), set(jobs))
            records = [json.loads(line) for line in (folder / "scores.jsonl").read_text().splitlines()]
            self.assertEqual(len(records), 4)
            self.assertTrue(all(r["SourceHash"] == "new" and not r.get("Error") for r in records))

    def test_cached_resampling_is_byte_identical_to_scipy_default(self):
        import io
        from scipy.signal import resample_poly
        with tempfile.TemporaryDirectory() as temp:
            for rate in [44099, 44100, 48000]:
                path = Path(temp) / (str(rate) + ".wav")
                sf.write(path, np.random.default_rng(42).normal(0, .05, rate), rate, subtype="PCM_16")
                samples, _ = sf.read(path, dtype="float32")
                import math
                common = math.gcd(rate, 24000)
                expected = resample_poly(samples, 24000 // common, rate // common)
                buffer = io.BytesIO()
                sf.write(buffer, expected, 24000, format="WAV", subtype="PCM_16")
                self.assertEqual(normalize(path)[2], buffer.getvalue())

    def test_selection_keeps_good_baseline_and_rejects_bad_candidates(self):
        baseline = {"rank_score": 3.5, "rejection_reasons": []}
        low = {"rank_score": 2.0, "rejection_reasons": []}
        clipped = {"rank_score": 4.9, "rejection_reasons": ["clipping"]}
        self.assertIs(choose(baseline, [low, clipped])[0], baseline)
        self.assertIs(choose(baseline, [dict(baseline)])[0], baseline)
        good = {"rank_score": 4.0, "rejection_reasons": []}
        self.assertIs(choose(baseline, [good])[0], good)
        rejected = {"rank_score": 1.0, "rejection_reasons": ["too short"]}
        self.assertEqual(choose(rejected, [clipped])[1], "no_eligible_candidate")

    def fixture(self, root):
        library = root / "library"
        library.mkdir()
        candidates = root / "candidates"
        candidates.mkdir()
        samples = np.sin(np.arange(24000 * 5) * .05) * .1
        sf.write(library / "ja.wav", samples, 24000, subtype="PCM_16")
        sf.write(library / "en.wav", samples * .8, 24000, subtype="PCM_16")
        sf.write(candidates / "better.wav", samples * .6, 24000, subtype="PCM_16")
        catalog = {"Version": 1, "Sqpack": "unchanged", "BuiltAt": "2026-10-01T00:00:00Z", "Errors": [],
                   "Voices": [{"Token": "NPC", "Names": ["日本語名", "English alias"], "References": [
                       {"Language": lang, "RelativePath": lang + ".wav", "Seconds": 5, "Transcript": "original",
                        "GamePath": "old/" + lang, "Sheet": "old"} for lang in ["ja", "en"]]}]}
        catalog_path = library / "catalog.json"
        write_json(catalog_path, catalog)
        entries = [{"File": "better.wav", "Token": "NPC", "Language": "ja", "Text": "new text", "GamePath": "new/ja", "Sheet": "new"}]
        write_json(candidates / "candidates.json", entries)
        folder = root / "run"
        context = prepare(catalog_path, folder, candidates / "candidates.json")
        jobs, groups = build_jobs(catalog, library, candidates, entries)
        scores = {}
        for name, job in jobs.items():
            _, _, pcm = normalize(Path(job["Path"]))
            import hashlib
            scores[name] = {"Id": name, "PcmHash": hashlib.sha256(pcm).hexdigest(), "Seconds": 5,
                            "rank_score": 3 if job["Baseline"] else 4, "rejection_reasons": []}
        return catalog_path, folder, context, jobs, make_report(groups, scores, context)

    def test_install_and_restore_preserve_aliases_other_languages_and_original_wavs(self):
        with tempfile.TemporaryDirectory() as temp:
            catalog, folder, context, jobs, report = self.fixture(Path(temp))
            original = catalog.read_bytes()
            apply_report(report, jobs, folder)
            installed = json.loads(catalog.read_text(encoding="utf-8"))
            voice = installed["Voices"][0]
            self.assertEqual(voice["Names"], ["日本語名", "English alias"])
            self.assertEqual(voice["References"][1]["RelativePath"], "en.wav")
            self.assertEqual(voice["References"][0]["Transcript"], "new text")
            self.assertTrue(voice["References"][0]["RelativePath"].startswith("references-quality/"))
            for relative, expected in context["OriginalReferenceHashes"].items():
                self.assertEqual(digest(catalog.parent / relative), expected)
                self.assertEqual(digest(Path(context["Backup"]) / relative), expected)
            restore(folder)
            self.assertEqual(catalog.read_bytes(), original)

    def test_concurrent_edit_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            catalog, folder, _, jobs, report = self.fixture(Path(temp))
            original = catalog.read_bytes() + b"\n"
            catalog.write_bytes(original)
            with self.assertRaisesRegex(ValueError, "Catalog changed"):
                apply_report(report, jobs, folder)
            self.assertEqual(catalog.read_bytes(), original)

    def test_changed_candidate_is_rejected_before_catalog_switch(self):
        with tempfile.TemporaryDirectory() as temp:
            catalog, folder, _, jobs, report = self.fixture(Path(temp))
            original = catalog.read_bytes()
            source = Path(next(j["Path"] for j in jobs.values() if not j["Baseline"]))
            sf.write(source, np.zeros(24000 * 5), 24000)
            with self.assertRaisesRegex(ValueError, "Candidate changed"):
                apply_report(report, jobs, folder)
            self.assertEqual(catalog.read_bytes(), original)

    def test_paths_cannot_escape_root(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError): inside(Path(temp), "../outside.wav")


if __name__ == "__main__": unittest.main()
