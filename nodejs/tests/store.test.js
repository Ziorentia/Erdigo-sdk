// The file store: a private folder, a key that is never replaced, and counters that only go up, across processes.

import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { afterEach, beforeEach, test } from 'node:test';
import { ErdigoError, ErdigoErrorKind, StorageProblem, receiverPairing } from '../src/identity.js';
import { newPrivateKey, publicKeyOf } from '../src/noise.js';
import { fingerprint } from '../src/protocol.js';
import { FileDeviceStore, InMemoryDeviceStore } from '../src/store.js';

const unix = process.platform !== 'win32';
const asRoot = unix && process.getuid?.() === 0;
let folder = '';

beforeEach(() => {
  folder = mkdtempSync(join(tmpdir(), 'erdigo-store-'));
});

afterEach(() => {
  rmSync(folder, { recursive: true, force: true });
});

const mode = (/** @type {string} */ path) => statSync(path).mode & 0o777;

/** @param {string} problem */
const storage = (problem) => (/** @type {unknown} */ error) => error instanceof ErdigoError && error.kind === ErdigoErrorKind.storage && error.storageProblem === problem;

const pairingFor = (/** @type {Partial<import('../src/identity.js').ReceiverPairing>} */ fields = {}) =>
  receiverPairing({ label: 'home', receiverKey: publicKeyOf(newPrivateKey()), kinds: ['pin', 'track'], ...fields });

test('the key is private and survives a restart', () => {
  const directory = join(folder, 'state');
  const first = new FileDeviceStore(directory).loadOrCreateIdentity();
  const second = new FileDeviceStore(directory).loadOrCreateIdentity();
  assert.equal(second.fingerprint, first.fingerprint);
  assert.equal(readFileSync(join(directory, 'identity.key')).length, 36);
  if (unix) {
    assert.equal(mode(directory), 0o700);
    assert.equal(mode(join(directory, 'identity.key')), 0o600);
  }
});

test('a key file others could read is refused, and nothing is read', { skip: !unix }, () => {
  const directory = join(folder, 'state');
  new FileDeviceStore(directory).loadOrCreateIdentity();
  chmodSync(join(directory, 'identity.key'), 0o644);
  assert.throws(() => new FileDeviceStore(directory).loadOrCreateIdentity(), storage(StorageProblem.insecure));
});

test('a state folder others could read is refused', { skip: !unix }, () => {
  const directory = join(folder, 'state');
  mkdirSync(directory, { mode: 0o755 });
  chmodSync(directory, 0o755);
  assert.throws(() => new FileDeviceStore(directory), storage(StorageProblem.insecure));
});

test('a link in place of the key file is refused', { skip: !unix }, () => {
  const directory = join(folder, 'state');
  new FileDeviceStore(directory);
  const elsewhere = join(folder, 'elsewhere.key');
  writeFileSync(elsewhere, Buffer.concat([Buffer.from('EDK0'), newPrivateKey()]), { mode: 0o600 });
  symlinkSync(elsewhere, join(directory, 'identity.key'));
  assert.throws(() => new FileDeviceStore(directory).loadOrCreateIdentity(), storage(StorageProblem.insecure));
});

test('a link in place of the state folder is refused', { skip: !unix }, () => {
  const real = join(folder, 'real');
  mkdirSync(real, { mode: 0o700 });
  const link = join(folder, 'link');
  symlinkSync(real, link);
  assert.throws(() => new FileDeviceStore(link), storage(StorageProblem.insecure));
});

test('a damaged key is reported and left alone', () => {
  const directory = join(folder, 'state');
  new FileDeviceStore(directory);
  const path = join(directory, 'identity.key');
  writeFileSync(path, 'not a key', { mode: 0o600 });
  assert.throws(() => new FileDeviceStore(directory).loadOrCreateIdentity(), storage(StorageProblem.identityDamaged));
  assert.equal(readFileSync(path, 'utf8'), 'not a key');
});

test('an unreadable key is reported and left alone', { skip: !unix || asRoot }, () => {
  const directory = join(folder, 'state');
  const original = new FileDeviceStore(directory).loadOrCreateIdentity();
  const path = join(directory, 'identity.key');
  const before = readFileSync(path);
  chmodSync(path, 0o000);
  try {
    assert.throws(() => new FileDeviceStore(directory).loadOrCreateIdentity(), storage(StorageProblem.identityUnreadable));
  } finally {
    chmodSync(path, 0o600);
  }
  assert.ok(readFileSync(path).equals(before));
  assert.equal(new FileDeviceStore(directory).loadOrCreateIdentity().fingerprint, original.fingerprint);
});

test('pairings and counters persist', () => {
  const directory = join(folder, 'state');
  const store = new FileDeviceStore(directory);
  const pairing = pairingFor({ lastHost: '192.0.2.7', lastPort: 4242, counterFloor: 5 });
  store.save(pairing);
  const id = fingerprint(pairing.receiverKey);
  assert.equal(store.nextConnectCounter(id, 1), 6);
  assert.equal(store.nextConnectCounter(id, 1), 7);
  assert.equal(store.nextConnectCounter(id, 1_700_000_000_000), 1_700_000_000_000);
  store.updateAddress(id, '192.0.2.8', 4343);
  const reopened = new FileDeviceStore(directory);
  const [saved] = reopened.pairings();
  assert.equal(saved.label, 'home');
  assert.ok(saved.receiverKey.equals(pairing.receiverKey));
  assert.deepEqual(saved.kinds, ['pin', 'track']);
  assert.equal(saved.lastHost, '192.0.2.8');
  assert.equal(saved.lastPort, 4343);
  assert.equal(saved.pairedAt.toISOString(), pairing.pairedAt.toISOString());
  assert.equal(reopened.nextConnectCounter(id, 1), 1_700_000_000_001);
  if (unix) {
    assert.equal(mode(join(directory, 'pairings.json')), 0o600);
  }
  reopened.removePairing(id);
  assert.deepEqual(reopened.pairings(), []);
});

test('a counter floor takes effect when saved', () => {
  for (const store of [new FileDeviceStore(join(folder, 'state')), new InMemoryDeviceStore()]) {
    const pairing = pairingFor({ counterFloor: 2 ** 50 });
    store.save(pairing);
    assert.equal(store.nextConnectCounter(fingerprint(pairing.receiverKey), 1_700_000_000_000), 2 ** 50 + 1);
  }
});

test('a damaged pairing file is reported', () => {
  const directory = join(folder, 'state');
  const store = new FileDeviceStore(directory);
  writeFileSync(join(directory, 'pairings.json'), '{"version":1,"pairings":[{"label":"x"}],"counters":{}}', { mode: 0o600 });
  assert.throws(() => store.pairings(), storage(StorageProblem.stateDamaged));
  writeFileSync(join(directory, 'pairings.json'), '{"version":1,"pairings":[],"counters":{"a":-1}}', { mode: 0o600 });
  assert.throws(() => store.pairings(), storage(StorageProblem.stateDamaged));
});

const storeModule = fileURLToPath(new URL('../src/store.js', import.meta.url));

/**
 * Runs `code` in several processes at once, each with the store module at hand, and returns what each printed.
 * @param {number} count
 * @param {string} code
 * @returns {Promise<string[]>}
 */
async function inProcesses(count, code) {
  const script = `import { FileDeviceStore } from ${JSON.stringify(pathToFileURL(storeModule).href)};\n${code}`;
  return Promise.all(Array.from({ length: count }, () => new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['--input-type=module', '-e', script, folder], { stdio: ['ignore', 'pipe', 'inherit'] });
    let output = '';
    child.stdout.on('data', (chunk) => {
      output += chunk;
    });
    child.on('close', (status) => (status === 0 ? resolve(output) : reject(new Error(`a process failed with ${status}`))));
  })));
}

test('counters are safe across processes', async () => {
  const outputs = await inProcesses(6, `
    const store = new FileDeviceStore(process.argv[1] + '/state');
    const values = [];
    for (let index = 0; index < 20; index += 1) values.push(store.nextConnectCounter('receiver', 1));
    console.log(values.join(' '));
  `);
  const values = outputs.flatMap((output) => output.trim().split(' ').map(Number));
  assert.equal(values.length, 120);
  assert.equal(new Set(values).size, 120, 'every counter is different');
  assert.deepEqual([...values].sort((left, right) => left - right), Array.from({ length: 120 }, (_, index) => index + 1));
  assert.ok(!existsSync(join(folder, 'state', '.lock')));
});

test('many processes create one identity', async () => {
  const outputs = await inProcesses(6, `
    console.log(new FileDeviceStore(process.argv[1] + '/state').loadOrCreateIdentity().fingerprint);
  `);
  assert.equal(new Set(outputs.map((output) => output.trim())).size, 1);
});

test('a lock left by a process that has gone is taken over', () => {
  const directory = join(folder, 'state');
  const store = new FileDeviceStore(directory);
  // A process id that cannot be running.
  writeFileSync(join(directory, '.lock'), '2147483646', { mode: 0o600 });
  assert.equal(store.nextConnectCounter('receiver', 1), 1);
});

test('on Windows the state folder is restricted to the current user', { skip: unix }, () => {
  const directory = join(folder, 'state');
  new FileDeviceStore(directory);
  const listing = execFileSync('icacls', [directory], { encoding: 'utf8' });
  const entries = listing.split(/\r?\n/).map((line) => line.replace(directory, '').trim()).filter((line) => line.includes(':('));
  assert.equal(entries.length, 1, listing);
  assert.ok(!/Everyone|Users|Authenticated/i.test(listing), listing);
});
