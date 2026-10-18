import asyncio
import json
from pathlib import Path
import sys
from types import SimpleNamespace as NS
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import gemini_translation as gemini


class FakeClient:
    def __init__(self, reason='STOP', fail=None):
        self.aio = self
        self.models = self
        self.reason, self.fail = reason, fail
        self.closed = self.async_closed = False
        self.kwargs = None
    async def __aenter__(self):
        return self
    async def __aexit__(self, *args):
        self.async_closed = True
    def close(self):
        self.closed = True
    async def generate_content(self, **kwargs):
        self.kwargs = kwargs
        if self.fail:
            raise self.fail
        return NS(candidates=[NS(finish_reason=self.reason, content=NS(parts=[
            NS(text='private reasoning', thought=True), NS(text='你好呀。', thought=False)]))])


def test_sdk_payload_unicode_and_thought_filter(monkeypatch):
    client = FakeClient()
    monkeypatch.setattr(gemini, 'create_client', lambda _: client)
    result = asyncio.run(gemini.translate('unused', 'gemini-3.8-flash', 'こんにちは', 'NPC', 'translate'))
    assert result == '你好呀。'
    assert 'こんにちは' in client.kwargs['contents']
    assert json.loads(client.kwargs['contents'])['speaker'] == 'NPC'
    assert client.kwargs['config']['system_instruction'] == 'translate'
    assert client.closed and client.async_closed


def test_partial_result_and_sdk_errors_do_not_escape(monkeypatch):
    for reason, fail, status in [('MAX_TOKENS', None, 422), ('STOP', ValueError('secret-key-should-never-leak'), 502)]:
        client = FakeClient(reason, fail)
        monkeypatch.setattr(gemini, 'create_client', lambda _: client)
        with pytest.raises(gemini.GeminiFailure) as error:
            asyncio.run(gemini.translate('unused', 'gemini-3.8-flash', 'text', '', 'translate'))
        assert error.value.status == status
        assert 'secret-key' not in str(error.value)
        assert client.closed and client.async_closed


def test_context_is_user_data_separate_from_current_line(monkeypatch):
    client = FakeClient()
    monkeypatch.setattr(gemini, 'create_client', lambda _: client)
    previous = [{'speaker': 'A', 'text': '先に行って！', 'language': 'ja'}]
    asyncio.run(gemini.translate('unused', 'gemini-3.8-flash', '大丈夫？', 'B', 'analyze current tone', previous))
    data = json.loads(client.kwargs['contents'])
    assert data == {'speaker': 'B', 'text': '大丈夫？', 'previous_dialogue': previous}
    assert client.kwargs['config']['system_instruction'] == 'analyze current tone'


def test_context_bridge_forwarding_and_limits(monkeypatch):
    import ff14_server
    from fastapi.testclient import TestClient
    received = []
    async def fake_translate(*args):
        received.append(args)
        return '{"tone":{"emotion":"worried","intensity":"low","pace":"slow"}}'
    monkeypatch.setattr(ff14_server, 'gemini_translate', fake_translate)
    client = TestClient(ff14_server.create_app(NS()))
    payload = {'key_file': 'unused', 'model': 'fake-model', 'text': '大丈夫？', 'speaker': 'B',
               'system_prompt': 'analyze', 'previous_dialogue': [{'speaker': 'A', 'text': '先に行って！', 'language': 'ja'}]}
    response = client.post('/api/gemini/translate', json=payload)
    assert response.status_code == 200 and response.json()['dialogue_context_version'] == 1
    assert received[0][-1] == payload['previous_dialogue']
    payload['previous_dialogue'] *= 5
    assert client.post('/api/gemini/translate', json=payload).status_code == 422
    payload.pop('previous_dialogue')
    assert client.post('/api/gemini/translate', json=payload).status_code == 200
    assert received[-1][-1] == []


def test_cancel_closes_sdk_clients(monkeypatch):
    client = FakeClient(fail=asyncio.CancelledError())
    monkeypatch.setattr(gemini, 'create_client', lambda _: client)
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(gemini.translate('unused', 'gemini-3.8-flash', 'text', '', 'translate'))
    assert client.closed and client.async_closed


def test_key_file_bom_and_error_redaction(tmp_path):
    path = tmp_path / 'key.txt'
    path.write_text('test-key\n', encoding='utf-8-sig')
    assert gemini.read_key(path) == 'test-key'
    path.write_text('secret key second line', encoding='utf-8')
    with pytest.raises(gemini.GeminiFailure) as error:
        gemini.read_key(path)
    assert 'secret' not in str(error.value)
    assert gemini.safe_error(NS(code=429)).status == 429
    assert gemini.safe_error(NS(code=402)).status == 402


def test_disconnected_gui_cancels_sdk_operation():
    from ff14_server import while_connected
    from fastapi import HTTPException
    async def run():
        closed = asyncio.Event()
        class Request:
            async def is_disconnected(self):
                return True
        async def work():
            try:
                await asyncio.Event().wait()
            finally:
                closed.set()
        with pytest.raises(HTTPException) as error:
            await while_connected(Request(), work())
        assert error.value.status_code == 499 and closed.is_set()
    asyncio.run(run())
