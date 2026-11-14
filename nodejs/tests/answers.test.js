// How the client takes error answers, including ones a mock receiver would not give by itself. Each test replaces the
// receiver's answer to chosen requests and records the numbers it was sent. These are the cases of the .NET and
// Swift AnswerHandlingTests.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RetryPolicy } from '../src/client.js';
import { ErdigoError, ErdigoErrorKind } from '../src/identity.js';
import { errorAnswer } from './support/receiver.js';
import { pairedDevice } from './support/paired.js';

/**
 * Answers the first `times` requests as told, then lets requests run.
 * @param {(envelope: { seq: number }) => object} answer
 * @param {number} [times]
 */
function script(answer, times = 1) {
  /** @type {number[]} */
  const seen = [];
  return {
    seen,
    answer: (/** @type {{ seq: number }} */ envelope) => {
      seen.push(envelope.seq);
      return seen.length <= times ? errorAnswer(answer(envelope)) : null;
    },
  };
}

/** @param {{ answer: (envelope: any) => any }} plan @param {import('../src/client.js').RetryPolicy} [retry] */
async function scripted(plan, retry) {
  const device = await pairedDevice({ retry });
  device.receiver.answerOverride = plan.answer;
  return device;
}

/**
 * @param {string} kind
 * @param {() => Promise<unknown>} body
 * @returns {Promise<ErdigoError>}
 */
async function expectKind(kind, body) {
  try {
    await body();
  } catch (error) {
    assert.ok(error instanceof ErdigoError, String(error));
    assert.equal(error.kind, kind, error.message);
    return error;
  }
  assert.fail(`expected ${kind}`);
}

const waitFor = async (/** @type {() => boolean} */ condition) => {
  for (let tries = 0; tries < 100 && !condition(); tries += 1) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  assert.ok(condition());
};

const palace = { lat: 37.5796, lon: 126.977 };

// Answers that end the connection

test('a fatal rate_limited ends the connection with one error', async () => {
  const plan = script((request) => ({ code: 'rate_limited', seq: request.seq, retryAfterMs: 50, fatal: true }));
  const device = await scripted(plan);
  try {
    const error = await expectKind(ErdigoErrorKind.rejected, () => device.client.ping());
    assert.equal(error.receiverError?.code, 'rate_limited');
    assert.equal(error.receiverError?.fatal, true);
    // Sent once, never resent on the closed connection or on a new one.
    assert.deepEqual(plan.seen, [1]);
    assert.equal(device.receiver.count('connected'), 1);
    assert.equal(device.client.limits, null);
    await waitFor(() => device.receiver.count('disconnected') === 1);
    assert.equal(device.receiver.devices.get(device.identity.fingerprint)?.lastSeq, 0);
  } finally {
    await device.done();
  }
});

// A fatal answer is about the connection, so a number it carries for another request changes nothing.
for (const [code, expected, numberForAnotherRequest] of /** @type {Array<[string, string, boolean]>} */ ([
  ['rate_limited', ErdigoErrorKind.rejected, false],
  ['rate_limited', ErdigoErrorKind.rejected, true],
  ['too_many_errors', ErdigoErrorKind.rejected, false],
  ['too_many_errors', ErdigoErrorKind.rejected, true],
  ['internal_error', ErdigoErrorKind.rejected, false],
  ['replaced', ErdigoErrorKind.replaced, false],
  ['replaced', ErdigoErrorKind.replaced, true],
  ['seq_conflict', ErdigoErrorKind.sequenceConflict, false],
  ['stale_seq', ErdigoErrorKind.sequenceConflict, true],
  ['future_code', ErdigoErrorKind.rejected, true],
])) {
  test(`a fatal ${code}${numberForAnotherRequest ? ' for another number' : ''} ends the connection as its code says`, async () => {
    const plan = script((request) => ({
      code,
      seq: numberForAnotherRequest ? request.seq + 7 : request.seq,
      lastSeq: numberForAnotherRequest ? 1000 : null,
      retryAfterMs: 50,
      fatal: true,
    }));
    const device = await scripted(plan);
    try {
      const error = await expectKind(expected, () => device.client.ping());
      assert.deepEqual(plan.seen, [1]);
      assert.equal(device.receiver.count('connected'), 1);
      assert.equal(device.client.limits, null);
      if (expected === ErdigoErrorKind.sequenceConflict) {
        // The number in an answer to another request is not taken as where the receiver stands.
        assert.equal(error.lastSeq, 0);
      }
    } finally {
      await device.done();
    }
  });
}

test('paused still means reconnect and resend with the same number', async () => {
  const plan = script(() => ({ code: 'paused', fatal: true }));
  const device = await scripted(plan);
  try {
    await device.client.ping();
    assert.deepEqual(plan.seen, [1, 1]);
    assert.equal(device.receiver.count('connected'), 2);
    assert.equal(device.receiver.devices.get(device.identity.fingerprint)?.lastSeq, 1);
  } finally {
    await device.done();
  }
});

test('paused without retrying reports no answer', async () => {
  const plan = script(() => ({ code: 'paused', fatal: true }));
  const device = await scripted(plan, RetryPolicy.none);
  try {
    await expectKind(ErdigoErrorKind.noAnswer, () => device.client.ping());
    assert.deepEqual(plan.seen, [1]);
  } finally {
    await device.done();
  }
});

// Answers that keep the connection

for (const carriesNumber of [true, false]) {
  test(`rate_limited ${carriesNumber ? 'with' : 'without'} a number is resent with the same number on the same connection`, async () => {
    // A receiver leaves out the number when it limits a message before reading it.
    const plan = script((request) => ({ code: 'rate_limited', seq: carriesNumber ? request.seq : null, retryAfterMs: 20 }), 2);
    const device = await scripted(plan);
    try {
      await device.client.ping();
      await device.client.ping();
      assert.deepEqual(plan.seen, [1, 1, 1, 2]);
      assert.equal(device.receiver.count('connected'), 1);
      assert.equal(device.receiver.devices.get(device.identity.fingerprint)?.lastSeq, 2);
    } finally {
      await device.done();
    }
  });
}

test('a receiver that never stops limiting is given up on', async () => {
  const plan = script((request) => ({ code: 'rate_limited', seq: request.seq, retryAfterMs: 1 }), Number.MAX_SAFE_INTEGER);
  const device = await scripted(plan);
  try {
    const error = await expectKind(ErdigoErrorKind.rejected, () => device.client.ping());
    assert.equal(error.receiverError?.code, 'rate_limited');
    // The first answer and twenty more, all for the same number.
    assert.deepEqual(plan.seen, Array(21).fill(1));
    assert.equal(device.receiver.count('connected'), 1);
  } finally {
    await device.done();
  }
});

for (const code of ['not_found', 'rate_limited', 'stale_seq']) {
  test(`${code} for another number is a protocol violation and changes no numbers`, async () => {
    const plan = script((request) => ({ code, seq: request.seq + 5, lastSeq: 1000, retryAfterMs: 20 }));
    const device = await scripted(plan);
    try {
      await expectKind(ErdigoErrorKind.protocolViolation, () => device.client.ping());
      assert.equal(device.client.limits, null);
      // The next request starts from what the receiver says on a new connection, not from the stray number.
      await device.client.ping();
      assert.deepEqual(plan.seen, [1, 1]);
      assert.equal(device.receiver.devices.get(device.identity.fingerprint)?.lastSeq, 1);
    } finally {
      await device.done();
    }
  });
}

test('a stale number for this request moves the number on', async () => {
  const plan = script((request) => ({ code: 'stale_seq', seq: request.seq, lastSeq: 10 }));
  const device = await scripted(plan);
  try {
    const error = await expectKind(ErdigoErrorKind.sequenceConflict, () => device.client.ping());
    assert.equal(error.lastSeq, 10);
    await device.client.ping();
    assert.deepEqual(plan.seen, [1, 11]);
    assert.equal(device.receiver.count('connected'), 1);
    assert.equal(device.receiver.devices.get(device.identity.fingerprint)?.lastSeq, 11);
  } finally {
    await device.done();
  }
});

for (const carriesNumber of [true, false]) {
  test(`a rejection ${carriesNumber ? 'with' : 'without'} a number keeps the connection, and uses up the number only when it says which request`, async () => {
    const plan = script((request) => ({ code: 'not_found', seq: carriesNumber ? request.seq : null }));
    const device = await scripted(plan);
    try {
      const error = await expectKind(ErdigoErrorKind.rejected, () => device.client.move('missing', palace));
      assert.equal(error.receiverError?.code, 'not_found');
      assert.notEqual(device.client.limits, null);
      await device.client.ping();
      assert.deepEqual(plan.seen, carriesNumber ? [1, 2] : [1, 1]);
      assert.equal(device.receiver.count('connected'), 1);
    } finally {
      await device.done();
    }
  });
}
