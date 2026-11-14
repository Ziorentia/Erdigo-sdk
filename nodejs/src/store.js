// Persistent device state: its key, its pairings and the per-receiver connect counter.

import { closeSync, constants, openSync, readFileSync, statSync, unlinkSync, writeSync } from 'node:fs';
import { homedir } from 'node:os';
import { isAbsolute, join, resolve } from 'node:path';
import { DeviceIdentity, ErdigoError, ErdigoErrorKind, StorageProblem, pairingFingerprint, receiverPairing } from './identity.js';
import { isItemKind } from './model.js';
import { DH_LENGTH, isCanonicalPublicKey } from './noise.js';
import {
  PrivateFileError,
  PrivateFileProblem,
  ensurePrivateDirectory,
  isWindows,
  readPrivateFile,
  sleepSync,
  writePrivateFileAtomically,
} from './private-file.js';
import { MAX_SEQUENCE, hexDecode } from './protocol.js';

/** @typedef {import('./identity.js').ReceiverPairing} ReceiverPairing */

/**
 * What the client needs from a store. {@link FileDeviceStore} and {@link InMemoryDeviceStore} are two; a program may
 * supply its own.
 * @typedef {object} DeviceStore
 * @property {() => DeviceIdentity} loadOrCreateIdentity
 * @property {() => ReceiverPairing[]} pairings
 * @property {(pairing: ReceiverPairing) => void} save adds a pairing, or replaces the one for the same receiver. Its
 *   counter floor takes effect here.
 * @property {(receiverFingerprint: string, host: string, port: number) => void} updateAddress remembers where a
 *   receiver was found. Does nothing if the pairing has since been forgotten.
 * @property {(receiverFingerprint: string) => void} removePairing
 * @property {(receiverFingerprint: string, nowMs: number) => number} nextConnectCounter returns a value strictly
 *   greater than any previous result for this receiver. The value must be durable before it is returned, because a
 *   receiver ignores a counter it has seen.
 */

/** @param {ReceiverPairing} pairing */
function copyPairing(pairing) {
  return receiverPairing(pairing);
}

/** Keeps everything in memory. Intended for tests and short-lived programs. */
export class InMemoryDeviceStore {
  /** @param {DeviceIdentity | null} [identity] */
  constructor(identity = null) {
    this.identity = identity;
    /** @type {ReceiverPairing[]} */
    this.stored = [];
    /** @type {Map<string, number>} */
    this.counters = new Map();
  }

  loadOrCreateIdentity() {
    this.identity ??= DeviceIdentity.generate();
    return this.identity;
  }

  pairings() {
    return this.stored.map(copyPairing);
  }

  /** @param {ReceiverPairing} pairing */
  save(pairing) {
    const fingerprint = pairingFingerprint(pairing);
    this.stored = this.stored.filter((existing) => !existing.receiverKey.equals(pairing.receiverKey));
    this.stored.push(copyPairing(pairing));
    this.counters.set(fingerprint, Math.max(this.counters.get(fingerprint) ?? 0, pairing.counterFloor));
  }

  /**
   * @param {string} receiverFingerprint
   * @param {string} host
   * @param {number} port
   */
  updateAddress(receiverFingerprint, host, port) {
    const found = this.stored.find((pairing) => pairingFingerprint(pairing) === receiverFingerprint);
    if (found) {
      found.lastHost = host;
      found.lastPort = port;
    }
  }

  /** @param {string} receiverFingerprint */
  removePairing(receiverFingerprint) {
    this.stored = this.stored.filter((pairing) => pairingFingerprint(pairing) !== receiverFingerprint);
    this.counters.delete(receiverFingerprint);
  }

  /**
   * @param {string} receiverFingerprint
   * @param {number} nowMs
   */
  nextConnectCounter(receiverFingerprint, nowMs) {
    const next = Math.max(nowMs, (this.counters.get(receiverFingerprint) ?? 0) + 1);
    this.counters.set(receiverFingerprint, next);
    return next;
  }
}

const PAIRINGS_FILE = 'pairings.json';
const KEY_FILE = 'identity.key';
const LOCK_FILE = '.lock';
const KEY_MAGIC = Buffer.from('EDK0', 'ascii');
/** A lock older than this, from a process that has gone, is taken over. */
const STALE_LOCK_MS = 5000;

/**
 * Stores state in a private folder: `identity.key` and `pairings.json`. On macOS and Linux it refuses files that
 * other users could read or replace, the same way ssh treats its key files. A key file that exists but cannot be read
 * is reported, and never silently replaced by a new key.
 */
export class FileDeviceStore {
  /** @param {string} directory */
  constructor(directory) {
    this.directory = resolve(directory);
    try {
      ensurePrivateDirectory(this.directory);
    } catch (error) {
      if (error instanceof PrivateFileError) {
        throw new ErdigoError(ErdigoErrorKind.storage, { detail: error.message, storageProblem: StorageProblem.insecure, cause: error });
      }
      throw new ErdigoError(ErdigoErrorKind.storage, { detail: 'could not create the state folder', cause: error });
    }
  }

  /**
   * `%LOCALAPPDATA%\ErdigoSDK.Node\<profile>` on Windows, `~/Library/Application Support/ErdigoSDK.Node/<profile>`
   * on macOS and `~/.local/share/ErdigoSDK.Node/<profile>` elsewhere. The name is this edition's own, so that it
   * never shares a device key with the Swift or .NET SDK.
   * @param {string} [profile]
   */
  static defaultDirectory(profile = 'default') {
    let base;
    if (isWindows) {
      base = process.env.LOCALAPPDATA;
    } else if (process.platform === 'darwin') {
      base = join(homedir(), 'Library', 'Application Support');
    } else {
      const data = process.env.XDG_DATA_HOME;
      base = data && isAbsolute(data) ? data : join(homedir(), '.local', 'share');
    }
    if (!base) {
      throw new ErdigoError(ErdigoErrorKind.storage, { detail: 'the folder for application data could not be found' });
    }
    return join(base, 'ErdigoSDK.Node', profile);
  }

  /** @param {string} name */
  pathOf(name) {
    return join(this.directory, name);
  }

  /**
   * Serialises access between processes. JavaScript runs one piece of code at a time, and every method here is
   * synchronous, so a process needs no lock of its own.
   * @template T
   * @param {() => T} body
   * @returns {T}
   */
  withLock(body) {
    const path = this.pathOf(LOCK_FILE);
    const giveUp = Date.now() + 30_000;
    let descriptor;
    for (;;) {
      try {
        descriptor = openSync(path, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL, 0o600);
        break;
      } catch (error) {
        const code = /** @type {NodeJS.ErrnoException} */ (error).code;
        // On Windows a lock file that another process is just deleting cannot be created again for a moment.
        const busy = isWindows && (code === 'EPERM' || code === 'EACCES' || code === 'EBUSY');
        if (code !== 'EEXIST' && !(busy && Date.now() <= giveUp)) {
          throw new ErdigoError(ErdigoErrorKind.storage, { detail: 'could not lock the state folder', cause: error });
        }
        if (busy) {
          sleepSync(10);
          continue;
        }
        if (this.lockIsStale(path)) {
          try {
            unlinkSync(path);
          } catch {
            // Someone else took it over first.
          }
          continue;
        }
        if (Date.now() > giveUp) {
          throw new ErdigoError(ErdigoErrorKind.storage, { detail: 'could not lock the state folder: another program holds it' });
        }
        sleepSync(10);
      }
    }
    try {
      writeSync(descriptor, String(process.pid));
    } finally {
      closeSync(descriptor);
    }
    try {
      return body();
    } finally {
      try {
        unlinkSync(path);
      } catch {
        // Already gone.
      }
    }
  }

  /**
   * A lock is stale when the process that took it has ended, or when it says nothing and is old.
   * @param {string} path
   */
  lockIsStale(path) {
    let text;
    let age;
    try {
      text = readFileSync(path, 'utf8');
      age = Date.now() - statSync(path).mtimeMs;
    } catch {
      return false;
    }
    const pid = Number(text);
    if (!Number.isSafeInteger(pid) || pid <= 0) {
      return age > STALE_LOCK_MS;
    }
    if (pid === process.pid) {
      return true;
    }
    try {
      process.kill(pid, 0);
      return false;
    } catch (error) {
      return /** @type {NodeJS.ErrnoException} */ (error).code === 'ESRCH';
    }
  }

  /**
   * @param {string} name
   * @param {string} unreadable
   * @param {number} [maxBytes]
   * @returns {Buffer | null}
   */
  readPrivate(name, unreadable, maxBytes = 4 * 1024 * 1024) {
    try {
      return readPrivateFile(this.pathOf(name), maxBytes);
    } catch (error) {
      if (!(error instanceof PrivateFileError)) {
        throw error;
      }
      if (error.problem === PrivateFileProblem.missing) {
        return null;
      }
      if (error.problem === PrivateFileProblem.insecure) {
        throw new ErdigoError(ErdigoErrorKind.storage, { detail: error.message, storageProblem: StorageProblem.insecure, cause: error });
      }
      throw new ErdigoError(ErdigoErrorKind.storage, {
        detail: `${name} exists but could not be read. Nothing was changed.`,
        storageProblem: unreadable,
        cause: error,
      });
    }
  }

  /**
   * @param {string} name
   * @param {Uint8Array} data
   * @param {boolean} [overwrite]
   */
  writePrivate(name, data, overwrite = true) {
    try {
      writePrivateFileAtomically(this.pathOf(name), data, overwrite);
    } catch (error) {
      if (!overwrite && /** @type {NodeJS.ErrnoException} */ (error).code === 'EEXIST') {
        throw new ErdigoError(ErdigoErrorKind.storage, {
          detail: `${name} exists but could not be read. Nothing was changed, and no new key was created.`,
          storageProblem: StorageProblem.identityUnreadable,
          cause: error,
        });
      }
      throw new ErdigoError(ErdigoErrorKind.storage, { detail: `could not write ${name}`, cause: error });
    }
  }

  /** @returns {DeviceIdentity} */
  loadOrCreateIdentity() {
    return this.withLock(() => {
      const stored = this.readPrivate(KEY_FILE, StorageProblem.identityUnreadable, 64 * 1024);
      if (stored !== null) {
        try {
          if (stored.length !== KEY_MAGIC.length + DH_LENGTH || !stored.subarray(0, KEY_MAGIC.length).equals(KEY_MAGIC)) {
            throw new ErdigoError(ErdigoErrorKind.storage, {
              detail: `${KEY_FILE} exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.`,
              storageProblem: StorageProblem.identityDamaged,
            });
          }
          return DeviceIdentity.fromPrivateKey(stored.subarray(KEY_MAGIC.length));
        } finally {
          stored.fill(0);
        }
      }
      // Only a file that is not there at all leads to a new key.
      const created = DeviceIdentity.generate();
      const raw = created.exportPrivateKeyForStorage();
      const sealed = Buffer.concat([KEY_MAGIC, raw]);
      try {
        // Never over an existing file, even one this process could not see.
        this.writePrivate(KEY_FILE, sealed, false);
      } catch (error) {
        created.dispose();
        throw error;
      } finally {
        raw.fill(0);
        sealed.fill(0);
      }
      return created;
    });
  }

  /** @returns {{ pairings: ReceiverPairing[], counters: Map<string, number> }} */
  loadState() {
    const data = this.readPrivate(PAIRINGS_FILE, StorageProblem.stateUnreadable);
    if (data === null) {
      return { pairings: [], counters: new Map() };
    }
    try {
      return decodeState(data);
    } catch (error) {
      throw new ErdigoError(ErdigoErrorKind.storage, {
        detail: `${PAIRINGS_FILE} could not be read`,
        storageProblem: StorageProblem.stateDamaged,
        cause: error,
      });
    }
  }

  /** @param {{ pairings: ReceiverPairing[], counters: Map<string, number> }} state */
  saveState(state) {
    const counters = Object.fromEntries([...state.counters.entries()].sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0)));
    const document = {
      version: 1,
      pairings: state.pairings.map((pairing) => ({
        label: pairing.label,
        receiverKey: pairing.receiverKey.toString('hex'),
        kinds: pairing.kinds,
        ...(pairing.lastHost !== null ? { lastHost: pairing.lastHost } : {}),
        ...(pairing.lastPort !== null ? { lastPort: pairing.lastPort } : {}),
        pairedAt: pairing.pairedAt.toISOString(),
        counterFloor: pairing.counterFloor,
      })),
      counters,
    };
    this.writePrivate(PAIRINGS_FILE, Buffer.from(`${JSON.stringify(document, null, 2)}\n`, 'utf8'));
  }

  pairings() {
    return this.withLock(() => this.loadState().pairings);
  }

  /** @param {ReceiverPairing} pairing */
  save(pairing) {
    this.withLock(() => {
      const state = this.loadState();
      const fingerprint = pairingFingerprint(pairing);
      state.pairings = state.pairings.filter((existing) => !existing.receiverKey.equals(pairing.receiverKey));
      state.pairings.push(copyPairing(pairing));
      state.counters.set(fingerprint, Math.max(state.counters.get(fingerprint) ?? 0, pairing.counterFloor));
      this.saveState(state);
    });
  }

  /**
   * @param {string} receiverFingerprint
   * @param {string} host
   * @param {number} port
   */
  updateAddress(receiverFingerprint, host, port) {
    this.withLock(() => {
      const state = this.loadState();
      const found = state.pairings.find((pairing) => pairingFingerprint(pairing) === receiverFingerprint);
      if (!found) {
        return;
      }
      found.lastHost = host;
      found.lastPort = port;
      this.saveState(state);
    });
  }

  /** @param {string} receiverFingerprint */
  removePairing(receiverFingerprint) {
    this.withLock(() => {
      const state = this.loadState();
      state.pairings = state.pairings.filter((pairing) => pairingFingerprint(pairing) !== receiverFingerprint);
      state.counters.delete(receiverFingerprint);
      this.saveState(state);
    });
  }

  /**
   * @param {string} receiverFingerprint
   * @param {number} nowMs
   */
  nextConnectCounter(receiverFingerprint, nowMs) {
    return this.withLock(() => {
      const state = this.loadState();
      const next = Math.max(nowMs, (state.counters.get(receiverFingerprint) ?? 0) + 1);
      state.counters.set(receiverFingerprint, next);
      this.saveState(state);
      return next;
    });
  }
}

/**
 * @param {unknown} value
 * @param {string} what
 */
function requireCount(value, what) {
  if (!Number.isSafeInteger(value) || /** @type {number} */ (value) < 0 || /** @type {number} */ (value) > MAX_SEQUENCE) {
    throw new Error(`${what} is not a valid number`);
  }
  return /** @type {number} */ (value);
}

/** @param {Buffer} data */
function decodeState(data) {
  const root = JSON.parse(data.toString('utf8'));
  if (root === null || typeof root !== 'object' || root.version !== 1 || !Array.isArray(root.pairings)
    || root.counters === null || typeof root.counters !== 'object' || Array.isArray(root.counters)) {
    throw new Error('unknown format');
  }
  const pairings = root.pairings.map((/** @type {any} */ element) => {
    const key = hexDecode(element?.receiverKey);
    if (key === null || !isCanonicalPublicKey(key)) {
      throw new Error('a receiver key is not valid');
    }
    if (typeof element.label !== 'string' || !Array.isArray(element.kinds) || !element.kinds.every(isItemKind)) {
      throw new Error('a pairing is not valid');
    }
    const lastHost = element.lastHost ?? null;
    const lastPort = element.lastPort ?? null;
    if ((lastHost !== null && typeof lastHost !== 'string')
      || (lastPort !== null && (!Number.isInteger(lastPort) || lastPort < 1 || lastPort > 65535))) {
      throw new Error('an address is not valid');
    }
    const pairedAt = new Date(element.pairedAt);
    if (typeof element.pairedAt !== 'string' || Number.isNaN(pairedAt.getTime())) {
      throw new Error('a date is not valid');
    }
    return receiverPairing({
      label: element.label,
      receiverKey: key,
      kinds: element.kinds,
      lastHost,
      lastPort,
      pairedAt,
      counterFloor: requireCount(element.counterFloor, 'a counter floor'),
    });
  });
  /** @type {Map<string, number>} */
  const counters = new Map();
  for (const [name, value] of Object.entries(root.counters)) {
    counters.set(name, requireCount(value, 'a counter'));
  }
  return { pairings, counters };
}
