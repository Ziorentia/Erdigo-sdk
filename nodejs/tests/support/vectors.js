// Reads the shared test vectors in spec/vectors, which every edition of the SDK must reproduce.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const REPOSITORY = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

/** @param {string} relative a path under the repository, such as `spec/vectors/noise/cacophony-subset.json` */
export function loadJson(relative) {
  return JSON.parse(readFileSync(join(REPOSITORY, relative), 'utf8'));
}

/** @param {string | undefined | null} text */
export function bytes(text) {
  return text === undefined || text === null ? null : Buffer.from(text, 'hex');
}
