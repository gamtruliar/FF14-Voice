import os
from pathlib import Path
import sys
from types import SimpleNamespace as NS

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import restart_backend as restart


class Process:
    def __init__(self, root, pid=100, args=None, parent=None, exe=None):
        self.root, self.pid, self._parent = root, pid, parent
        self.args = args or ['-u', 'ff14_server.py', '--omni-url', 'http://127.0.0.1:8809']
        self.executable = exe or str(root / 'VoxCPM/.venv/Scripts/python.exe')
        self.stopped = self.waited = False
    def cwd(self):
        return str(self.root / 'tools/voxcpm')
    def cmdline(self):
        return [self.executable, *self.args]
    def exe(self):
        return self.executable
    def parent(self):
        return self._parent
    def terminate(self):
        self.stopped = True
    def wait(self, timeout):
        self.waited = True
    def children(self, recursive=False):
        return []


@pytest.fixture
def root(tmp_path):
    for relative in ('tools/voxcpm/ff14_server.py', 'VoxCPM/.venv/Scripts/python.exe'):
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
    return tmp_path.resolve()


def connections(monkeypatch, process, port=8808):
    monkeypatch.setattr(restart.psutil, 'net_connections', lambda **_: [NS(pid=process.pid, status='LISTEN', laddr=NS(port=port))])
    monkeypatch.setattr(restart.psutil, 'Process', lambda _: process)


def test_owned_venv_and_base_interpreter_child(monkeypatch, root):
    launcher = Process(root)
    child = Process(root, 101, parent=launcher, exe=str(root / 'base-python/python.exe'))
    connections(monkeypatch, child)
    listener, owner, args = restart.find_backend(root, 8808)
    assert listener is child and owner is launcher and '--omni-url' in args
    assert not child.stopped and not launcher.stopped


@pytest.mark.parametrize('url', ['https://127.0.0.1:8808', 'http://example.com:8808',
    'http://user:pass@localhost:8808', 'http://localhost:8808/path', 'http://localhost:8808/?x=1'])
def test_remote_or_ambiguous_urls_are_rejected(url):
    with pytest.raises(ValueError):
        restart.local_url(url)


@pytest.mark.parametrize('args', [
    ['other_server.py'], ['ff14_server.py', '--port', '9999'],
    ['ff14_server.py', '--gguf-url', 'http://127.0.0.1:8810'],
    ['ff14_server.py', '--unknown', 'value'], ['ff14_server.py', '--device'],
    ['ff14_server.py', '--omni-url', 'http://remote:8809'],
])
def test_unknown_services_and_unsupported_modes_never_stop(monkeypatch, root, args):
    process = Process(root, args=args)
    connections(monkeypatch, process)
    with pytest.raises(ValueError):
        restart.find_backend(root, 8808)
    assert not process.stopped


def test_same_script_but_unrelated_python_is_rejected(monkeypatch, root):
    process = Process(root, exe=str(root / 'unrelated/python.exe'))
    connections(monkeypatch, process)
    with pytest.raises(ValueError):
        restart.find_backend(root, 8808)
    assert not process.stopped


def test_inspect_is_read_only(monkeypatch, root):
    process = Process(root)
    connections(monkeypatch, process)
    restart.restart(root, 'http://localhost:8808', inspect=True)
    assert not process.stopped


@pytest.mark.skipif(os.name != 'nt', reason='Windows restart lock and process flags')
def test_restart_preserves_parameters_and_waits_for_new_process(monkeypatch, root):
    old = Process(root)
    monkeypatch.setattr(restart, 'find_backend', lambda *_: (old, old, old.args[2:]))
    launched = []
    child = Process(root, 200)
    child.poll = lambda: None
    def popen(args, **kwargs):
        assert old.stopped and old.waited
        launched.append((args, kwargs))
        return child
    monkeypatch.setattr(restart.subprocess, 'Popen', popen)
    monkeypatch.setattr(restart.psutil, 'Process', lambda _: child)
    responses = iter([{'status': 'ready', 'process_id': old.pid}, {'status': 'ready', 'process_id': child.pid}])
    monkeypatch.setattr(restart, 'health', lambda _: next(responses))
    monkeypatch.setattr(restart.time, 'sleep', lambda _: None)
    restart.restart(root, 'http://127.0.0.1:8808')
    assert len(launched) == 1
    args, kwargs = launched[0]
    assert args == [str(root / 'VoxCPM/.venv/Scripts/python.exe'), '-u', str(root / 'tools/voxcpm/ff14_server.py'),
                    '--omni-url', 'http://127.0.0.1:8809']
    assert kwargs['cwd'] == root / 'VoxCPM' and kwargs['creationflags'] == restart.subprocess.CREATE_NO_WINDOW
    assert (root / 'artifacts/omni-gateway.pid').read_text() == '200'


def test_native_parameters_and_process_identity(root, monkeypatch):
    old = Process(root, args=['ff14_server.py', '--model-id', 'path with spaces/model', '--device', 'cuda:0', '--port', '8808'])
    assert restart.launch_arguments(old, root, 8808) == old.args[1:]
    monkeypatch.setattr(restart.psutil, 'Process', lambda _: old)
    assert not restart.ready_for_process({'status': 'ready'}, old)
    assert not restart.ready_for_process({'status': 'ready', 'process_id': 999}, old)
    assert restart.ready_for_process({'status': 'ready', 'process_id': old.pid}, old)


@pytest.mark.skipif(os.name != 'nt', reason='Windows restart lock')
def test_changed_process_is_not_stopped(monkeypatch, root):
    old, changed = Process(root), Process(root, 101)
    snapshots = iter([(old, old, []), (changed, changed, [])])
    monkeypatch.setattr(restart, 'find_backend', lambda *_: next(snapshots))
    with pytest.raises(ValueError, match='程序已改變'):
        restart.restart(root, 'http://127.0.0.1:8808')
    assert not old.stopped and not changed.stopped


@pytest.mark.skipif(os.name != 'nt', reason='Windows restart lock')
def test_startup_exit_reports_log_without_retry(monkeypatch, root):
    old = Process(root)
    monkeypatch.setattr(restart, 'find_backend', lambda *_: (old, old, []))
    launched = []
    def popen(*args, **kwargs):
        launched.append(args)
        return NS(pid=200, poll=lambda: 1)
    monkeypatch.setattr(restart.subprocess, 'Popen', popen)
    with pytest.raises(ValueError, match='stderr.log'):
        restart.restart(root, 'http://127.0.0.1:8808')
    assert len(launched) == 1
