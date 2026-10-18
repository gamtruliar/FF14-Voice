"""Official google-genai SDK adapter. Credentials never enter responses or logs."""
import asyncio
import json
from pathlib import Path
import re


class GeminiFailure(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def read_key(filename):
    try:
        path = Path(filename)
        if not path.is_file() or path.stat().st_size > 8192:
            raise ValueError()
        key = path.read_text(encoding='utf-8-sig').strip()
        if not key or any(c.isspace() or ord(c) < 32 for c in key):
            raise ValueError()
        return key
    except (OSError, ValueError, UnicodeError):
        raise GeminiFailure(422, 'API key 檔案不存在或格式不符；請只放一行 key。') from None


def create_client(key_file):
    try:
        from google import genai
        from google.genai import types
    except ImportError:
        raise GeminiFailure(503, '尚未安裝 google-genai SDK，請執行 Setup-Gemini.cmd。') from None
    return genai.Client(api_key=read_key(key_file), http_options=types.HttpOptions(
        api_version='v1beta', base_url='https://generativelanguage.googleapis.com', timeout=90000,
        retry_options=types.HttpRetryOptions(attempts=1)))


def safe_error(exc):
    # SDK exception text may include server content. Only expose status and our own fixed messages.
    code = getattr(exc, 'code', None)
    messages = {
        400: 'Google 拒絕請求：請檢查 API key、模型及設定。',
        401: 'Google 驗證失敗：請檢查 API key。',
        402: 'Google 預付額度不足；請到 AI Studio 專案計費頁補充額度，再重試翻譯。',
        403: 'Google 拒絕存取：請檢查 API key／專案／模型權限。',
        404: '模型不存在或未開放，請更新模型清單。',
        429: 'Google 配額／速率限制；請稍後再試，唔會自動重試扣額。',
    }
    return GeminiFailure(code if code in messages else 502, messages.get(code, 'Gemini 連線或服務失敗，請檢查網絡及 Google API 狀態。'))


async def list_models(key_file):
    client = None
    try:
        client = create_client(key_file)
        async with client.aio as api:
            pager = await api.models.list(config={'page_size': 100})
            return sorted({m.name.removeprefix('models/') async for m in pager
                           if m.name and m.name.removeprefix('models/').startswith('gemini-')
                           and 'generateContent' in (m.supported_actions or [])
                           and not any(kind in m.name for kind in ('image', 'tts', 'live', 'omni', 'transcribe', 'robotics', 'computer-use', 'customtools', 'nano-banana'))})
    except (GeminiFailure, asyncio.CancelledError):
        raise
    except Exception as exc:
        raise safe_error(exc) from None
    finally:
        if client is not None:
            client.close()


async def translate(key_file, model, text, speaker, system_prompt):
    model = model.removeprefix('models/')
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,127}', model):
        raise GeminiFailure(422, 'Gemini 模型名稱格式不符。')
    client = None
    try:
        client = create_client(key_file)
        config = {'system_instruction': system_prompt, 'temperature': 0.2, 'max_output_tokens': 4096,
                  'automatic_function_calling': {'disable': True}}
        if model.startswith('gemini-3'):
            config['thinking_config'] = {'thinking_level': 'LOW', 'include_thoughts': False}
        async with client.aio as api:
            result = await api.models.generate_content(model=model,
                contents=json.dumps({'speaker': speaker, 'text': text}, ensure_ascii=False),
                config=config)
        candidates = result.candidates or []
        if not candidates:
            raise GeminiFailure(422, 'Gemini 冇回傳譯文，請檢查模型回應或平台限制。')
        candidate = candidates[0]
        if candidate.finish_reason != 'STOP':
            raise GeminiFailure(422, 'Gemini 翻譯未完整完成，已停止配音，避免讀出半截內容。')
        parts = candidate.content.parts if candidate.content else []
        translated = ''.join(p.text for p in (parts or []) if p.text and not p.thought).strip()
        if not translated or '<think' in translated.lower() or '</think' in translated.lower():
            raise GeminiFailure(422, 'Gemini 只回傳思考／空白，冇可用譯文。')
        return translated
    except (GeminiFailure, asyncio.CancelledError):
        raise
    except Exception as exc:
        raise safe_error(exc) from None
    finally:
        if client is not None:
            client.close()
