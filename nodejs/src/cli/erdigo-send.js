// erdigo-send: the command-line tool, with the same commands and options as the Swift and .NET tools.

import { ErdigoClient, RetryPolicy } from '../client.js';
import { ReceiverTarget } from '../discovery.js';
import { ErdigoError, ErdigoErrorKind, pairingFingerprint } from '../identity.js';
import { ITEM_KINDS, MARKER_COLORS, PairingTicket, trimWhiteSpace, validateCoordinate, validateTrackPoint } from '../model.js';
import { pair } from '../pairing.js';
import { WireError, isFingerprint, shortFingerprint } from '../protocol.js';
import { FileDeviceStore } from '../store.js';
import { readCodeFile, readNote, readPhoto } from './files.js';
import { Interrupted, UsageError, ask, personIsThere, readSecretLine } from './terminal.js';

export const USAGE = `erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.

Pairing
  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]
       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
      The code is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument.
      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.
      That needs standard input and output to be a terminal, so a bare code is refused without one.
      The QR text or --fingerprint compares the key for you, so nothing is asked.
  list
  forget --to LABEL
  identity
      Prints this device's fingerprint.

Writing
  ping
  pin          --id ID --lat N --lon N [--title TEXT]
  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]
  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)
  photo        --id ID --lat N --lon N --file FILE [--title TEXT]
  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]
  track-finish --id ID
  move         --id ID --lat N --lon N
  delete       --id ID
  delete-all   --yes
      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS

Testing without the Erdigo app
  This edition has no mock receiver. Run the one in the .NET SDK (dotnet/) or the Swift SDK (swift/):
      dotnet run --project dotnet/src/erdigo-send -- serve --mock
      swift run erdigo-send serve --mock

For pairing and writing commands
  --profile NAME    Use a separate identity and pairings (default: "default").
  --state-dir DIR   Keep state in DIR instead of the default folder.
  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.
                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.

The device key is kept in a file only you can read: mode 0600 on macOS and Linux, and in a folder restricted
to you on Windows (%LOCALAPPDATA%\\ErdigoSDK.Node). It is not encrypted.`;

/**
 * Every option the tool has. Errors name an option only if it is on this list, because anything else that was typed
 * might be a pairing code.
 */
const KNOWN_OPTIONS = new Set([
  'alt', 'ask', 'auto-approve', 'bonjour', 'code-file', 'color', 'file', 'fingerprint', 'host', 'id', 'keychain', 'kinds', 'label', 'lan',
  'lat', 'lon', 'mock', 'name', 'port', 'profile', 'redact', 'retry-for', 'state-dir', 'text', 'text-file', 'title', 'to', 'ts', 'yes',
]);

/** @param {string} name */
function optionDescription(name) {
  return KNOWN_OPTIONS.has(name) ? `--${name}` : 'an unknown option';
}

/** Numbers as people type them: digits with an optional sign, point and exponent, and white space around them. */
const DECIMAL = /^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/;

/** A deliberately small option parser: `--name value` pairs and bare flags, nothing else. */
class Options {
  /**
   * @param {string[]} args
   * @param {string[]} allowedFlags
   */
  constructor(args, allowedFlags) {
    /** @type {Map<string, string>} */
    this.values = new Map();
    /** @type {Set<string>} */
    this.flags = new Set();
    /** @type {Set<string>} */
    this.used = new Set();
    let index = 0;
    while (index < args.length) {
      const argument = args[index];
      // Errors here never repeat what was typed, because it might be a pairing code.
      if (!argument.startsWith('--')) {
        throw new UsageError('unexpected argument. Options start with --, and a pairing code is never given as an argument.');
      }
      const name = argument.slice(2);
      if (name === 'code' || name.startsWith('code=')) {
        throw new UsageError('the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.');
      }
      const equals = name.indexOf('=');
      if (equals >= 0) {
        throw new UsageError(`write ${optionDescription(name.slice(0, equals))} and its value as two separate arguments`);
      }
      if (allowedFlags.includes(name)) {
        this.flags.add(name);
        index += 1;
        continue;
      }
      if (!KNOWN_OPTIONS.has(name)) {
        throw new UsageError("an unknown option was given. See 'erdigo-send help'.");
      }
      if (index + 1 >= args.length) {
        throw new UsageError(`--${name} needs a value`);
      }
      if (this.values.has(name)) {
        throw new UsageError(`--${name} was given twice`);
      }
      this.values.set(name, args[index + 1]);
      index += 2;
    }
  }

  /** @param {string} name */
  flag(name) {
    this.used.add(name);
    return this.flags.has(name);
  }

  /** @param {string} name @returns {string | null} */
  optional(name) {
    this.used.add(name);
    return this.values.get(name) ?? null;
  }

  /** @param {string} name */
  required(name) {
    const value = this.optional(name);
    if (value === null) {
      throw new UsageError(`--${name} is required`);
    }
    return value;
  }

  /** @param {string} name */
  requiredNumber(name) {
    const text = this.required(name);
    const value = Number(text);
    if (!DECIMAL.test(text) || !Number.isFinite(value)) {
      throw new UsageError(`--${name} must be a number`);
    }
    return value;
  }

  optionalPort() {
    const text = this.optional('port');
    if (text === null) {
      return null;
    }
    if (!/^\d{1,5}$/.test(text) || Number(text) > 65535) {
      throw new UsageError('--port must be between 0 and 65535');
    }
    return Number(text);
  }

  /** Call after reading every option the command understands. */
  finish() {
    const unknown = [...this.values.keys(), ...this.flags].filter((name) => !this.used.has(name)).sort()[0];
    if (unknown !== undefined) {
      throw new UsageError(`--${unknown} does not apply to this command`);
    }
  }
}

/**
 * @typedef {{ store: FileDeviceStore, redact: boolean, shown: (text: string) => string }} Context
 */

/**
 * @param {Options} options
 * @returns {Context}
 */
function makeContext(options) {
  const profile = options.optional('profile') ?? 'default';
  if (!/^[\p{L}\p{M}\p{Nd}\p{Nl}\p{No}_-]+$/u.test(profile)) {
    throw new UsageError("--profile may contain only letters, digits, '-' and '_'");
  }
  let directory;
  const path = options.optional('state-dir');
  if (path !== null) {
    if (options.optional('profile') !== null) {
      throw new UsageError('--state-dir and --profile cannot be used together, because a state folder holds one identity');
    }
    directory = path;
  } else {
    directory = FileDeviceStore.defaultDirectory(profile);
  }
  if (options.flag('keychain')) {
    throw new UsageError('--keychain belongs to the Swift SDK. This edition keeps the device key in a file only you can read.');
  }
  const redact = options.flag('redact');
  return { store: new FileDeviceStore(directory), redact, shown: (text) => (redact ? '<redacted>' : text) };
}

/** @param {string} text */
function parseKinds(text) {
  /** @type {Set<string>} */
  const kinds = new Set();
  for (const piece of text.split(',')) {
    if (piece.length === 0) {
      continue;
    }
    const kind = trimWhiteSpace(piece);
    if (!ITEM_KINDS.includes(kind)) {
      throw new UsageError('--kinds takes pin, marker, note, photo and track, separated by commas.');
    }
    kinds.add(kind);
  }
  if (kinds.size === 0) {
    throw new UsageError('--kinds needs at least one item kind');
  }
  return /** @type {import('../model.js').ItemKind[]} */ ([...kinds]);
}

/** @param {Options} options */
function targetOf(options) {
  const host = options.optional('host');
  const port = options.optionalPort();
  if (host === null && port === null) {
    return ReceiverTarget.discover;
  }
  if (host === null || port === null) {
    throw new UsageError('--host and --port go together');
  }
  return ReceiverTarget.address(host, port);
}

/**
 * @param {Context} context
 * @param {string | null} label
 */
function selectPairing(context, label) {
  const pairings = context.store.pairings();
  if (label !== null) {
    const found = pairings.filter((pairing) => pairing.label === label);
    if (found.length !== 1) {
      throw new UsageError(found.length === 0
        ? "no receiver is paired under that label. See 'erdigo-send list'."
        : 'several receivers have that label. Forget one of them first.');
    }
    return found[0];
  }
  if (pairings.length === 0) {
    throw new UsageError("no receiver is paired yet. Run 'erdigo-send pair' first.");
  }
  if (pairings.length > 1) {
    throw new UsageError('several receivers are paired. Choose one with --to LABEL.');
  }
  return pairings[0];
}

/** @param {Options} options */
function parseCoordinate(options) {
  const lat = options.requiredNumber('lat');
  const lon = options.requiredNumber('lon');
  try {
    return validateCoordinate({ lat, lon });
  } catch (error) {
    if (error instanceof WireError) {
      throw new UsageError(error.detail);
    }
    throw error;
  }
}

async function readCodeFromKeyboard() {
  if (process.platform === 'win32') {
    if (!process.stdin.isTTY) {
      throw new UsageError('no terminal to read the code from. Use --code-file with a file only you can read.');
    }
  }
  const code = await readSecretLine('Pairing code: ', 1024);
  if (code === null) {
    throw new UsageError(process.platform === 'win32'
      ? 'no terminal to read the code from. Use --code-file with a file only you can read.'
      : 'no terminal to read the code from. Use --code-file with a file of mode 0600.');
  }
  return code;
}

/** @param {string[]} args */
async function runPair(args) {
  const options = new Options(args, ['keychain', 'redact']);
  const context = makeContext(options);
  const name = options.required('name');
  const kinds = parseKinds(options.required('kinds'));
  const label = options.optional('label');
  const codeFile = options.optional('code-file');
  const fingerprint = options.optional('fingerprint')?.toLowerCase() ?? null;
  const where = targetOf(options);
  options.finish();
  if (fingerprint !== null && !isFingerprint(fingerprint)) {
    throw new UsageError('--fingerprint must be 64 hexadecimal digits');
  }
  if (label !== null && context.store.pairings().some((pairing) => pairing.label === label)) {
    throw new UsageError("a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.");
  }
  const text = codeFile !== null ? readCodeFile(codeFile) : await readCodeFromKeyboard();
  let ticket;
  try {
    ticket = PairingTicket.parse(text);
  } catch (error) {
    if (error instanceof WireError) {
      throw new UsageError(error.detail);
    }
    throw error;
  }
  if (fingerprint !== null) {
    if (ticket.fingerprint !== null && ticket.fingerprint !== fingerprint) {
      throw new ErdigoError(ErdigoErrorKind.fingerprintMismatch);
    }
    ticket = new PairingTicket(ticket.code, fingerprint);
  }
  if (ticket.fingerprint === null && context.redact) {
    throw new UsageError("--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint.");
  }
  // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it is
  // no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no answer
  // is possible.
  if (ticket.fingerprint === null && !personIsThere()) {
    throw new UsageError("a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
  }
  const waiting = 'Waiting for the request to be confirmed on the receiver...';
  const personCompares = ticket.fingerprint === null;
  console.log(personCompares ? 'Checking the code with the receiver...' : waiting);
  const identity = context.store.loadOrCreateIdentity();
  try {
    const pairing = await pair({
      target: where,
      ticket,
      identity,
      deviceName: name,
      kinds,
      label: label ?? undefined,
      // A bare code proves only that the other side knows the code, and only a person can tell whether this is the
      // right receiver. This runs before the receiver is asked to pair.
      confirmKey: async (key) => {
        // Only an answer given after seeing the key counts, so anything typed before it is discarded. Standard input
        // is a terminal here, because a bare code is refused otherwise.
        const answer = await ask([`Receiver key: ${key}`, 'If this matches the key shown on the receiver, type yes: ']);
        if (answer === null || trimWhiteSpace(answer).toLowerCase() !== 'yes') {
          console.log();
          return false;
        }
        console.log(waiting);
        return true;
      },
    });
    context.store.save(pairing);
    console.log(`Paired as '${context.shown(pairing.label)}'. Allowed: ${pairing.kinds.join(', ')}.`);
    if (!personCompares) {
      console.log(`Receiver key: ${context.shown(shortFingerprint(pairingFingerprint(pairing)))}`);
    }
  } finally {
    identity.dispose();
  }
}

/** @param {string[]} args */
function runList(args) {
  const options = new Options(args, ['keychain', 'redact']);
  const context = makeContext(options);
  options.finish();
  const pairings = context.store.pairings();
  if (pairings.length === 0) {
    console.log('No receivers are paired.');
  }
  for (const pairing of pairings) {
    console.log(`${context.shown(pairing.label)}  key ${context.shown(shortFingerprint(pairingFingerprint(pairing)))}  allowed ${pairing.kinds.join(',')}`);
  }
}

/** @param {string[]} args */
function runForget(args) {
  const options = new Options(args, ['keychain', 'redact']);
  const context = makeContext(options);
  const label = options.required('to');
  options.finish();
  const pairing = selectPairing(context, label);
  context.store.removePairing(pairingFingerprint(pairing));
  console.log('Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.');
}

/** @param {string[]} args */
function runIdentity(args) {
  const options = new Options(args, ['keychain', 'redact']);
  const context = makeContext(options);
  options.finish();
  const identity = context.store.loadOrCreateIdentity();
  try {
    console.log(context.shown(identity.fingerprint));
  } finally {
    identity.dispose();
  }
}

/**
 * Everything common to the writing commands. `prepare` reads its own options and returns the work to do.
 * @param {string[]} args
 * @param {(options: Options) => (client: ErdigoClient) => Promise<void>} prepare
 * @param {string[]} [extraFlags]
 */
async function runWrite(args, prepare, extraFlags = []) {
  const options = new Options(args, [...extraFlags, 'keychain', 'redact']);
  const context = makeContext(options);
  const label = options.optional('to');
  const where = targetOf(options);
  let retry = RetryPolicy.upTo(30_000);
  const retryText = options.optional('retry-for');
  if (retryText !== null) {
    const seconds = Number(retryText);
    if (!DECIMAL.test(retryText) || !Number.isFinite(seconds) || seconds < 0 || seconds > 1e9) {
      throw new UsageError('--retry-for must be a number of seconds');
    }
    retry = seconds === 0 ? RetryPolicy.none : RetryPolicy.upTo(seconds * 1000);
  }
  const body = prepare(options);
  options.finish();
  const pairing = selectPairing(context, label);
  const identity = context.store.loadOrCreateIdentity();
  const client = new ErdigoClient({ identity, pairing, store: context.store, target: where, retry });
  try {
    await body(client);
  } finally {
    client.close();
    identity.dispose();
  }
  console.log('OK');
}

/**
 * @param {string} command
 * @param {string[]} args
 */
async function runCommand(command, args) {
  switch (command) {
    case 'pair':
      return runPair(args);
    case 'list':
      return runList(args);
    case 'forget':
      return runForget(args);
    case 'identity':
      return runIdentity(args);
    case 'serve':
      throw new UsageError('this edition has no mock receiver. Run the one in the .NET SDK (dotnet/) or the Swift SDK (swift/), as \'erdigo-send help\' shows.');
    case 'ping':
      return runWrite(args, () => (client) => client.ping());
    case 'pin':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        const title = options.optional('title');
        return (client) => client.putPin(id, at, { title });
      });
    case 'marker':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        const title = options.optional('title');
        const color = options.optional('color');
        if (color !== null && !MARKER_COLORS.includes(color)) {
          throw new UsageError(`--color must be one of: ${MARKER_COLORS.join(', ')}`);
        }
        return (client) => client.putMarker(id, at, { title, color: /** @type {import('../model.js').MarkerColor | null} */ (color) });
      });
    case 'note':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        const inline = options.optional('text');
        const file = options.optional('text-file');
        if ((inline === null) === (file === null)) {
          throw new UsageError('give either --text or --text-file');
        }
        const text = inline ?? readNote(/** @type {string} */ (file));
        return (client) => client.putNote(id, at, text);
      });
    case 'photo':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        const title = options.optional('title');
        const data = readPhoto(options.required('file'));
        return (client) => client.putPhoto(id, at, data, { title });
      });
    case 'track-add':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        const title = options.optional('title');
        /** @type {number | null} */
        let ts = null;
        const tsText = options.optional('ts');
        if (tsText !== null) {
          if (!/^[+-]?\d+$/.test(tsText)) {
            throw new UsageError('--ts must be whole milliseconds');
          }
          const value = BigInt(tsText);
          if (value < -(2n ** 63n) || value > 2n ** 63n - 1n) {
            throw new UsageError('--ts must be whole milliseconds');
          }
          ts = Number(value);
          if (!Number.isSafeInteger(ts)) {
            // Out of range, and refused below with the reason.
            ts = value < 0n ? -1 : Number.MAX_SAFE_INTEGER + 2;
          }
        }
        /** @type {number | null} */
        let alt = null;
        const altText = options.optional('alt');
        if (altText !== null) {
          alt = Number(altText);
          if (!DECIMAL.test(altText)) {
            throw new UsageError('--alt must be a number');
          }
        }
        let point;
        try {
          point = validateTrackPoint({ ...at, ts, alt });
        } catch (error) {
          if (error instanceof WireError) {
            throw new UsageError(error.detail);
          }
          throw error;
        }
        return (client) => client.appendTrack(id, [point], { title });
      });
    case 'track-finish':
      return runWrite(args, (options) => {
        const id = options.required('id');
        return (client) => client.finishTrack(id);
      });
    case 'move':
      return runWrite(args, (options) => {
        const id = options.required('id');
        const at = parseCoordinate(options);
        return (client) => client.move(id, at);
      });
    case 'delete':
      return runWrite(args, (options) => {
        const id = options.required('id');
        return (client) => client.delete(id);
      });
    case 'delete-all':
      return runWrite(args, (options) => {
        if (!options.flag('yes')) {
          throw new UsageError('this deletes every item this device put on the receiver. Add --yes to confirm.');
        }
        return (client) => client.deleteAll();
      }, ['yes']);
    default:
      console.log(USAGE);
      throw new UsageError('unknown command');
  }
}

/**
 * Runs the tool and returns its exit status.
 * @param {string[]} args
 * @returns {Promise<number>}
 */
export async function main(args) {
  if (args.length === 0 || args[0] === 'help' || args[0] === '--help' || args[0] === '-h') {
    console.log(USAGE);
    return 0;
  }
  try {
    await runCommand(args[0], args.slice(1));
    return 0;
  } catch (error) {
    if (error instanceof Interrupted) {
      process.stderr.write('\n');
      return 130;
    }
    const known = error instanceof UsageError || error instanceof ErdigoError || error instanceof WireError
      || (error instanceof Error && typeof (/** @type {NodeJS.ErrnoException} */ (error).code) === 'string');
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`erdigo-send: ${known ? message : `unexpected error: ${message}`}\n`);
    return 1;
  }
}
