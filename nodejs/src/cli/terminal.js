// Reads from the person at the keyboard, and only from a terminal.
//
// On macOS and Linux this reads the terminal itself (/dev/tty), as readpassphrase and the Swift tool do. On Windows it
// reads the console through standard input, which must then be the console.

import { closeSync, constants, openSync, readSync, writeSync } from 'node:fs';
import { StringDecoder } from 'node:string_decoder';
import { ReadStream, isatty } from 'node:tty';

/** A mistake in how the tool was called. Its message never repeats what was typed. */
export class UsageError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'UsageError';
  }
}

/** The person pressed Ctrl-C at a question. */
export class Interrupted extends Error {
  constructor() {
    super('interrupted');
    this.name = 'Interrupted';
  }
}

/** True if both standard input and standard output are a terminal, so that a person can see a question and answer it. */
export function personIsThere() {
  return isatty(0) && isatty(1);
}

const isWindows = process.platform === 'win32';

/**
 * A terminal in raw mode: nothing typed is shown or acted on by the system, and every key arrives at once. Opening
 * one discards anything typed before it was opened, because only what is typed after a question appears is an answer
 * to it.
 */
class RawTerminal {
  /**
   * @param {ReadStream} input
   * @param {(text: string) => void} write
   * @param {() => void} closeOutput
   */
  constructor(input, write, closeOutput) {
    this.input = input;
    this.write = write;
    this.closeOutput = closeOutput;
    /** @type {Buffer[]} */
    this.queue = [];
    /** @type {(() => void) | null} */
    this.wake = null;
    this.ended = false;
    this.restore = () => {
      try {
        this.input.setRawMode(false);
      } catch {
        // Already closed.
      }
    };
    this.onSignal = (/** @type {NodeJS.Signals} */ signal) => {
      this.restore();
      process.kill(process.pid, signal);
    };
  }

  /** @returns {Promise<RawTerminal | null>} null when there is no terminal to read from */
  static async open() {
    if (isWindows) {
      if (!isatty(0)) {
        return null;
      }
      const input = /** @type {ReadStream} */ (process.stdin);
      const terminal = new RawTerminal(input, (text) => process.stderr.write(text), () => {});
      terminal.start();
      // The console gives no way to see what is waiting, so whatever arrives at once was typed before.
      await new Promise((resolve) => setTimeout(resolve, 150));
      terminal.queue = [];
      return terminal;
    }
    let readDescriptor;
    let writeDescriptor;
    try {
      // Non-blocking, so that what was typed earlier can be read and discarded without waiting for more.
      readDescriptor = openSync('/dev/tty', constants.O_RDWR | constants.O_NOCTTY | constants.O_NONBLOCK);
    } catch {
      return null;
    }
    if (!isatty(readDescriptor)) {
      closeSync(readDescriptor);
      return null;
    }
    try {
      writeDescriptor = openSync('/dev/tty', constants.O_WRONLY | constants.O_NOCTTY);
    } catch {
      closeSync(readDescriptor);
      return null;
    }
    const input = new ReadStream(readDescriptor);
    const output = writeDescriptor;
    const terminal = new RawTerminal(
      input,
      (text) => {
        writeSync(output, text);
      },
      () => closeSync(output),
    );
    input.setRawMode(true);
    terminal.installSignalHandlers();
    // Anything typed before this point is discarded, as readpassphrase does.
    const scratch = Buffer.alloc(4096);
    for (;;) {
      let read;
      try {
        read = readSync(readDescriptor, scratch, 0, scratch.length, null);
      } catch {
        break;
      }
      if (read <= 0) {
        break;
      }
    }
    scratch.fill(0);
    terminal.start();
    return terminal;
  }

  installSignalHandlers() {
    for (const signal of /** @type {NodeJS.Signals[]} */ (['SIGTERM', 'SIGHUP'])) {
      process.once(signal, this.onSignal);
    }
  }

  start() {
    if (isWindows) {
      this.input.setRawMode(true);
    }
    this.input.on('data', (/** @type {Buffer} */ chunk) => {
      this.queue.push(chunk);
      this.wake?.();
    });
    this.input.on('end', () => {
      this.ended = true;
      this.wake?.();
    });
    this.input.on('error', () => {
      this.ended = true;
      this.wake?.();
    });
    this.input.resume();
  }

  /** @returns {Promise<Buffer | null>} */
  async next() {
    while (this.queue.length === 0 && !this.ended) {
      await new Promise((resolve) => {
        this.wake = () => resolve(undefined);
      });
      this.wake = null;
    }
    return this.queue.shift() ?? null;
  }

  /**
   * Reads one line, showing what is typed only if `echo` is set. Returns null at the end of input (Ctrl-D, or Ctrl-Z
   * on Windows).
   * @param {{ echo: boolean, maxLength: number }} options
   * @returns {Promise<string | null>}
   */
  async readLine({ echo, maxLength }) {
    const decoder = new StringDecoder('utf8');
    /** @type {string[]} */
    const typed = [];
    let escape = 0;
    for (;;) {
      const chunk = await this.next();
      if (chunk === null) {
        return null;
      }
      const text = decoder.write(chunk);
      chunk.fill(0);
      for (const character of text) {
        const code = /** @type {number} */ (character.codePointAt(0));
        if (escape === 1) {
          // An escape sequence, such as an arrow key: ESC [ ... final, or ESC O final.
          escape = character === '[' || character === 'O' ? 2 : 0;
          continue;
        }
        if (escape === 2) {
          if (code >= 0x40 && code <= 0x7e) {
            escape = 0;
          }
          continue;
        }
        if (character === '\r' || character === '\n') {
          if (echo) {
            this.write('\n');
          }
          const line = typed.join('');
          typed.fill('');
          return line;
        }
        if (code === 0x03) {
          throw new Interrupted();
        }
        if (code === 0x04 || code === 0x1a) {
          typed.fill('');
          return null;
        }
        if (code === 0x7f || code === 0x08) {
          if (typed.length > 0) {
            typed.pop();
            if (echo) {
              this.write('\b \b');
            }
          }
          continue;
        }
        if (code === 0x1b) {
          escape = 1;
          continue;
        }
        if (code < 0x20 || (code >= 0x80 && code <= 0x9f)) {
          continue;
        }
        if (typed.length >= maxLength) {
          typed.fill('');
          throw new UsageError('the text typed is too long');
        }
        typed.push(character);
        if (echo) {
          this.write(character);
        }
      }
    }
  }

  close() {
    this.restore();
    process.off('SIGTERM', this.onSignal);
    process.off('SIGHUP', this.onSignal);
    this.input.removeAllListeners('data');
    this.input.pause();
    if (!isWindows) {
      this.input.destroy();
      this.closeOutput();
    }
  }
}

/**
 * Shows `prompt` on the terminal and reads one line from it, without showing what is typed. Anything typed before
 * the prompt is discarded. Returns null if there is no terminal, and an empty string at the end of input.
 * @param {string} prompt
 * @param {number} maxLength
 * @returns {Promise<string | null>}
 */
export async function readSecretLine(prompt, maxLength) {
  const terminal = await RawTerminal.open();
  if (terminal === null) {
    return null;
  }
  try {
    terminal.write(prompt);
    const line = await terminal.readLine({ echo: false, maxLength });
    terminal.write('\n');
    return line ?? '';
  } finally {
    terminal.close();
  }
}

/**
 * Asks a question and reads the answer, showing what is typed. Only an answer given after the question has appeared
 * counts, so anything typed before it is discarded. The question goes to standard output, which the caller has
 * checked is a terminal. Returns null at the end of input or when there is no terminal.
 * @param {string[]} lines what to show, the last of them the question itself
 * @returns {Promise<string | null>}
 */
export async function ask(lines) {
  const terminal = await RawTerminal.open();
  if (terminal === null) {
    return null;
  }
  try {
    // The question appears only now, after what was typed before has been discarded.
    process.stdout.write(lines.join('\n'));
    return await terminal.readLine({ echo: true, maxLength: 256 });
  } finally {
    terminal.close();
  }
}
