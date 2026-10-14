"""Probe official Gemini using google-genai, without printing credentials or raw SDK errors."""
import asyncio
import json
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / 'VoxCPM'))
from gemini_translation import list_models, GeminiFailure

async def main():
    try:
        models = await list_models(str(root / 'APIKey/qqDragon.txt'))
        (root / 'artifacts/gemini-models.json').write_text(json.dumps(models, indent=2), encoding='utf-8')
        print(json.dumps({'status': 'ok', 'models': models}))
    except GeminiFailure as exc:
        print(json.dumps({'status': 'failed', 'http_status': exc.status, 'detail': str(exc)}))
        raise SystemExit(1)

asyncio.run(main())
