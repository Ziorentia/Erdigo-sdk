// How the command-line tool reads a note file, as the .NET and Swift tools read it.

import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { NOTE_TOO_LARGE, noteFromFile, readNote } from '../src/cli/files.js';
import { LIMITS_V1 } from '../src/model.js';

/** @param {(directory: string) => void} body */
function inFolder(body) {
  const directory = mkdtempSync(join(tmpdir(), 'erdigo-files-'));
  try {
    body(directory);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

const bom = Buffer.from([0xef, 0xbb, 0xbf]);

test('a note saved by Notepad is read without its byte order mark or carriage returns', () => {
  inFolder((directory) => {
    const path = join(directory, 'note.txt');
    writeFileSync(path, Buffer.concat([bom, Buffer.from('First line\r\nSecond é\r\n\r\nend', 'utf8')]));
    assert.equal(readNote(path), 'First line\nSecond é\n\nend');
  });
});

test('only one byte order mark and only CR LF pairs are changed', () => {
  assert.equal(noteFromFile('\uFEFF\uFEFFa'), '\uFEFFa');
  assert.equal(noteFromFile('a\rb'), 'a\rb');
  assert.equal(noteFromFile('a\r\r\nb'), 'a\r\nb');
  assert.equal(noteFromFile('a\n\rb'), 'a\n\rb');
  assert.equal(noteFromFile('a\r'), 'a\r');
});

test('the note limit counts the note that is sent, not the file', () => {
  inFolder((directory) => {
    const lines = LIMITS_V1.noteBytes / 2;
    const atLimit = join(directory, 'at-limit.txt');
    writeFileSync(atLimit, Buffer.concat([bom, Buffer.from('a\r\n'.repeat(lines), 'utf8')]));
    assert.equal(readNote(atLimit), 'a\n'.repeat(lines));
    const over = join(directory, 'over.txt');
    writeFileSync(over, Buffer.from('a\r\n'.repeat(lines) + 'a', 'utf8'));
    assert.throws(() => readNote(over), { message: NOTE_TOO_LARGE });
    const plain = join(directory, 'plain.txt');
    writeFileSync(plain, Buffer.alloc(LIMITS_V1.noteBytes + 1, 'a'));
    assert.throws(() => readNote(plain), { message: NOTE_TOO_LARGE });
  });
});
