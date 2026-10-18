import asyncio
import base64
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from priority_gate import PriorityGate


def test_async_gate_releases_cancelled_waiter_and_owner():
    async def check():
        gate = PriorityGate(idle_seconds=0)
        entered = asyncio.Event()
        async def owner():
            async with gate.ahold():
                entered.set()
                await asyncio.Event().wait()
        first = asyncio.create_task(owner())
        await entered.wait()
        second = asyncio.create_task(owner())
        await asyncio.sleep(.05)
        second.cancel()
        with pytest.raises(asyncio.CancelledError):
            await second
        assert gate.active and gate.interactive_waiters == 0
        first.cancel()
        with pytest.raises(asyncio.CancelledError):
            await first
        assert not gate.active
        async with gate.ahold(background=True):
            assert gate.active
    asyncio.run(check())


def test_stream_protocol_complete_error_and_superseded(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from ff14_server import create_app
    class Demo:
        voxcpm_model = None
        calls = 0
        closed = 0
        async def stream_tts_audio(self, **kwargs):
            self.calls += 1
            assert kwargs['seed'] == 4294967295
            try:
                yield b'\x01\x02' * 480
                if kwargs['text_input'] == 'fail':
                    raise RuntimeError('inference failed')
                yield b'\x03\x04' * 480
            finally:
                self.closed += 1
    monkeypatch.chdir(tmp_path)
    demo = Demo()
    with TestClient(create_app(demo)) as client:
        def run(text, revision):
            response = client.post('/api/tts/stream', json=dict(text=text, session_id='test', revision=revision, seed=4294967295))
            assert response.status_code == 200
            return [json.loads(line) for line in response.text.splitlines()]
        result = run('hello', 1)
        assert [x['type'] for x in result] == ['format', 'audio', 'audio', 'done']
        assert base64.b64decode(result[1]['pcm']) == b'\x01\x02' * 480
        result = run('fail', 2)
        assert [x['type'] for x in result] == ['format', 'audio', 'error']
        client.post('/api/cancel', json=dict(session_id='test', revision=4))
        assert run('stale', 3) == [{'type': 'cancelled'}]
        assert demo.calls == demo.closed == 2
