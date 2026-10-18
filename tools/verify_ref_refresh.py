"""Audit every installed reference and the highest-eligible-score selection."""
import json
from pathlib import Path
import soundfile as sf

from refresh_voice_refs import digest, inside, choose, write_json


def verify(folder):
    report = json.loads((folder / "report.json").read_text(encoding="utf-8"))
    context = report["Run"]
    catalog_path = Path(context["Catalog"])
    assert digest(catalog_path) == report["InstalledCatalogHash"]
    backup = Path(context["Backup"])
    assert digest(backup / "catalog.json") == context["OriginalCatalogHash"]
    old = json.loads((backup / "catalog.json").read_text(encoding="utf-8-sig"))
    new = json.loads(catalog_path.read_text(encoding="utf-8"))
    assert {k: v for k, v in old.items() if k != "Voices"} == {k: v for k, v in new.items() if k != "Voices"}
    assert len(old["Voices"]) == len(new["Voices"])
    installed = {}
    for a, b in zip(old["Voices"], new["Voices"]):
        assert {k: v for k, v in a.items() if k != "References"} == {k: v for k, v in b.items() if k != "References"}
        assert [r["Language"] for r in a["References"]] == [r["Language"] for r in b["References"]]
        for ref in b["References"]: installed[(b["Token"], ref["Language"])] = ref
    scores = {}
    for line in (folder / "scores.jsonl").read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        scores[row["Id"]] = row
    assert not any(r.get("Error") for r in scores.values())
    candidates = json.loads((folder / "candidates/candidates.json").read_text(encoding="utf-8"))
    grouped = {}
    for row in candidates:
        grouped.setdefault((row["Token"], row["Language"]), []).append(scores["candidate:" + row["File"]])
    assert len(report["Rows"]) == len(installed)
    assert len({(r["Token"], r["Language"]) for r in report["Rows"]}) == len(installed)
    for row in report["Rows"]:
        key = (row["Token"], row["Language"])
        baseline = scores["original:" + row["Token"] + ":" + row["Language"]]
        expected, _ = choose(baseline, grouped.get(key, []))
        assert row["After"]["PcmHash"] == expected["PcmHash"], "Not the highest eligible score: " + str(key)
        ref = installed[key]
        if row["Status"] == "replaced":
            path = inside(catalog_path.parent, ref["RelativePath"])
            assert digest(path) == row["After"]["PcmHash"]
            info = sf.info(path)
            assert info.samplerate == 24000 and info.channels == 1 and info.subtype == "PCM_16"
            assert abs(info.duration - ref["Seconds"]) < 1 / 24000
            assert ref["GamePath"] == row["After"]["GamePath"] and ref["Transcript"] == row["After"]["Text"]
            assert not row["After"]["rejection_reasons"]
        else:
            assert ref == row["Original"]
    for relative, expected in context["OriginalReferenceHashes"].items():
        assert digest(inside(catalog_path.parent, relative)) == expected
        assert digest(inside(backup, relative)) == expected
    protected = folder / "protected-files.json"
    if protected.exists():
        for path, expected in json.loads(protected.read_text(encoding="utf-8-sig")).items():
            assert digest(Path(path)).upper() == expected
    result = {"Verified": True, "Voices": len(new["Voices"]), "References": len(installed),
              "ScoredItems": len(scores), **report["Summary"], "CatalogHash": digest(catalog_path)}
    write_json(folder / "verification.json", result)
    print(json.dumps(result))


if __name__ == "__main__": verify(Path("artifacts/ref-quality/all"))
