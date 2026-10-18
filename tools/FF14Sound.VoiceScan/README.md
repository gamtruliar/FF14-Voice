# Local game voice discovery

Reads the installed client with Lumina 7.4.0. Does not modify game files.

```powershell
dotnet run --project tools/FF14Sound.VoiceScan -- "D:\Games\FFXIV\game\sqpack" "artifacts\game-voices"
VoxCPM/.venv/Scripts/python.exe tools/prepare_voice_samples.py artifacts/game-voices
```

`voice-clips.json` records successfully parsed voice resources and their dialogue keys, localized text, speaker tokens and game paths. `characters.json` groups by speaker token; exact normalized matches from ENpcResident are name **candidates**, not an assertion that every token is a distinct person. Unknown aliases remain unresolved. Language and player-dependent line variants count separately.

For the complete resource workflow, use [Restore-GameResources](../../docs/game-resources.md). It runs this scanner and the library builder, including the runtime index used to skip recognized native voiced dialogue.

`scan-summary.json` records coverage and parse failures. Coverage is limited to `cut_scene/*/VoiceMan_*` sheets. A missing file is not proof that an NPC has no voice or a line is unvoiced: other naming families, placeholder assets and unavailable expansion data exist. Inspect the report for unreadable candidates; the manifest only includes successfully parsed clips.

Twelve preview/reference samples cover Alphinaud, Alisaie, Thancred, Urianger, Y'shtola and Tataru, English and Japanese. Prefer the latest available dialogue sheets, and select 5–12 seconds of audio. `*-source.wav` keeps MS-ADPCM audio; the Python helper creates compatible `en.wav` / `ja.wav`, matching transcript files and `sample-index.json`. Review samples by ear before treating them as final voice profiles. Samples retain language and chapter provenance; do not mix voice-actor eras indiscriminately.

Pass `--samples-only` after the two directory arguments to reselect samples from the existing manifest without rescanning all voice files.

Format references: [Lumina](https://github.com/NotAdam/Lumina), [voice path conventions](https://github.com/RenNagasaki/Echokraut/blob/main/Echokraut/Helper/Functional/VoiceScdPaths.cs). The scanner uses the installed Lumina package and independently checks naming conventions against local game data; no Echokraut implementation is included in the project.
