"""One active inference; queued interactive dialogue precedes background preloading."""
import threading
import time
import asyncio
from contextlib import contextmanager, asynccontextmanager


class PriorityGate:
    def __init__(self, idle_seconds=2.0):
        self.condition = threading.Condition()
        self.active = False
        self.interactive_waiters = 0
        self.last_interactive = float("-inf")
        self.idle_seconds = idle_seconds

    @asynccontextmanager
    async def ahold(self, background=False):
        """Same admission state as hold(), without blocking the HTTP event loop."""
        acquired = False
        with self.condition:
            if not background:
                self.interactive_waiters += 1
                self.last_interactive = time.monotonic()
                self.condition.notify_all()
        try:
            while not acquired:
                with self.condition:
                    if not self.active and not (background and (self.interactive_waiters or
                            time.monotonic() - self.last_interactive < self.idle_seconds)):
                        self.active = acquired = True
                if not acquired:
                    await asyncio.sleep(0.02)
        finally:
            with self.condition:
                if not background:
                    self.interactive_waiters -= 1
        try:
            yield
        finally:
            with self.condition:
                self.active = False
                if not background:
                    self.last_interactive = time.monotonic()
                self.condition.notify_all()

    @contextmanager
    def hold(self, background=False):
        with self.condition:
            if not background:
                self.interactive_waiters += 1
                self.last_interactive = time.monotonic()
                self.condition.notify_all()
            try:
                while self.active or (background and (self.interactive_waiters or
                        time.monotonic() - self.last_interactive < self.idle_seconds)):
                    self.condition.wait(timeout=0.2)
                self.active = True
            finally:
                if not background:
                    self.interactive_waiters -= 1
        try:
            yield
        finally:
            with self.condition:
                self.active = False
                if not background:
                    self.last_interactive = time.monotonic()
                self.condition.notify_all()
