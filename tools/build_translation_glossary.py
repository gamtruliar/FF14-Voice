"""Build a pinned multilingual FF14 term index. Windows NLS converts CN names to traditional characters.
The conversion is not a claim of Taiwan-client localization. No dialogue text is downloaded.
"""
import concurrent.futures
import csv
import ctypes
import hashlib
import io
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
REVISION = "935169e14dfa0525bd4f8f41cea53ce5d21a5c6d"
REPOSITORY = "https://github.com/InfSein/ffxiv-datamining-mixed"
SHEETS = ["ENpcResident", "BNpcName", "PlaceName", "ClassJob", "GrandCompany", "Race", "Tribe", "Item", "Action"]
LANGUAGES = {"ja": "ja", "en": "en", "de": "de", "fr": "fr", "chs": "zh-CN"}
CACHE = ROOT / "artifacts" / "glossary-source" / REVISION


def traditional(text):
    mapping = ctypes.windll.kernel32.LCMapStringEx
    mapping.argtypes = [ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_int, ctypes.c_wchar_p, ctypes.c_int,
                        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_longlong]
    mapping.restype = ctypes.c_int
    count = mapping("zh-HK", 0x04000000, text, len(text), None, 0, None, None, 0)
    if not count:
        raise RuntimeError("Windows traditional Chinese conversion failed")
    result = ctypes.create_unicode_buffer(count + 1)
    if not mapping("zh-HK", 0x04000000, text, len(text), result, count, None, None, 0):
        raise RuntimeError("Windows traditional Chinese conversion failed")
    return result.value


def download(pair):
    folder, sheet = pair
    file = CACHE / folder / (sheet + ".csv")
    if not file.exists():
        url = f"https://raw.githubusercontent.com/InfSein/ffxiv-datamining-mixed/{REVISION}/{folder}/{sheet}.csv"
        with urllib.request.urlopen(url, timeout=60) as response:
            content = response.read()
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(content)
    raw = file.read_bytes()
    rows = csv.reader(io.StringIO(raw.decode("utf-8-sig"), newline=""))
    next(rows)
    header = next(rows)
    types = next(rows)
    # These sheets explicitly name their primary label, never infer a column from offsets.
    field = next((name for name in ("Singular", "Name", "Masculine") if name in header), None)
    if field is None:
        raise RuntimeError(f"Unknown {sheet} fields: {header[:12]}")
    column = header.index(field)
    if types[column] != "str":
        raise RuntimeError(f"Unexpected {sheet}/{field} type")
    values = {}
    for row in rows:
        if len(row) <= column or not row[0].isdigit():
            continue
        name = row[column].strip()
        if 1 <= len(name) <= 100 and any(c.isalpha() for c in name) and "<" not in name and not any(ord(c) < 32 for c in name):
            values[row[0]] = name
    return folder, sheet, values, hashlib.sha256(raw).hexdigest(), field


def main():
    tables, hashes = {}, {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as workers:
        for folder, sheet, values, digest, field in workers.map(download, [(l, s) for l in LANGUAGES for s in SHEETS]):
            tables[folder, sheet] = values
            hashes[f"{folder}/{sheet}.csv"] = {"sha256": digest, "field": field}
    entries, seen = [], set()
    for sheet in SHEETS:
        ids = set().union(*(tables[l, sheet] for l in LANGUAGES))
        for row in sorted(ids, key=int):
            names = {code: tables[folder, sheet][row] for folder, code in LANGUAGES.items() if row in tables[folder, sheet]}
            if len(names) < 2:
                continue
            if "zh-CN" in names:
                names["zh-TW"] = traditional(names["zh-CN"])
            signature = (sheet, tuple(sorted(names.items())))
            if signature in seen:
                continue
            seen.add(signature)
            entries.append({"Id": f"{sheet}:{row}", "Category": sheet, "Names": names})
    lore = json.loads((ROOT / "data" / "translation-lore.json").read_text(encoding="utf-8"))
    entries.extend(lore)
    result = {"Schema": 1, "Repository": REPOSITORY, "Revision": REVISION,
              "TraditionalChineseBasis": "國服中文譯名經 Windows NLS 轉繁體；非台服官方詞表。",
              "Sources": hashes, "Entries": entries}
    output = ROOT / "data" / "translation-glossary.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    temporary.replace(output)
    print(json.dumps({"entries": len(entries), "bytes": output.stat().st_size, "output": str(output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
