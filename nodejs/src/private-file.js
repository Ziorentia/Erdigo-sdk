// Files and folders that only the current user can read, the way ssh treats its keys.
//
// On macOS and Linux a private folder has mode 0700 and a private file mode 0600, and both must belong to the
// current user. Node.js cannot read or change a Windows access list without a native module, so on Windows a new
// folder loses what it inherits and is granted to the current user with the system's icacls tool, and nothing is
// checked when reading. The README says what this means.

import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  closeSync,
  constants,
  fstatSync,
  fsyncSync,
  linkSync,
  lstatSync,
  mkdirSync,
  openSync,
  readSync,
  renameSync,
  rmdirSync,
  unlinkSync,
  writeSync,
} from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';

export const isWindows = process.platform === 'win32';

/** Why a private file was refused. */
export const PrivateFileProblem = Object.freeze({
  /** There is no such file. */
  missing: 'missing',
  /** The file exists but could not be opened or read. */
  unreadable: 'unreadable',
  /** The file is a link or not a regular file, or someone other than its owner could read or replace it. */
  insecure: 'insecure',
  /** The file is larger than the caller allows. */
  tooLarge: 'tooLarge',
});

export class PrivateFileError extends Error {
  /**
   * @param {string} problem one of {@link PrivateFileProblem}
   * @param {string} message
   * @param {unknown} [cause]
   */
  constructor(problem, message, cause) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = 'PrivateFileError';
    this.problem = problem;
  }
}

/** @param {unknown} error */
function codeOf(error) {
  return /** @type {NodeJS.ErrnoException} */ (error)?.code;
}

const NO_FOLLOW = constants.O_NOFOLLOW ?? 0;
// A FIFO would otherwise make opening it wait for a writer.
const NON_BLOCKING = constants.O_NONBLOCK ?? 0;

/**
 * Reads a whole file that only the current user can read, refusing links, other kinds of file and files over
 * `maxBytes`.
 * @param {string} path
 * @param {number} maxBytes
 * @returns {Buffer}
 */
export function readPrivateFile(path, maxBytes) {
  const name = basename(path);
  if (isWindows) {
    let link;
    try {
      link = lstatSync(path);
    } catch (error) {
      if (codeOf(error) === 'ENOENT' || codeOf(error) === 'ENOTDIR') {
        throw new PrivateFileError(PrivateFileProblem.missing, `${name} does not exist`, error);
      }
      throw new PrivateFileError(PrivateFileProblem.unreadable, `${name} could not be opened`, error);
    }
    if (link.isSymbolicLink()) {
      throw new PrivateFileError(PrivateFileProblem.insecure, `${name} must be a regular file, not a link`);
    }
  }
  let descriptor;
  try {
    descriptor = openSync(path, constants.O_RDONLY | NO_FOLLOW | NON_BLOCKING);
  } catch (error) {
    const code = codeOf(error);
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      throw new PrivateFileError(PrivateFileProblem.missing, `${name} does not exist`, error);
    }
    if (code === 'ELOOP' || code === 'EMLINK') {
      throw new PrivateFileError(PrivateFileProblem.insecure, `${name} must be a regular file, not a link`, error);
    }
    if (code === 'EISDIR') {
      throw new PrivateFileError(PrivateFileProblem.insecure, `${name} must be a regular file`, error);
    }
    throw new PrivateFileError(PrivateFileProblem.unreadable, `${name} could not be opened`, error);
  }
  try {
    let status;
    try {
      status = fstatSync(descriptor);
    } catch (error) {
      throw new PrivateFileError(PrivateFileProblem.unreadable, `${name} could not be read`, error);
    }
    if (!status.isFile()) {
      throw new PrivateFileError(PrivateFileProblem.insecure, `${name} must be a regular file`);
    }
    if (!isWindows && ((status.mode & 0o077) !== 0 || status.uid !== process.getuid?.())) {
      throw new PrivateFileError(PrivateFileProblem.insecure, `${name} must be a regular file owned by you with mode 0600`);
    }
    if (status.size > maxBytes) {
      throw new PrivateFileError(PrivateFileProblem.tooLarge, `${name} is unexpectedly large`);
    }
    const data = Buffer.alloc(status.size);
    let filled = 0;
    try {
      while (filled < data.length) {
        const read = readSync(descriptor, data, filled, data.length - filled, null);
        if (read === 0) {
          break;
        }
        filled += read;
      }
      if (filled !== data.length || readSync(descriptor, Buffer.alloc(1), 0, 1, null) !== 0) {
        throw new PrivateFileError(PrivateFileProblem.tooLarge, `${name} changed while it was read`);
      }
    } catch (error) {
      if (error instanceof PrivateFileError) {
        throw error;
      }
      throw new PrivateFileError(PrivateFileProblem.unreadable, `${name} could not be read`, error);
    }
    return data;
  } finally {
    closeSync(descriptor);
  }
}

/**
 * Writes a whole file through a temporary file and a rename, so that a crash never leaves a partial file and a
 * reader never sees one. Used inside a private folder. Without `overwrite` it fails if anything at all is at the
 * path, even something it cannot read, so a new device key can never take the place of one that exists.
 * @param {string} path
 * @param {Uint8Array} data
 * @param {boolean} [overwrite]
 */
export function writePrivateFileAtomically(path, data, overwrite = true) {
  const full = resolve(path);
  const temporary = join(dirname(full), `.${basename(full)}.${randomBytes(8).toString('hex')}`);
  try {
    const descriptor = openSync(temporary, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | NO_FOLLOW, 0o600);
    try {
      let written = 0;
      while (written < data.length) {
        written += writeSync(descriptor, data, written, data.length - written);
      }
      fsyncSync(descriptor);
    } finally {
      closeSync(descriptor);
    }
    if (overwrite) {
      replace(temporary, full);
    } else {
      // A hard link fails if anything is at the path, where a rename would replace it.
      linkSync(temporary, full);
    }
  } finally {
    // After a rename it is gone already. After a link, or a failure, it is removed here.
    try {
      unlinkSync(temporary);
    } catch {
      // A leftover temporary file is private and harmless.
    }
  }
}

/**
 * @param {string} source
 * @param {string} destination
 */
function replace(source, destination) {
  // A virus scanner or an indexer can hold a file open for a moment on Windows.
  for (let attempt = 0; ; attempt += 1) {
    try {
      renameSync(source, destination);
      return;
    } catch (error) {
      const code = codeOf(error);
      if (isWindows && attempt < 20 && (code === 'EPERM' || code === 'EBUSY' || code === 'EACCES')) {
        sleepSync(50);
        continue;
      }
      throw error;
    }
  }
}

/** @param {number} milliseconds */
export function sleepSync(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

/**
 * Creates a private folder, or checks that an existing one is private. Its parent folders get default permissions.
 * @param {string} path
 */
export function ensurePrivateDirectory(path) {
  const full = resolve(path);
  mkdirSync(dirname(full), { recursive: true });
  let created = false;
  try {
    mkdirSync(full, { mode: 0o700 });
    created = true;
  } catch (error) {
    if (codeOf(error) !== 'EEXIST') {
      throw error;
    }
    // Created by someone else, or earlier. It is checked below like any other.
  }
  if (created && isWindows) {
    try {
      restrictWindowsFolder(full);
    } catch (error) {
      try {
        rmdirSync(full);
      } catch {
        // Left for the person to remove. It is empty.
      }
      throw error;
    }
  }
  checkPrivateDirectory(full);
}

/** @param {string} path */
function checkPrivateDirectory(path) {
  let status;
  try {
    status = lstatSync(path);
  } catch (error) {
    throw new PrivateFileError(PrivateFileProblem.insecure, 'the state folder must be a real folder', error);
  }
  if (!status.isDirectory() || status.isSymbolicLink()) {
    throw new PrivateFileError(PrivateFileProblem.insecure, 'the state folder must be a real folder');
  }
  if (isWindows) {
    return;
  }
  if ((status.mode & 0o077) !== 0 || status.uid !== process.getuid?.()) {
    throw new PrivateFileError(PrivateFileProblem.insecure, 'the state folder must be a real folder owned by you with mode 0700');
  }
}

/**
 * Removes everything a new folder inherits and grants the current user full control, using the icacls tool that comes
 * with Windows. SYSTEM and the Administrators group may keep access, as the .NET SDK allows.
 * @param {string} path
 */
function restrictWindowsFolder(path) {
  const system32 = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32');
  const who = spawnSync(join(system32, 'whoami.exe'), ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true });
  const sid = /"(S-1-[0-9-]+)"/.exec(who.stdout ?? '')?.[1];
  if (who.status !== 0 || !sid) {
    throw new PrivateFileError(PrivateFileProblem.insecure, 'the current Windows user could not be determined');
  }
  const result = spawnSync(
    join(system32, 'icacls.exe'),
    [path, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`],
    { encoding: 'utf8', windowsHide: true },
  );
  if (result.status !== 0) {
    throw new PrivateFileError(PrivateFileProblem.insecure, 'the state folder could not be restricted to you alone');
  }
}
