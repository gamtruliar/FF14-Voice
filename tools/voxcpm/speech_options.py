"""Shared UI/API speech options. VoxCPM2 uses text instructions, not language IDs."""
import re

LANGUAGES = {
    "auto": "Auto / 跟隨文字", "yue": "廣東話 / Cantonese",
    "zh": "普通話 / Mandarin", "en": "English", "ja": "日本語",
    "ko": "한국어", "fr": "Français", "de": "Deutsch",
}
INSTRUCTIONS = {
    "yue": "用廣東話講", "zh": "用普通話說話", "en": "Speak in English",
    "ja": "Speak in Japanese", "ko": "Speak in Korean",
    "fr": "Speak in French", "de": "Speak in German",
}


def prepare_speech(text, control="", language="auto", reference=None, transcript=""):
    text = (text or "").strip()
    if not text:
        raise ValueError("Please input text to synthesize.")
    if language not in LANGUAGES:
        raise ValueError(f"Unsupported speech language: {language}")
    if transcript and not reference:
        raise ValueError("Transcript-guided cloning requires reference audio.")
    # Continuation mode does not support control instructions. Never silently
    # ignore a selected language; callers should use controllable cloning.
    if transcript and (language != "auto" or control.strip()):
        raise ValueError("Transcript cloning requires Auto language and no style instruction. Use voice cloning for language control.")
    instruction = "; ".join(filter(None, [INSTRUCTIONS.get(language), control.strip()]))
    instruction = re.sub(r"[()（）]", "", instruction).strip()
    return f"({instruction}){text}" if instruction else text
