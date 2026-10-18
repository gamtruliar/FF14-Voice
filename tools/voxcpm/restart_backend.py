"""Restart only this checkout's native server or Omni gateway, never a port's arbitrary owner."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from urllib.parse import urlsplit
from urllib.request import build_opener, ProxyHandler
import uuid

import psutil


def progress(message):
    try:
        print(message, flush=True)
    except (BrokenPipeError, OSError):
        pass  # Closing the GUI must not interrupt a restart halfway through.


def local_url(value):
    url = urlsplit(value)
    if (url.scheme != 'http' or url.hostname not in ('127.0.0.1', 'localhost', '::1')
            or url.username or url.password or url.query or url.fragment or url.path not in ('', '/')):
        raise ValueError('只支援本機 HTTP backend 地址。')
    port = url.port or 80
    return f'http://127.0.0.1:{port}', port


def launch_arguments(process, root, port):
    args = process.cmdline()[1:]
    if args and args[0] == '-u':
        args = args[1:]
    if not args:
        raise ValueError('無法確認 backend 啟動參數。')
    script = (Path(process.cwd()) / args[0]).resolve()
    allowed = {(root / 'tools/voxcpm/ff14_server.py').resolve(), (root / 'VoxCPM/ff14_server.py').resolve()}
    if script not in allowed:
        raise ValueError('呢個 port 唔係由本專案嘅 VoxCPM 服務使用，冇停止任何程序。')
    options = {}
    rest = args[1:]
    if len(rest) % 2:
        raise ValueError('無法完整保留 backend 啟動參數，請手動重啟。')
    for key, value in zip(rest[::2], rest[1::2]):
        if key not in ('--port', '--device', '--model-id', '--omni-url', '--gguf-url') or key in options:
            raise ValueError('有未支援嘅 backend 啟動參數，請手動重啟。')
        options[key] = value
    if int(options.get('--port', '8808')) != port:
        raise ValueError('Backend port 同啟動參數唔一致，未有停止服務。')
    if '--gguf-url' in options:
        raise ValueError('GGUF 由獨立啟動器管理，請喺原啟動視窗停止後重開 Start-VoxCPM-GGUF.cmd。')
    if '--omni-url' in options:
        local_url(options['--omni-url'])
    return rest


def find_backend(root, port):
    owners = {c.pid for c in psutil.net_connections(kind='tcp')
              if c.status == psutil.CONN_LISTEN and c.laddr.port == port}
    if not owners:
        raise ValueError('VoxCPM 未有啟動；請先用原本嘅 Start-VoxCPM／Omni 啟動器開啟服務。')
    if len(owners) != 1 or None in owners:
        raise ValueError('無法確認唯一 backend 程序，未有停止服務。')
    listener = psutil.Process(owners.pop())
    args = launch_arguments(listener, root, port)
    expected_python = (root / 'VoxCPM/.venv/Scripts/python.exe').resolve()
    launcher = listener
    if Path(listener.exe()).resolve() != expected_python:
        launcher = listener.parent()  # Windows venv launcher delegates to its base interpreter.
        if launcher is None or Path(launcher.exe()).resolve() != expected_python:
            raise ValueError('Backend 唔係由本專案 Python 環境啟動，未有停止服務。')
        if launch_arguments(launcher, root, port) != args:
            raise ValueError('Backend 父程序參數唔一致，未有停止服務。')
    return listener, launcher, args


def health(url):
    try:
        with build_opener(ProxyHandler({})).open(url + '/api/health', timeout=2) as response:
            return json.load(response)
    except (OSError, ValueError):
        return {}


def ready_for_process(state, process):
    if state.get('status') != 'ready':
        return False
    ids = {process.pid, *(child.pid for child in psutil.Process(process.pid).children(recursive=True))}
    return state.get('process_id') in ids


def restart(root, server, inspect=False):
    root = root.resolve()
    url, port = local_url(server)
    python = root / 'VoxCPM/.venv/Scripts/python.exe'
    script = root / 'tools/voxcpm/ff14_server.py'
    if not python.is_file() or not script.is_file():
        raise ValueError('搵唔到本專案 VoxCPM Python 或服務程式。')
    listener, launcher, args = find_backend(root, port)
    if inspect:
        progress(json.dumps({'listener': listener.pid, 'launcher': launcher.pid, 'arguments': args}, ensure_ascii=False))
        return
    # Acquire a per-port lock before any process mutation; a second GUI cannot race a restart.
    import msvcrt
    folder = root / 'artifacts/backend-restarts'
    folder.mkdir(parents=True, exist_ok=True)
    with (folder / f'{port}.lock').open('a+b') as lock:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            raise ValueError('另一個視窗正在重啟呢個 backend，請稍候。') from None
        # Revalidate after locking, before stopping only the verified listener.
        current, current_launcher, current_args = find_backend(root, port)
        if current != listener or current_launcher != launcher or current_args != args:
            raise ValueError('Backend 程序已改變，請重新按重啟。')
        name = time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8]
        stdout_path, stderr_path = folder / (name + '.stdout.log'), folder / (name + '.stderr.log')
        # Ensure log files can be opened before interrupting the existing server.
        with stdout_path.open('wb') as out, stderr_path.open('wb') as err:
            progress('正在重啟 VoxCPM API；WSL 模型會保留…' if '--omni-url' in args else '正在停止舊 VoxCPM backend…')
            listener.terminate()
            listener.wait(timeout=15)
            if launcher != listener:
                launcher.wait(timeout=15)
            env = os.environ.copy()
            env.update(HF_HOME=str(root / '.cache/huggingface'), MODELSCOPE_CACHE=str(root / '.cache/modelscope'),
                       GRADIO_ANALYTICS_ENABLED='False', PYTHONUTF8='1')
            progress('正在啟動更新後嘅 VoxCPM 服務…')
            process = subprocess.Popen([str(python), '-u', str(script), *args], cwd=root / 'VoxCPM',
                env=env, stdin=subprocess.DEVNULL, stdout=out, stderr=err, creationflags=subprocess.CREATE_NO_WINDOW)
            if '--omni-url' in args:
                (root / 'artifacts/omni-gateway.pid').write_text(str(process.pid), encoding='ascii')
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise ValueError(f'VoxCPM 啟動失敗，請查看 {stderr_path}')
                state = health(url)
                try:
                    if ready_for_process(state, process):
                        progress('VoxCPM 重啟完成 · API 已就緒' + (' · WSL 模型保留' if '--omni-url' in args else ' · 模型會按需載入'))
                        return
                except psutil.NoSuchProcess:
                    pass
                time.sleep(0.5)
            raise ValueError(f'服務未能喺 120 秒內就緒，請查看 {stderr_path}；未有重複啟動。')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--server', required=True)
    parser.add_argument('--inspect', action='store_true')
    args = parser.parse_args()
    try:
        restart(Path(__file__).resolve().parents[2], args.server, args.inspect)
    except psutil.AccessDenied:
        progress('重啟失敗：無權讀取或停止 VoxCPM 程序；請以同等權限開啟 FF14 Voice。')
        sys.exit(1)
    except Exception as exc:
        progress('重啟失敗：' + str(exc))
        sys.exit(1)
