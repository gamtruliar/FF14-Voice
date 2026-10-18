"""Discard superseded dialogue before queued requests reach GPU inference."""
import threading
from collections import OrderedDict


class RequestVersions:
    def __init__(self, capacity=256):
        self.versions = OrderedDict()
        self.lock = threading.Lock()
        self.capacity = capacity

    def advance(self, session, revision):
        if not session:
            return
        with self.lock:
            self.versions[session] = max(revision, self.versions.get(session, -1))
            self.versions.move_to_end(session)
            while len(self.versions) > self.capacity:
                self.versions.popitem(last=False)

    def current(self, session, revision):
        if not session:
            return True
        with self.lock:
            return self.versions.get(session) == revision
