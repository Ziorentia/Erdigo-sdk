// The cloud gateway example over HTTPS: the relay sits behind a small TLS proxy, as it would behind a reverse proxy,
// with a certificate made for the test by the openssl tool. The gateway trusts it through NODE_EXTRA_CA_CERTS, which
// Node.js reads itself. Skipped where the openssl tool cannot be run.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { connect } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, test } from 'node:test';
import { createServer } from 'node:tls';
import { DEFAULT_LIMITS, startRelay } from '../examples/cloud-relay/lib/relay.js';
import { GATEWAY, call, run, token } from './support/cloud-relay.js';
import { TestReceiver } from './support/receiver.js';

const work = mkdtempSync(join(realpathSync.native(tmpdir()), 'erdigo-gateway-https-'));
after(() => rmSync(work, { recursive: true, force: true }));

/** A self-signed certificate for 127.0.0.1, or null without the openssl tool. */
function makeCertificate() {
  const keyFile = join(work, 'key.pem');
  const certFile = join(work, 'cert.pem');
  const made = spawnSync('openssl', [
    'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:prime256v1', '-nodes', '-keyout', keyFile, '-out', certFile,
    '-days', '1', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
  ], {
    stdio: 'ignore',
    timeout: 30_000,
    // An openssl from Git for Windows would otherwise read '/CN=localhost' as a path.
    env: { ...process.env, MSYS2_ARG_CONV_EXCL: '*', MSYS_NO_PATHCONV: '1' },
  });
  if (made.status !== 0) {
    return null;
  }
  return { key: readFileSync(keyFile), cert: readFileSync(certFile), certFile };
}

const certificate = makeCertificate();
const skip = certificate === null ? 'the openssl tool is needed to make a test certificate' : false;

/**
 * Ends TLS and passes the bytes on to a port on this computer, as a reverse proxy would.
 * @param {number} port
 */
async function tlsProxy(port) {
  const server = createServer({ key: certificate?.key, cert: certificate?.cert }, (socket) => {
    const upstream = connect(port, '127.0.0.1');
    socket.pipe(upstream).pipe(socket);
    const end = () => {
      socket.destroy();
      upstream.destroy();
    };
    for (const side of [socket, upstream]) {
      side.on('error', end);
      side.on('close', end);
    }
  });
  server.on('tlsClientError', () => {});
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  return {
    url: `https://127.0.0.1:${/** @type {import('node:net').AddressInfo} */ (server.address()).port}`,
    close: () => new Promise((resolve) => server.close(() => resolve(undefined))),
  };
}

/** A relay behind the proxy, a test receiver, and a gateway paired with it. */
async function setUp() {
  const receiver = new TestReceiver();
  await receiver.start();
  const source = token();
  const gateway = token();
  const relay = await startRelay({ host: '127.0.0.1', port: 0, sourceTokens: [source], gatewayToken: gateway, limits: DEFAULT_LIMITS });
  const proxy = await tlsProxy(relay.port);
  const state = join(work, `state-${Math.random().toString(36).slice(2)}`);
  const codeFile = `${state}-code`;
  writeFileSync(codeFile, receiver.qrText, { mode: 0o600 });
  const paired = await run(GATEWAY, ['pair', '--name', 'HTTPS gateway', '--host', '127.0.0.1', '--port', String(receiver.port), '--code-file', codeFile,
    '--state-dir', state]);
  assert.equal(paired.code, 0, paired.stderr);
  const posted = await call(`${proxy.url}/v1/ops`, {
    method: 'POST', token: source, body: { ops: [{ op: 'pin', id: 'p1', lat: 37.5663, lon: 126.9779 }] }, ca: certificate?.cert,
  });
  assert.equal(posted.status, 200, posted.text);
  return {
    receiver,
    relay,
    proxy,
    /** @param {Record<string, string | undefined>} env */
    runOnce: (env) => run(GATEWAY, ['run', '--relay', proxy.url, '--host', '127.0.0.1', '--port', String(receiver.port), '--state-dir', state, '--once'], {
      env: { ERDIGO_RELAY_TOKEN: gateway, NODE_EXTRA_CA_CERTS: undefined, ...env },
    }),
    async close() {
      await proxy.close();
      await relay.close();
      await receiver.stop();
    },
  };
}

test('fetches from a relay over HTTPS', { skip }, async () => {
  const setup = await setUp();
  try {
    const result = await setup.runOnce({ NODE_EXTRA_CA_CERTS: certificate?.certFile });
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assert.equal(result.stdout, 'Done with 1 operation, up to 1\n');
    assert.deepEqual(setup.receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['p1']);
  } finally {
    await setup.close();
  }
});

test('does not trust a certificate it cannot check, and with --once gives up after about 30 seconds with status 3', { skip }, async () => {
  const setup = await setUp();
  try {
    const started = Date.now();
    const result = await setup.runOnce({});
    const seconds = (Date.now() - started) / 1000;
    assert.equal(result.code, 3, result.stdout + result.stderr);
    assert.ok(seconds > 25 && seconds < 60, `gave up after ${seconds} s`);
    const lines = result.stdout.trim().split('\n');
    assert.equal(lines[0], 'The relay could not be reached; trying again in 1 second.');
    assert.ok(lines.slice(0, -1).every((line) => /^The relay could not be reached; trying again in \d+ seconds?\.$/.test(line)), result.stdout);
    assert.equal(lines.at(-1), 'The relay could not be reached.');
    assert.equal(setup.relay.queue.count, 1);
    assert.equal(setup.receiver.requests.length, 0);
  } finally {
    await setup.close();
  }
});
