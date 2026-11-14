// Reads the files named on the command line: the pairing code, a note and a photo.

import { closeSync, fstatSync, openSync, readSync, realpathSync } from 'node:fs';
import { relative, isAbsolute } from 'node:path';
import { LIMITS_V1 } from '../model.js';
import { PrivateFileError, PrivateFileProblem, readPrivateFile } from '../private-file.js';
import { UsageError } from './terminal.js';

const strictUtf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

/**
 * On Windows, Node.js cannot read a file's access list without a native module. A file inside the current user's
 * own profile folder has, unless someone changed it, an access list that grants that user, SYSTEM and the
 * Administrators group, so the code file must be there.
 * @param {string} path
 */
function insideOwnProfile(path) {
  const profile = process.env.USERPROFILE;
  if (!profile) {
    return false;
  }
  try {
    const real = realpathSync(path).toLowerCase();
    const home = realpathSync(profile).toLowerCase();
    const inside = relative(home, real);
    return inside.length > 0 && !inside.startsWith('..') && !isAbsolute(inside);
  } catch {
    return false;
  }
}

/**
 * Reads a code file only if nobody else can read it, as ssh does for its keys.
 * @param {string} path
 */
export function readCodeFile(path) {
  if (process.platform === 'win32' && !insideOwnProfile(path)) {
    throw new UsageError('the code file must be a regular file in your own profile folder, where only you can read it');
  }
  let data;
  try {
    data = readPrivateFile(path, 1024);
  } catch (error) {
    if (!(error instanceof PrivateFileError)) {
      throw error;
    }
    if (error.problem === PrivateFileProblem.insecure) {
      throw new UsageError(process.platform === 'win32'
        ? 'the code file must be a regular file that only you can read'
        : 'the code file must be a regular file you own, with mode 0600, so that nobody else can read it');
    }
    if (error.problem === PrivateFileProblem.tooLarge) {
      throw new UsageError('the code file is too large');
    }
    throw new UsageError('could not open the code file');
  }
  try {
    // A byte order mark is how some editors mark UTF-8. It is not part of the code.
    const start = data.length >= 3 && data[0] === 0xef && data[1] === 0xbb && data[2] === 0xbf ? 3 : 0;
    return strictUtf8.decode(data.subarray(start));
  } catch {
    throw new UsageError('the code file is not UTF-8 text');
  } finally {
    data.fill(0);
  }
}

export const NOTE_TOO_LARGE = `a note is at most ${LIMITS_V1.noteBytes} bytes of UTF-8`;
export const PHOTO_TOO_LARGE = `a photo is at most ${LIMITS_V1.photoBytes} bytes`;

/**
 * A note file holds the note as UTF-8, with nothing added: no byte order mark, and no trailing newline unless it
 * belongs to the note.
 * @param {string} path
 */
export function readNote(path) {
  const data = readAtMost(path, LIMITS_V1.noteBytes, 'note', NOTE_TOO_LARGE);
  try {
    return strictUtf8.decode(data);
  } catch {
    throw new UsageError('the note file is not UTF-8 text');
  }
}

/**
 * The format is checked by the SDK when the photo is sent, before it connects.
 * @param {string} path
 */
export function readPhoto(path) {
  return readAtMost(path, LIMITS_V1.photoBytes, 'photo', PHOTO_TOO_LARGE);
}

/**
 * Reads a file, refusing it with `tooLarge` if it holds more than `maxBytes`. Its size is not trusted: nothing past
 * the limit is read, so a huge file, or one that keeps growing while it is read, costs no more memory than the
 * largest file that is allowed, and is refused before any connection is made.
 * @param {string} path
 * @param {number} maxBytes
 * @param {string} what
 * @param {string} tooLarge
 * @returns {Buffer}
 */
export function readAtMost(path, maxBytes, what, tooLarge) {
  let descriptor;
  try {
    descriptor = openSync(path, 'r');
  } catch (error) {
    const code = /** @type {NodeJS.ErrnoException} */ (error).code;
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      throw new UsageError(`the ${what} file does not exist`);
    }
    throw new UsageError(`could not open the ${what} file`);
  }
  try {
    const status = fstatSync(descriptor);
    if (status.isFile() && status.size > maxBytes) {
      throw new UsageError(tooLarge);
    }
    // One byte more than allowed is enough to know that it is too much.
    const buffer = Buffer.alloc(maxBytes + 1);
    let filled = 0;
    for (;;) {
      const read = readSync(descriptor, buffer, filled, buffer.length - filled, null);
      if (read === 0) {
        break;
      }
      filled += read;
      if (filled > maxBytes) {
        throw new UsageError(tooLarge);
      }
    }
    return Buffer.from(buffer.subarray(0, filled));
  } catch (error) {
    if (error instanceof UsageError) {
      throw error;
    }
    throw new UsageError(`could not read the ${what} file`);
  } finally {
    closeSync(descriptor);
  }
}
