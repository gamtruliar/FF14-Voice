"""Small regression checks for screening and an actual completed ranking."""
import hashlib
import json
from pathlib import Path
import unittest

import numpy as np
from rank_voice_refs import metrics


class QualityChecks(unittest.TestCase):
    def test_silent_and_clipped_audio_are_rejected(self):
        self.assertTrue(metrics(np.zeros(16000 * 5), 16000)["rejection_reasons"])
        self.assertIn("clipping > 0.1%", metrics(np.ones(16000 * 5), 16000)["rejection_reasons"])

    def test_clean_signal_is_not_rejected(self):
        signal = .1 * np.sin(2 * np.pi * 200 * np.arange(16000 * 5) / 16000)
        self.assertEqual(metrics(signal, 16000)["rejection_reasons"], [])

    def test_real_reference_baseline_and_candidate_agree(self):
        folder = Path("artifacts/ref-quality/minfilia-ja")
        if not (folder / "ranking.json").exists():
            self.skipTest("Run pilot scoring first")
        report = json.loads((folder / "ranking.json").read_text(encoding="utf-8"))
        rows = report["rows"]
        baseline = next(r for r in rows if r.get("Baseline"))
        extracted = next(r for r in rows if not r.get("Baseline") and r["GamePath"] == baseline["GamePath"])
        self.assertEqual(extracted["sha256"], baseline["sha256"])
        self.assertEqual(extracted["SIG"], baseline["SIG"])
        self.assertEqual(extracted["OVRL"], baseline["OVRL"])
        # The later full-library pass may point the catalog at a new versioned
        # reference, but must preserve the original game WAV used by this pilot.
        original = Path("data/game-voices/references/MINFILIA/ja.wav")
        self.assertEqual(hashlib.sha256(original.read_bytes()).hexdigest(), baseline["sha256"], "Original reference must remain recoverable")
        self.assertGreater(len(rows), 3)
        for row in rows:
            self.assertEqual(row["Token"], "MINFILIA")
            self.assertEqual(row["Language"], "ja")
            self.assertTrue(all(np.isfinite(row[k]) for k in ["SIG", "BAK", "OVRL"]))


if __name__ == "__main__":
    unittest.main()
