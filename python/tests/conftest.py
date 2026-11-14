from __future__ import annotations

from typing import Iterator

import pytest

from erdigo_sdk import ErdigoClient, InMemoryDeviceStore, ReceiverTarget, RetryPolicy, pair
from erdigo_sdk.model import ALL_KINDS

from .receiver import TestReceiver


@pytest.fixture
def receiver() -> Iterator[TestReceiver]:
    running = TestReceiver()
    yield running
    running.close()


class Paired:
    def __init__(self, receiver: TestReceiver, retry: RetryPolicy = RetryPolicy.up_to(10), kinds=ALL_KINDS) -> None:
        self.receiver = receiver
        self.store = InMemoryDeviceStore()
        self.identity = self.store.load_or_create_identity()
        self.target = ReceiverTarget.address("127.0.0.1", receiver.port)
        self.pairing = pair(self.target, receiver.ticket, self.identity, "Test device", kinds)
        self.store.save(self.pairing)
        self.client = ErdigoClient(self.identity, self.pairing, self.store, self.target, retry)

    def close(self) -> None:
        self.client.close()


@pytest.fixture
def paired(receiver: TestReceiver) -> Iterator[Paired]:
    device = Paired(receiver)
    yield device
    device.close()
