"""Download immutable GGUF files with resumable transfers and SHA-256 verification."""
import argparse
import hashlib
import json
from pathlib import Path
from huggingface_hub import hf_hub_download


def verify(path, item):
    if not path.is_file() or path.stat().st_size != item["size"]:
        return False
    checksum = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(8 * 1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest() == item["sha256"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(Path(__file__).with_name("manifest.json").read_text(encoding="utf-8"))
    for item in manifest["files"]:
        destination = args.directory / item["name"]
        if not verify(destination, item):
            path = hf_hub_download(manifest["model_repository"], item["name"],
                revision=manifest["model_revision"], local_dir=args.directory,
                force_download=destination.exists())
            if not verify(Path(path), item):
                raise RuntimeError(f"Model verification failed: {item['name']}; rerun setup to download it again.")
        print(f"Verified: {item['name']}", flush=True)


if __name__ == "__main__":
    main()
