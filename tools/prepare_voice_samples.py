"""Convert VoiceScan samples to PCM WAV and retain text/path provenance."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import wave

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
args = parser.parse_args()
ffmpeg = shutil.which("ffmpeg")
if not ffmpeg:
    raise SystemExit("ffmpeg must be on PATH")
characters = json.loads((args.directory / "characters.json").read_text(encoding="utf-8-sig"))
samples = []
for character in characters:
    for sample in character["Samples"]:
        source = Path(sample["SampleFile"])
        output = source.with_name(sample["Language"] + ".wav")
        subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-y", "-i", str(source),
                        "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", str(output)], check=True)
        with wave.open(str(output), "rb") as wav:
            duration = wav.getnframes() / wav.getframerate()
            assert wav.getnchannels() == 1 and wav.getsampwidth() == 2 and 5 <= duration <= 12.01
        output.with_suffix(".txt").write_text(sample["Text"], encoding="utf-8")
        samples.append({"speaker_token": character["SpeakerToken"], "name_candidates": character["NameCandidates"],
                        "language": sample["Language"], "wav": str(output), "seconds": round(duration, 3),
                        "transcript": sample["Text"], "game_path": sample["GamePath"]})
(args.directory / "sample-index.json").write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"Verified {len(samples)} PCM WAV samples (48 kHz mono, 5–12 seconds)")
