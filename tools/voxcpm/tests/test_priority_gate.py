import sys
import threading
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from priority_gate import PriorityGate


def test_interactive_overtakes_background_without_interrupting_active_sentence():
    gate = PriorityGate(idle_seconds=0)
    interactive_queued = threading.Event()
    order = []

    def background():
        with gate.hold(background=True):
            order.append("background")

    def interactive():
        interactive_queued.set()
        with gate.hold():
            order.append("interactive")

    with ThreadPoolExecutor(max_workers=2) as workers:
        with gate.hold(background=True):
            pending_background = workers.submit(background)
            pending_interactive = workers.submit(interactive)
            assert interactive_queued.wait(5)
            # Wait until admission has registered the foreground request.
            with gate.condition:
                assert gate.condition.wait_for(lambda: gate.interactive_waiters == 1, timeout=5)
            assert order == []
        pending_background.result(timeout=5)
        pending_interactive.result(timeout=5)
    assert order == ["interactive", "background"]


def test_gate_releases_after_failed_generation():
    gate = PriorityGate(idle_seconds=0)
    try:
        with gate.hold(background=True):
            raise ValueError("model failed")
    except ValueError:
        pass
    with gate.hold():
        assert gate.active
    assert not gate.active
